using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

internal static class ReusableBaselineChecks
{
    public static int Run(string folder)
    {
        folder=Path.GetFullPath(folder);Directory.CreateDirectory(folder);int passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
        string source="资料收集/工件_20261117_112539654/整件_3acc0f792fde4b639a14921455ef5de0/本件资料/定位/整组记录.json";
        var template=HolePatternTemplate.LoadJson("templates/dial_18holes.json");
        var raw=RingScanSession.Load(Path.GetFullPath(source));string before=JsonSerializer.Serialize(raw);
        var eleven=raw.Frames.Single(f=>f.Role==RingFrameRole.Reference11);
        Check(new DatumDirectionSolver().Inspect(eleven,raw.Settings,raw.DirectionOptions).Status==DatumHoleInspectionStatus.Rejected,
            "缓存上次失败六图时，复现旧加工前检查拒绝11点基准");
        var prepared=ReusableCaptureBaseline.Prepare(raw,template);
        Check(prepared.RebuiltRegions && prepared.Prediction is not null && prepared.Alignment.Targets.Count==18,
            "启动与加工前共用完整六图复核，从已有照片恢复11点并验证18孔");
        Check(new[]{RingFrameRole.Reference1,RingFrameRole.Reference11}.All(role=>
            new DatumDirectionSolver().Inspect(prepared.Session.Frames.Single(f=>f.Role==role),prepared.Session.Settings,prepared.Session.DirectionOptions).Status==DatumHoleInspectionStatus.Detected),
            "复核后的基准通过原严格单参考检查，未删除或跳过基准保护");
        Check(GuidedCapture.Route(raw).SequenceEqual(GuidedCapture.Route(prepared.Session)),"恢复参考框不修改六个机床采图坐标");
        Check(JsonSerializer.Serialize(raw)==before && !prepared.CurrentWorkpieceCoordinates,"原缓存记录未改写，基准18孔不当作当前工件坐标");
        var reused=ReusableCaptureBaseline.Prepare(prepared.Session,template);
        Check(!reused.RebuiltRegions && reused.Reference11.InitialAttempt is null,"已复核基准再次载入保持已有合格结果");
        var invalid=Clone(raw);invalid.DirectionOptions=invalid.DirectionOptions with {ExpectedGapDeg=120};
        Check(Reject(()=>ReusableCaptureBaseline.Prepare(invalid,template)),"方向未通过的六图不能成为可复用基准");
        var missing=Clone(raw);missing.Frames.RemoveAll(f=>f.Role==RingFrameRole.Reference11);
        Check(Reject(()=>ReusableCaptureBaseline.Prepare(missing,template)),"六图不全不能作为基准");
        var noSeed=Clone(raw);int index=noSeed.Frames.FindIndex(f=>f.Role==RingFrameRole.Reference11);noSeed.Frames[index]=noSeed.Frames[index] with {FaceSeed=null};
        Check(Reject(()=>ReusableCaptureBaseline.Prepare(noSeed,template)),"原基准无11点框初值时不能无约束寻圆");
        var wrongHash=Clone(raw);int first=wrongHash.Frames.FindIndex(f=>f.Role==RingFrameRole.Reference1);
        wrongHash.Frames[first]=wrongHash.Frames[first] with {FaceSeed=wrongHash.Frames[first].FaceSeed! with {ImageSha256=new string('0',64)}};
        Check(Reject(()=>ReusableCaptureBaseline.Prepare(wrongHash,template)),"照片绑定错误时基准复核仍拒绝");
        string blank=Path.Combine(folder,"no-reference11.bmp");RingScanSolver.SaveFrame(blank,new(new byte[1920*1200],1920,1200,DateTime.Now));
        var unreadable=Clone(raw);var f=unreadable.Frames[index];string hash=LaserSpotCalibration.Hash(blank);
        unreadable.Frames[index]=f with {ImagePath=blank,FaceSeed=f.FaceSeed! with {ImageSha256=hash},
            Capture=f.Capture! with {ImageSha256=hash,DeviceMode="Sim",Purpose="SyntheticNoReference11"}};
        Check(Reject(()=>ReusableCaptureBaseline.Prepare(unreadable,template)),"实际11点无圆边时，不能用1点加圆环预测冒充合格基准");
        var old=RingScanSession.Load(Path.GetFullPath("资料收集/整件试走复核_20261116_1541/ZIP原样/定位/整组记录.json"));
        var good=ReusableCaptureBaseline.Prepare(old,template);
        Check(!good.RebuiltRegions && good.Alignment.Targets.Count==18 && Math.Abs(good.Alignment.Direction.GapDeg-60.428698)<.0001,
            "昨天已通过的六图保留原结果与实测方向");
        double initialJ=raw.Settings.Jxx;prepared.Session.Settings.Jxx+=1;
        Check(raw.Settings.Jxx==initialJ,"复核副本参数与原记录隔离");
        using(var stop=new CancellationTokenSource())
        {
            stop.Cancel();bool canceled=false;
            try{ReusableCaptureBaseline.Prepare(raw,template,stop.Token);}catch(OperationCanceledException){canceled=true;}
            Check(canceled,"取消后不继续基准复核");
        }
        var final=ReusableCaptureBaseline.Prepare(raw,template);
        File.WriteAllText(Path.Combine(folder,"baseline-review.json"),JsonSerializer.Serialize(final,new JsonSerializerOptions {WriteIndented=true}));
        final.Session.Save(Path.Combine(folder,"checked-baseline.json"));
        Console.WriteLine($"Reusable baseline: {passed} passed; read-only source images, no device connection.");return 0;

        static RingScanSession Clone(RingScanSession value)=>JsonSerializer.Deserialize<RingScanSession>(JsonSerializer.Serialize(value))!;
    }
}
