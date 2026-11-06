using Hummingbird.Core;

namespace Hummingbird.Devices.Sim;

/// <summary>
/// 转台的「人工抄角」与「离线仿真」两个实现（共用一份安全校验）。
///
/// ManualEntry（现场首选，OPC UA 没通也能干活）：
///   操作工手动把转台转到 HMI 显示的某个角度 → 在界面填角度 → 程序记下来 → 拍照。
///   MoveToAsync 会明确返回「请人工转动」而不是假装转到位，避免上层误以为轴已运动。
///
/// Simulated：按角速度模拟转动，用于离线开发与 SmokeTest。
/// </summary>
public sealed class SimRotaryTableService : IRotaryTableService
{
    private readonly IAppLogger _log;
    private readonly RotaryTableMode _mode;

    public SimRotaryTableService(RotaryTableMode mode, RotaryTableOptions? options = null, IAppLogger? log = null)
    {
        if (mode == RotaryTableMode.OpcUaRPlc)
            throw new ArgumentException("OPC UA 模式请用 OpcUaRotaryTableService", nameof(mode));
        _mode = mode;
        Options = options ?? new RotaryTableOptions();
        _log = log ?? new NoOpLogger();
    }

    public RotaryTableMode Mode => _mode;
    public RotaryTableOptions Options { get; }
    public double CurrentAngleDeg { get; private set; }
    public bool IsReady => true;

    public string ReadinessDetail => _mode == RotaryTableMode.ManualEntry
        ? "人工模式就绪：不需要 OPC UA。转动转台后把 HMI 上的实际角度填进界面即可。"
        : "仿真模式就绪：按配置角速度模拟转位，仅用于离线测试。";

    public Task<double?> ReadAngleAsync(CancellationToken ct = default)
        => Task.FromResult<double?>(CurrentAngleDeg);

    public async Task<bool> SetAngleAsync(double actualDeg, CancellationToken ct = default)
    {
        var err = Validate(actualDeg);
        if (err is not null)
        {
            _log.Warn("[转台] 人工抄角被拒绝：" + err);
            return false;
        }
        CurrentAngleDeg = Normalize(actualDeg);
        _log.Info($"[转台] 人工设定当前角度 = {CurrentAngleDeg:F3}°（来源：HMI 读数，未经通讯校验）");
        await Task.Yield();
        return true;
    }

    public async Task<RotaryMoveResult> MoveToAsync(double targetDeg, CancellationToken ct = default)
    {
        var err = Validate(targetDeg);
        if (err is not null)
            return new RotaryMoveResult(false, double.NaN, "目标角被拒绝：" + err);

        double target = Normalize(targetDeg);

        if (_mode == RotaryTableMode.ManualEntry)
        {
            return new RotaryMoveResult(false, double.NaN,
                $"人工模式：程序不能驱动转台。请手动转到 {target:F3}°，" +
                "确认 HMI 读数后点界面【已转到该角度】，再拍下一帧。");
        }

        double delta = Math.Abs(target - CurrentAngleDeg);
        double waitMs = delta / Math.Max(1e-6, Options.SimSpeedDegPerSec) * 1000.0;
        _log.Info($"[转台] 仿真转动 {CurrentAngleDeg:F3}° → {target:F3}°（{delta:F3}°，预计 {waitMs:F0}ms）");
        await Task.Delay(TimeSpan.FromMilliseconds(Math.Min(waitMs, 3000)), ct);
        CurrentAngleDeg = target;
        return new RotaryMoveResult(true, target,
            $"仿真转位完成：{target:F3}°，偏差 0.000°（无真实轴反馈）");
    }

    /// <summary>角度范围 + 单次步长 + 有限数校验。三种模式共用，防误输入导致转台飞车。</summary>
    private string? Validate(double deg)
    {
        if (double.IsNaN(deg) || double.IsInfinity(deg)) return "角度不是有效数字。";
        if (deg < Options.MinAngleDeg || deg > Options.MaxAngleDeg)
            return $"角度 {deg:F2}° 超出允许范围 [{Options.MinAngleDeg}, {Options.MaxAngleDeg}]°。";
        double step = Math.Abs(Normalize(deg) - CurrentAngleDeg);
        // 只有归一化到 0~360 时，才允许走「跨 0° 的反向最短路」（如 350°→10° 视为 20°）。
        if (Options.Normalize360) step = Math.Min(step, 360 - step);
        if (step > Options.MaxStepDeg + 1e-9)
            return $"单次转角 {step:F2}° 超过上限 {Options.MaxStepDeg:F0}°（安全联锁）。" +
                   "请分多步转，或确认 MaxStepDeg 配置。";
        return null;
    }

    private double Normalize(double deg)
        => Options.Normalize360 ? ((deg % 360) + 360) % 360 : deg;
}
