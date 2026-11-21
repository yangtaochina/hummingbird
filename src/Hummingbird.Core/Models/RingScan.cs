using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hummingbird.Core;

/// <summary>机床运动对像素位置的响应，单位 px/mm。与九点绝对像素标定分别保存。</summary>
public sealed class RingScanSettings
{
    [DisplayName("粗中心 X (MCS mm)")]
    public double HintX { get; set; }
    [DisplayName("粗中心 Y (MCS mm)")]
    public double HintY { get; set; }
    [DisplayName("X移动产生像素X (px/mm)")]
    public double Jxx { get; set; } = 582.841;
    [DisplayName("Y移动产生像素X (px/mm)")]
    public double Jxy { get; set; } = -48.884;
    [DisplayName("X移动产生像素Y (px/mm)")]
    public double Jyx { get; set; } = 49.023;
    [DisplayName("Y移动产生像素Y (px/mm)")]
    public double Jyy { get; set; } = 583.025;
    [DisplayName("标定图像宽度")]
    public int ImageWidth { get; set; } = 1920;
    [DisplayName("标定图像高度")]
    public int ImageHeight { get; set; } = 1200;
    [DisplayName("圆环预期半径 (mm)")]
    public double ExpectedRadius { get; set; } = 14.45;
    [DisplayName("半径允许差 (mm，试验筛选)")]
    public double RadiusTolerance { get; set; } = 0.5;
    [DisplayName("拟合RMS上限 (mm，试验筛选)")]
    public double MaxRms { get; set; } = 0.05;
    [DisplayName("边缘参数导致圆心变化上限 (mm)")]
    public double MaxSensitivity { get; set; } = 0.03;
    [DisplayName("标定来源")]
    public string CalibrationNote { get; set; } = "2026-11-09 X/Y ±0.100mm 试测。当前高度和成像设置需复核。";

    public void Validate()
    {
        double[] values = [HintX, HintY, Jxx, Jxy, Jyx, Jyy, ExpectedRadius, RadiusTolerance, MaxRms, MaxSensitivity];
        if (values.Any(v => !double.IsFinite(v)) || ImageWidth < 32 || ImageHeight < 32 ||
            ExpectedRadius <= 0 || RadiusTolerance <= 0 || MaxRms <= 0 || MaxSensitivity <= 0)
            throw new InvalidDataException("圆环参数必须为有效数值，半径和试验阈值须大于零。");
        double scale = Math.Sqrt(Jxx * Jxx + Jxy * Jxy + Jyx * Jyx + Jyy * Jyy);
        if (scale < 1 || Math.Abs(Jxx * Jyy - Jxy * Jyx) < scale * scale * 1e-4)
            throw new InvalidDataException("X/Y像素响应矩阵无效或接近奇异，请复核标定。");
    }

    public (double X, double Y) ToMachine(RingScanFrame frame, double px, double py)
    {
        double dx = px - (ImageWidth - 1) / 2.0, dy = py - (ImageHeight - 1) / 2.0;
        double determinant = Jxx * Jyy - Jxy * Jyx;
        // 同一特征在机床正向移动时向 J 的正向运动，因此用负的逆矩阵还原特征坐标。
        return (frame.X - (Jyy * dx - Jxy * dy) / determinant,
                frame.Y - (-Jyx * dx + Jxx * dy) / determinant);
    }

    /// <summary>ToMachine 的逆：机床坐标在这一帧照片上的像素位置。仅用于在照片上叠加显示。</summary>
    public (double Px, double Py) ToPixel(RingScanFrame frame, double x, double y)
    {
        double mx = x - frame.X, my = y - frame.Y;
        return ((ImageWidth - 1) / 2.0 - (Jxx * mx + Jxy * my), (ImageHeight - 1) / 2.0 - (Jyx * mx + Jyy * my));
    }
}

[JsonConverter(typeof(JsonStringEnumConverter<RingFrameRole>))]
public enum RingFrameRole { RingArc, Reference11, Reference1 }

public sealed record RingScanFrame(string Name, string ImagePath, double X, double Y, double Z, double C)
{
    public DatumCircleSeed? FaceSeed { get; init; }
    public RingCaptureMetadata? Capture { get; init; }
    public RingFrameRole Role { get; init; } = RingFrameRole.RingArc;
}

public sealed class RingScanSession
{
    public int Version { get; set; } = 1;
    public RingScanSettings Settings { get; set; } = new();
    public DatumDirectionOptions DirectionOptions { get; set; } = new();
    public List<RingScanFrame> Frames { get; set; } = new();

    public static RingScanSession Load(string path)
    {
        var session = JsonSerializer.Deserialize<RingScanSession>(File.ReadAllText(path))
            ?? throw new InvalidDataException("采图记录为空。");
        if (session.Version != 1 || session.Settings is null || session.Frames is null || session.DirectionOptions is null)
            throw new InvalidDataException("采图记录版本或内容无效。");
        session.Settings.Validate();
        session.DirectionOptions.Validate();
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        session.Frames = session.Frames.Select(f => f with
        {
            ImagePath = Path.GetFullPath(f.ImagePath, folder)
        }).ToList();
        return session;
    }

