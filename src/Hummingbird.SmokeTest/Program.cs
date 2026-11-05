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
    }
    else
    {
        Console.WriteLine($"  [跳过] 真机图不存在：{realImg}");
    }
}

/// <summary>
/// 读取 8 位灰度 BMP（Windows 拍摄/保存的常见格式），不依赖 OpenCV 的文件 IO API。
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

static double AngleDiff(double a, double b)
{
    double d = Math.Abs(a - b) % 360;
    return d > 180 ? 360 - d : d;
}

Console.WriteLine(fails == 0 ? "===== ALL PASS =====" : $"===== {fails} 项 FAILED =====");
return fails == 0 ? 0 : 1;
