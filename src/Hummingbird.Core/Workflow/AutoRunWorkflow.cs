using System.Text;

namespace Hummingbird.Core;

/// <summary>
/// 自动加工流程编排：拍照定位 → 坐标计算 → 程序下发 → 到位触发出光。
/// 只依赖抽象接口（ICamera/IVision/ICnc/ILaser），不依赖任何具体设备实现——
/// 换相机/换数控/换激光器只换 Devices 层，本类零改动。
///
/// 完整加工时序（现场操作视角）：
///   [人工装卡] → 上位机拍照定位 → 算25孔机床坐标 → 生成SPF程序下发
///   → [人工在NC面板按启动] → 本类轮询轴位置，到位一孔触发出光一孔 → [人工下料]
/// </summary>
public sealed class AutoRunWorkflow
{
    private readonly IAppLogger _log;
    private readonly ICameraService? _camera;
    private readonly IVisionService _vision;
    private readonly ICncService _cnc;
    private readonly ILaserService _laser;

    /// <summary>九点标定结果（像素→机床坐标）。为空或无效时 ComputeTargets 会拒绝执行。</summary>
    public CalibrationResult? Calibration { get; set; }

    /// <summary>孔位几何模板（表盘坐标系，+Y=12H），来自 templates\dial_25holes.json。</summary>
    public HolePatternTemplate Template { get; set; } = new();

    /// <summary>视觉参数（预期外径/名义零位角等），用于定位结果的尺寸校验。</summary>
    public VisionOptions VisionOptions { get; set; } = new();

    /// <summary>表盘系→机床系的轴向约定（SwapXY/取反/附加旋转），现场标定时确定。</summary>
    public TransformConvention Convention { get; set; } = new();

    /// <summary>到位判定容差（mm）。轴位置与目标孔位距离在此范围内即认为到位，触发出光。</summary>
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
            throw new InvalidOperationException("相机不可用（未安装 pylon SDK 或未配置，详见 Hummingbird.Devices.Basler/README.md）");

        _log.Info("【步骤1】采集图像 ...");
        var frame = await _camera.GrabAsync(5000);

