using System.Text.Json;
using Hummingbird.Core;

namespace Hummingbird.Vision;

public sealed record ReusableCaptureBaselineReview(RingScanSession Session, DialAlignmentPreview Alignment,
    ReferenceRegionReview Reference1, ReferenceRegionReview Reference11, ReferenceRegionPrediction? Prediction)
{
    public bool RebuiltRegions => Reference1.Adjusted || Reference11.Adjusted;
    public bool CurrentWorkpieceCoordinates => false;
}

/// <summary>复用路线前独立复核全部六图；仅返回内存副本，不改写原图、原记录或请求设备动作。</summary>
public static class ReusableCaptureBaseline
{
    public static ReusableCaptureBaselineReview Prepare(RingScanSession source, HolePatternTemplate template,
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        if(source.Version!=1 || source.Frames.Count!=6 || source.DirectionOptions.BoundaryMode!=DatumBoundaryMode.CircularFaceEdge)
            throw new InvalidDataException("复用基准须为完整六步实体圆边记录。");
        _=GuidedCapture.Route(source);
        var copy=JsonSerializer.Deserialize<RingScanSession>(JsonSerializer.Serialize(source))!;
        foreach(var frame in copy.Frames){ct.ThrowIfCancellationRequested();RingCaptureArchive.VerifyImage(frame);}
        var one=copy.Frames.Single(f=>f.Role==RingFrameRole.Reference1);
        var eleven=copy.Frames.Single(f=>f.Role==RingFrameRole.Reference11);
        if(one.FaceSeed is null || eleven.FaceSeed is null)throw new InvalidDataException("复用基准缺少两参考框初值，未自动猜测。");
        var first=ReferenceRegionTracking.Inspect(one,copy.Settings,copy.DirectionOptions,ct);
        Require(first);
        var ring=new RingScanSolver().Analyze(new RingScanSession {Settings=copy.Settings,Frames=copy.Frames.Where(f=>f.Role==RingFrameRole.RingArc).ToList()});
        var last=ReferenceRegionTracking.Inspect(eleven,copy.Settings,copy.DirectionOptions,ct);
        ReferenceRegionPrediction? prediction=null;
        if(last.Inspection.Status==DatumHoleInspectionStatus.Rejected)
        {
            prediction=CurrentReferenceRegion.PredictEleven(first.Frame,copy.Settings,copy.DirectionOptions,ring,
                new(eleven.Name,eleven.Role,eleven.X,eleven.Y,eleven.Z,eleven.C),eleven.FaceSeed);
            last=ReferenceRegionTracking.Inspect(eleven,copy.Settings,copy.DirectionOptions,ct,prediction.Seed);
        }
        Require(last);
        copy.Frames[copy.Frames.IndexOf(one)]=first.Frame;
        copy.Frames[copy.Frames.IndexOf(eleven)]=last.Frame;
        ct.ThrowIfCancellationRequested();
        var alignment=new DialAlignmentSolver().Analyze(copy,template);
        if(alignment.Targets.Count!=18)throw new InvalidDataException("基准六图未完整生成18孔预览，不能复用。");
        return new(copy,alignment,first,last,prediction);

        static void Require(ReferenceRegionReview review)
        {
            if(review.Inspection.Status!=DatumHoleInspectionStatus.Detected)
                throw new InvalidDataException(review.Frame.Name+"基准未通过："+review.Inspection.Message+" "+string.Join("；",review.Inspection.Diagnostics));
        }
    }
}
