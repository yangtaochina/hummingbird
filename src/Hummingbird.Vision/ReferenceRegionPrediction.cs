using Hummingbird.Core;

namespace Hummingbird.Vision;

public sealed record ReferenceRegionPrediction(DatumCircleSeed Seed, DatumCircleSeed OriginalSeed,
    double PredictedMachineX, double PredictedMachineY, double ExpectedGapDeg,
    RingScanResult Ring, DatumHoleDetection MeasuredReference1)
{
    public bool ProposalOnly => true;
    public bool MotionRequested => false;
}

/// <summary>用本件圆环和已独立识别的1点预测11点搜索初值。预测不得代替11点测量或最终双参考方向检查。</summary>
public static class CurrentReferenceRegion
{
    public static ReferenceRegionPrediction PredictEleven(RingScanFrame one, RingScanSettings settings,
        DatumDirectionOptions options, RingScanResult ring, CaptureWaypoint target, DatumCircleSeed originalSeed)
    {
        settings.Validate(); options.Validate();
        if (one.Role != RingFrameRole.Reference1 || target.Role != RingFrameRole.Reference11 ||
            options.BoundaryMode != DatumBoundaryMode.CircularFaceEdge ||
            new[] { ring.CenterX, ring.CenterY, ring.RadiusMm, ring.RmsMm, ring.SensitivityMm, ring.ReferenceZ,
                ring.ReferenceC, target.X, target.Y, target.Z, target.C, originalSeed.RadiusPx }.Any(v => !double.IsFinite(v)) ||
            ring.RadiusMm <= 0 || ring.RmsMm < 0 || ring.RmsMm > settings.MaxRms ||
            ring.SensitivityMm < 0 || ring.SensitivityMm > settings.MaxSensitivity || originalSeed.RadiusPx <= 0 ||
            Math.Abs(one.Z - ring.ReferenceZ) > .010 || Math.Abs(target.Z - ring.ReferenceZ) > .010 ||
            Math.Abs(Math.IEEERemainder(one.C - ring.ReferenceC, 360)) > .005 ||
            Math.Abs(Math.IEEERemainder(target.C - ring.ReferenceC, 360)) > .005)
            throw new InvalidDataException("11点框预测要求本件合格圆环、参考1点及相同固定Z/C。");
        RingCaptureArchive.VerifyImage(one);
        var check = new DatumDirectionSolver().Inspect(one, settings, options);
        if (check.Status != DatumHoleInspectionStatus.Detected || check.Candidates.Count != 1)
            throw new InvalidDataException("参考1点尚未独立通过实体圆边检查，不能预测11点。");
        var measured = check.Candidates.Single();
        double x = measured.MachineX - ring.CenterX, y = measured.MachineY - ring.CenterY;
        double radius = double.Hypot(x, y);
        if (!double.IsFinite(radius) || radius <= 0 || Math.Abs(radius - ring.RadiusMm) > options.MaxRadialErrorMm)
            throw new InvalidDataException("本件1点与圆环径向关系不符，未预测11点。");
        // 与最终DatumDirectionGeometry使用同一正向夹角约定；最终仍用真实11点重新计算夹角。
        double a = options.ExpectedGapDeg * Math.PI / 180;
        double worldX = ring.CenterX + x * Math.Cos(a) - y * Math.Sin(a);
        double worldY = ring.CenterY + x * Math.Sin(a) + y * Math.Cos(a);
        double dx = target.X - worldX, dy = target.Y - worldY;
        double px = (settings.ImageWidth - 1) / 2.0 + settings.Jxx * dx + settings.Jxy * dy;
        double py = (settings.ImageHeight - 1) / 2.0 + settings.Jyx * dx + settings.Jyy * dy;
        double margin = originalSeed.RadiusPx * 1.7 + 8;
        if (double.Hypot(dx, dy) > options.MaxImageCenterDistanceMm || px < margin || py < margin ||
            px >= settings.ImageWidth - margin || py >= settings.ImageHeight - margin)
            throw new InvalidDataException("预测的11点不在当前有效视野内；未扩大搜索或请求运动。");
        return new(originalSeed with { Px = px, Py = py,
            SelectionSource = "CurrentRingAndMeasuredReference1_ProposalOnly_MustRefit11" }, originalSeed,
            worldX, worldY, options.ExpectedGapDeg, ring, measured);
    }
}
