using Hummingbird.Core;
using System.IO.Compression;
using System.Text.Json;
using System.Security.Cryptography;

internal static class FieldEvidenceChecks
{
    public static int Run(string? output)
    {
        int passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("[PASS] "+name);}
        bool Reject(Action action){try{action();return false;}catch{return true;}}
        string root=Path.GetFullPath(output??Path.Combine("obj","field-evidence-checks"));Directory.CreateDirectory(root);
        string source=Path.Combine(root,"source.bmp");File.WriteAllBytes(source,[1,2,3,4]);
        var archive=new FieldEvidenceArchive(Path.Combine(root,"bundle"),"Sim",["test-private-password"]);
        archive.AddFile(source,"images/source.bmp","原图");archive.AddJson("position.json",new {X=1,Y=2,CSource="ManualPanelEntry"},"位置");
        Check(Reject(()=>archive.AddJson("config.json",new {Password="x"},"配置")),"拒绝收集设备配置");
        Check(Reject(()=>archive.AddJson("operator-state.json",new {},"偏好")),"拒绝收集操作偏好");
        Check(Reject(()=>archive.AddJson("../outside.json",new {},"越界")),"拒绝包内路径越界");
        Check(Reject(()=>archive.AddJson("sensitive.json",new {Value="test-private-password"},"敏感值")),"拒绝包含实际连接密码的资料");
        Check(Reject(()=>archive.AddJson("nested.json",new {Nested=new {CncPassword="x"}},"嵌套配置")),"拒绝嵌套凭据字段，短密码也不导出");
        Check(Reject(()=>archive.AddJson("manifest.json",new {},"覆盖清单")),"清单名称保留，禁止夹带重复ZIP清单");
        string rawLog=Path.Combine(root,"raw.log");File.WriteAllText(rawLog,"unselected");
        Check(Reject(()=>archive.AddFile(rawLog,"raw.log","日志")),"拒绝原始日志扩散");
        File.WriteAllText(Path.Combine(archive.Folder,"not-selected.json"),"{}");
        string zipPath=archive.Export("# 取证\n测试汇总");
        using(var zip=ZipFile.OpenRead(zipPath))
        {
            Check(zip.GetEntry("manifest.json") is not null && zip.GetEntry("汇总.md") is not null && zip.GetEntry("images/source.bmp") is not null,"资料包包含清单、汇总和原图");
            Check(zip.GetEntry("not-selected.json") is null && zip.GetEntry("raw.log") is null,"仅导出登记文件，不扫描夹带其他文件");
            using var reader=new StreamReader(zip.GetEntry("manifest.json")!.Open());using var manifest=JsonDocument.Parse(reader.ReadToEnd());
            Check(manifest.RootElement.GetProperty("Synthetic").GetBoolean(),"模拟来源明确标记");
            foreach(var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
            {
                using var data=zip.GetEntry(file.GetProperty("Path").GetString()!)!.Open();
                Check(Convert.ToHexString(SHA256.HashData(data))==file.GetProperty("Sha256").GetString(),"清单指纹匹配实际ZIP内容");
            }
        }
        Check(File.ReadAllText(zipPath+".sha256")==Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zipPath))),"整个ZIP有传输校验值");
        File.WriteAllBytes(Path.Combine(archive.Folder,"images","source.bmp"),[9]);
        Check(Reject(()=>archive.Export("changed")),"已登记资料被修改时拒绝打包");
        Check(!File.Exists(zipPath+".partial") && File.Exists(zipPath),"失败后保留前次完整包并清理半成品");
        var settings=new RingScanSettings();
        var frame=RingCaptureArchive.BindImage(new("参考1点",source,1,2,-1,0){Role=RingFrameRole.Reference1},settings,DateTime.Now,1920,1200,
            AxisCoordinateSource.OpcUaReadback,AxisCoordinateSource.ManualPanelEntry,null,"FieldCheck");
        var portable=new FieldEvidenceArchive(Path.Combine(root,"portable"),"Real");
        portable.CaptureSession(new(){Settings=settings,Frames=[frame]});
        string portableZip=portable.Export("portable");string extracted=Path.Combine(root,"extracted_"+Guid.NewGuid().ToString("N"));ZipFile.ExtractToDirectory(portableZip,extracted);
        var restored=RingScanSession.Load(Path.Combine(extracted,"定位","整组记录.json"));RingCaptureArchive.VerifyImage(restored.Frames.Single());
        Check(restored.Frames[0].ImagePath.StartsWith(extracted) && restored.Frames[0].Capture!.CSource==AxisCoordinateSource.ManualPanelEntry,"换目录解压后定位照片引用和C来源仍有效");
        var side=Directory.GetFiles(Path.Combine(extracted,"定位","配套"),"*.json").Single();
        RingCaptureArchive.VerifyImage(RingScanSession.Load(side).Frames.Single());Check(true,"单图配套记录可独立重载");
        Console.WriteLine($"Field evidence: {passed} passed; no hardware accessed.");return 0;
    }
}
