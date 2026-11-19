using System.Diagnostics;
using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

internal static class RingExposureChecks
{
    public static int Run(string sessionPath,string outputFolder)
    {
        string output=Path.GetFullPath(outputFolder);Directory.CreateDirectory(output);
        var checks=new List<object>();
        void Check(bool pass,string name)
        {
            checks.Add(new {Passed=pass,Name=name});
            if(!pass)throw new InvalidOperationException(name);
            Console.WriteLine("[PASS] "+name);
        }
        bool Fails(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
        var source=RingScanSession.Load(Path.GetFullPath(sessionPath));
        var arcs=source.Frames.Where(f=>f.Role==RingFrameRole.RingArc).ToList();
        var solver=new RingScanSolver();
        string originalSettings=JsonSerializer.Serialize(source.Settings);
        string sourceHash=LaserSpotCalibration.Hash(sessionPath);
        var hashes=source.Frames.Select(f=>LaserSpotCalibration.Hash(f.ImagePath)).ToArray();
        Check(Fails(()=>solver.Analyze(new RingScanSession {Settings=source.Settings,Frames=arcs})),"现场旧选优组合按原标准拒绝");
        var timer=Stopwatch.StartNew();
        var selected=solver.SelectExposure(source);timer.Stop();
        Check(selected.Fit is not null && selected.Changed,"现场原候选自动恢复有效圆环");
        Check(selected.SelectedFrames.All(f=>f.Capture?.RequestedExposureUs==500),"回放真实资料自动选择四处500us，算法未固定曝光值");
        Check(Math.Abs(selected.Fit!.RmsMm-.02315291515737353)<1e-9,"真实资料RMS恢复到23.15um");
        var independent=solver.Analyze(new RingScanSession {Settings=source.Settings,Frames=selected.SelectedFrames.ToList()});
        Check(Math.Abs(independent.RmsMm-selected.Fit.RmsMm)<1e-12 && independent.SensitivityMm<=source.Settings.MaxSensitivity,
            "获选图独立重跑原求解器，原阈值通过");
        Check(JsonSerializer.Serialize(source.Settings)==originalSettings && LaserSpotCalibration.Hash(sessionPath)==sourceHash &&
            hashes.SequenceEqual(source.Frames.Select(f=>LaserSpotCalibration.Hash(f.ImagePath))),"不修改原会话、原图或阈值");
        Check(source.Frames.Where(f=>f.Role!=RingFrameRole.RingArc).All(f=>f.FaceSeed is null),"曝光恢复不伪造参考实体边界确认");
        var derived=new RingScanSession {Settings=source.Settings,DirectionOptions=source.DirectionOptions,
            Frames=source.Frames.Where(f=>f.Role!=RingFrameRole.RingArc).Concat(selected.SelectedFrames).ToList()};
        derived.Save(Path.Combine(output,"自动曝光恢复_待确认参考圆面.json"));
        var repeated=solver.SelectExposure(derived);
        Check(repeated.Fit is not null && !repeated.Changed,"恢复后重复检查保持同一组原图");
        IReadOnlyList<IReadOnlyList<RingScanFrame>> onlyOriginal=arcs.Select(f=>(IReadOnlyList<RingScanFrame>)new[]{f}).ToArray();
        var failed=solver.SelectExposure(source,onlyOriginal);
        Check(failed.Fit is null && !failed.Changed && failed.SelectedFrames.SequenceEqual(arcs),"候选全失败时不生成圆心或替换原图");
        var issues=new List<string>();var candidates=arcs.Select(f=>RingExposureArchive.LoadCandidates(f,issues)).ToArray();
        var good=candidates[0].Single(f=>f.Capture?.RequestedExposureUs==500);
        Check(Fails(()=>RingExposureArchive.ValidateCandidate(arcs[0],good with {X=good.X+.01})),"拒绝混入不同机床位置的候选");
        Check(Fails(()=>RingExposureArchive.ValidateCandidate(arcs[0],good with {Name="历史同名点"})),"拒绝混入不同步骤的候选");
        Check(Fails(()=>RingExposureArchive.ValidateCandidate(arcs[0],good with {Capture=good.Capture! with {
            HostCaptureTime=good.Capture.HostCaptureTime!.Value.AddMinutes(1)}})),"拒绝混入不同采集时段的候选");
        var wrongOptics=JsonSerializer.Deserialize<RingScanSettings>(JsonSerializer.Serialize(good.Capture!.SettingsAtRegistration))!;
        wrongOptics.Jxx+=1;
        Check(Fails(()=>RingExposureArchive.ValidateCandidate(arcs[0],good with {Capture=good.Capture with {SettingsAtRegistration=wrongOptics}})),
            "拒绝光学快照不匹配的候选");
        Check(Fails(()=>RingExposureArchive.ValidateCandidate(arcs[0],good with {Capture=good.Capture with {DeviceMode="Sim"}})),"拒绝混入模拟照片来源");
        Check(Fails(()=>RingExposureArchive.ValidateCandidate(arcs[0],good with {Capture=good.Capture with {ImageSha256=new string('0',64)}})),
            "拒绝原图指纹不匹配的候选");
        Check(Fails(()=>solver.SelectExposure(source,candidates.Select(g=>(IReadOnlyList<RingScanFrame>)g.Where(f=>
            !f.Capture!.ImageSha256.Equals(arcs[Array.IndexOf(candidates,g)].Capture!.ImageSha256,StringComparison.OrdinalIgnoreCase)).ToArray()).ToArray())),
            "候选批次缺原图指纹时拒绝自动拼组");
        // Simulated exposure labels force the mixed-combination branch; never exported as field measurements.
        var syntheticSource=new RingScanSession {Settings=source.Settings,Frames=arcs.Select((f,i)=>f with {Capture=f.Capture! with {
            DeviceMode="Sim",RequestedExposureUs=10000+i}}).ToList()};
        var syntheticCandidates=syntheticSource.Frames.Select((f,i)=>(IReadOnlyList<RingScanFrame>)new[]{f,
            selected.SelectedFrames[i] with {Capture=selected.SelectedFrames[i].Capture! with {DeviceMode="Sim",RequestedExposureUs=20000+i}}}).ToArray();
        var mixed=solver.SelectExposure(syntheticSource,syntheticCandidates);
        Check(mixed.Fit is not null && mixed.Trials.Count>1 && mixed.SelectedFrames.All(f=>f.Capture!.RequestedExposureUs>=20000),
            "无共同曝光档时有限混合搜索仍按原标准选出有效组合");
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();bool cancelled=false;
        try{solver.SelectExposure(source,cancellation.Token);}catch(OperationCanceledException){cancelled=true;}
        Check(cancelled,"曝光几何搜索响应取消");
        File.WriteAllText(Path.Combine(output,"result.json"),JsonSerializer.Serialize(new {Kind="RingExposureRegression",HardwareAccess=false,
            Checks=checks,ElapsedMs=timer.ElapsedMilliseconds,Selection=selected},new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"Ring exposure checks: {checks.Count} passed; real replay {timer.ElapsedMilliseconds}ms.");return 0;
    }
}
