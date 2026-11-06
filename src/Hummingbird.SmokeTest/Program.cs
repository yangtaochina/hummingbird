using Hummingbird.Core;
using Hummingbird.Devices.OpcUa;
using Hummingbird.Devices.Sim;
using Hummingbird.Vision;

var log = new MemoryLogger();
log.LineAdded += (lvl, msg) => Console.WriteLine($"  [{lvl}] {msg}");

var template = HolePatternTemplate.LoadJson("templates/dial_18holes.json");
var cam = new SimCameraService
{
    Template = template,
    // 工件自带的定位孔组（角向基准）：名义角 + 转角 = 实测角
    DatumHoleAngles = template.DatumHoleAnglesDeg.ToArray(),
    DatumHoleRadiusMm = 14.45,
    DatumHoleDiameterMm = 0.30
};
var cnc = new SimCncService();
var laser = new SimLaserService();
var wf = new AutoRunWorkflow(log, cam, new DialPositioning(log), cnc, laser)
{
    Template = template,
    VisionOptions = new VisionOptions
    {
        ExpectedOuterDiameterMm = template.PartOuterDiameterMm,
        NominalDatumAngleDeg = template.NominalDatumAngleDeg,
        DatumMode = template.DatumMode,               // 模板指向 datumHoles（外环自带定位孔组）
        DatumCircleDiameterMm = template.DatumCircleDiameterMm,
        DatumHoleAngles = template.DatumHoleAnglesDeg.ToArray(),
        DatumHoleRadiusMm = template.DatumHoleRadiusMm,
        // 人工装夹粗对齐：操作工把工件大致按 12H 摆好（模拟 ±5° 随机偏差）
        CoarseAlignDeg = 0.0
    },
};

int fails = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "[PASS] " : "[FAIL] ") + what);
    if (!ok) fails++;
}

Console.WriteLine("== 1) 九点标定求解器（带旋转真值 + 2px 噪声）==");
{
    double s = 0.02, ca = Math.Cos(0.005), sa = Math.Sin(0.005);
    double A = s * ca, B = -s * sa, C = 3.7, D = s * sa, E = -s * ca, F = -1.2;
    var rnd = new Random(7);
    var pairs = new List<CalibPointPair>();
    for (int i = 0; i < 9; i++)
    {
        double px = 300 + (i % 3) * 800 + (rnd.NextDouble() * 4 - 2);
        double py = 200 + (i / 3) * 700 + (rnd.NextDouble() * 4 - 2);
        pairs.Add(new CalibPointPair(px, py, A * px + B * py + C, D * px + E * py + F));
    }
    var res = new AffineCalibrationSolver().Fit(pairs);
    Check(res.Valid, $"标定有效，RMS={res.RmsErrorMm:F5} mm");
}

Console.WriteLine("== 2) 视觉定位精度（5 个随机位姿 vs 合成图真值）==");
PartPose pose = new(false, 0, 0, 0, 0, 0, 0, "-");
{
    double s = 1 / cam.PixelSizeMm;
    double maxCenterErr = 0, maxAngErr = 0;
    bool allOk = true;
    for (int i = 0; i < 5; i++)
    {
        var (p, _) = await wf.GrabAndLocateAsync();
        allOk &= p.Success;
        double expX = cam.Width / 2.0 + cam.LastOffsetXmm * s;
        double expY = cam.Height / 2.0 - cam.LastOffsetYmm * s;
        double ce = Math.Max(Math.Abs(p.CenterPxX - expX), Math.Abs(p.CenterPxY - expY));
        double ae = Math.Abs(p.AngleOffsetDeg - cam.LastRotationDeg);
        maxCenterErr = Math.Max(maxCenterErr, ce);
        maxAngErr = Math.Max(maxAngErr, ae);
        if (i == 0) pose = p;   // 保留一个位姿给后续测试
        Console.WriteLine($"    位姿{i + 1}: 中心误差 {ce:F2}px, 角度误差 {ae:F2}°, 置信度 {p.Confidence:F2}");
    }
    Check(allOk, "5 个位姿全部定位成功");
    Check(maxCenterErr < 3, $"中心最大误差 {maxCenterErr:F2}px < 3px");
    Check(maxAngErr < 1.5, $"角度最大误差 {maxAngErr:F2}° < 1.5°");
}

Console.WriteLine("== 3) 全链路坐标（标定=真值映射, Convention=identity）==");
{
    (pose, _) = await wf.GrabAndLocateAsync();   // 用最新位姿，与 cam.Last* 真值对齐
    double pxs = cam.PixelSizeMm;
    wf.Calibration = new CalibrationResult(true, 0.0001,
        pxs, 0, -cam.Width / 2.0 * pxs,
        0, -pxs, cam.Height / 2.0 * pxs);
    var targets = wf.ComputeTargets(pose);
    Check(targets.Count == template.Holes.Count, $"目标孔数 = {targets.Count}（模板 {template.Holes.Count}）");
    double rot = Math.PI / 180 * cam.LastRotationDeg;
    double c = Math.Cos(rot), si = Math.Sin(rot);
    double cxM = cam.LastOffsetXmm, cyM = cam.LastOffsetYmm;
    double maxErr = 0;
    foreach (var t in targets)
    {
        var h = template.Holes.First(x => x.Id == t.Id);
        double ex = cxM + h.X * c + h.Y * si;
        double ey = cyM + -h.X * si + h.Y * c;
        maxErr = Math.Max(maxErr, Math.Max(Math.Abs(t.MachineX - ex), Math.Abs(t.MachineY - ey)));
    }
    Check(maxErr < 0.05, $"{template.Holes.Count}孔坐标最大误差 {maxErr:F4} mm < 0.05 mm");
}

