namespace Hummingbird.Core;

public sealed class OpticalCalibrationProfile
{
    public int Version {get;set;}=1;
    public NcTravelEnvelope? Envelope {get;set;}
    public int SessionToken {get;set;}=171501;
    public bool R220To229Reserved {get;set;}
    public double StepMm {get;set;}=.1;
    public void Validate()
    {
        if(Version!=1 || Envelope is null || !R220To229Reserved || SessionToken<=0 ||
            !double.IsFinite(StepMm) || StepMm<=0 || StepMm>.1)
            throw new InvalidDataException("自动光学校准参数无效，步长不得超过0.10mm，须使用已核定R区与运动范围。");
        Envelope.Validate();
    }
    public IReadOnlyList<(double X,double Y)> Targets(CaptureWaypoint origin)
    {
        Validate();var b=Envelope!;
        if(!b.Contains(origin.X,origin.Y) || !double.IsFinite(origin.Z) || !double.IsFinite(origin.C) ||
            Math.Abs(origin.Z-b.Z)>.010 || Math.Abs(Math.IEEERemainder(origin.C-b.C,360))>.005)
            throw new InvalidOperationException("校准起点不在已核定XY范围或固定Z/C条件内。");
        double x=b.Contains(origin.X+StepMm,origin.Y)?origin.X+StepMm:origin.X-StepMm;
        double y=b.Contains(origin.X,origin.Y+StepMm)?origin.Y+StepMm:origin.Y-StepMm;
        if(!b.Contains(x,origin.Y) || !b.Contains(origin.X,y))throw new InvalidOperationException("当前位置没有两个独立的校准位移方向。");
        return [(origin.X,origin.Y),(origin.X,origin.Y),(x,origin.Y),(origin.X,y),(origin.X,origin.Y)];
    }
}

public sealed class OpticalSamplingRecord
{
    public Guid RunId {get;init;}=Guid.NewGuid();
    public DateTimeOffset StartedAt {get;init;}=DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt {get;set;}
    public string Phase {get;set;}="Created";
    public OpticalCalibrationProfile? Profile {get;set;}
    public CaptureWaypoint? Origin {get;set;}
    public List<string> Captures {get;set;}=[];
    public List<string> Events {get;set;}=[];
    public bool Completed {get;set;}
    public bool NcExitConfirmed {get;set;}
    public string? Error {get;set;}
    public string? CleanupError {get;set;}
}

/// <summary>自动原位/重复/独立XY/返回采样，始终关光；不包含出光接口，不自动重发运动。</summary>
public sealed class AutomaticOpticalSampling(
    OpticalCalibrationProfile profile,
    Func<CancellationToken,Task<CaptureWaypoint>> preflight,
    Func<CancellationToken,Task> ensureOff,
    Func<double,double,CancellationToken,Task> move,
    Func<int,double,double,CancellationToken,Task<string>> capture,
    Func<IReadOnlyList<string>,CancellationToken,Task> calculate,
    Func<Task> endNc)
{
    private readonly SemaphoreSlim gate=new(1,1);
    public async Task RunAsync(OpticalSamplingRecord record,Func<OpticalSamplingRecord,Task> persist,CancellationToken ct=default)
    {
        if(!await gate.WaitAsync(0,ct))throw new InvalidOperationException("自动光学校准已经进行中。");
        bool owns=false,armed=false;
        try
        {
            if(record.Phase!="Created" || record.FinishedAt is not null || record.Captures.Count!=0)
                throw new InvalidOperationException("校准记录不可重跑；失败或中断后须另建轮次并重新启动校准NC。");
            var frozen=new OpticalCalibrationProfile {Version=profile.Version,Envelope=profile.Envelope,SessionToken=profile.SessionToken,
                R220To229Reserved=profile.R220To229Reserved,StepMm=profile.StepMm};
            frozen.Validate();owns=true;record.Profile=frozen;
            async Task Phase(string phase){record.Phase=phase;record.Events.Add(phase);await persist(record);}
            await Phase("Checking");ct.ThrowIfCancellationRequested();await ensureOff(ct);
            var origin=await preflight(ct);armed=true;record.Origin=origin;
            var points=frozen.Targets(origin);
            for(int i=0;i<points.Count;i++)
            {
                ct.ThrowIfCancellationRequested();await ensureOff(ct);
                var (x,y)=points[i];
                if(i>=2)
                {
                    await Phase("MoveIntent:"+i);ct.ThrowIfCancellationRequested();await ensureOff(ct);
                    await move(x,y,ct); // 失败不重发，也不盲目回位。
                }
                await Phase("Capturing:"+i);ct.ThrowIfCancellationRequested();await ensureOff(ct);
                record.Captures.Add(await capture(i,x,y,ct));await persist(record);
            }
            await Phase("Calculating");ct.ThrowIfCancellationRequested();await ensureOff(ct);
            await calculate(record.Captures,ct);
            await Phase("ExitingNc");
            // 清理成功后才允许调用方继续任何后续试打。
            armed=false;await endNc();record.NcExitConfirmed=true;
            await Phase("Completed");record.Completed=true;
        }
        catch(Exception ex)
        {
            if(owns){record.Error=ex.Message;record.Phase=ex is OperationCanceledException?"Canceled":"Failed";}
            throw;
        }
        finally
        {
            try
            {
                if(owns)
                {
                    if(armed)
                    {
                        try{await endNc();record.NcExitConfirmed=true;}
                        catch(Exception cleanup){record.CleanupError=cleanup.Message;}
                    }
                    record.FinishedAt=DateTimeOffset.Now;await persist(record);
                }
            }
            finally{gate.Release();}
        }
    }
}
