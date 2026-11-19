using System.Text.Json;
using Hummingbird.Core;

namespace Hummingbird.Vision;

public sealed record RingExposureTrial(IReadOnlyList<string> Images, IReadOnlyList<double?> ExposureUs,
    bool Accepted, double? RmsMm, double? SensitivityMm, string? Rejection);

public sealed record RingExposureSelection(IReadOnlyList<RingScanFrame> OriginalFrames,
    IReadOnlyList<RingScanFrame> SelectedFrames, RingScanResult? Fit,
    IReadOnlyList<RingExposureTrial> Trials, IReadOnlyList<string> CandidateIssues, bool Changed, string Message)
{
    public string Algorithm => "FourViewGeometryExposure-v1";
    public bool PreviewOnly => true;
}

public sealed partial class RingScanSolver
{
    /// <summary>只读已存候选。优先比较同档曝光的整组几何，均失败时才搜索有限混合组合；使用原筛选限值。</summary>
    public RingExposureSelection SelectExposure(RingScanSession source, CancellationToken ct = default)
    {
        var originals=source.Frames.Where(f=>f.Role==RingFrameRole.RingArc).ToArray();
        if(originals.Length!=4)throw new InvalidDataException("曝光几何选优需要四个圆环方位。");
        var issues=new List<string>();
        var candidates=originals.Select(f=>RingExposureArchive.LoadCandidates(f,issues,ct)).ToArray();
        return SelectExposure(source,candidates,issues,ct);
    }

