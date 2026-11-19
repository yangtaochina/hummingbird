using Hummingbird.Core;

namespace Hummingbird.Devices.LaserRtu;

public sealed record SpotTrialPosition(double X, double Y, double Z, double C,
    AxisCoordinateSource CSource, string? CNode, DateTimeOffset ReadAt)
{
    public string XyzSource { get; init; } = "Unspecified";
}

public sealed record SpotTrialImage(string FileName, string Sha256, int Width, int Height,
    DateTime CameraTime, SpotTrialPosition Position);

public sealed class SpotTrialRecord
{
    public int Version { get; init; } = 1;
    public string DeviceMode { get; init; } = "Unspecified";
    public Guid? ProductionRunId {get;set;}
    public int? HoleId {get;set;}
    public bool DryRun { get; init; }
    public bool Completed { get; set; }
    public string Phase { get; set; } = "Created";
    public DateTimeOffset Started { get; init; } = DateTimeOffset.Now;
    public DateTimeOffset? Finished { get; set; }
    public int? RequestedExposureMs { get; init; }
    public LaserProcessPreset? RequestedPreset { get; init; }
    public double? CommandWindowMs { get; set; }
    public double? OpticalExposureMs => null;
    public bool EmissionRequested { get; set; }
    public bool? ShutdownConfirmed { get; set; }
    public LaserCalibrationState? LaserStateBefore { get; set; }
    public SpotTrialPosition? StartPosition { get; set; }
    public SpotTrialPosition? EndPosition { get; set; }
    public SpotTrialImage? BeforeImage { get; set; }
    public SpotTrialImage? AfterImage { get; set; }
    public string OperatorNotes { get; init; } = "";
    public RingScanSettings? SoftwareOpticalSettings { get; init; }
    public double? RequestedCameraExposureUs { get; init; }
    public double? RequestedCameraGain { get; init; }
    public string? Error { get; set; }
    public string[] LogLines { get; set; } = [];
    public string[] Limitations { get; } = [
        "请求时长及通信窗口不等于真实光学出光时长；未验证加工精度。",
        "EmissionRequested记录软件的出光请求意图，不保证设备已实际出光。",
        "本流程不发送轴运动或参数修改指令；快照核对不代替机床硬件互锁或参考状态确认。",
        "C无实际反馈节点时是手填值，不表示软件监测了C轴全程不动。",
        "相机设置记录为软件请求值，未自动证明光学状态、实际曝光或标定有效。",
        "演练不发送激光指令，不能据此判断外部软件是否已关光。" ];
}

/// <summary>固定轴单点采图/试打。只有激光控制器可出光，未传入轴运动接口。</summary>
public sealed class StationarySpotTrial(
    Func<Task<SpotTrialPosition>> readPosition,
    Func<string, SpotTrialPosition, Task<SpotTrialImage>> capture,
    Func<SpotTrialRecord, Task> persist,
    LaserCalibrationController? laser,
    Action<string> trace)
{
    public static void CheckStable(SpotTrialPosition expected, SpotTrialPosition actual)
    {
        if (new[] { actual.X, actual.Y, actual.Z, actual.C, expected.X, expected.Y, expected.Z, expected.C }
            .Any(v => !double.IsFinite(v))) throw new InvalidDataException("实际坐标读数无效。");
        if (Math.Abs(expected.X-actual.X)>.002 || Math.Abs(expected.Y-actual.Y)>.002 ||
            Math.Abs(expected.Z-actual.Z)>.002 || Math.Abs(Math.IEEERemainder(expected.C-actual.C,360))>.001)
            throw new InvalidOperationException("单点试打前后坐标发生变化；停止本轮，不把图像作为固定轴结果。");
    }

    public async Task RunAsync(SpotTrialRecord record, CancellationToken ct = default)
    {
        // No image acquisition or controller call until the supplied request has been validated.
        if (!record.DryRun)
        {
            LaserCalibrationController.ParseRequestMilliseconds(record.RequestedExposureMs?.ToString(
                System.Globalization.CultureInfo.InvariantCulture));
            if (laser is null || !laser.ProtocolConfirmed || laser.LastState is not { CanExpose:true })
                throw new InvalidOperationException("真实试打前须先只读核对协议、参数和关光状态。");
            if(record.RequestedPreset is { } preset && (preset.RequestedExposureMs!=record.RequestedExposureMs || !preset.Matches(laser.LastState)))
                throw new InvalidOperationException("客户选择的时长或功率与本次请求/设备读回不匹配。");
        }
        try
        {
            ct.ThrowIfCancellationRequested();
            var start=await readPosition();CheckStable(start,start);record.StartPosition=start;
            await Task.Delay(250,ct);
            CheckStable(start,await readPosition());
            record.BeforeImage=await capture("before",start);
            CheckStable(start,await readPosition());
            record.LaserStateBefore=record.DryRun?null:laser!.LastState;
            record.Phase="BeforeImageSaved";
            await persist(record); // A failed baseline archive must prevent ON.
            ct.ThrowIfCancellationRequested();
            CheckStable(start,await readPosition());
            if (!record.DryRun)
            {
                record.EmissionRequested=true;record.Phase="ExposureRequested";
                // Persist the conservative intent before entering any laser command path.
                await persist(record);
                ct.ThrowIfCancellationRequested();
                CheckStable(start,await readPosition());
                record.CommandWindowMs=await laser!.ExposeAsync(record.RequestedExposureMs!.Value,ct,
                    async()=>CheckStable(start,await readPosition()));
                record.ShutdownConfirmed=laser.ShutdownConfirmed;
                if (!laser.ShutdownConfirmed) throw new IOException("设备关光尚未确认，不采集打后图。");
            }
            else trace("无出光演练：仅采集前后照片，不发送激光读写或轴运动指令。");
            ct.ThrowIfCancellationRequested();
            var afterPosition=await readPosition();CheckStable(start,afterPosition);
            record.AfterImage=await capture("after",afterPosition);
            record.EndPosition=await readPosition();CheckStable(start,record.EndPosition);
            record.Completed=true;record.Phase=record.DryRun?"DryRunCompleted":"Completed";
            record.Finished=DateTimeOffset.Now;
            await persist(record);
        }
        catch (Exception ex)
        {
            record.Completed=false;record.Error=ex.Message;
            record.Phase=ex is OperationCanceledException?"Canceled":"Failed";
            record.Finished=DateTimeOffset.Now;
            record.ShutdownConfirmed=record.DryRun?null:laser?.ShutdownConfirmed;
            try { await persist(record); }
            catch (Exception archiveError) { trace("失败记录也未能保存："+archiveError.Message); }
            throw;
        }
    }
}