Console.WriteLine("== 4) 端到端：下发 → 模拟NC走位 → 激光同步触发 ==");
{
    var targets = wf.ComputeTargets(pose);
    await cnc.ConnectAsync("sim://test");
    bool ok = await wf.DeployProgramAsync(pose);
    Check(ok && cnc.Targets.Count == template.Holes.Count, $"程序下发并解析出 {cnc.Targets.Count} 个定位点");
    cnc.StartSimulatedRun();
    int fired = await wf.RunLaserSyncAsync(targets);
    Check(fired == template.Holes.Count, $"激光触发 {fired}/{template.Holes.Count} 孔");
    Check(laser.FireCount == template.Holes.Count, $"激光出光计数 {laser.FireCount} = {template.Holes.Count}");
}

Console.WriteLine("== 5) 角向多解与消歧（8 孔 90° 对称 → 4 个等效转角）==");
{
    // (a) 不提供粗对齐角 → 必须拒绝（不能瞎猜一个）
    wf.VisionOptions = wf.VisionOptions.WithCoarseAlign(double.NaN);
    var (pNo, _) = await wf.GrabAndLocateAsync();
    Check(!pNo.Success && pNo.Detail.Contains("等效解"),
          $"未给粗对齐角时拒绝定位并说明多解（实际: {(pNo.Success ? "竟然成功了" : pNo.Detail.Substring(0, Math.Min(60, pNo.Detail.Length)))})");

    // (b) 给出粗对齐角 → 4 个候选，选中最接近的那个
    wf.VisionOptions = wf.VisionOptions.WithCoarseAlign(0.0);
    var (pYes, _) = await wf.GrabAndLocateAsync();
    Check(pYes.Success, "给出粗对齐角后定位成功");
    Check(pYes.AngleCandidatesDeg.Count == 4,
          $"识别出 4 个等效转角（实际 {pYes.AngleCandidatesDeg.Count}）");
    if (pYes.AngleCandidatesDeg.Count == 4)
    {
        var sorted = pYes.AngleCandidatesDeg.OrderBy(x => x).ToList();
        double maxGap = 0;
        for (int i = 0; i < sorted.Count; i++)
        {
            double d = sorted[(i + 1) % sorted.Count] - sorted[i];
            if (d < 0) d += 360;
            maxGap = Math.Max(maxGap, Math.Abs(d - 90));
        }
        Check(maxGap < 1.0, $"4 个候选彼此间隔 90°（最大偏差 {maxGap:F3}°）");
        Check(pYes.AngleCandidatesDeg.Any(c => AngleDiff(c, pYes.AngleOffsetDeg) < 20),
              $"选定转角 {pYes.AngleOffsetDeg:F2}° 落在候选集内（归一化到±180°后比较）");
    }

    // (c) 粗对齐角指向另一个候选 → 必须切到那个候选（而不是始终选最近的）
    double alt = pYes.AngleCandidatesDeg.Count >= 2
        ? pYes.AngleCandidatesDeg.OrderBy(x => x).Skip(1).First()
        : 90;
    wf.VisionOptions = wf.VisionOptions.WithCoarseAlign(alt);
    var (pAlt, _) = await wf.GrabAndLocateAsync();
    Check(pAlt.Success && AngleDiff(pAlt.AngleOffsetDeg, alt) < 15,
          $"粗对齐角={alt:F1}° 时选中对应候选（实际 {pAlt.AngleOffsetDeg:F2}°）");
}

