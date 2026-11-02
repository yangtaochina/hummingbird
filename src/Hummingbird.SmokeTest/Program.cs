using Hummingbird.Core;
using Hummingbird.Devices.Sim;
using Hummingbird.Vision;

var log = new MemoryLogger();
log.LineAdded += (lvl, msg) => Console.WriteLine($"  [{lvl}] {msg}");

var template = HolePatternTemplate.LoadJson("templates/dial_25holes.json");
var cam = new SimCameraService { Template = template };
var cnc = new SimCncService();
var laser = new SimLaserService();
var wf = new AutoRunWorkflow(log, cam, new DialPositioning(), cnc, laser)
{
    Template = template,
    VisionOptions = new VisionOptions
    {
        ExpectedOuterDiameterMm = 34.5,
        NominalDatumAngleDeg = template.NominalDatumAngleDeg,
        DatumMode = template.DatumMode,               // 模板指向 offsetCircle（现场确认的 12H 基准）
        DatumCircleDiameterMm = template.DatumCircleDiameterMm
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
    Check(targets.Count == 25, $"目标孔数 = {targets.Count}");
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
    Check(maxErr < 0.05, $"25孔坐标最大误差 {maxErr:F4} mm < 0.05 mm");
}

Console.WriteLine("== 4) 端到端：下发 → 模拟NC走位 → 激光同步触发 ==");
{
    var targets = wf.ComputeTargets(pose);
    await cnc.ConnectAsync("sim://test");
    bool ok = await wf.DeployProgramAsync(pose);
    Check(ok && cnc.Targets.Count == 25, $"程序下发并解析出 {cnc.Targets.Count} 个定位点");
    cnc.StartSimulatedRun();
    int fired = await wf.RunLaserSyncAsync(targets);
    Check(fired == 25, $"激光触发 {fired}/25 孔");
    Check(laser.FireCount == 25, $"激光出光计数 {laser.FireCount} = 25");
}

Console.WriteLine(fails == 0 ? "===== ALL PASS =====" : $"===== {fails} 项 FAILED =====");
return fails == 0 ? 0 : 1;
