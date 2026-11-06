using System.Globalization;
using Hummingbird.Core;

namespace Hummingbird.Devices.OpcUa;

/// <summary>
/// 转台自动分度（SINUMERIK 828D 经 OPC UA）。
///
/// ⚠ 重要前提：**828D 的轴位置变量是只读的**（actToolBasePos 等），
/// 不能靠"写轴位置"驱动转台。必须让机床侧预置一小段程序来执行运动。
/// 本服务采用工业上最稳妥的「R 参数 + PLC 握手」方式：
///
///   ① 上位机写 R 参数（目标角度）        ← OPC UA 变量写，权限 StateWrite/GudWrite
///   ② 上位机写 PLC 触发位 = true          ← 权限 PlcWrite
///   ③ 机床 PLC 检测到触发 → 调预置 NC 子程序（如 G0 C=R90 / POS[C]=R90）执行转位
///   ④ 机床 PLC 置"到位"位，或上位机读 C 轴实际角度判断到位
///   ⑤ 上位机写触发位 = false 复位握手
///
/// 这样运动全程受机床 PLC 的安全联锁（门、夹紧松开、急停）管控，
/// 上位机不绕过任何安全逻辑 —— 这是现场必须守住的底线。
///
/// ⚠ 所有节点路径默认留空，绝不预设臆造 NodeId。
///   现场用 UaExpert 展开地址空间后填入 config.json，未配置时本服务明确拒绝动作。
/// </summary>
public sealed class OpcUaRotaryTableService : IRotaryTableService
{
    private readonly ISinumerikVarAccess _cnc;
    private readonly IAppLogger _log;

    public OpcUaRotaryTableService(ISinumerikVarAccess cnc, RotaryTableOptions? options = null, IAppLogger? log = null)
    {
        _cnc = cnc;
        Options = options ?? new RotaryTableOptions();
        _log = log ?? new NoOpLogger();
    }

    public RotaryTableMode Mode => RotaryTableMode.OpcUaRPlc;
    public RotaryTableOptions Options { get; }
    public double CurrentAngleDeg { get; private set; }

    /// <summary>目标角节点：配了就用配置的，否则用西门子 VarProvider 标准 R 参数路径。</summary>
    private string TargetNode => string.IsNullOrWhiteSpace(Options.TargetRNode)
        ? $"/Channel/Parameter/R[u1, {Options.TargetRNumber}]"
        : Options.TargetRNode;

    public bool IsReady => _cnc.IsConnected && Missing().Count == 0;

    public string ReadinessDetail
    {
        get
        {
            var missing = Missing();
            if (!_cnc.IsConnected) missing.Insert(0, "OPC UA 会话未连接");
            if (missing.Count > 0)
                return "转台自动控制未就绪，缺 " + missing.Count + " 项：" + string.Join("；", missing) +
                       "。⚠ 请先切到『人工抄角』模式，或完成机床侧配置后再用自动模式。";
            return $"转台自动控制就绪：轴 {Options.AxisName}，目标角 → {TargetNode}，" +
                   $"触发位 {Options.TriggerNode}，到位判据 " +
                   (Options.DoneNode != "" ? Options.DoneNode : Options.ActualAngleNode) + "。";
        }
    }

    private List<string> Missing()
    {
        var m = new List<string>();
        if (string.IsNullOrWhiteSpace(Options.TriggerNode)) m.Add("PLC 触发位节点 RotaryTriggerNode（UaExpert 核实）");
        if (string.IsNullOrWhiteSpace(Options.DoneNode) && string.IsNullOrWhiteSpace(Options.ActualAngleNode))
            m.Add("到位判据：RotaryDoneNode 或 RotaryActualAngleNode 至少配一个（UaExpert 核实）");
        if (string.IsNullOrWhiteSpace(Options.TargetRNode) && Options.TargetRNumber <= 0)
            m.Add("目标角 R 参数号 RotaryTargetRNumber");
        return m;
    }