// ============================================================
// 【组6】视野诊断 FieldDiagnosis（2026-11-05 现场必做的一步）
//
// 标尺 = 白色外环（Ø34.2mm，图纸已知）。
// 判据用**垂直方向**（图幅 1920×1200 横图，垂直半高 600px 才是限制）。
// ============================================================
{
    var fdiag = new FieldDiagnosis(log);

    // 情形 A：视野足够。合成 Ø34.2mm 外环（半径 560px）→ mm/px = 17.1/560 = 0.03054
    //         画面 1920×1200 → 垂直半高 600px = 18.3mm ≥ 14.45mm → 在视野内
    var frameA = MakeFrameWithOuterRing(1920, 1200, 560.0);
    var rA = fdiag.Analyze(frameA, 14.45);
    Check(rA.Success, $"视野诊断A：成功量到白色外环（{rA.ScalePx:F1}px 直径）");
    Check(rA.Success && Math.Abs(rA.MmPerPx - 0.030536) < 0.002,
          $"视野诊断A：反算 mm/px 正确（实际 {rA.MmPerPx:F5}，期望 0.03054）");
    Check(rA.DatumInView, $"视野诊断A：判定基准孔在视野内（余量 {rA.MarginMm:F2}mm）");

    // 情形 B：视野太小。合成 Ø34.2mm 外环（半径 800px）→ mm/px = 17.1/800 = 0.021375
    //         画面 1920×1200 → 垂直半高 600px = 12.8mm < 14.45mm → 基准孔不在视野内
    var frameB = MakeFrameWithOuterRing(1920, 1200, 800.0);
    var rB = fdiag.Analyze(frameB, 14.45);
    Check(rB.Success, $"视野诊断B：成功量到白色外环（{rB.ScalePx:F1}px 直径）");
    Check(rB.Success && !rB.DatumInView,
          $"视野诊断B：判定基准孔**不在**视野内（超 {Math.Abs(rB.MarginMm):F2}mm）");

    // 情形 C：画面全黑 → 必须明确报"没量到"，而不是给出瞎猜的 mm/px
    var frameC = new CameraFrame(new byte[1200 * 1920], 1920, 1200, DateTime.Now);
    var rC = fdiag.Analyze(frameC, 14.45);
    Check(!rC.Success, "视野诊断C：全黑画面明确报失败（不瞎猜 mm/px）");

    // 情形 D：过曝全白 → 同样必须报失败（不能当成"结构完整"）
    var frameD = new CameraFrame(new byte[1920 * 1200].Select(_ => (byte)255).ToArray(), 1920, 1200, DateTime.Now);
    var rD = fdiag.Analyze(frameD, 14.45);
    Check(!rD.Success, "视野诊断D：全白过曝画面明确报失败");

    // ---------- 情形 E：真机实测图离线复现（决定性验证） ----------
    // 真图：Image__2026-11-05__14-04-13.bmp（曝光 3114μs）
    // Python 离线定量分析结论：白色外环 R ≈ 700px → mm/px ≈ 0.0244，视野 46.9×29.3mm
    //                        基准孔 R14.45mm = 592px，垂直半高 600px → 勉强在视野内
    // 这张图验证了：①外环能被测到 ②mm/px 量级正确 ③"边缘余量"这个结论能自动算出来
    string realImg = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..",
                                  "Image__2026-11-05__14-04-13.bmp");
    realImg = Path.GetFullPath(realImg);
    if (File.Exists(realImg))
    {
        var realFrame = ReadGrayBmp(realImg);
        var rE = fdiag.Analyze(realFrame, 14.45);
        Console.WriteLine("  --- 真机图诊断结果 ---");
        Console.WriteLine("  " + rE.Detail.Replace("\n", "\n  "));
        Check(rE.Success, "视野诊断E：真机图成功量到标尺");
        Check(rE.Success && rE.MmPerPx > 0.020 && rE.MmPerPx < 0.030,
              $"视野诊断E：mm/px 量级正确（实际 {rE.MmPerPx:F6}，应在 0.020~0.030 之间）");
        Check(rE.Success && rE.FovWidthMm > 40 && rE.FovWidthMm < 60,
              $"视野诊断E：视野宽量级正确（实际 {rE.FovWidthMm:F1}mm，应在 40~60mm 之间）");
        Check(rE.Success && rE.DatumInView && rE.MarginMm < 2.0,
              $"视野诊断E：判定基准孔在视野内但余量 < 2mm（实际 {rE.MarginMm:F2}mm）——边缘状态必须暴露出来");
        // 2026-11-05 真图决定性结论：基准孔"在视野内"但极坐标展开后环带对比度仅 5/255，
        // 8 个双环孔在红光下**完全不可见**。这条断言防止有人把 DatumVisible 判据改松。
        Check(rE.Success && !rE.DatumVisible,
              "视野诊断E：判定基准孔在视野内但**实际不可见**（红光下无反差）——必须与'在视野内'区分开");
    }
    else
    {
        Console.WriteLine($"  [跳过] 真机图不存在：{realImg}");
    }
}