        _log.Info("【步骤2】视觉定位（找中心 + 找基准小圆 12H）...");
        var pose = await _vision.LocateAsync(frame, VisionOptions, ct);
        if (pose.Success)
            _log.Info($"【步骤2】定位成功：中心=({pose.CenterPxX:F1},{pose.CenterPxY:F1})px 半径={pose.OuterRadiusPx:F0}px 转角={pose.AngleOffsetDeg:F2}° 置信度={pose.Confidence:F2}");
        else
        {
            _log.Error("【步骤2】定位失败：" + pose.Detail);
            try   // 失败现场存图：远程离线分析全靠它（无它只能猜）
            {
                var dir = Path.Combine(AppContext.BaseDirectory, "dumps");
                Directory.CreateDirectory(dir);
                var p = Path.Combine(dir, $"fail_{DateTime.Now:HHmmss_fff}.bmp");
                SaveGrayBmp(p, frame);
                _log.Warn($"已保存失败现场图：{p}（发给开发做离线复现）");
            }
            catch (Exception ex) { _log.Warn("失败现场图保存失败：" + ex.Message); }
        }
        return (pose, frame);
    }

    /// <summary>纯 C# 写 8 位灰度 BMP（不引第三方依赖，PS/画图/OpenCV 都能开）。</summary>
    private static void SaveGrayBmp(string path, CameraFrame f)
    {
        int w = f.Width, h = f.Height;
        int stride = (w + 3) / 4 * 4;               // 行按 4 字节对齐
        int dataOffset = 14 + 40 + 256 * 4;          // 文件头+信息头+灰度调色板
        int fileSize = dataOffset + stride * h;
        var buf = new byte[fileSize];
        buf[0] = (byte)'B'; buf[1] = (byte)'M';
        BitConverter.GetBytes(fileSize).CopyTo(buf, 2);
        BitConverter.GetBytes(dataOffset).CopyTo(buf, 10);
        BitConverter.GetBytes(40).CopyTo(buf, 14);
        BitConverter.GetBytes(w).CopyTo(buf, 18);
        BitConverter.GetBytes(h).CopyTo(buf, 22);    // 正值=行序自下而上
        BitConverter.GetBytes((short)1).CopyTo(buf, 26);
        BitConverter.GetBytes((short)8).CopyTo(buf, 28);
        BitConverter.GetBytes(stride * h).CopyTo(buf, 34);
        BitConverter.GetBytes(3780).CopyTo(buf, 38);
        BitConverter.GetBytes(3780).CopyTo(buf, 42);
        BitConverter.GetBytes(256).CopyTo(buf, 46);
        BitConverter.GetBytes(256).CopyTo(buf, 50);
        for (int i = 0; i < 256; i++)               // 256 灰阶调色板
            buf[dataOffset - 1024 + i * 4] = buf[dataOffset - 1024 + i * 4 + 1] = buf[dataOffset - 1024 + i * 4 + 2] = (byte)i;
        for (int y = 0; y < h; y++)
            Buffer.BlockCopy(f.Gray, y * w, buf, dataOffset + (h - 1 - y) * stride, w);
        File.WriteAllBytes(path, buf);
    }

    /// <summary>
    /// 模板孔位 + 定位结果 + 标定 → 每个孔的机床坐标。
    ///
    /// 变换链（理解这个链就理解了整个坐标体系）：
    ///   孔(表盘系,mm) --装卡转角θ顺时针旋转--> --轴向约定(Convention)--> + 零件中心(机床系,mm)
    ///     = 该孔的机床坐标 → 写进 SPF 程序的 G0 X.. Y..
    /// 其中零件中心(机床系) = Calibration.Apply(像素中心) 再过 Convention。
    /// </summary>
    public List<HoleTarget> ComputeTargets(PartPose pose)
    {
        if (Calibration is not { Valid: true })
            throw new InvalidOperationException("未标定或标定无效，请先在\"标定\"页完成九点标定");
        if (Template.Holes.Count == 0)
            throw new InvalidOperationException("未加载孔位模板");

        // 零件中心的机床坐标：像素 → (仿射标定) → 机床原始 → (轴向约定) → 机床最终
        var (rawCx, rawCy) = Calibration.Apply(pose.CenterPxX, pose.CenterPxY);
        var (cx, cy) = Convention.Apply(rawCx, rawCy);

        double t = Math.PI / 180 * pose.AngleOffsetDeg;   // 装卡转角（顺时针为正）
        double c = Math.Cos(t), s = Math.Sin(t);

        var targets = new List<HoleTarget>(Template.Holes.Count);
        foreach (var h in Template.Holes)
        {
            // 表盘系内顺时针旋转 θ（+Y=12H 朝上、顺时针为正 → 数学上是旋转 -θ，即此处的矩阵形式）
            double rx = h.X * c + h.Y * s;
            double ry = -h.X * s + h.Y * c;
            var (hx, hy) = Convention.Apply(rx, ry);
            targets.Add(new HoleTarget(h.Id, cx + hx, cy + hy, h.DiameterMm, h.Clock));
        }
        _log.Info($"【步骤3】目标坐标计算完成：{targets.Count}孔，零件中心(机床)=({cx:F3},{cy:F3})mm，装卡转角={pose.AngleOffsetDeg:F2}°");
        return targets;
    }

    /// <summary>
    /// 生成 SINUMERIK 828D 刻孔定位程序（.SPF 文本）。
    /// 设计原则：NC 只负责"移动到孔位 + 停顿"，激光出光由上位机在停顿窗口内触发——
    /// 这样不用改机床 PLC，也不需要 NC 侧激光握手信号。
    /// 停顿 0.8s > 出光脉冲串（毫秒级），窗口足够。
    /// </summary>
    public string GenerateSpf(IEnumerable<HoleTarget> targets, string? partName = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine($";HM_DIAL.SPF  自动生成 {DateTime.Now:yyyy-MM-dd HH:mm:ss}  {partName ?? Template.PartName}");
        sb.AppendLine(";激光出光由上位机在停顿窗口内触发，本程序只负责定位");
        sb.AppendLine("N10 G90 G71");                              // 绝对坐标 + 公制
        int n = 20, i = 1, count = targets.Count();
        foreach (var t in targets)
        {
            sb.AppendLine($"N{n} G0 X={t.MachineX:F4} Y={t.MachineY:F4}   ;孔{i}/{count} #{t.Id} {t.Clock}");
            sb.AppendLine($"N{n + 10} G4 F0.8");                     // 停顿 0.8s = 上位机出光窗口
            n += 20; i++;
        }
        sb.AppendLine($"N{n} M2");                                   // 程序结束
        _log.Info($"【步骤4】SPF 程序生成：{count} 孔，{n / 10 + 1} 条 NC 段");
        return sb.ToString();
    }

    /// <summary>计算并下发程序（ComputeTargets + GenerateSpf + Upload 一步到位）。</summary>
    public async Task<bool> DeployProgramAsync(PartPose pose, string name = "HM_DIAL.SPF", CancellationToken ct = default)
    {
        var targets = ComputeTargets(pose);
        var spf = GenerateSpf(targets);
        bool ok = await _cnc.UploadProgramAsync(name, spf, ct);
        _log.Info(ok ? $"【步骤5】程序已下发：{name}（{targets.Count}孔）——请操作工在NC面板选择并启动该程序" : "【步骤5】程序下发失败，请检查连接与共享路径");
        return ok;
    }

    /// <summary>取消激光同步监控（安全停：只停监控，正在出的光会正常关掉）。</summary>
    public void CancelRun()
    {
        _runCts?.Cancel();
        _log.Warn("已请求取消激光同步监控");
    }

    /// <summary>
    /// 【步骤6】激光同步监控：轮询机床轴位置（100ms 周期），到位（容差内）即触发出光，逐孔推进。
    /// 调用时机：操作工在 NC 侧按下程序启动之后。
    /// 结束条件：全部孔完成 / 被取消。
    /// </summary>
    public async Task<int> RunLaserSyncAsync(IReadOnlyList<HoleTarget> targets, CancellationToken ct = default)
    {
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _runCts.Token;
        var fired = new HashSet<int>();   // 已出光孔的 Id 集合（防重复触发）
        _log.Info($"【步骤6】激光同步监控开始：{targets.Count}孔，到位容差 {ArrivalToleranceMm}mm（等待NC启动...）");

        try
        {
            while (!token.IsCancellationRequested && fired.Count < targets.Count)
            {
                await Task.Delay(100, token);                     // 100ms 轮询一次轴位置
                var st = await _cnc.GetStatusAsync(token);
                var next = targets.FirstOrDefault(t => !fired.Contains(t.Id));   // 下一个待打孔
                if (next is null) break;

                bool arrived = Math.Abs(st.PosX - next.MachineX) <= ArrivalToleranceMm
                            && Math.Abs(st.PosY - next.MachineY) <= ArrivalToleranceMm;
                if (!arrived) continue;                           // 还没到位，继续等

                fired.Add(next.Id);
                _log.Info($"【步骤6】孔#{next.Id}（{next.Clock}）到位 → 出光（进度 {fired.Count}/{targets.Count}）");
                await _laser.FireBurstAsync();                    // 打孔：出光→脉冲串→关光
            }
        }
        catch (OperationCanceledException)
        {
            _log.Warn("【步骤6】激光同步监控被取消");
        }
        finally
        {
            _log.Info($"【步骤6】监控结束：完成 {fired.Count}/{targets.Count} 孔" +
                      (fired.Count < targets.Count ? "（有孔未完成！检查NC是否执行完/是否被取消）" : "，全部完成 ✅"));
        }
        return fired.Count;
    }
}