    public RingExposureSelection SelectExposure(RingScanSession source,
        IReadOnlyList<IReadOnlyList<RingScanFrame>> candidates, IReadOnlyList<string>? candidateIssues = null,
        CancellationToken ct = default)
    {
        var originals=source.Frames.Where(f=>f.Role==RingFrameRole.RingArc).ToArray();
        if(originals.Length!=4 || candidates.Count!=4 || originals.Select(f=>f.Name).Distinct().Count()!=4)
            throw new InvalidDataException("曝光几何选优需要四个不同的圆环方位。");
        source.Settings.Validate();ValidatePoses(originals,source.Settings);
        foreach(var original in originals)RingCaptureArchive.VerifyImage(original);
        var initialization=ResolveInitialization(originals,source.Settings);
        var settings=JsonSerializer.Deserialize<RingScanSettings>(JsonSerializer.Serialize(source.Settings))!;
        settings.HintX=initialization.HintX;settings.HintY=initialization.HintY;
        var issues=candidateIssues?.ToList()??[];
        var groups=new List<PreparedExposure[]>();
        for(int i=0;i<4;i++)
        {
            ct.ThrowIfCancellationRequested();
            if(candidates[i].Count>5)throw new InvalidDataException("每处最多支持五档曝光候选。");
            if(!candidates[i].Any(f=>f.ImagePath==originals[i].ImagePath ||
                f.Capture?.ImageSha256.Equals(originals[i].Capture?.ImageSha256,StringComparison.OrdinalIgnoreCase)==true))
                throw new InvalidDataException("曝光候选批次未包含本点原图指纹。");
            var prepared=new List<PreparedExposure>();
            var hashes=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach(var frame in new[]{originals[i]}.Concat(candidates[i]))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    RingExposureArchive.ValidateCandidate(originals[i],frame);
                    string hash=LaserSpotCalibration.Hash(frame.ImagePath);
                    if(!hashes.Add(hash))continue;
                    var image=LoadFrame(frame.ImagePath);ValidateFrame(image);
                    if(image.Width!=settings.ImageWidth || image.Height!=settings.ImageHeight)
                        throw new InvalidDataException("候选尺寸与当前标定不一致。");
                    var single=new RingScanSession {Settings=settings,Frames=[frame]};
                    var profiles=ExtractProfiles(image,frame,0,settings);
                    var edges=new[]{.30,.20,.40,.50}.Select(level=>ExtractEdges(profiles,single,level)
                        .Select(p=>p with {FrameIndex=i}).ToArray()).ToArray();
                    prepared.Add(new(frame,edges));
                }
                catch(Exception ex) when(ex is InvalidDataException or IOException)
                {issues.Add(frame.Name+" "+Path.GetFileName(frame.ImagePath)+"："+ex.Message);}
            }
            groups.Add(prepared.ToArray());
        }
        var trials=new List<RingExposureTrial>();
        RingScanResult? bestFit=null;RingScanFrame[] best=originals;
        bool recoveryPass=false;
        var tried=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Evaluate(PreparedExposure[] combination)
        {
            ct.ThrowIfCancellationRequested();
            if(!tried.Add(string.Join("|",combination.Select(c=>c.Frame.ImagePath))))return;
            var frames=combination.Select(c=>c.Frame).ToArray();
            var images=frames.Select(f=>f.ImagePath).ToArray();
            var exposures=frames.Select(f=>f.Capture?.RequestedExposureUs).ToArray();
            try
            {
                var edges=Enumerable.Range(0,4).Select(k=>(IReadOnlyList<RingEdgePoint>)combination.SelectMany(c=>c.Edges[k]).ToArray()).ToArray();
                var result=FitPrepared(new RingScanSession {Settings=settings,Frames=frames.ToList()},initialization,edges,allowRecovery:recoveryPass);
                trials.Add(new(images,exposures,true,result.RmsMm,result.SensitivityMm,null));
                if(bestFit is null || bestFit.CompatibilityRecovery is not null && result.CompatibilityRecovery is null ||
                    (bestFit.CompatibilityRecovery is null)==(result.CompatibilityRecovery is null) &&
                    (result.RmsMm<bestFit.RmsMm || result.RmsMm==bestFit.RmsMm && result.SensitivityMm<bestFit.SensitivityMm))
                {bestFit=result;best=frames;}
            }
            catch(InvalidDataException ex){trials.Add(new(images,exposures,false,null,null,ex.Message));}
        }
        for(int pass=0;pass<2 && bestFit is null;pass++)
        {
        recoveryPass=pass==1;tried.Clear();
        if(groups.All(g=>g.Length>0))
        {
            // Retain the original selection as a candidate when it has usable edges.
            var originalGroup=groups.Select((g,i)=>g.FirstOrDefault(p=>p.Frame.ImagePath==originals[i].ImagePath)).ToArray();
            if(originalGroup.All(p=>p is not null))Evaluate(originalGroup.Select(p=>p!).ToArray());
            var common=groups.Select(g=>g.Select(p=>p.Frame.Capture?.RequestedExposureUs).Where(e=>e is >0).Distinct())
                .Aggregate((a,b)=>a.Intersect(b)).Order().ToArray();
            foreach(var exposure in common)
                Evaluate(groups.Select(g=>g.First(p=>p.Frame.Capture?.RequestedExposureUs==exposure)).ToArray());
            if(bestFit is null)
            {
                var combination=new PreparedExposure[4];
                void Search(int depth)
                {
                    ct.ThrowIfCancellationRequested();
                    if(depth==4){Evaluate(combination);return;}
                    foreach(var option in groups[depth]){combination[depth]=option;Search(depth+1);}
                }
                Search(0);
            }
        }
        }
        bool changed=bestFit is not null && best.Where((f,i)=>f.ImagePath!=originals[i].ImagePath).Any();
        string message=bestFit is null ? "已存曝光候选均未通过原有圆环标准；保留原图，不生成孔位。"
            : $"圆环曝光已按四方位几何选优，RMS={bestFit.RmsMm*1000:F1}µm；"+
                string.Join("、",best.Select(f=>$"{f.Name} {f.Capture?.RequestedExposureUs:F0}µs"));
        return new(originals,best,bestFit,trials,issues,changed,message);
    }

    private sealed record PreparedExposure(RingScanFrame Frame,RingEdgePoint[][] Edges);
}