Console.WriteLine("== 7) 定位策略模式（客户 2026-11-05 提议：单标记孔 + 夹具锁心）==");
{
    var strategies = new IPositioningStrategy[]
    {
        new FullFieldDatumHolesStrategy(log),
        new SingleFiducialStrategy(log),
        new ManualFixtureStrategy(log)
    };
    var sel = new PositioningStrategySelector(strategies);

    // 7.1 最坏现场：什么前提都没有 → 一个策略都不该可用
    var worst = StrategyContext.WorstCase;
    var (recWorst, _) = sel.Recommend(worst);
    Check(sel.All.All(s => !s.Check(worst).CanRun),
          "策略7.1：现场前提全缺时，三个策略全部拒绝执行（不会误出坐标）");
    Check(recWorst is null, "策略7.1：前提全缺时不推荐任何策略");

    // 7.2 整盘条件齐备 → 优先推荐现役整盘视觉（精度最高）
    var fullOk = worst with
    {
        FullPartInView = true, DatumHolesVisible = true,
        CalibrationValid = true, CoarseAlignProvided = true
    };
    var (recFull, _) = sel.Recommend(fullOk);
    Check(recFull?.Id == StrategyIds.FullFieldDatumHoles,
          "策略7.2：整盘入画+基准孔可见+已标定时，自动推荐『①整盘视觉定位』");

    // 7.3 真机现状（整盘不入画、基准孔不可见）+ 客户新方案前提齐备 → 推荐单标记孔
    var siteNow = worst with
    {
        FixtureCentersPart = true, FiducialAvailable = true,
        FiducialRotatesWithPart = true, FiducialTaught = true,
        CalibrationValid = true
    };
    var (recFid, _) = sel.Recommend(siteNow);
    Check(recFid?.Id == StrategyIds.SingleFiducial,
          "策略7.3：整盘方案失效但夹具能锁心+有标记孔时，自动推荐『②单标记孔』");
    Check(sel.Find(StrategyIds.FullFieldDatumHoles)!.Check(siteNow).CanRun == false,
          "策略7.3：整盘方案在当前真机条件下明确不可用（不会被误选）");

    // 7.4 标记孔固定在夹具上（不随工件转）→ 单标记孔必须拒绝，否则转角永远测不出
    var fixedMark = siteNow with { FiducialRotatesWithPart = false };
    Check(!sel.Find(StrategyIds.SingleFiducial)!.Check(fixedMark).CanRun,
          "策略7.4：标记孔不随工件转动时，单标记孔策略拒绝执行（数学上测不出转角）");

    // 7.5 夹具没锁心 → 单标记孔必须拒绝（2 个约束 < 3 个自由度，欠定）
    var noFixture = siteNow with { FixtureCentersPart = false };
    Check(!sel.Find(StrategyIds.SingleFiducial)!.Check(noFixture).CanRun,
          "策略7.5：夹具没锁死中心时，单标记孔策略拒绝执行（单孔数学上欠定）");

    // 7.6 单标记孔角度计算：合成「暗底 + 亮圆孔」，孔相对夹具中心顺时针转 30°
    const double mmPerPx = 0.1;        // 测试用放大值，只为让 145px ↔ 14.5mm 对得上
    const double fidR = 14.5;
    const double cx0 = 200, cy0 = 200;
    double rPx = fidR / mmPerPx;       // 145px
    double a = 30 * Math.PI / 180;     // 顺时针 30°
    double hx = cx0 + rPx * Math.Sin(a), hy = cy0 - rPx * Math.Cos(a);
    var opt = new VisionOptions
    {
        FixtureCenterPxX = cx0, FixtureCenterPxY = cy0,
        PixelSizeMm = mmPerPx, FiducialRadiusMm = fidR,
        FiducialDiameterMm = 0.8, FiducialRadiusToleranceMm = 1.5,
        FiducialNominalClockDeg = 0, StrategyId = StrategyIds.SingleFiducial
    };
    var frame30 = MakeFiducialFrame(400, 400, hx, hy, 8);
    var pose30 = await new SingleFiducialStrategy(log).LocateAsync(frame30, opt);
    Check(pose30.Success, "策略7.6：合成单孔图成功定位");
    Check(pose30.Success && AngleDiff(pose30.AngleOffsetDeg, 30) < 1.0,
          $"策略7.6：装卡转角算对（实际 {pose30.AngleOffsetDeg:F2}°，期望 30°）");
    Check(pose30.Success && Math.Abs(pose30.CenterPxX - cx0) < 0.01 && Math.Abs(pose30.CenterPxY - cy0) < 0.01,
          "策略7.6：中心取夹具锁定值（不需要出现在画面里）");

    // 7.7 转到 -75°（跨象限）也要算对
    double a2 = -75 * Math.PI / 180;
    double hx2 = cx0 + rPx * Math.Sin(a2), hy2 = cy0 - rPx * Math.Cos(a2);
    var poseNeg = await new SingleFiducialStrategy(log).LocateAsync(
        MakeFiducialFrame(400, 400, hx2, hy2, 8), opt);
    Check(poseNeg.Success && AngleDiff(poseNeg.AngleOffsetDeg, -75) < 1.0,
          $"策略7.7：负角度/跨象限也算对（实际 {poseNeg.AngleOffsetDeg:F2}°，期望 -75°）");

    // 7.8 孔心距离超差（工件没装到位）→ 必须拒绝，避免打废工件
    var optBad = opt with { FiducialRadiusMm = 30.0 };
    var poseBad = await new SingleFiducialStrategy(log).LocateAsync(frame30, optBad);
    Check(!poseBad.Success && poseBad.Detail.Contains("偏差"),
          "策略7.8：孔心到夹具中心距离超差时拒绝出坐标（防打废工件）");

    // 7.9 夹具中心未标定 → 拒绝
    var poseNoCal = await new SingleFiducialStrategy(log).LocateAsync(
        frame30, opt with { FixtureCenterPxX = double.NaN });
    Check(!poseNoCal.Success, "策略7.9：夹具中心像素未标定时拒绝执行");

    // 7.10 全白过曝 → 找不到孔，明确失败
    var poseWhite = await new SingleFiducialStrategy(log).LocateAsync(
        MakeSolidFrame(400, 400, 255), opt);
    Check(!poseWhite.Success, "策略7.10：全白过曝画面明确失败（不瞎猜孔位）");

    // 7.11 端到端：运行时切换策略 → ComputeTargets 仍算出 18 个孔，且按转角旋转
    var wfSwitch = new AutoRunWorkflow(log, cam, strategies[0], cnc, laser)
    {
        Template = template,
        VisionOptions = opt,
        Calibration = new CalibrationResult(true, 0, mmPerPx, 0, 0, 0, mmPerPx, 0)
    };
    Check(ReferenceEquals(wfSwitch.Vision, strategies[0]),
          "策略7.11：工作流初始注入整盘策略");
    wfSwitch.Vision = strategies[1];      // 运行时切换，不用重建 workflow
    Check(ReferenceEquals(wfSwitch.Vision, strategies[1]),
          "策略7.11：运行时可切换到单标记孔策略（流程编排零改动）");
    var targets = wfSwitch.ComputeTargets(pose30);
    Check(targets.Count == 18, $"策略7.11：单标记孔位姿算出全部 18 个孔（实际 {targets.Count}）");
    // ComputeTargets：中心 = Calibration.Apply(夹具中心像素) = (20,20)mm，
    // 模板孔偏移是 mm 直接旋转相加（不再乘 mm/px）。角度用 pose 实测值，
    // 这样本项验证的是"坐标链按 pose 转角正确旋转"，角度精度由 7.6 单独验证。
    var h1 = template.Holes[0];
    double t1 = pose30.AngleOffsetDeg * Math.PI / 180;
    double c1 = Math.Cos(t1), s1 = Math.Sin(t1);
    double expX = 20 + (h1.X * c1 + h1.Y * s1);
    double expY = 20 + (-h1.X * s1 + h1.Y * c1);
    Check(Math.Abs(targets[0].MachineX - expX) < 0.02 && Math.Abs(targets[0].MachineY - expY) < 0.02,
          $"策略7.11：第 1 孔坐标按装卡转角正确旋转（得到 {targets[0].MachineX:F3},{targets[0].MachineY:F3}，" +
          $"期望 {expX:F3},{expY:F3}）");

    // 7.12 纯机械对刀兜底
    var poseManual = await new ManualFixtureStrategy(log).LocateAsync(frame30,
        opt with { FixtureAssumedAngleDeg = 12.0 });
    Check(poseManual.Success && Math.Abs(poseManual.AngleOffsetDeg - 12) < 0.01 && poseManual.Confidence < 0.5,
          "策略7.12：纯机械对刀按人工转角输出，且置信度故意压低以提醒是兜底方案");
}

