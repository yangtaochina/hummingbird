using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hummingbird.Core;

public sealed record EvidenceFile(string Path,long Bytes,string Sha256,string Purpose);

/// <summary>只导出明确登记的资料；不遍历程序目录，不收集配置或原始日志。</summary>
public sealed class FieldEvidenceArchive
{
    public string Folder {get;}
    public string DeviceMode {get;}
    private readonly string[] secrets;
    private readonly Dictionary<string,EvidenceFile> files=new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions=new(){WriteIndented=true};
    public FieldEvidenceArchive(string folder,string deviceMode,IEnumerable<string>? excludedSecrets=null)
    {
        Folder=Path.GetFullPath(folder);DeviceMode=deviceMode;
        secrets=(excludedSecrets??[]).Where(s=>s is {Length:>=4}).ToArray();
        Directory.CreateDirectory(Folder);
    }
    private string Target(string relative)
    {
        if(Path.IsPathRooted(relative) || relative.Split('/','\\').Any(s=>s is ".." or "." || s.Length==0))
            throw new InvalidDataException("取证文件名必须是包内相对路径。");
        string name=Path.GetFileName(relative);
        if(relative.Equals("manifest.json",StringComparison.OrdinalIgnoreCase) || name.Equals("config.json",StringComparison.OrdinalIgnoreCase) || name.Equals("operator-state.json",StringComparison.OrdinalIgnoreCase) ||
            !new[]{".json",".bmp",".png",".mpf",".md",".txt"}.Contains(Path.GetExtension(name),StringComparer.OrdinalIgnoreCase))
            throw new InvalidDataException("此类文件不纳入资料包。");
        string target=Path.GetFullPath(Path.Combine(Folder,relative));
        if(!target.StartsWith(Folder+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase))throw new InvalidDataException("取证目标越界。");
        for(string? p=Path.GetDirectoryName(target);p is not null && p.StartsWith(Folder,StringComparison.OrdinalIgnoreCase);p=Path.GetDirectoryName(p))
            if(Directory.Exists(p) && (File.GetAttributes(p)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("取证目录不能使用链接。");
        if(File.Exists(target) && (File.GetAttributes(target)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("取证文件不能使用链接。");
        return target;
    }
    private string Write(string relative,byte[] data,string purpose)
    {
        string target=Target(relative);
        if(Path.GetExtension(target) is not (".bmp" or ".png"))
        {
            string text=Encoding.UTF8.GetString(data);
            if(secrets.Any(s=>text.Contains(s,StringComparison.Ordinal)))throw new InvalidDataException("资料包含连接凭据，已拒绝纳入。");
            if(Path.GetExtension(target).Equals(".json",StringComparison.OrdinalIgnoreCase))
            {
                using var document=JsonDocument.Parse(data);
                bool HasCredentials(JsonElement value)
                {
                    if(value.ValueKind==JsonValueKind.Object)
                        return value.EnumerateObject().Any(p=>p.Name.Contains("password",StringComparison.OrdinalIgnoreCase)||p.Name.Equals("pwd",StringComparison.OrdinalIgnoreCase)||
                            p.Name.Equals("ClientSecret",StringComparison.OrdinalIgnoreCase)||HasCredentials(p.Value));
                    return value.ValueKind==JsonValueKind.Array && value.EnumerateArray().Any(HasCredentials);
                }
                if(HasCredentials(document.RootElement))throw new InvalidDataException("资料含凭据字段，已拒绝纳入。");
            }
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string temporary=target+".tmp";File.WriteAllBytes(temporary,data);File.Move(temporary,target,true);
        string normalized=relative.Replace('\\','/');
        files[normalized]=new(normalized,data.LongLength,Convert.ToHexString(SHA256.HashData(data)),purpose);
        SaveManifest();return target;
    }
    private object Manifest()=>new {Kind="HummingbirdFieldEvidence",Version=1,DeviceMode,Synthetic=DeviceMode=="Sim",UpdatedAt=DateTimeOffset.Now,
        Files=files.Values.OrderBy(f=>f.Path).ToArray(),Excluded="设备连接配置、操作偏好、可执行文件和原始日志未收集"};
    private void SaveManifest()=>File.WriteAllText(Path.Combine(Folder,"manifest.json"),JsonSerializer.Serialize(Manifest(),JsonOptions));
    public string AddJson<T>(string relative,T value,string purpose)=>Write(relative,JsonSerializer.SerializeToUtf8Bytes(value,JsonOptions),purpose);
    public string AddFile(string source,string relative,string purpose)=>Write(relative,File.ReadAllBytes(source),purpose);
    public void CaptureSession(RingScanSession session)
    {
        var frames=new List<RingScanFrame>();
        foreach(var frame in session.Frames)
        {
            RingCaptureArchive.VerifyImage(frame);
            string relative=$"定位/原图/{GuidedCapture.Index(frame)}_{Path.GetFileName(frame.ImagePath)}";
            string copied=AddFile(frame.ImagePath,relative,"定位原图");frames.Add(frame with {ImagePath=copied});
        }
        // 通过序列化生成可移植路径，保留照片指纹和采集来源。
        string sessionPath=Path.Combine(Folder,"定位","整组记录.json");
        var copy=new RingScanSession {Settings=session.Settings,DirectionOptions=session.DirectionOptions,Frames=frames};
        Directory.CreateDirectory(Path.GetDirectoryName(sessionPath)!);copy.Save(sessionPath);
        AddFile(sessionPath,"定位/整组记录.json","可移植定位记录");
        foreach(var f in frames)
        {
            string relative="定位/配套/"+Path.GetFileNameWithoutExtension(f.ImagePath)+".capture.json";
            var single=new RingScanSession {Settings=copy.Settings,DirectionOptions=copy.DirectionOptions,
                Frames=[f with {ImagePath=Path.GetRelativePath(Path.Combine(Folder,"定位","配套"),f.ImagePath)}]};
            AddJson(relative,single,"单图坐标和标定");
        }
    }
    public string Export(string summary)
    {
        Write("汇总.md",Encoding.UTF8.GetBytes(summary),"现场资料汇总");
        string output=Folder+".zip",temporary=output+".partial";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.Create,FileAccess.Write,FileShare.None))
            using(var zip=new ZipArchive(stream,ZipArchiveMode.Create))
            {
                foreach(var file in files.Values.OrderBy(f=>f.Path))
                {
                    byte[] data=File.ReadAllBytes(Target(file.Path));
                    if(Convert.ToHexString(SHA256.HashData(data))!=file.Sha256)throw new InvalidDataException("资料在归档后发生变化，已拒绝生成包："+file.Path);
                    using var entry=zip.CreateEntry(file.Path,CompressionLevel.Fastest).Open();entry.Write(data);
                }
                using var manifest=zip.CreateEntry("manifest.json").Open();manifest.Write(JsonSerializer.SerializeToUtf8Bytes(Manifest(),JsonOptions));
            }
            File.Move(temporary,output,true);
            File.WriteAllText(output+".sha256",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(output))));
            return output;
        }
        finally{if(File.Exists(temporary))File.Delete(temporary);}
    }
}
