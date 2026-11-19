using Hummingbird.Core;
using OpenCvSharp;

namespace Hummingbird.Vision;

public sealed record ReferenceRegionReview(RingScanFrame Frame, DatumHoleInspection Inspection,
    DatumCircleSeed? BaselineSeed, int ProposalCount, bool Adjusted)
{
    public ReferenceRegionReview? InitialAttempt { get; init; }
    public string SearchScope { get; init; } = "BaselineNeighbourhood";
    public bool MachineMotionRequested => false;
}

/// <summary>旧框只作局部搜索先验；当前图的候选必须重新通过原实体圆边全部筛选。只改图像范围，不请求机床运动。</summary>
public static class ReferenceRegionTracking
{
    public static ReferenceRegionReview Inspect(RingScanFrame frame, RingScanSettings settings, DatumDirectionOptions options,
        CancellationToken ct = default, DatumCircleSeed? fallbackSeed = null, bool reacquireInView = false)
    {
        var primary = InspectAround(frame, settings, options, ct);
        // 既有合格结果不改；旧范围歧义、输入错误或缺少原框时，也不借预测选择某一个圆。
        if (frame.FaceSeed is null || primary.Inspection.Status != DatumHoleInspectionStatus.Rejected)
            return primary;
        var previous=primary;
        if(fallbackSeed is not null)
        {
            var fallback = InspectAround(frame with { FaceSeed = fallbackSeed with { ImageSha256 = frame.FaceSeed.ImageSha256 } }, settings, options, ct);
            previous=fallback with { InitialAttempt = primary, Adjusted = fallback.Inspection.Status == DatumHoleInspectionStatus.Detected };
            if(previous.Inspection.Status!=DatumHoleInspectionStatus.Rejected)return previous;
        }
        if(!reacquireInView)return previous;
        var inView=InspectAround(frame,settings,options,ct,viewSearch:true);
        return inView with {InitialAttempt=previous,Adjusted=inView.Inspection.Status==DatumHoleInspectionStatus.Detected,
            SearchScope="CurrentValidCameraView_UniqueGeometryOnly"};
    }