/// <summary>
/// 读取 8 位灰度 BPM（Windows 拍摄/保存的常见格式），不依赖 OpenCV 的文件 IO API。
/// 支持 BITMAPINFOHEADER（40 字节）；行数据按 4 字节对齐，行序自下而上。
/// </summary>
static CameraFrame ReadGrayBmp(string path)
{
    byte[] d = File.ReadAllBytes(path);
    if (d.Length < 54 || d[0] != 'B' || d[1] != 'M')
        throw new InvalidDataException("不是有效的 BMP 文件：" + path);

    int dataOffset = BitConverter.ToInt32(d, 10);
    int width = BitConverter.ToInt32(d, 18);
    int height = BitConverter.ToInt32(d, 22);
    int bpp = BitConverter.ToInt16(d, 28);
    int headerSize = BitConverter.ToInt32(d, 14);

    if (bpp != 8)
        throw new NotSupportedException($"只支持 8 位灰度 BMP，实际 bpp={bpp}");
    if (height < 0) height = -height;          // 负高度 = 自下而上
    bool bottomUp = BitConverter.ToInt32(d, 22) < 0;

    int rowSize = ((width * bpp / 8) + 3) / 4 * 4;   // 4 字节对齐
    var gray = new byte[width * height];

    for (int y = 0; y < height; y++)
    {
        int srcRow = bottomUp ? (height - 1 - y) : y;
        int srcOff = dataOffset + srcRow * rowSize;
        Array.Copy(d, srcOff, gray, y * width, width);
    }

    _ = headerSize;
    return new CameraFrame(gray, width, height, DateTime.Now);
}

/// <summary>合成一张"暗底 + 白色外环（圆心在画面中心）"的灰度图，用于验证 FieldDiagnosis 的外环标尺逻辑。</summary>
static CameraFrame MakeFrameWithOuterRing(int w, int h, double ringRadiusPx)
{
    using var mat = new OpenCvSharp.Mat(h, w, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.All(30));
    // 外环画成较宽的亮带（真实白环在图上是几十像素宽的弧）
    OpenCvSharp.Cv2.Circle(mat, new OpenCvSharp.Point(w / 2, h / 2), (int)ringRadiusPx, OpenCvSharp.Scalar.All(235), 40);
    var buf = new byte[w * h];
    System.Runtime.InteropServices.Marshal.Copy(mat.Data, buf, 0, buf.Length);
    return new CameraFrame(buf, w, h, DateTime.Now);
}

/// <summary>合成一张「暗底 + 一个亮圆孔」的灰度图，用于验证单标记孔策略的角度计算。</summary>
static CameraFrame MakeFiducialFrame(int w, int h, double hx, double hy, int radius)
{
    using var mat = new OpenCvSharp.Mat(h, w, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.All(30));
    OpenCvSharp.Cv2.Circle(mat, new OpenCvSharp.Point((int)Math.Round(hx), (int)Math.Round(hy)),
        radius, OpenCvSharp.Scalar.All(235), -1);
    var buf = new byte[w * h];
    System.Runtime.InteropServices.Marshal.Copy(mat.Data, buf, 0, buf.Length);
    return new CameraFrame(buf, w, h, DateTime.Now);
}

/// <summary>合成一张纯色灰度图（全黑/全白过曝测试用）。</summary>
static CameraFrame MakeSolidFrame(int w, int h, byte gray)
{
    using var mat = new OpenCvSharp.Mat(h, w, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.All(gray));
    var buf = new byte[w * h];
    System.Runtime.InteropServices.Marshal.Copy(mat.Data, buf, 0, buf.Length);
    return new CameraFrame(buf, w, h, DateTime.Now);
}