    public async Task<double?> ReadAngleAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(Options.ActualAngleNode)) return null;
        if (!_cnc.IsConnected) return null;
        var raw = await _cnc.ReadVarAsync(Options.ActualAngleNode, ct);
        return TryDouble(raw, out double v) ? v : null;
    }

    public Task<bool> SetAngleAsync(double actualDeg, CancellationToken ct = default)
    {
        // 自动模式禁止人工抄角冒充轴反馈 —— 角度必须来自机床实际值。
        _log.Warn("[转台] 自动模式下不接受人工抄角（防误判到位）。请切到『人工抄角』模式或用【读回角度】。");
        return Task.FromResult(false);
    }

    public async Task<RotaryMoveResult> MoveToAsync(double targetDeg, CancellationToken ct = default)
    {
        // ---- 1. 就绪检查：配置不全 / 未连接，一律不出动作 ----
        if (!IsReady)
            return new RotaryMoveResult(false, double.NaN, ReadinessDetail);

        var err = Validate(targetDeg);
        if (err is not null)
            return new RotaryMoveResult(false, double.NaN, "目标角被拒绝：" + err);

        double target = Normalize(targetDeg);
        bool triggered = false;

        try
        {
            // ---- 2. 触发前先确认完成信号没残留（上次未复位 = 握手异常，拒绝再动）----
            if (!string.IsNullOrWhiteSpace(Options.DoneNode))
            {
                var stale = await _cnc.ReadVarAsync(Options.DoneNode, ct);
                if (TryBool(stale, out bool done) && done)
                    return new RotaryMoveResult(false, double.NaN,
                        "PLC『到位』信号在触发前就是 true —— 握手状态残留，已拒绝动作。" +
                        "请让机床侧复位该信号（或检查上一轮是否异常中断），避免误判到位。");
            }

            // ---- 3. 写目标角（R 参数）----
            if (!await _cnc.WriteVarAsync(TargetNode, target, ct))
                return new RotaryMoveResult(false, double.NaN,
                    $"写目标角失败：{TargetNode} = {target:F3}°。检查 OPC UA 用户是否有变量写权限（StateWrite/GudWrite）。");

            _log.Info($"[转台] 目标角已写入 {TargetNode} = {target:F3}°");

            // ---- 4. 触发转位 ----
            if (!await _cnc.WriteVarAsync(Options.TriggerNode, true, ct))
                return new RotaryMoveResult(false, double.NaN,
                    $"写 PLC 触发位失败：{Options.TriggerNode}。检查 PlcWrite 权限与节点是否可写。");
            triggered = true;
            _log.Info($"[转台] 已触发转位（{Options.TriggerNode}=true），等待到位…");

            // ---- 5. 等到位：优先 PLC 完成位，其次读实际角度 ----
            var sw = System.Diagnostics.Stopwatch.StartNew();
            double actual = double.NaN;
            while (sw.ElapsedMilliseconds < Options.TimeoutMs)
            {
                ct.ThrowIfCancellationRequested();

                if (!string.IsNullOrWhiteSpace(Options.ActualAngleNode))
                {
                    var rawAng = await _cnc.ReadVarAsync(Options.ActualAngleNode, ct);
                    if (TryDouble(rawAng, out double a)) actual = a;
                }

                bool done = false;
                if (!string.IsNullOrWhiteSpace(Options.DoneNode))
                {
                    var rawDone = await _cnc.ReadVarAsync(Options.DoneNode, ct);
                    done = TryBool(rawDone, out bool b) && b;
                }
                else if (!double.IsNaN(actual))
                {
                    done = Math.Abs(AngleDiff(actual, target)) <= Options.ToleranceDeg;
                }

                if (done)
                {
                    // 有实际角度时再做一次硬校验：完成位置=True 但角度不对 → 仍然失败
                    if (!double.IsNaN(actual) && Math.Abs(AngleDiff(actual, target)) > Options.ToleranceDeg)
                        return new RotaryMoveResult(false, actual,
                            $"PLC 报到位，但读回角度 {actual:F3}° 与目标 {target:F3}° 偏差超过 " +
                            $"{Options.ToleranceDeg:F3}°。⚠ 已拒绝后续拍照/打孔，请核对转台是否真的到位。");

                    CurrentAngleDeg = double.IsNaN(actual) ? target : actual;
                    _log.Info($"[转台] 到位确认：目标 {target:F3}°，实际 {CurrentAngleDeg:F3}°，耗时 {sw.ElapsedMilliseconds}ms");
                    return new RotaryMoveResult(true, CurrentAngleDeg,
                        $"转位完成：目标 {target:F3}°，实际 {CurrentAngleDeg:F3}°，" +
                        $"耗时 {sw.ElapsedMilliseconds}ms（轴 {Options.AxisName}）");
                }

                await Task.Delay(Math.Max(20, Options.PollIntervalMs), ct);
            }

            return new RotaryMoveResult(false, actual,
                $"等待到位超时 {Options.TimeoutMs}ms（目标 {target:F3}°，最后读到 " +
                (double.IsNaN(actual) ? "无角度反馈" : $"{actual:F3}°") + "）。" +
                "检查：机床侧子程序是否被触发、转台夹紧是否松开、有无报警、RotaryDoneNode 是否配错。");
        }
        catch (OperationCanceledException)
        {
            return new RotaryMoveResult(false, double.NaN, "转台动作被取消。");
        }
        catch (Exception ex)
        {
            return new RotaryMoveResult(false, double.NaN, "转台通讯异常：" + ex.Message);
        }
        finally
        {
            // ---- 6. 无论成功失败都要复位触发位（安全：不留悬挂的启动脉冲）----
            if (triggered)
            {
                try
                {
                    bool ok = await _cnc.WriteVarAsync(Options.TriggerNode, false, default);
                    _log.Info(ok ? "[转台] 触发位已复位" : "[转台] ⚠ 触发位复位失败，请机床侧手动清掉");
                }
                catch (Exception ex) { _log.Warn("[转台] 触发位复位异常：" + ex.Message); }
            }
        }
    }

    private string? Validate(double deg)
    {
        if (double.IsNaN(deg) || double.IsInfinity(deg)) return "角度不是有效数字。";
        if (deg < Options.MinAngleDeg || deg > Options.MaxAngleDeg)
            return $"角度 {deg:F2}° 超出允许范围 [{Options.MinAngleDeg}, {Options.MaxAngleDeg}]°。";
        double step = Math.Abs(AngleDiff(Normalize(deg), CurrentAngleDeg));
        if (step > Options.MaxStepDeg + 1e-9)
            return $"单次转角 {step:F2}° 超过上限 {Options.MaxStepDeg:F0}°（安全联锁）。请分多步转。";
        return null;
    }

    private double Normalize(double deg)
        => Options.Normalize360 ? ((deg % 360) + 360) % 360 : deg;

    /// <summary>两角最短差（0~180）。</summary>
    private static double AngleDiff(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    private static bool TryDouble(object? raw, out double v)
    {
        v = 0;
        try
        {
            if (raw is null) return false;
            v = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
            return !double.IsNaN(v) && !double.IsInfinity(v);
        }
        catch { return false; }
    }

    private static bool TryBool(object? raw, out bool v)
    {
        v = false;
        try
        {
            if (raw is null) return false;
            if (raw is bool b) { v = b; return true; }
            v = Convert.ToDouble(raw, CultureInfo.InvariantCulture) != 0;
            return true;
        }
        catch { return false; }
    }
}
