using System.Text.Json;
using Hummingbird.Core;

namespace Hummingbird.Devices.Sim;

/// <summary>仅合成工件/坐标状态演练；不访问实际相机、串口、OPC UA，不验证视觉识别和加工质量。</summary>
public sealed class OfflineProductionDemo(string folder) : IContinuousProductionCell
{
    private readonly Dictionary<int,int> captures=new();
    private double x,y;
    public bool OffConfirmed {get;set;}=true;
    public int Moves {get;private set;}
    public int EmissionCalls {get;private set;}
    public int? FailCaptureStep {get;set;}
    public int? FailEmissionNumber {get;set;}
    public bool FailPersist {get;set;}
    public Action<ProductionRunRecord>? OnPersist {get;set;}
    public bool NcEnded {get;private set;}
    public static CaptureWaypoint[] Route()=>GuidedCapture.Steps.Select((s,i)=>new CaptureWaypoint(s.Name,s.Role,
        Math.Cos(i*Math.PI/3)*14.45,Math.Sin(i*Math.PI/3)*14.45,-1,0)).ToArray();
    public Task EnsureLaserOffAsync(CancellationToken ct)
    {ct.ThrowIfCancellationRequested();if(!OffConfirmed)throw new IOException("模拟关光未确认。");return Task.CompletedTask;}
    public async Task MoveAndSettleAsync(double targetX,double targetY,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(!OffConfirmed)throw new Exception("未关光不能移动。");
        await Task.Delay(5,ct);x=targetX;y=targetY;Moves++;
    }
    public async Task<CaptureCorrection> CaptureAndInspectAsync(int step,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(FailCaptureStep==step)throw new IOException("模拟采图失败。");
        int attempt=captures.GetValueOrDefault(step);captures[step]=attempt+1;
        Directory.CreateDirectory(folder);string path=Path.Combine(folder,$"capture_{step}_{attempt}.json");
        await File.WriteAllTextAsync(path,JsonSerializer.Serialize(new {Synthetic=true,VisionSimulated=true,Step=step,Attempt=attempt,X=x,Y=y,
            Explanation="合成纠偏结果，用于流程顺序与异常演练，未使用现场照片"}),ct);
        return attempt==0?new(.04,-.02,false,path):new(0,0,true,path);
    }
    public Task<IReadOnlyList<ProductionPoint>> LocateCompensatedHolesAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        IReadOnlyList<ProductionPoint> holes=Enumerable.Range(1,18).Select(i=>new ProductionPoint(i,
            Math.Cos(i*Math.PI/9)*10+.3,Math.Sin(i*Math.PI/9)*10+.36)).ToArray();
        return Task.FromResult(holes);
    }
    public Task ExposeAndConfirmOffAsync(int milliseconds,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();EmissionCalls++;
        if(FailEmissionNumber==EmissionCalls)throw new IOException("模拟出光应答丢失；关光结果单独核对。");
        return Task.CompletedTask;
    }
    public Task PersistAsync(ProductionRunRecord record)
    {
        if(FailPersist)throw new IOException("模拟归档失败。");
        record.Synthetic=true;
        ProductionJobArchive.SaveRunRecord(folder,record);OnPersist?.Invoke(record);return Task.CompletedTask;
    }
    public Task EndNcSessionAsync(){NcEnded=true;return Task.CompletedTask;}
    public static async Task<ProductionRunRecord> RunAsync(string folder,int? plannedExposureMs=null,CancellationToken ct=default)
    {
        var cell=new OfflineProductionDemo(folder);var record=new ProductionRunRecord {DryRun=true,RequestedExposureMs=plannedExposureMs};
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder,"HM_MASTER.MPF"),"; SIMULATED COORDINATES - DO NOT RUN ON MACHINE\r\n"+ContinuousNcProgram.Generate(new(-20,20,-20,20,-1,0),12345));
        File.WriteAllText(Path.Combine(folder,"说明.txt"),"仅合成数据离线演练。HM_MASTER.MPF中的±20mm、Z=-1、C=0均为模拟值，禁止直接用于真机。\n未使用实际视觉算法；现场照片须通过原实体圆边/圆环检查。\n");
        await new ContinuousProduction(cell).RunAsync(Route(),record,ct);return record;
    }
}