/// <summary>
/// 合成「暗底 + N 个亮圆孔」：按 工件中心(cx,cy) + 半径圈 + 装卡转角 放置孔。
/// 图像时钟角 = 名义角 + 装卡转角；12H=0°、顺时针为正，y 轴向下。
/// </summary>
static CameraFrame MakeTwoHoleFrame(int w, int h, double cx, double cy, double ringPx,
                                    double thetaDeg, double[] anglesDeg, int holeR)
{
    using var mat = new OpenCvSharp.Mat(h, w, OpenCvSharp.MatType.CV_8UC1, OpenCvSharp.Scalar.All(30));
    foreach (var a in anglesDeg)
    {
        double ang = (a + thetaDeg) * Math.PI / 180.0;
        double x = cx + ringPx * Math.Sin(ang);
        double y = cy - ringPx * Math.Cos(ang);
        if (x >= holeR && x < w - holeR && y >= holeR && y < h - holeR)
            OpenCvSharp.Cv2.Circle(mat,
                new OpenCvSharp.Point((int)Math.Round(x), (int)Math.Round(y)),
                holeR, OpenCvSharp.Scalar.All(235), -1);
    }
    var buf = new byte[w * h];
    System.Runtime.InteropServices.Marshal.Copy(mat.Data, buf, 0, buf.Length);
    return new CameraFrame(buf, w, h, DateTime.Now);
}

Console.WriteLine("== 8) 多孔自主定位（2026-11-05 现场照片复核：夹具无定位销也能打）==");
{
    const double mmpp = 0.02;
    const double R = 14.45;
    double ringPx = R / mmpp;                 // 722.5px
    double[] angles = { 30, 60, 120, 150, 210, 240, 300, 330 };

    var opt = new VisionOptions
    {
        PixelSizeMm = mmpp,
        DatumHoleAngles = angles,
        DatumHoleRadiusMm = R,
        FiducialDiameterMm = 0.8,
        CoarseAlignDeg = 30.0,
        TwoHoleScaleTolerancePct = 0.20,
        StrategyId = StrategyIds.TwoFiducials
    };
    var strat = new TwoFiducialStrategy(log);
    const double cx = 960, cy = 400;

    // 8.1 三个相邻基准孔入画，装卡转角 30° —— 中心/转角/缩放三项都要算对
    //     第 3 个孔是关键：孔组有 180° 对称，只有它能消掉镜像圆心二义性。
    var frame = MakeTwoHoleFrame(1920, 1200, cx, cy, ringPx, 30.0, new[] { 30.0, 60.0, 120.0 }, 12);
    var p8 = await strat.LocateAsync(frame, opt);
    Check(p8.Success, "策略8.1：合成三孔图成功定位" + (p8.Success ? "" : " —— " + p8.Detail));
    Check(p8.Success && Math.Abs(p8.CenterPxX - cx) < 2 && Math.Abs(p8.CenterPxY - cy) < 2,
          $"策略8.1：中心由弦中垂线 + 第三孔消镜像解出（实际 {p8.CenterPxX:F1},{p8.CenterPxY:F1}，期望 {cx},{cy}）");
    Check(p8.Success && AngleDiff(p8.AngleOffsetDeg, 30) < 1,
          $"策略8.1：装卡转角算对（实际 {p8.AngleOffsetDeg:F2}°，期望 30°）");
    Check(p8.Success && Math.Abs(p8.OuterRadiusPx - ringPx) < 2,
          $"策略8.1：由孔间距反算 mm/px 与标定一致（半径圈 {p8.OuterRadiusPx:F1}px，期望 {ringPx:F1}px）");
    Check(p8.Success && p8.Detail.Contains("✅"),
          "策略8.1：第三孔一致性校验通过（这是排除镜像圆心的证据）");

    // 8.2 工件中心在**画面外**（真实小视野就是这个情况）也要能解出中心
    const double cx2 = 960, cy2 = 1250;
    var frameOut = MakeTwoHoleFrame(1920, 1200, cx2, cy2, ringPx, 0.0, new[] { 300.0, 330.0, 30.0 }, 12);
    var poseOut = await strat.LocateAsync(frameOut, opt with { CoarseAlignDeg = 0.0 });
    Check(poseOut.Success && Math.Abs(poseOut.CenterPxX - cx2) < 2 && Math.Abs(poseOut.CenterPxY - cy2) < 2,
          $"策略8.2：中心在画面外也解得出来（实际 {poseOut.CenterPxX:F1},{poseOut.CenterPxY:F1}，期望 {cx2},{cy2}）");
    Check(poseOut.Success && AngleDiff(poseOut.AngleOffsetDeg, 0) < 1,
          $"策略8.2：跨 0°/360° 的孔角组也算对（实际 {poseOut.AngleOffsetDeg:F2}°）");

    // 8.3 粗对齐角控制 90° 对称消歧（同一张图给 210° 粗对齐 → 选 θ+180）
    var poseCoarse = await strat.LocateAsync(frame, opt with { CoarseAlignDeg = 210.0 });
    Check(poseCoarse.Success && AngleDiff(poseCoarse.AngleOffsetDeg, 210) < 1,
          $"策略8.3：90° 对称消歧受粗对齐角控制（实际 {poseCoarse.AngleOffsetDeg:F2}°，期望 210°）");

    // 8.4 只看见一个孔 → 拒绝（4 约束退化成 2 约束，数学上欠定）
    var oneHole = await strat.LocateAsync(
        MakeTwoHoleFrame(1920, 1200, cx, cy, ringPx, 30.0, new[] { 30.0 }, 12), opt);
    Check(!oneHole.Success && oneHole.Detail.Contains("至少需要 2 个"),
          "策略8.4：只检出一个孔时明确拒绝（单孔解不出中心）");

    // 8.5 mm/px 填得离谱（缩小视野后没重测）→ 所有配对都被尺度自检筛掉，拒绝出坐标
    var badScale = await strat.LocateAsync(frame, opt with { PixelSizeMm = 0.08 });
    Check(!badScale.Success,
          "策略8.5：VisionPixelSizeMm 与实际孔间距尺度完全不符时拒绝出坐标（提醒重测/重标定）");

    // 8.12 只有 2 个孔且没有粗略中心提示 → 必须拒绝（孔组 180° 对称 → 镜像圆心二义性）
    var twoOnly = MakeTwoHoleFrame(1920, 1200, cx, cy, ringPx, 30.0, new[] { 30.0, 60.0 }, 12);
    var twoNoHint = await strat.LocateAsync(twoOnly, opt);
    Check(!twoNoHint.Success && twoNoHint.Detail.Contains("镜像"),
          "策略8.12：只有 2 孔且无中心提示时明确拒绝（两候选圆心相距 27.9mm，选错会整体打偏）");

    // 8.13 只有 2 个孔，但给了粗略中心提示（故意偏 100px/2mm）→ 能选出正确圆心
    var twoWithHint = await strat.LocateAsync(twoOnly,
        opt with { FixtureCenterPxX = cx + 100, FixtureCenterPxY = cy - 80 });
    Check(twoWithHint.Success && Math.Abs(twoWithHint.CenterPxX - cx) < 2 && Math.Abs(twoWithHint.CenterPxY - cy) < 2,
          $"策略8.13：2 孔 + 粗略中心提示（±10mm 即可）也能消镜像（实际 {twoWithHint.CenterPxX:F1},{twoWithHint.CenterPxY:F1}）");

    // 8.6 没填粗对齐角 → 拒绝（避免 90° 等效解打错方位打废工件）
    var noCoarse = await strat.LocateAsync(frame, opt with { CoarseAlignDeg = double.NaN });
    Check(!noCoarse.Success && noCoarse.Detail.Contains("粗对齐"),
          "策略8.6：未填粗对齐角时拒绝执行");

    // 8.7 全白过曝 → 拒绝
    var white = await strat.LocateAsync(MakeSolidFrame(400, 400, 255), opt);
    Check(!white.Success, "策略8.7：全白过曝画面明确失败");

    // 8.8 关键对照：夹具没锁心、没示教、孔在红光下不可见 → 多孔可用，单孔拒绝
    var siteCtx = StrategyContext.WorstCase with
    {
        FiducialRotatesWithPart = true, CalibrationValid = true,
        CoarseAlignProvided = true, TwoHolesInView = true
    };
    Check(strat.Check(siteCtx).CanRun,
          "策略8.8：夹具无定位销/无示教时，多孔策略依然可用（本轮核心结论）");
    Check(!new SingleFiducialStrategy().Check(siteCtx).CanRun,
          "策略8.8：同样现场条件下单孔策略明确拒绝（对照：它必须依赖夹具锁心）");

    // 8.9 自动推荐：现场照片条件（夹具不能锁心）→ 推荐多孔，而不是单孔
    var all4 = new IPositioningStrategy[]
    {
        new FullFieldDatumHolesStrategy(log), new TwoFiducialStrategy(log),
        new SingleFiducialStrategy(log), new ManualFixtureStrategy(log)
    };
    var sel4 = new PositioningStrategySelector(all4);
    var (rec4, _) = sel4.Recommend(siteCtx);
    Check(rec4?.Id == StrategyIds.TwoFiducials,
          "策略8.9：整盘失效 + 夹具无定位销时，自动推荐『④多孔自主定位』");

    // 8.10 多孔前提也没了、但夹具改造好了 → 回落单孔
    var ctxFid = siteCtx with
    {
        TwoHolesInView = false, FixtureCentersPart = true,
        FiducialAvailable = true, FiducialTaught = true
    };
    var (recFid2, _) = sel4.Recommend(ctxFid);
    Check(recFid2?.Id == StrategyIds.SingleFiducial,
          "策略8.10：多孔不可见但夹具已加定位销时，自动回落『②单标记孔』");

    // 8.11 端到端：多孔定位结果喂进 ComputeTargets，18 个孔照样算得出
    var wf8 = new AutoRunWorkflow(log, cam, strat, cnc, laser)
    {
        Template = template,
        VisionOptions = opt,
        Calibration = new CalibrationResult(true, 0, mmpp, 0, 0, 0, mmpp, 0)
    };
    var targets8 = wf8.ComputeTargets(p8);
    Check(targets8.Count == 18,
          $"策略8.11：视觉解出的(画面外中心 + 转角)经标定换算后算出 18 个孔（实际 {targets8.Count}）");
}

