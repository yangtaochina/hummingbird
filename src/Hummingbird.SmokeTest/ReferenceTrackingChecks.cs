using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

internal static class ReferenceTrackingChecks
{
    public static int Run(string folder)
    {
        folder=Path.GetFullPath(folder);Directory.CreateDirectory(folder);int passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        var settings=new RingScanSettings();var options=new DatumDirectionOptions {BoundaryMode=DatumBoundaryMode.CircularFaceEdge};
        var image=FaceBoundaryChecks.Face(settings,folder,"known-face",RingFrameRole.Reference1,30,800,500);
        string hash=LaserSpotCalibration.Hash(image.ImagePath);
        var unchanged=ReferenceRegionTracking.Inspect(image,settings,options);
        Check(unchanged.Inspection.Status==DatumHoleInspectionStatus.Detected && !unchanged.Adjusted,"原框通过时保留原流程，不自动改框");
        var shifted=image with {FaceSeed=image.FaceSeed! with {Px=860,Py=450}};
        Check(new DatumDirectionSolver().Inspect(shifted,settings,options).Status==DatumHoleInspectionStatus.Rejected,"独立生成的0.13mm偏移会复现旧固定框失败");
        var found=ReferenceRegionTracking.Inspect(shifted,settings,options);
        Check(found.Adjusted && found.Inspection.Status==DatumHoleInspectionStatus.Detected &&
            double.Hypot(found.Inspection.Candidates.Single().Px-800,found.Inspection.Candidates.Single().Py-500)<2,
            "小幅放置偏移后恢复独立已知圆心，继续原实体圆边筛选");
        Check(hash==LaserSpotCalibration.Hash(image.ImagePath) && found.Frame.X==image.X && found.Frame.Y==image.Y &&
            found.Frame.Z==image.Z && found.Frame.C==image.C,"参考框跟随不修改原图、采图坐标或请求机床运动");
        var outside=ReferenceRegionTracking.Inspect(image with {FaceSeed=image.FaceSeed! with {Px=980}},settings,options);
        Check(!outside.Adjusted && outside.Inspection.Status!=DatumHoleInspectionStatus.Detected,"超过0.20mm范围不自动选圆");
        Check(ReferenceRegionTracking.Inspect(image with {FaceSeed=null},settings,options).Inspection.Status==DatumHoleInspectionStatus.Rejected,
            "没有基准框不进行无约束自动寻圆");
        Check(ReferenceRegionTracking.Inspect(image with {FaceSeed=image.FaceSeed! with {ImageSha256=new string('0',64)}},settings,options).Inspection.Status==DatumHoleInspectionStatus.InputError,
            "框与原图指纹不符不能借跟随流程绕过");
        var bright=FaceBoundaryChecks.Face(settings,folder,"bright-island",RingFrameRole.Reference1,30,800,500,plainBright:true);
        Check(ReferenceRegionTracking.Inspect(bright with {FaceSeed=bright.FaceSeed! with {Px=860,Py=450}},settings,options).Inspection.Status!=DatumHoleInspectionStatus.Detected,
            "无独立暗壁的高光岛仍不通过，不降低圆边门槛");
        var half=FaceBoundaryChecks.Face(settings,folder,"half-face",RingFrameRole.Reference1,30,800,500,halfOnly:true);
        Check(ReferenceRegionTracking.Inspect(half with {FaceSeed=half.FaceSeed! with {Px=860,Py=450}},settings,options).Inspection.Status!=DatumHoleInspectionStatus.Detected,
            "不完整圆周仍被拒绝");
        var other=FaceBoundaryChecks.Face(settings,folder,"other-face",RingFrameRole.Reference1,30,980,500);
        var firstPixels=RingScanSolver.LoadFrame(image.ImagePath);var secondPixels=RingScanSolver.LoadFrame(other.ImagePath);
        var both=new CameraFrame(firstPixels.Gray.Zip(secondPixels.Gray,(a,b)=>Math.Min(a,b)).ToArray(),1920,1200,DateTime.Now);
        string pair=Path.Combine(folder,"ambiguous-pair.bmp");RingScanSolver.SaveFrame(pair,both);
        var pairFrame=image with {ImagePath=pair,FaceSeed=image.FaceSeed! with {Px=890,ImageSha256=LaserSpotCalibration.Hash(pair)}};
        var pairReview=ReferenceRegionTracking.Inspect(pairFrame,settings,options);
        Check(pairReview.Inspection.Status==DatumHoleInspectionStatus.Ambiguous && !pairReview.Adjusted,"范围内存在两个合格圆时拒绝选择，不取最近者凑数");
        using(var canceled=new CancellationTokenSource())
        {
            canceled.Cancel();bool rejected=false;
            try{ReferenceRegionTracking.Inspect(image,settings,options,canceled.Token);}catch(OperationCanceledException){rejected=true;}
            Check(rejected,"取消后不继续图像搜索");
        }
        string current=Path.GetFullPath("资料收集/工件_20261117_104157495/hummingbird_capture_session.autosave.json");
        var site=RingScanSession.Load(current);var siteFrame=site.Frames.Single();
        Check(new DatumDirectionSolver().Inspect(siteFrame,site.Settings,site.DirectionOptions).Status==DatumHoleInspectionStatus.Rejected,
            "回放今天第一张原图，复现旧框被拒绝");
        var siteReview=ReferenceRegionTracking.Inspect(siteFrame,site.Settings,site.DirectionOptions);
        var sitePoint=siteReview.Inspection.Candidates.Single();
        Check(siteReview.Adjusted && siteReview.Inspection.Status==DatumHoleInspectionStatus.Detected && sitePoint.AngularSupportRatio>=.75 &&
            sitePoint.CenterSpreadMm<site.DirectionOptions.MaxFaceCenterSpreadMm,"同一张现场图换跟随初值后通过原圆周与稳定性标准");
        File.WriteAllText(Path.Combine(folder,"site-review.json"),JsonSerializer.Serialize(siteReview,new JsonSerializerOptions {WriteIndented=true}));
        ExposureQuality? best=null;double selected=0;var trials=new List<object>();
        foreach(double exposure in new[]{500.0,125,250,1000,2000})
        {
            string path=Path.Combine(Path.GetDirectoryName(current)!,"曝光选优_参考1点_105027899",exposure+"us.capture.json");
            var trial=RingScanSession.Load(path).Frames.Single();
            var review=ReferenceRegionTracking.Inspect(trial,site.Settings,site.DirectionOptions);
            var quality=ExposureSelectionPolicy.Measure(RingScanSolver.LoadFrame(trial.ImagePath),review.Frame.FaceSeed) with
                {FeatureDetected=review.Inspection.Status==DatumHoleInspectionStatus.Detected};
            if(best is null||ExposureSelectionPolicy.Better(quality,best)){best=quality;selected=exposure;}
            trials.Add(new {ExposureUs=exposure,Quality=quality,Review=review});
            Console.WriteLine($"SITE exposure {exposure}: {review.Inspection.Status}, score={quality.Score:F2}, saturation={quality.SaturatedFraction:P1}");
        }
        Check(best?.FeatureDetected==true && selected!=2000,"按实际跟随范围评分选出通过圆边的曝光，过曝失败档不胜出");
        File.WriteAllText(Path.Combine(folder,"site-exposure-review.json"),JsonSerializer.Serialize(new{SelectedExposureUs=selected,Trials=trials},new JsonSerializerOptions{WriteIndented=true}));
        RingCaptureArchive.SaveSidecar(found.Frame,settings,options);
        Check(RingScanSession.Load(RingCaptureArchive.SidecarPath(found.Frame.ImagePath)).DirectionOptions.BoundaryMode==DatumBoundaryMode.CircularFaceEdge,
            "配套JSON保存实体圆边模式，离线回放不误用旧暗孔模型");
        var wide=ReferenceRegionTracking.Inspect(image with {FaceSeed=image.FaceSeed! with {Px=980}},settings,options,reacquireInView:true);
        Check(wide.Inspection.Status==DatumHoleInspectionStatus.Detected && wide.SearchScope=="CurrentValidCameraView_UniqueGeometryOnly" &&
            double.Hypot(wide.Inspection.Candidates.Single().Px-800,wide.Inspection.Candidates.Single().Py-500)<2 && !wide.MachineMotionRequested,
            "原框之外但在有效视野内的独立已知圆可重新识别，不请求XY运动");
        var widePair=ReferenceRegionTracking.Inspect(pairFrame with {FaceSeed=pairFrame.FaceSeed! with {Px=1200}},settings,options,reacquireInView:true);
        Check(widePair.Inspection.Status==DatumHoleInspectionStatus.Ambiguous,"有效视野出现两个合格圆时仍拒绝，不靠扩展图像范围挑选其一");
        Check(ReferenceRegionTracking.Inspect(image with {FaceSeed=null},settings,options,reacquireInView:true).Inspection.Status!=DatumHoleInspectionStatus.Detected &&
            ReferenceRegionTracking.Inspect(image with {FaceSeed=image.FaceSeed! with {ImageSha256=new string('0',64)}},settings,options,reacquireInView:true).Inspection.Status==DatumHoleInspectionStatus.InputError,
            "视野重定位不能绕过缺少框先验和错误图像指纹");
        Check(ReferenceRegionTracking.Inspect(bright with {FaceSeed=bright.FaceSeed! with {Px=1200}},settings,options,reacquireInView:true).Inspection.Status!=DatumHoleInspectionStatus.Detected &&
            ReferenceRegionTracking.Inspect(half with {FaceSeed=half.FaceSeed! with {Px=1200}},settings,options,reacquireInView:true).Inspection.Status!=DatumHoleInspectionStatus.Detected,
            "扩展图像搜索仍拒绝高光岛和缺圆周支持");
        string rework=Path.GetFullPath("资料收集/补加工_6c6b896a_20261117_145158524/hummingbird_capture_session.autosave.json");
        var reworkSession=RingScanSession.Load(rework);var reworkFrame=reworkSession.Frames.Single();
        string reworkHash=LaserSpotCalibration.Hash(reworkFrame.ImagePath);
        Check(ReferenceRegionTracking.Inspect(reworkFrame,reworkSession.Settings,reworkSession.DirectionOptions).Inspection.Status!=DatumHoleInspectionStatus.Detected,
            "真实14:53补加工首图复现旧局部搜索失败");
        var recovered=ReferenceRegionTracking.Inspect(reworkFrame,reworkSession.Settings,reworkSession.DirectionOptions,reacquireInView:true);
        var recoveredPoint=recovered.Inspection.Candidates.Single();
        Check(recovered.Inspection.Status==DatumHoleInspectionStatus.Detected && recoveredPoint.AngularSupportRatio>=.75 &&
            recoveredPoint.CenterSpreadMm<reworkSession.DirectionOptions.MaxFaceCenterSpreadMm &&
            reworkHash==LaserSpotCalibration.Hash(reworkFrame.ImagePath) && recovered.Frame.X==reworkFrame.X && recovered.Frame.Y==reworkFrame.Y,
            "真实补加工首图通过原有圆周与稳定性标准，原图和机床坐标不变");
        File.WriteAllText(Path.Combine(folder,"rework-site-review.json"),JsonSerializer.Serialize(recovered,new JsonSerializerOptions{WriteIndented=true}));
        var reworkTrials=new List<object>();ExposureQuality? reworkBest=null;double reworkExposure=0;
        foreach(string path in Directory.GetFiles(Path.Combine(Path.GetDirectoryName(rework)!,"曝光选优_参考1点_145339181"),"*.capture.json"))
        {
            var candidate=RingScanSession.Load(path).Frames.Single();
            var review=ReferenceRegionTracking.Inspect(candidate,reworkSession.Settings,reworkSession.DirectionOptions,reacquireInView:true);
            var quality=ExposureSelectionPolicy.Measure(RingScanSolver.LoadFrame(candidate.ImagePath),review.Frame.FaceSeed) with {FeatureDetected=review.Inspection.Status==DatumHoleInspectionStatus.Detected};
            if(reworkBest is null || ExposureSelectionPolicy.Better(quality,reworkBest)){reworkBest=quality;reworkExposure=candidate.Capture!.RequestedExposureUs!.Value;}
            reworkTrials.Add(new{ExposureUs=candidate.Capture?.RequestedExposureUs,Quality=quality,Review=review});
        }
        Check(reworkTrials.Count==5 && reworkBest?.FeatureDetected==true,"补加工首图五档曝光按独立圆边检查选优，不用纹理峰差放行");
        Check(XyMicroMoveSession.MaximumOffsetMm==.20,"真实机床XY微调上限仍为0.20mm");
        File.WriteAllText(Path.Combine(folder,"rework-site-exposure-review.json"),JsonSerializer.Serialize(new{SelectedExposureUs=reworkExposure,Trials=reworkTrials},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Reference tracking: {passed} passed; images only, no machine connection. Selected site exposure={selected}us.");return 0;
    }

    public static int Review(string source, string output, string? optionsSource = null,bool reacquireInView=false)
    {
        var session = RingScanSession.Load(Path.GetFullPath(source));
        if(optionsSource is not null)session.DirectionOptions=RingScanSession.Load(Path.GetFullPath(optionsSource)).DirectionOptions;
        var results = new List<object>();
        foreach (var frame in session.Frames.Where(f => f.Role != RingFrameRole.RingArc))
        {
            var review = ReferenceRegionTracking.Inspect(frame, session.Settings, session.DirectionOptions,reacquireInView:reacquireInView);
            var detected = review.Inspection.Candidates.SingleOrDefault();
            Console.WriteLine($"{Path.GetFileName(frame.ImagePath)}: {review.Inspection.Status}, proposals={review.ProposalCount}, adjusted={review.Adjusted}; seed={review.Frame.FaceSeed?.Px:F2},{review.Frame.FaceSeed?.Py:F2},R={review.Frame.FaceSeed?.RadiusPx:F2}; center={detected?.Px:F2},{detected?.Py:F2}; support={detected?.AngularSupportRatio:P1}; spread={detected?.CenterSpreadMm * 1000:F2}um");
            results.Add(review);
        }
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        File.WriteAllText(output, JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }
}
