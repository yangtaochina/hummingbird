using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

internal static class ReferencePredictionChecks
{
    public static int Run(string folder)
    {
        folder=Path.GetFullPath(folder);Directory.CreateDirectory(folder);int passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        bool Reject(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
        var session=RingScanChecks.SyntheticSession(folder);
        var settings=session.Settings;var options=new DatumDirectionOptions {BoundaryMode=DatumBoundaryMode.CircularFaceEdge};
        session.DirectionOptions=options;
        var ring=new RingScanSolver().Analyze(session);
        var one=FaceBoundaryChecks.Face(settings,folder,"reference1",RingFrameRole.Reference1,30,800,500);
        var eleven=FaceBoundaryChecks.Face(settings,folder,"reference11",RingFrameRole.Reference11,90,1000,500);
        session.Frames.Add(one);
        var target=new CaptureWaypoint(eleven.Name,eleven.Role,eleven.X,eleven.Y,eleven.Z,eleven.C);
        var oldSeed=eleven.FaceSeed! with {Px=1210,Py=600};
        var prediction=CurrentReferenceRegion.PredictEleven(one,settings,options,ring,target,oldSeed);
        Check(double.Hypot(prediction.Seed.Px-1000,prediction.Seed.Py-500)<2,"用独立生成的圆环与1点恢复11点已知图像位置");
        Check(prediction.ProposalOnly && !prediction.MotionRequested && prediction.OriginalSeed==oldSeed,"预测明确为图像搜索初值，保留原框，不请求运动");
        Check(Reject(()=>new DialAlignmentSolver().Analyze(session,HolePatternTemplate.LoadJson("templates/dial_18holes.json"))),
            "只有圆环和1点预测、没有真实11点时不能生成18孔");
        var oldFrame=eleven with {FaceSeed=oldSeed};
        Check(ReferenceRegionTracking.Inspect(oldFrame,settings,options).Inspection.Status==DatumHoleInspectionStatus.Rejected,
            "旧框范围外的11点复现拒绝，不先扩大0.20mm门槛");
        var tracked=ReferenceRegionTracking.Inspect(oldFrame,settings,options,fallbackSeed:prediction.Seed);
        Check(tracked.Inspection.Status==DatumHoleInspectionStatus.Detected && tracked.InitialAttempt?.Inspection.Status==DatumHoleInspectionStatus.Rejected &&
            double.Hypot(tracked.Inspection.Candidates.Single().Px-1000,tracked.Inspection.Candidates.Single().Py-500)<2,
            "旧框失败后按预测初值找到真实11点，并通过原圆边检查");
        var preserved=ReferenceRegionTracking.Inspect(eleven,settings,options,fallbackSeed:oldSeed);
        Check(preserved.InitialAttempt is null && !preserved.Adjusted && preserved.Frame.FaceSeed==eleven.FaceSeed,
            "旧框本来合格时保留原结果，不用预测覆盖");
        session.Frames.Add(tracked.Frame);
        var result=new DialAlignmentSolver().Analyze(session,HolePatternTemplate.LoadJson("templates/dial_18holes.json"));
        Check(result.Targets.Count==18 && Math.Abs(result.Direction.GapDeg-60)<.03,"六张独立已知几何图通过最终双参考定向及18孔定位");
        Check(Reject(()=>CurrentReferenceRegion.PredictEleven(one,settings,options,ring,target with {C=.1},oldSeed)),"目标C变化不预测");
        Check(Reject(()=>CurrentReferenceRegion.PredictEleven(one,settings,options,ring,target with {Z=.1},oldSeed)),"目标Z变化不预测");
        Check(Reject(()=>CurrentReferenceRegion.PredictEleven(one,settings,options,ring with {RmsMm=1},target,oldSeed)),"不合格圆环不预测");
        Check(Reject(()=>CurrentReferenceRegion.PredictEleven(one with {FaceSeed=null},settings,options,ring,target,oldSeed)),"未独立识别的1点不能预测");
        Check(Reject(()=>CurrentReferenceRegion.PredictEleven(one,settings,options,ring with {CenterX=ring.CenterX+3},target,oldSeed)),"1点与圆环径向关系不符不预测");
        Check(Reject(()=>CurrentReferenceRegion.PredictEleven(one,settings,options,ring,target with {X=target.X+5},oldSeed)),"预测不在当前有效视野时不扩大搜索");
        var offset=FaceBoundaryChecks.Face(settings,folder,"reference11_offset",RingFrameRole.Reference11,90.5,1000,500);
        var offsetTarget=new CaptureWaypoint(offset.Name,offset.Role,offset.X,offset.Y,offset.Z,offset.C);
        var offsetPrediction=CurrentReferenceRegion.PredictEleven(one,settings,options,ring,offsetTarget,offset.FaceSeed! with {Px=1210});
        var independent=ReferenceRegionTracking.Inspect(offset with {FaceSeed=offset.FaceSeed! with {Px=1210}},settings,options,fallbackSeed:offsetPrediction.Seed);
        var varied=new RingScanSession {Settings=settings,DirectionOptions=options,Frames=session.Frames.Where(f=>f.Role!=RingFrameRole.Reference11).Append(independent.Frame).ToList()};
        var actual=new DialAlignmentSolver().Analyze(varied,HolePatternTemplate.LoadJson("templates/dial_18holes.json"));
        Check(Math.Abs(actual.Direction.GapDeg-60.5)<.03,"最终角度来自11点实际测量，未把预测60度强加为实测结果");
        string blankPath=Path.Combine(folder,"missing11.bmp");RingScanSolver.SaveFrame(blankPath,new(new byte[1920*1200],1920,1200,DateTime.Now));
        var missing=oldFrame with {ImagePath=blankPath,FaceSeed=oldSeed with {ImageSha256=LaserSpotCalibration.Hash(blankPath)}};
        var noCircle=ReferenceRegionTracking.Inspect(missing,settings,options,fallbackSeed:prediction.Seed);
        Check(noCircle.Inspection.Status!=DatumHoleInspectionStatus.Detected && !noCircle.Adjusted,"预测正确但实际照片没有圆边时仍拒绝");
        var failed=new RingScanSession {Settings=settings,DirectionOptions=options,Frames=session.Frames.Where(f=>f.Role!=RingFrameRole.Reference11).Append(noCircle.Frame).ToList()};
        Check(Reject(()=>new DialAlignmentSolver().Analyze(failed,HolePatternTemplate.LoadJson("templates/dial_18holes.json"))),"无合格11点不因存在预测而放行18孔");
        string source="资料收集/工件_20261117_112539654/整件_3acc0f792fde4b639a14921455ef5de0/本件资料/定位/整组记录.json";
        Check(Review(source,Path.Combine(folder,"site"))==0,"本次真实六图回放完成18孔及补偿范围核对，未操作设备");
        Console.WriteLine($"Reference prediction: {passed} passed; simulated geometry/read-only images only.");return 0;
    }

    public static int Review(string source, string folder)
    {
        folder=Path.GetFullPath(folder);Directory.CreateDirectory(folder);
        var session=RingScanSession.Load(Path.GetFullPath(source));
        var ring=new RingScanSolver().Analyze(new RingScanSession {Settings=session.Settings,Frames=session.Frames.Where(f=>f.Role==RingFrameRole.RingArc).ToList()});
        var one=session.Frames.Single(f=>f.Role==RingFrameRole.Reference1);
        var eleven=session.Frames.Single(f=>f.Role==RingFrameRole.Reference11);
        var target=new CaptureWaypoint(eleven.Name,eleven.Role,eleven.X,eleven.Y,eleven.Z,eleven.C);
        var prediction=CurrentReferenceRegion.PredictEleven(one,session.Settings,session.DirectionOptions,ring,target,eleven.FaceSeed!);
        var tracked=ReferenceRegionTracking.Inspect(eleven,session.Settings,session.DirectionOptions,fallbackSeed:prediction.Seed);
        File.WriteAllText(Path.Combine(folder,"prediction.json"),JsonSerializer.Serialize(prediction,new JsonSerializerOptions {WriteIndented=true}));
        File.WriteAllText(Path.Combine(folder,"tracked.json"),JsonSerializer.Serialize(tracked,new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"prediction={prediction.Seed.Px:F2},{prediction.Seed.Py:F2}; {tracked.Inspection.Status}; proposals={tracked.ProposalCount}; {string.Join(";",tracked.Inspection.Diagnostics)}");
        if(tracked.Inspection.Status!=DatumHoleInspectionStatus.Detected)return 1;
        // 用实际已有曝光重放原选优策略；不重新拍照，不只验证手挑的一张图。
        string job=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!,"..","..",".."));
        var exposureFolder=Directory.GetDirectories(job,"曝光选优_参考11点_*").SingleOrDefault();
        if(exposureFolder is not null)
        {
            var reviews=new List<object>();ExposureQuality? best=null;double selected=0;
            foreach(double exposure in new[]{500.0,125,250,1000,2000})
            {
                var trial=RingScanSession.Load(Path.Combine(exposureFolder,exposure+"us.capture.json")).Frames.Single();
                var review=ReferenceRegionTracking.Inspect(trial,session.Settings,session.DirectionOptions,fallbackSeed:prediction.Seed);
                var quality=ExposureSelectionPolicy.Measure(RingScanSolver.LoadFrame(trial.ImagePath),review.Frame.FaceSeed) with
                    {FeatureDetected=review.Inspection.Status==DatumHoleInspectionStatus.Detected};
                if(best is null||ExposureSelectionPolicy.Better(quality,best)){best=quality;selected=exposure;tracked=review;}
                reviews.Add(new{ExposureUs=exposure,Quality=quality,Review=review});
                Console.WriteLine($"11 exposure {exposure}: {review.Inspection.Status}, score={quality.Score:F2}");
            }
            File.WriteAllText(Path.Combine(folder,"exposure-review.json"),JsonSerializer.Serialize(new {SelectedExposureUs=selected,Trials=reviews},new JsonSerializerOptions{WriteIndented=true}));
            if(best?.FeatureDetected!=true)return 1;
            Console.WriteLine("Selected 11 exposure="+selected+"us");
        }
        session.Frames[session.Frames.IndexOf(eleven)]=tracked.Frame;
        var result=new DialAlignmentSolver().Analyze(session,HolePatternTemplate.LoadJson("templates/dial_18holes.json"));
        File.WriteAllText(Path.Combine(folder,"alignment.json"),JsonSerializer.Serialize(result,new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine($"18-hole preview: {result.Targets.Count}; gap={result.Direction.GapDeg:F5}; RMS={result.Ring.RmsMm*1000:F2}um; 11={result.Direction.Reference11.Px:F2},{result.Direction.Reference11.Py:F2}; spread={result.Direction.Reference11.CenterSpreadMm*1000:F2}um");
        string laserPath=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!,"..","落点来源","激光落点.json"));
        string runSource=Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(source))!,"..","..","运行来源.json"));
        if(File.Exists(laserPath)&&File.Exists(runSource))
        {
            var laser=LaserSpotCalibration.Load(laserPath);
            var compensated=LaserSpotCompensation.Preview(laser,session.Settings,result.Ring.ReferenceZ,result.Ring.ReferenceC,result.Targets);
            using var doc=JsonDocument.Parse(File.ReadAllText(runSource));
            var profile=doc.RootElement.GetProperty("Profile").Deserialize<ProductionStationProfile>()!;profile.Validate();
            if(compensated.Targets.Count!=18||compensated.Targets.Any(p=>!profile.Envelope!.Contains(p.LaserCommandX,p.LaserCommandY)))
                throw new InvalidDataException("本次补偿目标超出核定范围。");
            File.WriteAllText(Path.Combine(folder,"compensation-preview.json"),JsonSerializer.Serialize(compensated,new JsonSerializerOptions{WriteIndented=true}));
            Console.WriteLine("18 compensated targets within recorded approved envelope; no NC request, no laser.");
        }
        return 0;
    }
}