    private static ReferenceRegionReview InspectAround(RingScanFrame frame, RingScanSettings settings, DatumDirectionOptions options,
        CancellationToken ct,bool viewSearch=false,bool adaptive=false,bool elliptic=false)
    {
        ct.ThrowIfCancellationRequested();
        var solver = new DatumDirectionSolver();
        var original = solver.Inspect(frame, settings, options);
        ct.ThrowIfCancellationRequested();
        var baseline = frame.FaceSeed;
        if (original.Status != DatumHoleInspectionStatus.Rejected || baseline is null ||
            options.BoundaryMode != DatumBoundaryMode.CircularFaceEdge || frame.Role == RingFrameRole.RingArc)
            return new(frame, original, baseline, 0, false);
        RingCaptureArchive.VerifyImage(frame);
        using var image = Cv2.ImDecode(File.ReadAllBytes(frame.ImagePath), ImreadModes.Grayscale);
        double det = settings.Jxx * settings.Jyy - settings.Jxy * settings.Jyx;
        double Distance(double dx, double dy) => double.Hypot((settings.Jyy * dx - settings.Jxy * dy) / det,
            (-settings.Jyx * dx + settings.Jxx * dy) / det);
        // 图像重定位范围由既有有效视野决定，不改变XY物理微调上限。
        double anchorX=viewSearch?(image.Width-1)/2.0:baseline.Px,anchorY=viewSearch?(image.Height-1)/2.0:baseline.Py;
        double searchMm=viewSearch?options.MaxImageCenterDistanceMm:XyMicroMoveSession.MaximumOffsetMm;
        int reach = (int)Math.Ceiling(baseline.RadiusPx * (viewSearch?1.7:1.5) +
            Math.Max(Math.Abs(settings.Jxx) + Math.Abs(settings.Jxy), Math.Abs(settings.Jyx) + Math.Abs(settings.Jyy)) * searchMm);
        int left = Math.Max(0, (int)anchorX - reach), top = Math.Max(0, (int)anchorY - reach);
        var bounds = new Rect(left, top, Math.Min(image.Width - left, reach * 2 + 2), Math.Min(image.Height - top, reach * 2 + 2));
        using var local = new Mat(image, bounds);
        var seeds = new List<DatumCircleSeed>();
        int[] thresholds=[20,40,60,80,100,125,150,180,210];
        if(adaptive)
        {
            var values=new List<byte>();
            int height=local.Height,width=local.Width;
            for(int y=0;y<height;y+=4)for(int x=0;x<width;x+=4)values.Add(local.At<byte>(y,x));
            values.Sort();
            var levels=values.Distinct().ToArray();
            thresholds=(levels.Length<=32?levels.Select(v=>(int)v):
                new[]{.01,.02,.04,.06,.10,.20,.40,.60,.80,.90,.96,.98}.Select(q=>(int)values[(int)((values.Count-1)*q)])
                    .Concat(Enumerable.Range(1,16).Select(i=>values[0]+(values[^1]-values[0])*i/17)))
                .Where(v=>v>0 && v<255).Distinct().ToArray();
        }
        foreach (int threshold in thresholds)
        {
            ct.ThrowIfCancellationRequested();
            using var binary = new Mat(); Cv2.Threshold(local, binary, threshold, 255, ThresholdTypes.BinaryInv);
            Cv2.FindContours(binary, out Point[][] contours, out _, RetrievalModes.List, ContourApproximationModes.ApproxNone);
            foreach (var contour in contours.Where(c => c.Length >= 20))
            {
                var ellipse = Cv2.FitEllipse(contour);
                double radius = (ellipse.Size.Width + ellipse.Size.Height) / 4;
                double x = ellipse.Center.X + left, y = ellipse.Center.Y + top;
                if (radius < baseline.RadiusPx * .8 || radius > baseline.RadiusPx * 1.2 ||
                    Math.Min(ellipse.Size.Width, ellipse.Size.Height) <= 0 ||
                    Math.Max(ellipse.Size.Width, ellipse.Size.Height) / Math.Min(ellipse.Size.Width, ellipse.Size.Height) > 1.4 ||
                    Distance(x - anchorX, y - anchorY) > searchMm ||
                    seeds.Any(s => double.Hypot(s.Px - x, s.Py - y) < 3 && Math.Abs(s.RadiusPx - radius) < 3 &&
                        (!elliptic || s.Ellipse is {} prior && Math.Abs(prior.WidthPx-ellipse.Size.Width)<3 &&
                            Math.Abs(prior.HeightPx-ellipse.Size.Height)<3 && Math.Abs(Math.IEEERemainder(prior.AngleDeg-ellipse.Angle,180))<3))) continue;
                seeds.Add(baseline with { Px = x, Py = y, RadiusPx = radius,
                    Ellipse=elliptic?new(ellipse.Size.Width,ellipse.Size.Height,ellipse.Angle):null,
                    SelectionSource = elliptic?"CompatibilityEllipseProposal_OriginalBoundaryChecks":adaptive?"CompatibilityAdaptiveProposal_OriginalBoundaryChecks":viewSearch?"CurrentValidViewProposal_OriginalBoundaryChecks":"BoundedCurrentImageProposal_OriginalBoundaryChecks" });
            }
        }
        // 候选过多时整体拒绝，不截断候选后假称唯一。
        if (seeds.Count > 32) return new(frame, original with { Message = "参考圆搜索候选过多，未自动选择。" }, baseline, seeds.Count, false);
        var accepted = new List<(RingScanFrame Frame, DatumHoleInspection Inspection)>();
        foreach (var seed in seeds)
        {
            ct.ThrowIfCancellationRequested();
            var candidate = frame with { FaceSeed = seed };
            var check = solver.Inspect(candidate, settings, options);
            if (check.Status != DatumHoleInspectionStatus.Detected) continue;
            var d = check.Candidates.Single();
            if (Distance(d.Px - anchorX, d.Py - anchorY) <= searchMm)
                accepted.Add((candidate, check));
        }
        if(accepted.Count==0 && !adaptive)
            return InspectAround(frame,settings,options,ct,viewSearch,adaptive:true);
        if(accepted.Count==0 && adaptive && !elliptic)
            return InspectAround(frame,settings,options,ct,viewSearch,adaptive:true,elliptic:true);
        if (accepted.Count == 0) return new(frame, original, baseline, seeds.Count, false);
        var first = accepted[0].Inspection.Candidates.Single();
        if (accepted.Any(a => Distance(a.Inspection.Candidates.Single().Px - first.Px, a.Inspection.Candidates.Single().Py - first.Py) > .010))
            return new(frame, original with { Status = DatumHoleInspectionStatus.Ambiguous, Message = "参考圆搜索范围内存在多个通过检查的圆，未自动选择。" }, baseline, seeds.Count, false);
        var best = accepted.OrderBy(a => a.Inspection.Candidates.Single().CenterSpreadMm).First();
        return new(best.Frame, best.Inspection, baseline, seeds.Count, true);
    }
}