// ================= 9) 转台分度（2026-11-06 客户新方案）=================
Console.WriteLine("== 9) 转台分度（人工抄角 / 仿真 / OPC UA 握手）==");
{
    var rOpt = new RotaryTableOptions { MaxStepDeg = 90, SimSpeedDegPerSec = 3600 };

    // 9.1 人工模式：完全不依赖 OPC UA，也能记录每帧对应的角度
    var manual = new SimRotaryTableService(RotaryTableMode.ManualEntry, rOpt);
    Check(manual.IsReady, "转台9.1：人工抄角模式不依赖 OPC UA，永远就绪");
    Check(await manual.SetAngleAsync(30), "转台9.1：抄入 HMI 角度 30° 成功");
    Check(Math.Abs(manual.CurrentAngleDeg - 30) < 1e-6, "转台9.1：当前角 = 30°");
    Check((await manual.ReadAngleAsync()) == 30, "转台9.1：读回角度一致");

    // 9.2 人工模式绝不假装驱动了轴
    var m = await manual.MoveToAsync(60);
    Check(!m.Success && m.Detail.Contains("手动"), "转台9.2：人工模式明确提示需手动转台，不假装到位");

    // 9.3 仿真模式能转到目标角
    var sim = new SimRotaryTableService(RotaryTableMode.Simulated, rOpt);
    var s1 = await sim.MoveToAsync(45);
    Check(s1.Success && Math.Abs(s1.ActualAngleDeg - 45) < 1e-6, "转台9.3：仿真转到 45° 并确认到位");

    // 9.4 单次转角安全联锁
    var s2 = await sim.MoveToAsync(300);
    Check(!s2.Success && s2.Detail.Contains("安全联锁"), "转台9.4：单次转角超 90° 被安全联锁拒绝（防飞车）");

    // 9.5 角度范围校验
    Check(!await sim.SetAngleAsync(99999), "转台9.5：超出允许角度范围被拒绝");

    // 9.6 OPC UA 节点未核实 → 明确拒绝，绝不臆造 NodeId
    var fake0 = new FakeVarAccess();
    var unconfigured = new OpcUaRotaryTableService(fake0, new RotaryTableOptions());
    Check(!unconfigured.IsReady && unconfigured.ReadinessDetail.Contains("UaExpert"),
          "转台9.6：节点路径未核实时拒绝动作，并提示用 UaExpert 展开确认");
    var rFail = await unconfigured.MoveToAsync(30);
    Check(!rFail.Success && fake0.Writes.Count == 0, "转台9.6：未配置时 MoveTo 不产生任何写入");

    // 9.7 配置齐全 + 假机床：验证「R参数 → 触发 → 到位 → 复位」完整握手序列
    var optAuto = new RotaryTableOptions
    {
        TargetRNumber = 90,
        TriggerNode = "/Plc/DB99.DBX0.0",
        DoneNode = "/Plc/DB99.DBX0.1",
        ActualAngleNode = "/Channel/GeometricAxis/actToolBasePos[u1, 4]",
        PollIntervalMs = 1, TimeoutMs = 500, ToleranceDeg = 0.05
    };
    var fake2 = new FakeVarAccess { Angle = 30 };
    var auto = new OpcUaRotaryTableService(fake2, optAuto);
    Check(auto.IsReady, "转台9.7：节点齐全 + 已连接时自动模式就绪");
    var rOk = await auto.MoveToAsync(30);
    Check(rOk.Success, "转台9.7：自动分度成功" + (rOk.Success ? "" : " —— " + rOk.Detail));
    Check(fake2.Writes.Any(w => w.Path.Contains("R[u1, 90]")), "转台9.7：目标角写进 R90");
    Check(fake2.Writes.Any(w => w.Path == optAuto.TriggerNode && w.Value is true), "转台9.7：写触发位 true");
    Check(fake2.Writes.Any(w => w.Path == optAuto.TriggerNode && w.Value is false), "转台9.7：结束复位触发位 false");

    // 9.8 PLC 报到位但读回角度不对 → 硬校验拒绝（防误判打废工件）
    var fake3 = new FakeVarAccess { Angle = 12.5 };
    var rBad = await new OpcUaRotaryTableService(fake3, optAuto).MoveToAsync(30);
    Check(!rBad.Success && rBad.Detail.Contains("偏差"), "转台9.8：PLC 报到位但角度不对 → 拒绝后续动作");

    // 9.9 触发前完成信号残留 → 拒绝动作
    var fake4 = new FakeVarAccess { Angle = 30, StaleBeforeTrigger = true };
    var rStale = await new OpcUaRotaryTableService(fake4, optAuto).MoveToAsync(30);
    Check(!rStale.Success && rStale.Detail.Contains("残留"), "转台9.9：完成信号残留时拒绝触发（握手异常保护）");
}

