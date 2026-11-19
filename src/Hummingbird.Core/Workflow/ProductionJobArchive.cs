using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Hummingbird.Core;

/// <summary>工件级真实加工占用记录。崩溃或重启后也不能把同一工件目录当成未加工件重跑。</summary>
public static class ProductionJobArchive
{
    public const string MachiningRecordName="本件真实加工记录.json";
    private static readonly JsonSerializerOptions JsonOptions=new() {WriteIndented=true};

    public static void EnsureUnused(string jobFolder)
    {
        string path=Path.Combine(jobFolder,MachiningRecordName);
        if(File.Exists(path))throw new InvalidOperationException("本工件已有真实加工记录，禁止再次全18孔加工。请先查看本件结果并核对实物；更换全新待加工件后再新建工件。异常孔不会自动重打。");
    }
    public static ProductionRunRecord ReviewAfterRestart(ProductionRunRecord saved)
    {
        var review=JsonSerializer.Deserialize<ProductionRunRecord>(JsonSerializer.Serialize(saved))!;
        if(!review.Completed && review.Phase is not ("Failed" or "Canceled"))
        {
            review.StoppedDuring=review.Phase;review.Phase="Interrupted";
            foreach(int id in review.Holes.Where(h=>h.Value==ProductionHolePhase.Started).Select(h=>h.Key).ToArray())review.Holes[id]=ProductionHolePhase.Uncertain;
            review.LaserOffConfirmed=null;review.NcExitConfirmed=false;
            review.Error="上次运行未正常结束；已完成孔保留，进行中孔待核对。请核对实物及设备关光状态，禁止自动重打。";
        }
        return review;
    }
    public static void Begin(string jobFolder,ProductionRunRecord record)
    {
        if(record.DryRun)throw new ArgumentException("无出光试走不能占用真实加工记录。");
        EnsureUnused(jobFolder);
        string planPath=Path.Combine(jobFolder,"补加工计划.json");
        if(File.Exists(planPath))
        {
            var plan=JsonSerializer.Deserialize<ProductionReworkPlan>(File.ReadAllText(planPath))??throw new InvalidDataException("补加工计划为空。");
            if(record.Rework is null || record.Rework!=plan.Info)throw new InvalidOperationException("补加工计划与本轮记录不符，未开始。");
            var source=plan.Verify(record.RequestedExposureMs??0,record.Rework.PowerRaw);
            source.Reserve(plan,record.RunId,jobFolder);
        }
        else if(record.Rework is not null)throw new InvalidOperationException("缺少补加工计划文件，未开始。");
        Directory.CreateDirectory(jobFolder);
        // CreateNew同时拦住双击、两进程及断电后残留；写盘失败保留占用，不猜测执行情况。
        using var stream=new FileStream(Path.Combine(jobFolder,MachiningRecordName),FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        JsonSerializer.Serialize(stream,record,JsonOptions);stream.Flush(true);
    }
    public static void Save(string jobFolder,string runFolder,ProductionRunRecord record)
    {
        Directory.CreateDirectory(runFolder);
        if(!record.DryRun)
        {
            string path=Path.Combine(jobFolder,MachiningRecordName);
            var owner=JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(path))??throw new InvalidDataException("本件加工占用记录损坏，已停止。");
            if(owner.RunId!=record.RunId)throw new InvalidOperationException("本件加工记录属于另一次运行，已停止。");
            WriteAtomic(path,JsonSerializer.Serialize(record,JsonOptions));
        }
        SaveRunRecord(runFolder,record);
    }
    public static void SaveRunRecord(string runFolder,ProductionRunRecord record)
    {
        Directory.CreateDirectory(runFolder);
        WriteAtomic(Path.Combine(runFolder,"run.json"),JsonSerializer.Serialize(record,JsonOptions));
        WriteAtomic(Path.Combine(runFolder,"本件结果.md"),Summary(record));
        string eventsPath=Path.Combine(runFolder,"加工事件.json");
        var events=File.Exists(eventsPath)?JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(eventsPath))! : [];
        var entry=JsonSerializer.SerializeToElement(new {At=DateTimeOffset.Now,record.RunId,record.Phase,record.DryRun,record.Synthetic,
            record.CapturedSteps,record.CurrentHoleId,CompletedHoles=record.Holes.Where(p=>p.Value==ProductionHolePhase.Completed).Select(p=>p.Key).Order().ToArray(),
            UncertainHoles=record.Holes.Where(p=>p.Value==ProductionHolePhase.Uncertain).Select(p=>p.Key).Order().ToArray(),
            record.Error,record.LaserOffConfirmed,record.NcExitConfirmed,ParentRunId=record.Rework?.ParentRunId,ReworkRound=record.Rework?.Round});
        events.Add(entry);WriteAtomic(eventsPath,JsonSerializer.Serialize(events,JsonOptions));
    }
    private static void WriteAtomic(string path,string contents)
    {
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try
        {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None))
            {
                byte[] bytes=Encoding.UTF8.GetBytes(contents);stream.Write(bytes);stream.Flush(true);
            }
            // Windows索引/杀毒可能短暂持有旧文件；只重试原子写盘，绝不重试加工动作。
            for(int attempt=0;;attempt++)
            {
                try{File.Move(temporary,path,true);break;}
                catch(IOException) when(attempt<4){Thread.Sleep(25*(attempt+1));}
                catch(UnauthorizedAccessException) when(attempt<4){Thread.Sleep(25*(attempt+1));}
            }
        }
        finally
        {
            try{if(File.Exists(temporary))File.Delete(temporary);}
            catch(IOException){}catch(UnauthorizedAccessException){}
        }
    }
    public static string PhaseText(ProductionRunRecord record)
    {
        string[] parts=record.Phase.Split(':');
        return parts[0] switch
        {
            "Created"=>"等待开始",
            "Starting"=>"正在准备",
            "CaptureMove"=>$"自动定位采图：第{(parts.Length>1?int.Parse(parts[1],CultureInfo.InvariantCulture)+1:0)}/6处",
            "CaptureCompleted"=>$"已采图{record.CapturedSteps}/6处",
            "Locating"=>"正在计算圆心、方向及18孔补偿",
            "HoleMove"=>$"正在移动到孔{record.CurrentHoleId}",
            "HoleStarted"=>$"孔{record.CurrentHoleId}工序执行中（采图、出光或归档）",
            "HoleCompleted"=>$"孔{record.CurrentHoleId}工序已完成",
            "Completed"=>record.DryRun?"18孔无出光试走完成":record.Rework is null?"18孔加工工序完成，实物质量待检":"18孔补加工工序完成，实物质量待检",
            "Canceled"=>"已停止，请核对本件记录和实物",
            "Interrupted"=>"上次运行中断，需核对实物及关光状态",
            "Failed"=>"运行异常，已停止后续孔",
            _=>record.Phase
        };
    }
    public static string HoleText(ProductionRunRecord record,int id)=>record.Holes.GetValueOrDefault(id) switch
    {
        ProductionHolePhase.Completed=>record.DryRun?"试走完成":"工序完成",
        ProductionHolePhase.Uncertain=>"待核对，禁止自动重打",
        _=>record.Holes.ContainsKey(id)?"工序执行中":record.CurrentHoleId==id && record.Phase.StartsWith("HoleMove:")?"正在到位":"未开始"
    };
    public static string Summary(ProductionRunRecord record)
    {
        string Time(DateTimeOffset? value)=>value?.ToString("yyyy-MM-dd HH:mm:ss zzz",CultureInfo.InvariantCulture)??"—";
        var text=new StringBuilder();
        text.AppendLine("# 本件运行结果\n");
        text.AppendLine($"模式：{(record.DryRun?"无出光试走":"真实加工")}；运行号：{record.RunId}。\n");
        if(record.Rework is { } rework)text.AppendLine($"同件18孔补加工第{rework.Round}轮；关联前轮：{rework.ParentRunId}；本轮每孔追加{rework.AdditionalExposureMs/1000.0:0.###}秒、功率原始{rework.PowerRaw}；累计每孔请求{rework.CumulativeRequestedMs/1000.0:0.###}秒。\n");
        if(record.Rework?.RetryOfRunId is { } failed)text.AppendLine($"未出光重试关联失败轮次：{failed}；该失败轮次不计入累计出光请求，原记录保留。\n");
        text.AppendLine($"状态：{PhaseText(record)}；采图{record.CapturedSteps}/6；完成{record.Holes.Count(p=>p.Value==ProductionHolePhase.Completed)}/18。\n");
        text.AppendLine($"开始：{Time(record.StartedAt)}；结束：{Time(record.FinishedAt)}。\n");
        text.AppendLine($"每孔请求时长：{(record.RequestedExposureMs is { } ms?(ms/1000.0).ToString("0.###",CultureInfo.InvariantCulture)+"秒":"不出光")}；关光读回确认：{record.LaserOffConfirmed}；主控退出确认：{record.NcExitConfirmed}。\n");
        if(record.Error is not null)text.AppendLine($"异常：{record.Error.Replace('\r',' ').Replace('\n',' ')}；停止阶段：{record.StoppedDuring}。\n");
        if(record.ResultArchivePath is not null)text.AppendLine($"资料包：{record.ResultArchivePath}。\n");
        if(record.ArchiveError is not null)text.AppendLine($"资料包未完成：{record.ArchiveError}；原始目录仍保留。\n");
        text.AppendLine("| 孔号 | 工序状态 | 完成时间 |\n| --- | --- | --- |");
        foreach(int id in Enumerable.Range(1,18))text.AppendLine($"| {id} | {HoleText(record,id)} | {Time(record.HoleTimings.GetValueOrDefault(id)?.FinishedAt)} |");
        text.AppendLine("\n工序完成不等于孔径、孔深及崩边验收；逐孔前后图、参数读回和关光状态见hole_编号/trial.json。\n");
        if(!record.DryRun)text.AppendLine("本轮不自动重跑或续跑；完成后若确认需同件全18孔补加工，使用专门入口关联本轮，并回装重新定位，不得按新件绕过记录。");
        return text.ToString();
    }
}
