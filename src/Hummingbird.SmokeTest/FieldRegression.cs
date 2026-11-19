using System.Text.Encodings.Web;
using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

/// <summary>
/// 用“图片收集”里的现场六步记录回放当前识别算法，并与入库的基线逐项比较。
/// 只读图片和记录，不连接设备。基线记录的是“算法当时的输出”，不是实物真值。
/// </summary>
internal static class FieldRegression
{
    private const double CoordinateToleranceMm = 0.0005, PixelTolerance = 0.05, AngleToleranceDeg = 0.001;
    private static readonly string[] Sessions =
    [
        "现场图片2/自动定位_离线验证.json",
        "现场图片3/保存采图记录（圆环+孔）.json",
        "现场图片4/新环境_六帧定位记录_已核对.json",
        "现场图片4/新环境_当前六帧定位记录.json",
        "现场图片5/当前装夹_定位/ring_scan.json",
        "现场图片5/当前装夹_定位/ring_scan_11点已更新.json",
        "现场图片5/当前装夹_定位/ring_scan_实体圆边模型.json",
    ];

    public sealed record RingOutcome(bool Accepted, string? Rejection, double CenterX, double CenterY, double RadiusMm, double RmsMm,
        double SensitivityMm, double LeaveOneViewShiftMm, bool HintEstimated, bool Recovery, int[] PointsPerView, double[] ViewRmsMm);
    public sealed record ReferenceOutcome(string Frame, string Role, string Status, double? Px, double? Py, double? SpreadMm, double? RadialErrorMm);
    public sealed record AlignmentOutcome(bool Completed, string? Rejection, double? TwelveDeg, double? GapDeg, double? DirectionSpreadDeg,
        double? MaxTargetShiftMm, double[][]? Targets);
    public sealed record CaseOutcome(string Session, string BoundaryMode, RingOutcome Ring, ReferenceOutcome[] References, AlignmentOutcome Alignment);
    public sealed record Baseline(string Note, CaseOutcome[] Cases);

    public static int Run(string[] args)
    {
        bool update = args.Contains("--update");
        string? root = args.FirstOrDefault(a => !a.StartsWith("--")) ?? FindUp("图片收集");
        string? baselinePath = FindUp(Path.Combine("src", "Hummingbird.SmokeTest", "field-regression.json"));
        string? templatePath = FindUp(Path.Combine("src", "templates", "dial_18holes.json"));
        if (root is null || !Directory.Exists(root) || templatePath is null || baselinePath is null && !update)
        {
            Console.Error.WriteLine("找不到“图片收集”目录、18孔模板或 field-regression.json；请在仓库目录内运行，或把图片目录作为参数传入。");
            return 2;
        }
        var template = HolePatternTemplate.LoadJson(templatePath);
        var current = Sessions.Select(s => Evaluate(Path.Combine(root, s), s, template)).ToArray();
        Report(current);
        var json = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        if (update)
        {
            baselinePath ??= Path.Combine(Path.GetDirectoryName(templatePath)!, "..", "Hummingbird.SmokeTest", "field-regression.json");
            File.WriteAllText(baselinePath, JsonSerializer.Serialize(new Baseline(
                "现场六步记录的算法回归基线：数值是算法输出，不是实物真值；只有在确认算法改动符合预期后才用 --update 重写。", current), json) + "\n");
            Console.WriteLine("已重写基线 " + Path.GetFullPath(baselinePath));
            return 0;
        }
        var expected = JsonSerializer.Deserialize<Baseline>(File.ReadAllText(baselinePath!))!;
        var differences = Compare(expected.Cases, current);
        foreach (string line in differences) Console.Error.WriteLine("[DIFF] " + line);
        Console.WriteLine(differences.Count == 0 ? $"现场回归通过：{current.Length} 组记录与基线一致。" : $"现场回归发现 {differences.Count} 处差异。");
        return differences.Count == 0 ? 0 : 1;
    }

