using System.Text.Json.Serialization;

namespace Hummingbird.Core;

public sealed record ProductionPoint(int Id, double X, double Y);
public sealed record CaptureCorrection(double Dx, double Dy, bool Accepted, string Evidence);
public sealed class ProductionHoleTiming
{
    public double X {get;init;}
    public double Y {get;init;}
    public DateTimeOffset StartedAt {get;init;}
    public DateTimeOffset? FinishedAt {get;set;}
}
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProductionHolePhase { Started, Completed, Uncertain }
public sealed class ProductionRunRecord
{
    public Guid RunId { get; init; } = Guid.NewGuid();
    public bool DryRun { get; init; } = true;
    public bool Synthetic { get; set; }
    public int? RequestedExposureMs { get; init; }
    public ProductionReworkInfo? Rework { get; init; }
    public string Phase { get; set; } = "Created";
    public int CapturedSteps { get; set; }
    public Dictionary<int, ProductionHolePhase> Holes { get; set; } = new();
    public List<string> Events { get; set; } = new();
    public string? Error { get; set; }
    public bool Completed { get; set; }
    public DateTimeOffset? StartedAt {get;set;}
    public DateTimeOffset? UpdatedAt {get;set;}
    public DateTimeOffset? FinishedAt {get;set;}
    public int? CurrentHoleId {get;set;}
    public string? StoppedDuring {get;set;}
    public bool? LaserOffConfirmed {get;set;}
    public bool NcExitConfirmed {get;set;}
    public string? ResultArchivePath {get;set;}
    public string? ArchiveError {get;set;}
    public Dictionary<int,ProductionHoleTiming> HoleTimings {get;set;}=new();
}

/// <summary>运动、视觉、出光和归档分离；设备适配器须负责实际到位、坐标/光学及出光许可检查。</summary>
public interface IContinuousProductionCell
{
    Task EnsureLaserOffAsync(CancellationToken ct);
    Task MoveAndSettleAsync(double x, double y, CancellationToken ct);
    Task<CaptureCorrection> CaptureAndInspectAsync(int step, CancellationToken ct);
    Task<IReadOnlyList<ProductionPoint>> LocateCompensatedHolesAsync(CancellationToken ct);
    Task ExposeAndConfirmOffAsync(int milliseconds, CancellationToken ct);
    Task PersistAsync(ProductionRunRecord record);
    Task EndNcSessionAsync();
}

