namespace Hummingbird.Core;

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum DatumBoundaryMode { DarkHoleBrightRim, CircularFaceEdge }

/// <summary>用户框选定位特征的初值，不是孔心测量结果；绑定原图避免跨图沿用。</summary>
public sealed record DatumEllipseSeed(double WidthPx,double HeightPx,double AngleDeg);
public sealed record DatumCircleSeed(double Px,double Py,double RadiusPx,string ImageSha256,string SelectionSource)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public DatumEllipseSeed? Ellipse {get;init;}
}

/// <summary>完整参考孔的图像筛选参数，仅用于定位预览，不代表加工精度公差。</summary>
public sealed record DatumDirectionOptions
{
    public DatumBoundaryMode BoundaryMode { get; init; }
    public double MaxRecoveredTargetShiftMm {get;init;}=.025;
    public double MinFaceWallContrast { get; init; } = 8;
    public double MinFaceAngularSupport { get; init; } = 0.75;
    public double MaxFaceEdgeRmsMm { get; init; } = 0.010;
    public double MaxFaceCenterSpreadMm { get; init; } = 0.015;
    public double MinHoleDiameterMm { get; init; } = 0.15;
    public double MaxHoleDiameterMm { get; init; } = 0.9;
    public double MaxImageCenterDistanceMm { get; init; } = 0.9;
    public double MaxRadialErrorMm { get; init; } = 0.5;
    public double ExpectedGapDeg { get; init; } = 60;
    public double GapToleranceDeg { get; init; } = 2;
    public int MinimumThresholdCount { get; init; } = 3;
    public double MaxCenterSpreadMm { get; init; } = 0.060;
    public double CenterSpreadWarningMm { get; init; } = 0.010;
    public double MinAnnulusContrast { get; init; } = 15;
    public double MaxDirectionSpreadDeg { get; init; } = 0.2;

    public void Validate()
    {
        if (!double.IsFinite(MaxRecoveredTargetShiftMm) || MaxRecoveredTargetShiftMm<=0 || !Enum.IsDefined(BoundaryMode) || new[] { MinFaceWallContrast, MinFaceAngularSupport, MaxFaceEdgeRmsMm,
                MaxFaceCenterSpreadMm, MinHoleDiameterMm, MaxHoleDiameterMm, MaxImageCenterDistanceMm,
                MaxRadialErrorMm, ExpectedGapDeg, GapToleranceDeg, MaxCenterSpreadMm, CenterSpreadWarningMm, MaxDirectionSpreadDeg, MinAnnulusContrast }
            .Any(v => !double.IsFinite(v) || v <= 0) || MinHoleDiameterMm >= MaxHoleDiameterMm ||
            ExpectedGapDeg >= 180 || GapToleranceDeg >= ExpectedGapDeg || MinimumThresholdCount < 2 ||
            MinFaceAngularSupport < .5 || MinFaceAngularSupport > 1)
            throw new InvalidDataException("参考孔识别参数无效。");
    }
}

public sealed record DatumHoleSample(int Threshold, double Px, double Py)
{
    public string Criterion { get; init; } = "GrayThreshold";
    public double? EdgeFraction { get; init; }
    public double SeedDx { get; init; }
    public double SeedDy { get; init; }
    public double SeedRadiusRatio { get; init; } = 1;
}
public sealed record DatumHoleDetection(string FrameName, string ImagePath, double Px, double Py,
    double MachineX, double MachineY, double EllipseWidthPx, double EllipseHeightPx, double EllipseAngleDeg,
    double CenterSpreadMm, double CoreMedian, double AnnulusMedian, IReadOnlyList<DatumHoleSample> Samples)
{
    public DatumBoundaryMode BoundaryMode { get; init; }
    public double? AngularSupportRatio { get; init; }
    public double? EdgeRmsPx { get; init; }
}

[System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter))]
public enum DatumHoleInspectionStatus { Detected, Rejected, Ambiguous, InputError }

/// <summary>仅检查单张参考孔照片。Detected表示通过预览筛选，不表示加工精度合格。</summary>
public sealed record DatumHoleInspection(RingScanFrame Frame, DatumHoleInspectionStatus Status,
    IReadOnlyList<DatumHoleDetection> Candidates, string Message, IReadOnlyList<string> Diagnostics)
{
    public bool PreviewOnly => true;
    public DatumBoundaryMode BoundaryMode { get; init; }
    public string Algorithm => BoundaryMode==DatumBoundaryMode.CircularFaceEdge
        ? "实体圆边-框选后局部过渡与圆周支持-v1" : "逐轮廓暗孔/亮缘筛选-v2";
}
public sealed record DatumDirectionResult(double TwelveAngleDeg, double Reference11AngleDeg,
    double Reference1AngleDeg, double GapDeg, double DirectionSpreadDeg,
    DatumHoleDetection Reference11, DatumHoleDetection Reference1);

public sealed record DialAlignmentPreview(RingScanResult Ring, DatumDirectionResult Direction,
    IReadOnlyList<HoleTarget> Targets, DatumDirectionOptions DirectionOptions, IReadOnlyList<string> Warnings)
{
    public bool PreviewOnly => true;
    public TargetCoordinateStability? TargetStability {get;init;}
}

public sealed record TargetCoordinateStability(bool RecoveryUsed,double MaxTargetShiftMm,double RecoveredLimitMm,int EvaluatedVariants,int InvalidVariants=0);

public static class DatumDirectionGeometry
{
    public static double Normalize(double angle) => (angle % 360 + 360) % 360;

    /// <summary>选定11/1孔两条射线的内角平分线作为12点；不会缩放CAD孔距。</summary>
    public static (double Twelve, double Eleven, double One, double Gap) Compute(
        double cx, double cy, double x11, double y11, double x1, double y1, DatumDirectionOptions options)
    {
        options.Validate();
        if (new[] { cx, cy, x11, y11, x1, y1 }.Any(v => !double.IsFinite(v)) ||
            double.Hypot(x11 - cx, y11 - cy) < 1e-6 || double.Hypot(x1 - cx, y1 - cy) < 1e-6)
            throw new InvalidDataException("参考孔与圆心坐标无效或重合。");
        double a = Math.Atan2(y11 - cy, x11 - cx) * 180 / Math.PI;
        double b = Math.Atan2(y1 - cy, x1 - cx) * 180 / Math.PI;
        double gap = Math.IEEERemainder(a - b, 360);
        if (Math.Abs(gap - options.ExpectedGapDeg) > options.GapToleranceDeg)
            throw new InvalidDataException($"11点→1点参考孔夹角为{gap:F3}°，预期约{options.ExpectedGapDeg:F1}°。请检查孔用途是否选反、是否同次装夹及坐标是否对应。");
        return (Normalize(b + gap / 2), Normalize(a), Normalize(b), gap);
    }
}
