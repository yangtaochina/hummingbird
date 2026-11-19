using System.Text.Json;
using Hummingbird.Core;

internal static class ReworkRetryChecks
{
    public const string FailedJob="资料收集/补加工_6c6b896a_20261117_145158524";
    public static int Run(string output)
    {
        output=Path.GetFullPath(output);Directory.CreateDirectory(output);int passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        bool Fails(Action action){try{action();return false;}catch{return true;}}
        var original=ProductionReworkSource.Load(ProductionReworkChecks.SiteRun);
        string parentHash=ProductionReworkSource.Hash(original.RecordPath),failedHash=ProductionReworkSource.Hash(Path.Combine(FailedJob,ProductionJobArchive.MachiningRecordName));
        string bundle=Path.Combine(output,"source-bundle");Directory.CreateDirectory(bundle);
        using(var manifest=JsonDocument.Parse(File.ReadAllText(original.ManifestPath)))
            foreach(var entry in manifest.RootElement.GetProperty("Files").EnumerateArray())
            {
                string relative=entry.GetProperty("Path").GetString()!,target=Path.Combine(bundle,relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(Path.Combine(Path.GetDirectoryName(original.ManifestPath)!,relative),target);
            }
        File.Copy(original.ManifestPath,Path.Combine(bundle,"manifest.json"));
        var source=ProductionReworkSource.Load(Path.Combine(bundle,"整件","run.json"));
        var failed=JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(Path.Combine(FailedJob,ProductionJobArchive.MachiningRecordName)))!;
        var info=failed.Rework! with {ParentRecordPath=source.RecordPath,ParentManifestPath=source.ManifestPath};
        // 只在隔离副本中换成本机测试路径；真实现场记录、登记和原图一律只读。
        string failedJob=Path.Combine(output,"failed-job"),failedRun=Path.Combine(failedJob,"整件_"+failed.RunId.ToString("N"));Directory.CreateDirectory(failedRun);
        string failedRunPath=Path.Combine(failedRun,"run.json"),guard=Path.Combine(failedJob,ProductionJobArchive.MachiningRecordName),planPath=Path.Combine(failedJob,"补加工计划.json");
        var node=System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(failed))!;
        node["Rework"]=JsonSerializer.SerializeToNode(info);string failedJson=node.ToJsonString();
        File.WriteAllText(failedRunPath,failedJson);File.WriteAllText(guard,failedJson);
        File.WriteAllText(planPath,JsonSerializer.Serialize(new ProductionReworkPlan {Info=info}));
        File.WriteAllText(source.ReservationPath,JsonSerializer.Serialize(new {info.ParentRunId,info.PlanId,ChildRunId=failed.RunId,ChildJobFolder=failedJob}));
        byte[] reservation=File.ReadAllBytes(source.ReservationPath);
        var evidence=ReworkZeroExposureEvidence.Load(guard);
        Check(evidence.Verify().RunId==failed.RunId,"本次真实首图失败记录：独立run、占用和计划一致，0孔且关光/NC退出完成");
        var retry=ProductionReworkPlan.CreateRetry(source,evidence,60000,4420);
        Check(retry.Info!.RetryOfRunId==failed.RunId && retry.Info.ParentRunId==source.Record.RunId && retry.Info.Round==1 && retry.Info.CumulativeRequestedMs==120000,
            "未出光失败不算已追加一轮，重试仍首轮补加工60秒/4420，累计120秒");
        Check(retry.Verify(60000,4420).Record.RunId==source.Record.RunId && reservation.SequenceEqual(File.ReadAllBytes(source.ReservationPath)),
            "准备和预检不更改登记，参数及原来源再次核对");
        Check(Fails(()=>retry.Verify(60000,3950)),"改变已准备功率不能直接执行旧计划");
        Check(Fails(()=>ProductionReworkSource.Load(failedRunPath)),"失败补加工不能冒充已完成来源，必须走独立未出光重试");
        foreach(var (name,mutate) in new (string,Action<ProductionRunRecord>)[] {
            ("已有完成孔",r=>r.Holes[1]=ProductionHolePhase.Completed),
            ("不确定孔",r=>r.Holes[1]=ProductionHolePhase.Uncertain),
            ("已开始孔的意图",r=>r.Events.Add("HoleStarted:1")),
            ("正在孔位移动",r=>r.StoppedDuring="HoleMove:1"),
            ("有关光结果缺失",r=>r.LaserOffConfirmed=null),
            ("NC未退出",r=>r.NcExitConfirmed=false),
            ("崩溃未清理",r=>r.Phase="Interrupted"),
            ("存在孔计时",r=>r.HoleTimings[1]=new()),
            ("模拟记录",r=>r.Synthetic=true) })
        {
            var altered=JsonSerializer.Deserialize<ProductionRunRecord>(failedJson)!;mutate(altered);
            File.WriteAllText(failedRunPath,JsonSerializer.Serialize(altered));File.WriteAllText(guard,JsonSerializer.Serialize(altered));
            Check(Fails(()=>ReworkZeroExposureEvidence.Load(guard)),name+"时拒绝自动按零出光重试");
        }
        File.WriteAllText(failedRunPath,failedJson);File.WriteAllText(guard,failedJson);
        Directory.CreateDirectory(Path.Combine(failedRun,"hole_1"));
        Check(Fails(()=>ReworkZeroExposureEvidence.Load(guard)),"即使显示0/18，存在孔工序目录也拒绝");
        Directory.Move(Path.Combine(failedRun,"hole_1"),Path.Combine(output,"withheld-hole-1"));
        File.WriteAllText(guard,"{}");Check(Fails(()=>retry.Verify(60000,4420)),"准备后独立占用改变时拒绝执行");File.WriteAllText(guard,failedJson);
        File.WriteAllText(source.ReservationPath,JsonSerializer.Serialize(new{info.ParentRunId,PlanId=Guid.NewGuid(),ChildRunId=failed.RunId,ChildJobFolder=failedJob}));
        Check(Fails(()=>retry.Verify(60000,4420)),"登记计划与失败记录不一致时拒绝");File.WriteAllBytes(source.ReservationPath,reservation);
        File.WriteAllText(source.ReservationPath,JsonSerializer.Serialize(new{info.ParentRunId,info.PlanId,ChildRunId=failed.RunId,ChildJobFolder=Path.Combine(output,"another-job")}));
        Check(Fails(()=>retry.Verify(60000,4420)),"登记目录与失败记录不一致时拒绝");File.WriteAllBytes(source.ReservationPath,reservation);
        string job=Path.Combine(output,"retry-job");Directory.CreateDirectory(job);File.WriteAllText(Path.Combine(job,"补加工计划.json"),JsonSerializer.Serialize(retry));
        var record=new ProductionRunRecord {DryRun=false,RequestedExposureMs=60000,Rework=retry.Info};
        ProductionJobArchive.Begin(job,record);
        string history=Path.Combine(bundle,"补加工登记历史_"+failed.RunId.ToString("N")+".json");
        using(var registered=JsonDocument.Parse(File.ReadAllText(source.ReservationPath)))
            Check(registered.RootElement.GetProperty("ChildRunId").GetGuid()==record.RunId && registered.RootElement.GetProperty("PreviousChildRunId").GetGuid()==failed.RunId &&
                File.ReadAllBytes(history).SequenceEqual(reservation),"开始前原子替代登记、原始登记逐字保存到历史，形成重试关联");
        Check(Fails(()=>ProductionReworkPlan.CreateRetry(source,evidence,60000,4420)),"同一失败轮已被替代后不能再准备第二轮并行重试");
        Check(Fails(()=>ProductionJobArchive.Begin(job,record)) && Fails(()=>ProductionJobArchive.EnsureUnused(failedJob)),"新旧目录都保留独立防重跑记录，不删除保护文件");
        Check(ProductionJobArchive.Summary(record).Contains(failed.RunId.ToString()) && ProductionJobArchive.Summary(record).Contains("累计每孔请求120秒"),"重试结果同时标明失败来源和正确累计请求");
        Check(ProductionReworkSource.Hash(original.RecordPath)==parentHash && ProductionReworkSource.Hash(Path.Combine(FailedJob,ProductionJobArchive.MachiningRecordName))==failedHash && !File.Exists(original.ReservationPath),
            "所有写入限制于隔离测试目录，原资料未改变且不操作设备");
        // 提供GUI检查用、尚未被替代的独立登记副本，不让GUI触碰现场来源。
        File.WriteAllBytes(source.ReservationPath,reservation);
        File.WriteAllText(Path.Combine(output,"gui-failed-record.txt"),guard);
        Console.WriteLine($"Rework zero-exposure retry: {passed} passed; isolated records only.");return 0;
    }
}
