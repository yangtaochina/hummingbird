namespace Hummingbird.Core;

/// <summary>把已存在的采图、定位、NC和单孔工序接入统一调度。回调必须传播失败，不能把失败转换为成功。</summary>
public sealed class ConnectedProductionCell(
    Func<CancellationToken,Task> ensureOff,
    Func<double,double,CancellationToken,Task> move,
    Func<int,CancellationToken,Task<CaptureCorrection>> capture,
    Func<CancellationToken,Task<IReadOnlyList<ProductionPoint>>> locate,
    Func<int,CancellationToken,Task> expose,
    Func<ProductionRunRecord,Task> persist,
    Func<Task> end) : IContinuousProductionCell
{
    public Task EnsureLaserOffAsync(CancellationToken ct)=>ensureOff(ct);
    public Task MoveAndSettleAsync(double x,double y,CancellationToken ct)=>move(x,y,ct);
    public Task<CaptureCorrection> CaptureAndInspectAsync(int step,CancellationToken ct)=>capture(step,ct);
    public Task<IReadOnlyList<ProductionPoint>> LocateCompensatedHolesAsync(CancellationToken ct)=>locate(ct);
    public Task ExposeAndConfirmOffAsync(int milliseconds,CancellationToken ct)=>expose(milliseconds,ct);
    public Task PersistAsync(ProductionRunRecord record)=>persist(record);
    public Task EndNcSessionAsync()=>end();
}

public sealed class ProductionStationProfile
{
    public int Version {get;set;}=1;
    public NcTravelEnvelope? Envelope {get;set;}
    public int SessionToken {get;set;}=12345;
    public bool R220To229Reserved {get;set;}
    // 旧的无出光配置继续可用，但不隐式成为真实加工配置。
    public bool AllowMachining {get;set;}
    public void ValidateFor(bool dryRun)
    {
        Validate();
        if(!dryRun && !AllowMachining)throw new InvalidOperationException("当前配置仅供无出光试走，请载入本次整件加工配置。");
    }
    public void Validate()
    {
        if(Version!=1 || Envelope is null || SessionToken<=0)throw new InvalidDataException("主控配置版本、运动边界或会话编号无效。");
        Envelope.Validate();
        if(!R220To229Reserved)throw new InvalidDataException("R220～R229尚未核定可专用。");
    }
}