/// <summary>候选必须与正式原图指纹绑定，并核对实际坐标、光学快照和采集时间；不混入历史同名图。</summary>
public static class RingExposureArchive
{
    public static IReadOnlyList<RingScanFrame> LoadCandidates(RingScanFrame original,List<string> issues,CancellationToken ct=default)
    {
        RingCaptureArchive.VerifyImage(original);
        if(original.Capture is null)return [original];
        string imageFolder=Path.GetDirectoryName(Path.GetFullPath(original.ImagePath))!;
        var folders=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(Path.GetFileName(imageFolder).StartsWith("曝光选优_",StringComparison.Ordinal))folders.Add(imageFolder);
        var roots=new[]{imageFolder,Path.Combine(Path.GetDirectoryName(imageFolder)!,"过程")};
        foreach(string root in roots.Where(Directory.Exists))
            foreach(string folder in Directory.EnumerateDirectories(root))
                if(Path.GetFileName(folder).StartsWith("曝光选优_"+original.Name+"_",StringComparison.Ordinal))folders.Add(folder);
        var matches=new List<IReadOnlyList<RingScanFrame>>();
        foreach(string folder in folders.Order(StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            var loaded=new List<RingScanFrame>();
            foreach(string sidecar in Directory.EnumerateFiles(folder,"*us.capture.json").Order(StringComparer.Ordinal))
            {
                try
                {
                    var data=RingScanSession.Load(sidecar);
                    if(data.Frames.Count!=1)continue;
                    var candidate=data.Frames[0];
                    ValidateCandidate(original,candidate);loaded.Add(candidate);
                }
                catch(Exception ex) when(ex is IOException or InvalidDataException or JsonException)
                {issues.Add(Path.GetFileName(folder)+"/"+Path.GetFileName(sidecar)+"："+ex.Message);}
            }
            // The originally selected bitmap must be present in this exact search batch.
            if(loaded.Any(f=>f.Capture?.ImageSha256.Equals(original.Capture.ImageSha256,StringComparison.OrdinalIgnoreCase)==true))
                matches.Add(loaded);
        }
        if(matches.Count!=1)
        {
            if(matches.Count>1)issues.Add(original.Name+"：多个曝光批次与原图相同，未自动混用。");
            return [original];
        }
        if(matches[0].Count>5){issues.Add(original.Name+"：曝光批次超过五档，保留原图。");return [original];}
        return matches[0];
    }

    public static void ValidateCandidate(RingScanFrame original,RingScanFrame candidate)
    {
        RingCaptureArchive.VerifyImage(candidate);
        if(original.ImagePath==candidate.ImagePath)return;
        if(original.Capture is not { } origin || candidate.Capture is not { } capture ||
            capture.DeviceMode!=origin.DeviceMode || capture.XyzSource!=origin.XyzSource || capture.CSource!=origin.CSource ||
            candidate.Role!=RingFrameRole.RingArc || candidate.Name!=original.Name ||
            new[]{candidate.X-original.X,candidate.Y-original.Y,candidate.Z-original.Z}.Any(d=>!double.IsFinite(d)||Math.Abs(d)>.002) ||
            !double.IsFinite(candidate.C) || Math.Abs(Math.IEEERemainder(candidate.C-original.C,360))>.001 ||
            origin.HostCaptureTime is not { } originTime || capture.HostCaptureTime is not { } time || Math.Abs((time-originTime).TotalSeconds)>10 ||
            origin.SettingsAtRegistration is null || capture.SettingsAtRegistration is null ||
            !SameOptics(origin.SettingsAtRegistration,capture.SettingsAtRegistration) ||
            capture.RequestedGain!=origin.RequestedGain || capture.RequestedExposureUs is not >0)
            throw new InvalidDataException("曝光候选的坐标、来源、光学快照或采集时间与本点不匹配。");
    }

    private static bool SameOptics(RingScanSettings a,RingScanSettings b)=>a.ImageWidth==b.ImageWidth && a.ImageHeight==b.ImageHeight &&
        new[]{a.Jxx-b.Jxx,a.Jxy-b.Jxy,a.Jyx-b.Jyx,a.Jyy-b.Jyy}.All(d=>double.IsFinite(d)&&Math.Abs(d)<=1e-6);
}