/// <summary>同一次NC会话完成六步采图/有限纠偏/18孔。失败不会自动重打或跨次装夹续跑。</summary>
public sealed class ContinuousProduction(IContinuousProductionCell cell)
{
    private readonly SemaphoreSlim operation = new(1,1);
    public async Task RunAsync(IReadOnlyList<CaptureWaypoint> route, ProductionRunRecord record, CancellationToken ct=default)
    {
        if(!await operation.WaitAsync(0,ct))throw new InvalidOperationException("整件流程正在运行。");
        bool sessionStarted=false;
        try
        {
            if(record.Phase!="Created" || record.Holes.Count!=0 || record.CapturedSteps!=0 || record.HoleTimings.Count!=0)
                throw new InvalidOperationException("已有进度不能自动重跑；先核对实物和失败孔，再建立新记录。");
            if(route.Count!=6 || route.Where((p,i)=>p.Name!=GuidedCapture.Steps[i].Name || p.Role!=GuidedCapture.Steps[i].Role).Any() ||
                route.Any(p=>new[]{p.X,p.Y,p.Z,p.C}.Any(v=>!double.IsFinite(v))) ||
                route.Any(p=>Math.Abs(p.Z-route[0].Z)>.010 || Math.Abs(Math.IEEERemainder(p.C-route[0].C,360))>.005))
                throw new InvalidDataException("整件流程需要同一Z/C的完整六步路线。");
            if(!record.DryRun && (record.RequestedExposureMs is null or <LaserRequestLimits.MinimumExposureMs or >LaserRequestLimits.MaximumExposureMs))
                throw new InvalidOperationException("真实加工需要范围内的客户请求出光时长。");
            sessionStarted=true;record.StartedAt=DateTimeOffset.Now;
            async Task Save(string phase)
            {
                record.Phase=phase;record.UpdatedAt=DateTimeOffset.Now;record.Events.Add(phase);await cell.PersistAsync(record);
            }
            await Save("Starting");
            for(int i=0;i<route.Count;i++)
            {
                ct.ThrowIfCancellationRequested();var point=route[i];double x=point.X,y=point.Y;
                for(int attempt=0;;attempt++)
                {
                    await cell.EnsureLaserOffAsync(ct);await Save($"CaptureMove:{i}:{attempt}");
                    await cell.MoveAndSettleAsync(x,y,ct);
                    var correction=await cell.CaptureAndInspectAsync(i,ct);
                    if(!double.IsFinite(correction.Dx) || !double.IsFinite(correction.Dy) || string.IsNullOrWhiteSpace(correction.Evidence))
                        throw new InvalidDataException("采图检查缺少有效修正或证据。");
                    record.Events.Add("CaptureEvidence:"+correction.Evidence);
                    if(correction.Accepted)break;
                    if(attempt>=2 || double.Hypot(correction.Dx,correction.Dy)>.20 || double.Hypot(correction.Dx,correction.Dy)<1e-8)
                        throw new InvalidDataException("采图未通过或超过有限纠偏次数/范围。");
                    x+=correction.Dx;y+=correction.Dy;
                    if(double.Hypot(x-point.X,y-point.Y)>.20)throw new InvalidDataException("累计纠偏超出起点0.20mm。");
                }
                record.CapturedSteps++;await Save($"CaptureCompleted:{i}");
            }
            await Save("Locating");var holes=await cell.LocateCompensatedHolesAsync(ct);
            if(holes.Count!=18 || !holes.Select(h=>h.Id).Order().SequenceEqual(Enumerable.Range(1,18)) || holes.Any(h=>!double.IsFinite(h.X)||!double.IsFinite(h.Y)))
                throw new InvalidDataException("定位必须生成18个唯一且有效的补偿目标。");
            foreach(var hole in holes)
            {
                record.CurrentHoleId=hole.Id;
                ct.ThrowIfCancellationRequested();await cell.EnsureLaserOffAsync(ct);await Save($"HoleMove:{hole.Id}");
                await cell.MoveAndSettleAsync(hole.X,hole.Y,ct);
                record.HoleTimings.Add(hole.Id,new() {X=hole.X,Y=hole.Y,StartedAt=DateTimeOffset.Now});
                if(!record.DryRun)record.LaserOffConfirmed=null; // 本孔开始后不能把上一孔的关光确认当成本孔结果。
                record.Holes.Add(hole.Id,ProductionHolePhase.Started);await Save($"HoleStarted:{hole.Id}");
                // 先记录不可自动重试的意图，再进入出光接口。演练绝不调用该接口。
                ct.ThrowIfCancellationRequested();
                if(!record.DryRun){record.LaserOffConfirmed=false;await cell.ExposeAndConfirmOffAsync(record.RequestedExposureMs!.Value,ct);}
                await cell.EnsureLaserOffAsync(ct);record.LaserOffConfirmed=true;
                record.HoleTimings[hole.Id].FinishedAt=DateTimeOffset.Now;
                record.Holes[hole.Id]=ProductionHolePhase.Completed;await Save($"HoleCompleted:{hole.Id}");
            }
            await cell.EndNcSessionAsync();
            record.NcExitConfirmed=true;record.FinishedAt=DateTimeOffset.Now;record.Completed=true;await Save("Completed");
        }
        catch(Exception ex)
        {
            if(!sessionStarted)throw;
            record.Completed=false;record.Error=ex.Message;record.StoppedDuring=record.Phase;record.FinishedAt=record.UpdatedAt=DateTimeOffset.Now;
            foreach(int id in record.Holes.Where(p=>p.Value==ProductionHolePhase.Started).Select(p=>p.Key).ToArray())record.Holes[id]=ProductionHolePhase.Uncertain;
            record.Phase=ex is OperationCanceledException?"Canceled":"Failed";
            if(sessionStarted)
            {
                try{await cell.EnsureLaserOffAsync(CancellationToken.None);record.LaserOffConfirmed=true;}catch(Exception off){record.LaserOffConfirmed=false;record.Error+="；关光未确认："+off.Message;}
                try{await cell.EndNcSessionAsync();record.NcExitConfirmed=true;}catch(Exception end){record.Error+="；NC退出未确认："+end.Message;}
                try{await cell.PersistAsync(record);}catch(Exception persist){throw new AggregateException("流程失败且失败记录未能保存。",ex,persist);}
            }
            throw;
        }
        finally{operation.Release();}
    }
}
