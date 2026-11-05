using Hummingbird.Core;
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

static double AngleDiff(double a, double b)
{
    double d = Math.Abs(a - b) % 360;
    return d > 180 ? 360 - d : d;
}

Console.WriteLine(fails == 0 ? "===== ALL PASS =====" : $"===== {fails} 项 FAILED =====");
return fails == 0 ? 0 : 1;