    public void Save(string path)
    {
        string folder = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var relative = new RingScanSession
        {
            Settings = Settings,
            DirectionOptions = DirectionOptions,
            Frames = Frames.Select(f => f with { ImagePath = Path.GetRelativePath(folder, f.ImagePath) }).ToList()
        };
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, JsonSerializer.Serialize(relative, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

public sealed record RingEdgePoint(int FrameIndex, double Px, double Py, double X, double Y);
public sealed record RingViewFit(string Name, int Count, double RmsMm);
public sealed record RingScanInitialization(double HintX, double HintY, bool EstimatedFromCapturePositions,
    double MaxAngularGapDeg, double? CaptureCircleRadiusMm);
public sealed record RingFitVariant(double CenterX,double CenterY,double RadiusMm);
public sealed record RingFitRecovery(IReadOnlyList<int> RemovedPerView,double LeaveOneViewCenterShiftMm,string OriginalRejection);
public sealed record RingScanResult(double CenterX, double CenterY, double RadiusMm, double RmsMm,
    double SensitivityMm, double ReferenceZ, double ReferenceC,
    IReadOnlyList<RingEdgePoint> Points, IReadOnlyList<RingViewFit> Views)
{
    public RingScanInitialization? Initialization { get; init; }
    public IReadOnlyList<RingFitVariant> FitVariants {get;init;}=[];
    public RingFitRecovery? CompatibilityRecovery {get;init;}
}

/// <summary>一帧圆环照片上的叠加内容：这一帧取到的边缘点，以及拟合圆落在画面内的各段。只用于显示。</summary>
public sealed record RingFrameOverlay(IReadOnlyList<(double Px, double Py)> EdgePoints, IReadOnlyList<IReadOnlyList<(double Px, double Py)>> FittedArcs)
{
    public static RingFrameOverlay Build(RingScanSettings settings, RingScanFrame frame, RingScanResult fit)
    {
        // 拟合结果里的帧序号是相对参与拟合的那组照片的，这里按“像素换算回机床坐标是否就是这个点”来认这一帧的点。
        var points = fit.Points.Where(p =>
        {
            var (x, y) = settings.ToMachine(frame, p.Px, p.Py);
            return Math.Abs(x - p.X) < 1e-6 && Math.Abs(y - p.Y) < 1e-6;
        }).Select(p => (p.Px, p.Py)).ToArray();
        // 0.02°一步，在半径14.45mm处约5µm（约3像素），连成线足够平滑。
        const int steps = 18000;
        var arcs = new List<List<(double Px, double Py)>>(); List<(double Px, double Py)>? current = null;
        bool startsInside = false, endsInside = false;
        for (int i = 0; i < steps; i++)
        {
            double angle = 2 * Math.PI * i / steps;
            var (px, py) = settings.ToPixel(frame, fit.CenterX + fit.RadiusMm * Math.Cos(angle), fit.CenterY + fit.RadiusMm * Math.Sin(angle));
            bool inside = px >= 0 && py >= 0 && px <= settings.ImageWidth - 1 && py <= settings.ImageHeight - 1;
            if (i == 0) startsInside = inside;
            if (i == steps - 1) endsInside = inside;
            if (!inside) { current = null; continue; }
            if (current is null) arcs.Add(current = []);
            current.Add((px, py));
        }
        // 画面内的一段跨过0°时被拆成了首尾两段，接回去。
        if (startsInside && endsInside && arcs.Count > 1) { arcs[^1].AddRange(arcs[0]); arcs.RemoveAt(0); }
        return new(points, arcs.Where(a => a.Count > 1).ToArray());
    }
}

public static class RingScanPreview
{
    /// <param name="twelveAngleDeg">CAD +Y（12点）在机床平面中相对 +X 的逆时针角度。</param>
    public static List<HoleTarget> Targets(RingScanResult fit, HolePatternTemplate template, double twelveAngleDeg)
    {
        if (!double.IsFinite(twelveAngleDeg) || template.Holes.Count == 0 ||
            !double.IsFinite(fit.CenterX) || !double.IsFinite(fit.CenterY))
            throw new InvalidDataException("请填写12点方向角，并加载有效孔位模板和圆心。");
        double radians = (twelveAngleDeg - 90) * Math.PI / 180;
        double c = Math.Cos(radians), s = Math.Sin(radians);
        if (template.Holes.Any(h => !double.IsFinite(h.X) || !double.IsFinite(h.Y)))
            throw new InvalidDataException("CAD孔位坐标无效。");
        return template.Holes.Select(h => new HoleTarget(h.Id,
            fit.CenterX + h.X * c - h.Y * s,
            fit.CenterY + h.X * s + h.Y * c, h.DiameterMm, h.Clock)).ToList();
    }

    public static string CameraTrial(RingScanResult fit, IReadOnlyList<HoleTarget> targets)
    {
        if (targets.Count == 0 || targets.Any(t => !double.IsFinite(t.MachineX) || !double.IsFinite(t.MachineY)))
            throw new InvalidDataException("没有有效的相机试走坐标。");
        var text = new StringBuilder();
        text.AppendLine("; HUMMINGBIRD CAMERA POSITION CHECK - LASER OFF");
        text.AppendLine(FormattableString.Invariant($"; REFERENCE C={fit.ReferenceC:F4} Z={fit.ReferenceZ:F4} MCS"));
        text.AppendLine("; SET REFERENCE C/Z AND CHECK XY PATH BEFORE START");
        text.AppendLine("N10 G90 G71 G94 G60");
        text.AppendLine("N20 M0");
        int block = 30;
        foreach (var t in targets)
        {
            text.AppendLine(FormattableString.Invariant($"N{block} SUPA G1 X{t.MachineX:F4} Y{t.MachineY:F4} F60 ; HOLE {t.Id}"));
            text.AppendLine($"N{block + 10} M0");
            block += 20;
        }
        text.AppendLine($"N{block} M30");
        return text.ToString().ReplaceLineEndings("\r\n");
    }
}