    private static string? FindUp(string relative)
    {
        for (var folder = new DirectoryInfo(Directory.GetCurrentDirectory()); folder is not null; folder = folder.Parent)
        {
            string candidate = Path.Combine(folder.FullName, relative);
            if (File.Exists(candidate) || Directory.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static CaseOutcome Evaluate(string path, string name, HolePatternTemplate template)
    {
        var data = RingScanSession.Load(path);
        RingScanResult? ring = null; string? ringRejection = null;
        try
        {
            ring = new RingScanSolver().Analyze(new RingScanSession { Settings = data.Settings,
                Frames = data.Frames.Where(f => f.Role == RingFrameRole.RingArc).ToList() });
        }
        catch (InvalidDataException ex) { ringRejection = ex.Message; }
        var ringOutcome = ring is null
            ? new RingOutcome(false, ringRejection, 0, 0, 0, 0, 0, 0, false, false, [], [])
            : new RingOutcome(true, null, ring.CenterX, ring.CenterY, ring.RadiusMm, ring.RmsMm, ring.SensitivityMm, LeaveOneViewShift(ring),
                ring.Initialization?.EstimatedFromCapturePositions == true, ring.CompatibilityRecovery is not null,
                ring.Views.Select(v => v.Count).ToArray(), ring.Views.Select(v => v.RmsMm).ToArray());
        var references = data.Frames.Where(f => f.Role != RingFrameRole.RingArc).Select(f =>
        {
            var inspection = new DatumDirectionSolver().Inspect(f, data.Settings, data.DirectionOptions);
            var hole = inspection.Status == DatumHoleInspectionStatus.Detected ? inspection.Candidates[0] : null;
            double? radial = hole is null || ring is null ? null
                : double.Hypot(hole.MachineX - ring.CenterX, hole.MachineY - ring.CenterY) - data.Settings.ExpectedRadius;
            return new ReferenceOutcome(f.Name, f.Role.ToString(), inspection.Status.ToString(), hole?.Px, hole?.Py, hole?.CenterSpreadMm, radial);
        }).ToArray();
        AlignmentOutcome alignment;
        try
        {
            var result = new DialAlignmentSolver().Analyze(data, template);
            alignment = new(true, null, result.Direction.TwelveAngleDeg, result.Direction.GapDeg, result.Direction.DirectionSpreadDeg,
                result.TargetStability?.MaxTargetShiftMm, result.Targets.Select(t => new[] { t.MachineX, t.MachineY }).ToArray());
        }
        catch (InvalidDataException ex) { alignment = new(false, FirstSentence(ex.Message), null, null, null, null, null); }
        return new(name, data.DirectionOptions.BoundaryMode.ToString(), ringOutcome, references, alignment);
    }

    // 拒绝原因只比较第一句，后面的逐轮廓诊断数字随平台略有出入。
    private static string FirstSentence(string message)
    {
        int end = message.IndexOfAny(['。', '；']);
        return end < 0 ? message : message[..(end + 1)];
    }

    /// <summary>逐一去掉一个方位后重新拟合，圆心的最大变化；不依赖真值，反映圆心对单个方位的依赖程度。</summary>
    private static double LeaveOneViewShift(RingScanResult ring)
    {
        double shift = 0;
        foreach (int view in ring.Points.Select(p => p.FrameIndex).Distinct())
        {
            var (x, y) = FitCircle(ring.Points.Where(p => p.FrameIndex != view).ToArray(), ring.CenterX, ring.CenterY, ring.RadiusMm);
            shift = Math.Max(shift, double.Hypot(x - ring.CenterX, y - ring.CenterY));
        }
        return shift;
    }

    private static (double X, double Y) FitCircle(RingEdgePoint[] points, double cx, double cy, double r)
    {
        for (int iteration = 0; iteration < 30; iteration++)
        {
            // 高斯-牛顿：残差 d_i - r 对 (cx, cy, r) 的雅可比为 (-ux, -uy, -1)。
            double a11 = 0, a12 = 0, a13 = 0, a22 = 0, a23 = 0, a33 = 0, b1 = 0, b2 = 0, b3 = 0;
            foreach (var p in points)
            {
                double dx = p.X - cx, dy = p.Y - cy, d = double.Hypot(dx, dy), ux = -dx / d, uy = -dy / d, e = d - r;
                a11 += ux * ux; a12 += ux * uy; a13 -= ux; a22 += uy * uy; a23 -= uy; a33 += 1;
                b1 -= ux * e; b2 -= uy * e; b3 += e;
            }
            double det = a11 * (a22 * a33 - a23 * a23) - a12 * (a12 * a33 - a23 * a13) + a13 * (a12 * a23 - a22 * a13);
            double sx = (b1 * (a22 * a33 - a23 * a23) - a12 * (b2 * a33 - a23 * b3) + a13 * (b2 * a23 - a22 * b3)) / det;
            double sy = (a11 * (b2 * a33 - a23 * b3) - b1 * (a12 * a33 - a23 * a13) + a13 * (a12 * b3 - b2 * a13)) / det;
            double sr = (a11 * (a22 * b3 - a23 * b2) - a12 * (a12 * b3 - b2 * a13) + b1 * (a12 * a23 - a22 * a13)) / det;
            cx += sx; cy += sy; r += sr;
            if (Math.Abs(sx) + Math.Abs(sy) + Math.Abs(sr) < 1e-12) break;
        }
        return (cx, cy);
    }

    private static void Report(CaseOutcome[] cases)
    {
        foreach (var c in cases)
        {
            Console.WriteLine($"== {c.Session}（{c.BoundaryMode}）");
            Console.WriteLine(c.Ring.Accepted
                ? FormattableString.Invariant($"   圆环 中心({c.Ring.CenterX:F4},{c.Ring.CenterY:F4}) R={c.Ring.RadiusMm:F4} RMS={c.Ring.RmsMm * 1000:F1}µm 取边敏感性={c.Ring.SensitivityMm * 1000:F1}µm 去一方位圆心变化={c.Ring.LeaveOneViewShiftMm * 1000:F1}µm 每方位点数={string.Join("/", c.Ring.PointsPerView)}")
                : "   圆环 拒绝：" + c.Ring.Rejection);
            foreach (var r in c.References)
                Console.WriteLine(FormattableString.Invariant($"   {r.Role} {r.Status}") + (r.Px is null ? "" :
                    FormattableString.Invariant($" 孔心({r.Px:F1},{r.Py:F1})px 敏感性={r.SpreadMm * 1000:F1}µm 径向偏差={r.RadialErrorMm * 1000:F1}µm")));
            Console.WriteLine(c.Alignment.Completed
                ? FormattableString.Invariant($"   定位 完成 12点方向={c.Alignment.TwelveDeg:F3}° 11→1夹角={c.Alignment.GapDeg:F3}° 方向敏感性={c.Alignment.DirectionSpreadDeg:F3}° 18孔最大扰动={c.Alignment.MaxTargetShiftMm * 1000:F1}µm")
                : "   定位 拒绝：" + c.Alignment.Rejection);
        }
        var accepted = cases.Where(c => c.Ring.Accepted).ToArray();
        if (accepted.Length > 1)
            Console.WriteLine(FormattableString.Invariant($"各组圆环半径范围 {accepted.Min(c => c.Ring.RadiusMm):F4}～{accepted.Max(c => c.Ring.RadiusMm):F4}mm（同一种工件，差值反映拟合和标定的一致性）"));
    }

    private static List<string> Compare(CaseOutcome[] expected, CaseOutcome[] current)
    {
        var differences = new List<string>();
        foreach (var now in current)
        {
            var old = expected.FirstOrDefault(e => e.Session == now.Session);
            if (old is null) { differences.Add(now.Session + "：基线中没有这组记录"); continue; }
            void Same(bool same, string what) { if (!same) differences.Add($"{now.Session}：{what}"); }
            void Near(double? a, double? b, double tolerance, string what) =>
                Same(a is null == b is null && (a is null || Math.Abs(a.Value - b!.Value) <= tolerance),
                    FormattableString.Invariant($"{what} 基线{a:F6} 现在{b:F6}"));
            Same(old.Ring.Accepted == now.Ring.Accepted, $"圆环通过/拒绝结论变化（现在：{(now.Ring.Accepted ? "通过" : now.Ring.Rejection)}）");
            if (old.Ring.Accepted && now.Ring.Accepted)
            {
                Near(old.Ring.CenterX, now.Ring.CenterX, CoordinateToleranceMm, "圆心X");
                Near(old.Ring.CenterY, now.Ring.CenterY, CoordinateToleranceMm, "圆心Y");
                Near(old.Ring.RadiusMm, now.Ring.RadiusMm, CoordinateToleranceMm, "半径");
                Near(old.Ring.RmsMm, now.Ring.RmsMm, CoordinateToleranceMm, "RMS");
                Near(old.Ring.SensitivityMm, now.Ring.SensitivityMm, CoordinateToleranceMm, "取边敏感性");
                Near(old.Ring.LeaveOneViewShiftMm, now.Ring.LeaveOneViewShiftMm, CoordinateToleranceMm, "去一方位圆心变化");
                Same(old.Ring.HintEstimated == now.Ring.HintEstimated && old.Ring.Recovery == now.Ring.Recovery, "粗中心估计或兼容拟合的使用情况变化");
                Same(old.Ring.PointsPerView.SequenceEqual(now.Ring.PointsPerView), $"每方位取边点数 基线{string.Join("/", old.Ring.PointsPerView)} 现在{string.Join("/", now.Ring.PointsPerView)}");
            }
            foreach (var reference in now.References)
            {
                var before = old.References.FirstOrDefault(r => r.Frame == reference.Frame);
                if (before is null) { differences.Add($"{now.Session}：基线中没有 {reference.Frame}"); continue; }
                Same(before.Status == reference.Status, $"{reference.Frame} 状态 基线{before.Status} 现在{reference.Status}");
                Near(before.Px, reference.Px, PixelTolerance, reference.Frame + " 孔心Px");
                Near(before.Py, reference.Py, PixelTolerance, reference.Frame + " 孔心Py");
                Near(before.SpreadMm, reference.SpreadMm, CoordinateToleranceMm, reference.Frame + " 敏感性");
            }
            Same(old.Alignment.Completed == now.Alignment.Completed && old.Alignment.Rejection == now.Alignment.Rejection,
                $"定位结论变化（基线：{(old.Alignment.Completed ? "完成" : old.Alignment.Rejection)}；现在：{(now.Alignment.Completed ? "完成" : now.Alignment.Rejection)}）");
            if (old.Alignment.Completed && now.Alignment.Completed)
            {
                Near(old.Alignment.TwelveDeg, now.Alignment.TwelveDeg, AngleToleranceDeg, "12点方向");
                double shift = old.Alignment.Targets!.Zip(now.Alignment.Targets!, (a, b) => double.Hypot(a[0] - b[0], a[1] - b[1])).Max();
                Same(old.Alignment.Targets!.Length == now.Alignment.Targets!.Length && shift <= CoordinateToleranceMm,
                    FormattableString.Invariant($"18孔坐标最大变化{shift * 1000:F2}µm"));
            }
        }
        foreach (var old in expected.Where(e => current.All(c => c.Session != e.Session))) differences.Add(old.Session + "：本次没有回放这组基线记录");
        return differences;
    }
}
