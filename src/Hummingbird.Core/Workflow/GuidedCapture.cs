using System.Globalization;
using System.Text;

namespace Hummingbird.Core;

public sealed record CaptureStep(string Name, RingFrameRole Role, string Instruction);
public sealed record CaptureWaypoint(string Name, RingFrameRole Role, double X, double Y, double Z, double C);

public static class GuidedCapture
{
    public static IReadOnlyList<CaptureStep> Steps { get; } = Array.AsReadOnly(new[] {
        new CaptureStep("参考1点",RingFrameRole.Reference1,"1点实体小圆面完整入画，尽量置于中部。"),
        new CaptureStep("圆环12点",RingFrameRole.RingArc,"表盘大圆环12点附近弧段，亮带完整进入画面。"),
        new CaptureStep("圆环3点",RingFrameRole.RingArc,"同一大圆环3点附近弧段。"),
        new CaptureStep("圆环6点",RingFrameRole.RingArc,"同一大圆环6点附近弧段。"),
        new CaptureStep("圆环9点",RingFrameRole.RingArc,"同一大圆环9点附近弧段。"),
        new CaptureStep("参考11点",RingFrameRole.Reference11,"11点实体小圆面完整入画，和1点采用同一种边界。")});

    public static int Index(RingScanFrame frame)
    {
        if (frame.Role == RingFrameRole.Reference1) return 0;
        if (frame.Role == RingFrameRole.Reference11) return 5;
        string name = frame.Name.Replace("圆环", "").Replace(" ", "");
        if(name.StartsWith("新定位_",StringComparison.Ordinal))name=name[4..];
        return name switch { "12点" => 1, "3点" => 2, "6点" => 3, "9点" => 4, _ => -1 };
    }
    public static IReadOnlyList<CaptureWaypoint> Route(RingScanSession session)
    {
        session.Settings.Validate();
        if (session.Frames.Count != Steps.Count || session.Frames.Any(f => Index(f) < 0))
            throw new InvalidDataException("采图路线必须恰好包含六个固定C步骤。");
        var result = new List<CaptureWaypoint>();
        foreach (var step in Steps)
        {
            var frames = session.Frames.Where(f => Index(f) == Steps.ToList().IndexOf(step)).ToArray();
            if (frames.Length != 1) throw new InvalidDataException("复用采图路线需要六个明确且不重复的步骤；缺少或重复：" + step.Name);
            var f = frames[0]; RingCaptureArchive.VerifyImage(f);
            if (new[] { f.X, f.Y, f.Z, f.C }.Any(v => !double.IsFinite(v)) || f.Capture?.Purpose == "RotaryScan" || f.Capture?.XyzSource == AxisCoordinateSource.Simulator)
                throw new InvalidDataException("采图路线坐标无效或混入旋转记录。");
            result.Add(new(step.Name, step.Role, f.X, f.Y, f.Z, f.C));
        }
        if (result.Any(f => Math.Abs(f.Z - result[0].Z) > .010 || Math.Abs(Math.IEEERemainder(f.C - result[0].C, 360)) > .005) ||
            result.Max(f => f.X) - result.Min(f => f.X) > session.Settings.ExpectedRadius * 3 ||
            result.Max(f => f.Y) - result.Min(f => f.Y) > session.Settings.ExpectedRadius * 3)
            throw new InvalidDataException("采图路线的Z/C或XY范围不符合固定C表盘采图条件。");
        return result.AsReadOnly();
    }
    public static string Program(IReadOnlyList<CaptureWaypoint> route)
    {
        if (route.Count != Steps.Count || route.Where((point, i) => point.Name != Steps[i].Name || point.Role != Steps[i].Role).Any())
            throw new InvalidDataException("采图路线必须按六步顺序导出。");
        var text = new StringBuilder("; HUMMINGBIRD CAMERA CAPTURE ROUTE - NO LASER CONTROL\r\n");
        text.AppendLine("; DISABLE LASER, VERIFY SAME FIXTURE/OPTICS AND XY PATH BEFORE START");
        text.AppendLine(FormattableString.Invariant($"; KEEP Z={route[0].Z:F4} C={route[0].C:F4} FIXED"));
        text.AppendLine("N10 G90 G71 G94 G60"); text.AppendLine("N20 M0"); int block = 30;
        foreach (var point in route)
        {
            if (new[] { point.X, point.Y, point.Z, point.C }.Any(v => !double.IsFinite(v)) ||
                Math.Abs(point.Z - route[0].Z) > .010 || Math.Abs(Math.IEEERemainder(point.C - route[0].C, 360)) > .005)
                throw new InvalidDataException("采图位置或Z/C不一致。");
            text.AppendLine(FormattableString.Invariant($"N{block} SUPA G1 X{point.X:F4} Y{point.Y:F4} F30"));
            text.AppendLine($"N{block + 10} M0"); block += 20;
        }
        text.AppendLine($"N{block} M30"); return text.ToString().ReplaceLineEndings("\r\n");
    }
    public static DatumCircleSeed? TranslateRegion(RingScanFrame previous, RingScanSettings settings,
        double currentX, double currentY, string newImageHash)
    {
        if (previous.FaceSeed is not { } seed) return null;
        double dx = currentX - previous.X, dy = currentY - previous.Y;
        double x = seed.Px + settings.Jxx * dx + settings.Jxy * dy, y = seed.Py + settings.Jyx * dx + settings.Jyy * dy;
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || y < 0 || x >= settings.ImageWidth || y >= settings.ImageHeight) return null;
        return new(x, y, seed.RadiusPx, newImageHash, "TranslatedPreviousRegion_ProposalMustBeRefitted");
    }
}

/// <summary>仅给相机采图判稳，不提供出光许可，也不替代数控到位信号。</summary>
public sealed class CapturePositionGate
{
    private (double X, double Y, double Z, double C)? last;
    private DateTimeOffset? since;
    private DateTimeOffset? lastTime;
    public void Reset() { last = null; since = null; lastTime = null; }
    public bool Observe(CaptureWaypoint target, double x, double y, double z, double c, DateTimeOffset now)
    {
        if (new[] { x, y, z, c, target.X, target.Y, target.Z, target.C }.Any(v => !double.IsFinite(v)) ||
            Math.Abs(x - target.X) > .005 || Math.Abs(y - target.Y) > .005 || Math.Abs(z - target.Z) > .010 ||
            Math.Abs(Math.IEEERemainder(c - target.C, 360)) > .005)
        { Reset(); return false; }
        if (last is not { } old || Math.Abs(x - old.X) > .002 || Math.Abs(y - old.Y) > .002 || Math.Abs(z - old.Z) > .002 ||
            Math.Abs(Math.IEEERemainder(c - old.C, 360)) > .001 || lastTime is { } previous && now < previous)
            since = now;
        last = (x, y, z, c); lastTime = now; since ??= now;
        return now - since.Value >= TimeSpan.FromMilliseconds(500);
    }
}
