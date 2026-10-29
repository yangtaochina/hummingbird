using System.Text;

namespace Hummingbird.Core;

/// <summary>
/// 自动加工流程编排：拍照定位 → 坐标计算 → 程序下发 → 到位触发出光。
/// 只依赖抽象接口，不依赖任何具体设备实现。
/// </summary>
public sealed class AutoRunWorkflow
{
    private readonly IAppLogger _log;
    private readonly ICameraService? _camera;
    private readonly IVisionService _vision;
    private readonly ICncService _cnc;
    private readonly ILaserService _laser;

    public CalibrationResult? Calibration { get; set; }
    public HolePatternTemplate Template { get; set; } = new();
    public VisionOptions VisionOptions { get; set; } = new();
    public TransformConvention Convention { get; set; } = new();

    /// <summary>到位判定容差（mm）。</summary>
    public double ArrivalToleranceMm { get; set; } = 0.02;

    private CancellationTokenSource? _runCts;

    public AutoRunWorkflow(IAppLogger log, ICameraService? camera, IVisionService vision,
        ICncService cnc, ILaserService laser)
    {
        _log = log; _camera = camera; _vision = vision; _cnc = cnc; _laser = laser;
    }

    /// <summary>拍照并定位（顺带返回帧，供 UI 显示）。</summary>
    public async Task<(PartPose Pose, CameraFrame Frame)> GrabAndLocateAsync(CancellationToken ct = default)
    {
        if (_camera is null)
            throw new InvalidOperationException("相机不可用（Real 模式待接入 pylon SDK，见 Hummingbird.Devices.Basler/README.md）");
        var frame = await _camera.GrabAsync(5000);
        var pose = await _vision.LocateAsync(frame, VisionOptions, ct);
        if (pose.Success)
            _log.Info($"定位成功：中心=({pose.CenterPxX:F1},{pose.CenterPxY:F1})px 半径={pose.OuterRadiusPx:F0}px 转角={pose.AngleOffsetDeg:F2}° 置信度={pose.Confidence:F2}");
        else
            _log.Error("定位失败：" + pose.Detail);
        return (pose, frame);
    }

    /// <summary>
    /// 模板孔位 + 定位结果 + 标定 → 每个孔的机床坐标。
    /// 变换链：孔(表盘系) --转角θ顺时针--> --轴向约定--> + 零件中心(机床系)。
    /// </summary>
    public List<HoleTarget> ComputeTargets(PartPose pose)
    {
        if (Calibration is not { Valid: true })
            throw new InvalidOperationException("未标定或标定无效，请先在“标定”页完成九点标定");
        if (Template.Holes.Count == 0)
            throw new InvalidOperationException("未加载孔位模板");

        var (rawCx, rawCy) = Calibration.Apply(pose.CenterPxX, pose.CenterPxY);
        var (cx, cy) = Convention.Apply(rawCx, rawCy);

        double t = Math.PI / 180 * pose.AngleOffsetDeg;   // 顺时针为正
        double c = Math.Cos(t), s = Math.Sin(t);

        var targets = new List<HoleTarget>(Template.Holes.Count);
        foreach (var h in Template.Holes)
        {
            double rx = h.X * c + h.Y * s;      // 表盘系内顺时针旋转
            double ry = -h.X * s + h.Y * c;
            var (hx, hy) = Convention.Apply(rx, ry);
            targets.Add(new HoleTarget(h.Id, cx + hx, cy + hy, h.DiameterMm, h.Clock));
        }
        _log.Info($"目标坐标计算完成：{targets.Count}孔，零件中心(机床)=({cx:F3},{cy:F3})mm，转角={pose.AngleOffsetDeg:F2}°");
        return targets;
    }

    /// <summary>生成 SINUMERIK 828D 刻孔定位程序（NC 只定位+停顿，出光由上位机触发）。</summary>
    public string GenerateSpf(IEnumerable<HoleTarget> targets, string? partName = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($";HM_DIAL.SPF  自动生成 {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {partName ?? Template.PartName}");
        sb.AppendLine(";激光出光由上位机在停顿窗口内触发，本程序只负责定位");
        sb.AppendLine("N10 G90 G71");
        int n = 20, i = 1, count = targets.Count();
        foreach (var t in targets)
        {
            sb.AppendLine($"N{n} G0 X={t.MachineX:F4} Y={t.MachineY:F4}   ;孔{i}/{count} #{t.Id} {t.Clock}");
            sb.AppendLine($"N{n + 10} G4 F0.8");
            n += 20; i++;
        }
        sb.AppendLine($"N{n} M2");
        return sb.ToString();
    }

    /// <summary>计算并下发程序（一步到位）。</summary>
    public async Task<bool> DeployProgramAsync(PartPose pose, string name = "HM_DIAL.SPF", CancellationToken ct = default)
    {
        var targets = ComputeTargets(pose);
        var spf = GenerateSpf(targets);
        bool ok = await _cnc.UploadProgramAsync(name, spf, ct);
        _log.Info(ok ? $"程序已下发：{name}（{targets.Count}孔）" : "程序下发失败，请检查连接与共享路径");
        return ok;
    }

    public void CancelRun()
    {
        _runCts?.Cancel();
        _log.Warn("已请求取消激光同步监控");
    }

    /// <summary>
    /// 激光同步监控：轮询机床轴位置，到位（容差内）即触发出光，逐孔推进。
    /// 调用时机：操作工在 NC 侧按下程序启动之后。
    /// </summary>
    public async Task<int> RunLaserSyncAsync(IReadOnlyList<HoleTarget> targets, CancellationToken ct = default)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _runCts.Token;
        var fired = new HashSet<int>();
        _log.Info($"激光同步监控开始：{targets.Count}孔，到位容差 {ArrivalToleranceMm}mm");

        try
        {
            while (!token.IsCancellationRequested && fired.Count < targets.Count)
            {
                await Task.Delay(100, token);
                var st = await _cnc.GetStatusAsync(token);
                var next = targets.FirstOrDefault(t => !fired.Contains(t.Id));
                if (next is null) break;

                bool arrived = Math.Abs(st.PosX - next.MachineX) <= ArrivalToleranceMm
                            && Math.Abs(st.PosY - next.MachineY) <= ArrivalToleranceMm;
                if (!arrived) continue;

                fired.Add(next.Id);
                _log.Info($"孔#{next.Id}（{next.Clock}）到位 → 出光（{fired.Count}/{targets.Count}）");
                await _laser.FireBurstAsync(token);
            }
        }
        catch (OperationCanceledException)
        {
            _log.Warn("激光同步监控被取消");
        }
        finally
        {
            _log.Info($"监控结束：完成 {fired.Count}/{targets.Count} 孔");
        }
        return fired.Count;
    }
}