static double AngleDiff(double a, double b)
{
    double d = Math.Abs(a - b) % 360;
    return d > 180 ? 360 - d : d;
}

Console.WriteLine(fails == 0 ? "===== ALL PASS =====" : $"===== {fails} 项 FAILED =====");
return fails == 0 ? 0 : 1;

/// <summary>假 SINUMERIK 变量访问：离线验证转台握手序列，不连真机床。</summary>
sealed class FakeVarAccess : ISinumerikVarAccess
{
    public bool IsConnected { get; set; } = true;
    /// <summary>模拟转台实际角度（读回值）。</summary>
    public double Angle { get; set; }
    /// <summary>触发后 PLC「到位」位。</summary>
    public bool DoneAfterTrigger { get; set; } = true;
    /// <summary>触发前「到位」位就为 true（模拟上一次握手残留）。</summary>
    public bool StaleBeforeTrigger { get; set; }
    public List<(string Path, object Value)> Writes { get; } = new();
    private bool _triggered;

    public Task<bool> WriteVarAsync(string nodePath, object value, CancellationToken ct = default)
    {
        Writes.Add((nodePath, value));
        if (nodePath.Contains("DBX0.0")) _triggered = value is true;
        return Task.FromResult(true);
    }

    public Task<object?> ReadVarAsync(string nodePath, CancellationToken ct = default)
    {
        if (nodePath.Contains("actToolBasePos")) return Task.FromResult<object?>(Angle);
        if (nodePath.Contains("DBX0.1")) return Task.FromResult<object?>(_triggered ? DoneAfterTrigger : StaleBeforeTrigger);
        return Task.FromResult<object?>(null);
    }
}
