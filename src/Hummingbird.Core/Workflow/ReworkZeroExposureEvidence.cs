using System.Text.Json;

namespace Hummingbird.Core;

/// <summary>仅允许采图/定位阶段已经正常清理、没有任何孔意图的失败补加工重新准备。不给不确定出光开自动重打通道。</summary>
public sealed record ReworkZeroExposureEvidence(Guid FailedRunId,string RunPath,string RunSha256,
    string GuardPath,string GuardSha256,string PlanPath,string PlanSha256)
{
    public static ReworkZeroExposureEvidence Load(string selected)
    {
        var record=Read(selected);
        string directory=Path.GetDirectoryName(Path.GetFullPath(selected))!;
        string job=Path.GetFileName(selected)==ProductionJobArchive.MachiningRecordName?directory:Directory.GetParent(directory)!.FullName;
        string run=Path.Combine(job,"整件_"+record.RunId.ToString("N"),"run.json");
        string guard=Path.Combine(job,ProductionJobArchive.MachiningRecordName),plan=Path.Combine(job,"补加工计划.json");
        var evidence=new ReworkZeroExposureEvidence(record.RunId,run,ProductionReworkSource.Hash(run),guard,
            ProductionReworkSource.Hash(guard),plan,ProductionReworkSource.Hash(plan));
        _=evidence.Verify();return evidence;
    }
    public ProductionRunRecord Verify()
    {
        if(ProductionReworkSource.Hash(RunPath)!=RunSha256 || ProductionReworkSource.Hash(GuardPath)!=GuardSha256 ||
            ProductionReworkSource.Hash(PlanPath)!=PlanSha256)throw new InvalidDataException("未出光重试依据已改变，未允许重试。");
        var run=Read(RunPath);var guard=Read(GuardPath);Validate(run);Validate(guard);
        var plan=JsonSerializer.Deserialize<ProductionReworkPlan>(File.ReadAllText(PlanPath))??throw new InvalidDataException("失败轮次计划为空。");
        if(run.RunId!=FailedRunId || guard.RunId!=FailedRunId || run.Rework!=guard.Rework || run.Rework!=plan.Info ||
            run.Phase!=guard.Phase || run.CapturedSteps!=guard.CapturedSteps || !run.Events.SequenceEqual(guard.Events))
            throw new InvalidDataException("失败run、加工占用和计划内容不一致，不能认定未出光。");
        if(Directory.GetDirectories(Path.GetDirectoryName(RunPath)!,"hole_*").Length!=0)
            throw new InvalidDataException("失败轮次已存在孔位工序目录，不能自动按零出光重试。");
        return run;
    }
    private static ProductionRunRecord Read(string path)=>JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(path))??throw new InvalidDataException("失败轮次记录为空。");
    private static void Validate(ProductionRunRecord record)
    {
        if(record.DryRun || record.Synthetic || record.Rework is null || record.Completed || record.Phase is not ("Failed" or "Canceled") ||
            record.Holes.Count!=0 || record.HoleTimings.Count!=0 || record.CurrentHoleId is not null ||
            record.LaserOffConfirmed!=true || !record.NcExitConfirmed || record.CapturedSteps is <0 or >6 ||
            record.StoppedDuring is not { } stage || !(stage=="Starting" || stage=="Locating" || stage.StartsWith("Capture",StringComparison.Ordinal)) ||
            record.Events.Count==0 || record.Events[0]!="Starting" || record.Events.Any(e=>!IsCaptureStage(e)) ||
            record.RequestedExposureMs!=record.Rework.AdditionalExposureMs)
            throw new InvalidDataException("只有采图/定位阶段已停止、0个孔意图且确认关光和NC退出的失败补加工才能重新准备；不确定孔不能自动重打。");
    }
    private static bool IsCaptureStage(string stage)=>stage is "Starting" or "Locating" ||
        stage.StartsWith("CaptureMove:",StringComparison.Ordinal) || stage.StartsWith("CaptureCompleted:",StringComparison.Ordinal) ||
        stage.StartsWith("CaptureEvidence:",StringComparison.Ordinal);
}
