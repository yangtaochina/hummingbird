using System.Text.Json;
using System.Text.Json.Nodes;
using Hummingbird.Core;

internal static class ProductionReworkChecks
{
    public const string SiteRun="资料收集/工件_20261117_123839174/整件_6c6b896ad7b745a1a068317702e4ff72/run.json";
    public static async Task<int> Run(string folder)
    {
        folder=Path.GetFullPath(folder);Directory.CreateDirectory(folder);int passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        bool Fails(Action action){try{action();return false;}catch{return true;}}
        var original=ProductionReworkSource.Load(SiteRun);
        Check(original.ValidatedFiles==116 && original.Record.Holes.Count==18 && original.Record.RequestedExposureMs==60000 && original.PowerRaw==4420,
            "本次现场116项归档完整，18孔完成/关光及60秒4420一致，缺少顶层重复文件不影响来源核对");
        string sourceHash=ProductionReworkSource.Hash(original.RecordPath);
        string fixture=Path.Combine(folder,"source-bundle");Directory.CreateDirectory(fixture);
        using(var manifest=JsonDocument.Parse(File.ReadAllText(original.ManifestPath)))
            foreach(var entry in manifest.RootElement.GetProperty("Files").EnumerateArray())
            {
                string name=entry.GetProperty("Path").GetString()!;string target=Path.Combine(fixture,name);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);File.Copy(Path.Combine(Path.GetDirectoryName(original.ManifestPath)!,name),target);
            }
        File.Copy(original.ManifestPath,Path.Combine(fixture,"manifest.json"));
        string recordPath=Path.Combine(fixture,"整件","run.json");var source=ProductionReworkSource.Load(recordPath);
        var plan=ProductionReworkPlan.Create(source,60000,4420);
        Check(plan.Info!.ParentRunId==source.Record.RunId && plan.Info.AdditionalExposureMs==60000 && plan.Info.CumulativeRequestedMs==120000 && plan.Info.Round==1,
            "同件追加60秒单独记录，首轮加本轮累计请求120秒，关联原轮次");
        Check(plan.Verify(60000,4420).Record.RunId==source.Record.RunId,"补加工参数和来源指纹复核通过");
        Check(Fails(()=>plan.Verify(30000,4420)) && Fails(()=>plan.Verify(60000,3950)),"准备后修改时长或功率不能沿用旧计划");
        var badPlan=new ProductionReworkPlan {Info=plan.Info with {ParentRecordSha256=new string('0',64)}};
        Check(Fails(()=>badPlan.Verify(60000,4420)),"原记录指纹不符不启用补加工");
        Check(Fails(()=>ProductionReworkSource.Load("资料收集/工件_20261117_112539654/整件_3acc0f792fde4b639a14921455ef5de0/run.json")),
            "中断失败轮次不能作为全18孔补加工来源");
        string missing=Path.Combine(fixture,"孔位","hole_1","before.bmp"), moved=Path.Combine(folder,"withheld-before.bmp");
        File.Move(missing,moved);
        try{Check(Fails(()=>ProductionReworkSource.Load(recordPath)),"复制缺失照片时明确拒绝，不以完成标记代替证据");}
        finally{File.Move(moved,missing);}
        string firstTrial=Path.Combine(fixture,"孔位","hole_1","trial.json");byte[] originalTrial=File.ReadAllBytes(firstTrial);
        File.WriteAllText(firstTrial,"{}");
        try{Check(Fails(()=>ProductionReworkSource.Load(recordPath)),"试打记录损坏或未完整复制时由manifest指纹拒绝");}
        finally{File.WriteAllBytes(firstTrial,originalTrial);}
        var old=source.PreviousTargets;
        var current=old.Select(p=>new ProductionPoint(p.Id,p.X+.1,p.Y-.1)).ToArray();source.VerifyPattern(current);
        Check(current[0].X!=old[0].X,"重装夹后允许新的刚体位置，不能沿用原绝对坐标");
        var changed=current.ToArray();changed[0]=changed[0] with {X=changed[0].X+.03};
        Check(Fails(()=>source.VerifyPattern(changed)),"孔位几何变化不作为同件相同18孔加工");
        Check(Fails(()=>source.VerifyPattern(current.Take(17).ToArray())) && Fails(()=>source.VerifyPattern(current.Select(p=>p.Id==1?p with {X=double.NaN}:p).ToArray())),
            "缺孔或无效坐标不进入补加工");
        string job=Path.Combine(folder,"rework-job");Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job,"补加工计划.json"),JsonSerializer.Serialize(plan));
        var record=new ProductionRunRecord {DryRun=false,Synthetic=true,RequestedExposureMs=60000,Rework=plan.Info};
        ProductionJobArchive.Begin(job,record);
        Check(File.Exists(source.ReservationPath) && File.Exists(Path.Combine(job,ProductionJobArchive.MachiningRecordName)),
            "开始前持久登记来源轮次和新轮次占用，不移除原防重跑保护");
        Check(Fails(source.EnsureNoRework),"同一首轮已登记补加工后，不能再建另一轮重复执行");
        Check(Fails(()=>ProductionJobArchive.Begin(job,record)),"双击或重启后同一补加工目录不能重跑");
        int captures=0,moves=0,emissions=0;bool ended=false;
        var cell=new ConnectedProductionCell(ct=>Task.CompletedTask,(x,y,ct)=>{moves++;return Task.CompletedTask;},
            (step,ct)=>{captures++;return Task.FromResult(new CaptureCorrection(0,0,true,"SIMULATED_NEW_REWORK_CAPTURE_"+step));},
            ct=>{source.VerifyPattern(current);return Task.FromResult<IReadOnlyList<ProductionPoint>>(current);},
            (ms,ct)=>{if(ms!=60000)throw new Exception("追加参数丢失");emissions++;return Task.CompletedTask;},
            state=>{ProductionJobArchive.Save(job,Path.Combine(job,"run"),state);return Task.CompletedTask;},
            ()=>{ended=true;return Task.CompletedTask;});
        await new ContinuousProduction(cell).RunAsync(GuidedCapture.Route(source.Session),record);
        Check(record.Completed && captures==6 && moves==24 && emissions==18 && ended,"补加工完整重新采六处，再依新位置执行18孔；仅模拟计数，不实际出光或等待60秒");
        Check(record.Holes.Count==18 && record.Holes.Values.All(v=>v==ProductionHolePhase.Completed) && record.Rework!.ParentRunId==source.Record.RunId,
            "补加工不继承首轮完成标记，逐孔重新记录并保留关联");
        Check(ProductionJobArchive.Summary(record).Contains("累计每孔请求120秒") && ProductionJobArchive.Summary(record).Contains(source.Record.RunId.ToString()),
            "结果明确区分本轮追加量、累计请求和前轮来源");
        Check(ProductionReworkSource.Hash(original.RecordPath)==sourceHash && !File.Exists(original.ReservationPath),
            "测试只登记隔离副本，现场原文件及原轮次未被修改或占用");
        File.WriteAllText(Path.Combine(folder,"source-review.json"),JsonSerializer.Serialize(new {original.Record.RunId,original.Record.RequestedExposureMs,
            original.PowerRaw,original.ValidatedFiles,original.RecordPath,original.RecordSha256},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Production rework: {passed} passed; isolated files and simulated workflow only.");return 0;
    }
}
