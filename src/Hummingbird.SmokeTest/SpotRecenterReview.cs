using System.Text.Json;
using Hummingbird.Core;

internal static class SpotRecenterReview
{
    private sealed record Center(double X,double Y,double SpreadPx,IReadOnlyList<LaserSpotReview.Fit> Fits);
    public static int Run(string evidencePath,string calibrationPath,string outputPath)
    {
        const double previewLimitMm=.015; // Local preview criterion, never a machining tolerance or full-cell approval.
        var calibration=LaserSpotCalibration.Load(Path.GetFullPath(calibrationPath));
        var settings=calibration.OpticalSettings;double det=settings.Jxx*settings.Jyy-settings.Jxy*settings.Jyx;
        double NormMm(double x,double y)=>double.Hypot((settings.Jyy*x-settings.Jxy*y)/det,(-settings.Jyx*x+settings.Jxx*y)/det);
        using var sourceDocument=JsonDocument.Parse(File.ReadAllText(calibration.SourceTrialPath));var sourceTrial=sourceDocument.RootElement;
        var start=sourceTrial.GetProperty("StartPosition");double startX=start.GetProperty("X").GetDouble(),startY=start.GetProperty("Y").GetDouble();
        var offset=LaserSpotCompensation.Offset(calibration,settings);
        var beforeSource=new RingScanFrame("OriginalTrialMark",calibration.AfterImagePath,startX,startY,calibration.ReferenceZ,calibration.ReferenceC);
        var sourceFrame=RingCaptureArchive.BindImage(beforeSource,settings,sourceTrial.GetProperty("AfterImage").GetProperty("CameraTime").GetDateTime(),
            settings.ImageWidth,settings.ImageHeight,AxisCoordinateSource.OpcUaReadback,AxisCoordinateSource.ManualPanelEntry,null,"OfflineExistingMarkReview");
        var sourceSample=new XyEvidenceReview.Sample(sourceFrame.ImagePath,sourceFrame);
        Center Measure(string path,double x,double y)
        {
            var fits=LaserSpotReview.MeasureExistingMark(path,x,y,96.22594462864899);
            double Median(IEnumerable<double> values){var a=values.Order().ToArray();return a[a.Length/2];}
            double px=Median(fits.Select(f=>f.X)),py=Median(fits.Select(f=>f.Y));
            return new(px,py,fits.Max(f=>double.Hypot(f.X-px,f.Y-py)),fits);
        }
        var sourceCenter=Measure(calibration.AfterImagePath,calibration.PixelX,calibration.PixelY);
        using var document=JsonDocument.Parse(File.ReadAllText(evidencePath));
        var groups=document.RootElement.GetProperty("Groups").EnumerateArray().ToArray();
        if(groups.Length!=2)throw new InvalidDataException("往返复核需要居中和返回两组。");
        var reports=new List<object>();var allCenters=new List<Center>();bool allPassed=true;
        for(int index=0;index<groups.Length;index++)
        {
            var group=groups[index];var env=group.GetProperty("Environment");var p=env.GetProperty("StartPosition");
            calibration.ValidateOptics(env.GetProperty("Settings").Deserialize<RingScanSettings>()!);
            calibration.ValidateReference(p.GetProperty("Z").GetDouble(),p.GetProperty("C").GetDouble());
            double x=p.GetProperty("X").GetDouble(),y=p.GetProperty("Y").GetDouble();
            double expectedX=index==0?Math.Round(startX-offset.X,4):Math.Round(startX,4);
            double expectedY=index==0?Math.Round(startY-offset.Y,4):Math.Round(startY,4);
            bool poseMatches=Math.Abs(x-expectedX)<=.002&&Math.Abs(y-expectedY)<=.002;
            if(!poseMatches)throw new InvalidDataException("复核照片的实际位置不符合本次居中/返回目标。");
            if(group.GetProperty("LaserReadback").GetProperty("Emission").GetInt32()!=0)throw new InvalidDataException("复核组未读回关光。");
            var imported=group.GetProperty("Calibration").Deserialize<LaserSpotCalibration>()!;
            if(imported.SourceTrialSha256!=calibration.SourceTrialSha256 || imported.PixelX!=calibration.PixelX || imported.PixelY!=calibration.PixelY)
                throw new InvalidDataException("现场导入落点与本次来源不符。");
            double dx=x-startX,dy=y-startY,expectedPx=settings.Jxx*dx+settings.Jxy*dy,expectedPy=settings.Jyx*dx+settings.Jyy*dy;
            var measured=new List<object>();
            foreach(var image in group.GetProperty("Images").EnumerateArray().Where(im=>im.GetProperty("Evidence").GetProperty("Frame").GetProperty("Capture").GetProperty("RequestedExposureUs").GetDouble()==2000))
            {
                string path=image.GetProperty("File").GetString()!;var data=image.GetProperty("Evidence");
                var frame=data.GetProperty("Frame").Deserialize<RingScanFrame>()! with {ImagePath=path};RingCaptureArchive.VerifyImage(frame);
                if(!data.GetProperty("StableSinceStart").GetBoolean())throw new InvalidDataException("复核采图期间位置不稳。");
                var center=Measure(path,calibration.PixelX+expectedPx,calibration.PixelY+expectedPy);allCenters.Add(center);
                double targetX=index==0?(settings.ImageWidth-1)/2.0:sourceCenter.X;
                double targetY=index==0?(settings.ImageHeight-1)/2.0:sourceCenter.Y;
                double errorX=center.X-targetX,errorY=center.Y-targetY,errorMm=NormMm(errorX,errorY);
                XyEvidenceReview.Motion? registration=null;string? registrationError=null;
                try{registration=XyEvidenceReview.Measure(sourceSample,new(path,frame));}catch(InvalidDataException ex){registrationError=ex.Message;}
                double? responseError=registration is null?null:NormMm(registration.Dx-expectedPx,registration.Dy-expectedPy);
                bool passed=errorMm<=previewLimitMm && center.SpreadPx<=5 && responseError is <=previewLimitMm;
                allPassed&=passed;
                measured.Add(new {File=path,center.X,center.Y,center.SpreadPx,ErrorPxX=errorX,ErrorPxY=errorY,ErrorMm=errorMm,
                    ResponseErrorMm=responseError,TemplateRegistration=registration,TemplateRejection=registrationError,
                    LocalPreviewCriterionPassed=passed,Fits=center.Fits});
                Console.WriteLine(FormattableString.Invariant($"{(index==0?"CENTER":"RETURN")} {Path.GetFileName(path)} center({center.X:F4},{center.Y:F4}) err={errorMm*1000:F3}um responseErr={responseError*1000:F3}um"));
            }
            if(measured.Count!=4)throw new InvalidDataException("同档曝光与三张重复图不完整。");
            reports.Add(new {Phase=index==0?"N40Center":"N60Return",ActualX=x,ActualY=y,ExpectedX=expectedX,ExpectedY=expectedY,
                PoseMatches=poseMatches,ExpectedPixelShiftX=expectedPx,ExpectedPixelShiftY=expectedPy,Images=measured});
        }
        File.WriteAllText(outputPath,JsonSerializer.Serialize(new {Kind="LaserSpotRecenterReview",HardwareAccess=false,
            LocalPreviewCheckPassed=allPassed,LocalPreviewLimitMm=previewLimitMm,CriterionScope="Local positioning preview only; not machining tolerance or full-cell commissioning",
            ApprovedForMachining=false,Full18HoleMotionVerified=false,Calibration=calibration,SourceCenter=sourceCenter,Groups=reports},
            new JsonSerializerOptions {WriteIndented=true}));
        return allPassed?0:1;
    }
}
