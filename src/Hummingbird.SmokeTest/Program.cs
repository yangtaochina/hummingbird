using Hummingbird.Core;
using Hummingbird.Devices.OpcUa;
using Hummingbird.Devices.Sim;
using Hummingbird.Vision;

if(args.FirstOrDefault()=="--station-readiness")
    return await StationReadinessChecks.Run();

if(args.FirstOrDefault()=="--elliptical-references")
    return EllipticalReferenceChecks.Run(args[1]);

if(args.FirstOrDefault()=="--vision-compatibility")
    return VisionCompatibilityChecks.Run(args[1]);

if(args.FirstOrDefault()=="--automatic-optics")
    return await AutomaticOpticsChecks.Run();

if(args.FirstOrDefault()=="--rework-retry")
    return ReworkRetryChecks.Run(args[1]);

if(args.FirstOrDefault()=="--reference-view-review")
    return ReferenceTrackingChecks.Review(args[1],args[2],args.ElementAtOrDefault(3),reacquireInView:true);

if(args.FirstOrDefault()=="--production-rework")
    return await ProductionReworkChecks.Run(args[1]);

if(args.FirstOrDefault()=="--reusable-baseline")
    return ReusableBaselineChecks.Run(args[1]);

if(args.FirstOrDefault()=="--reference-prediction")
    return ReferencePredictionChecks.Run(args[1]);

if(args.FirstOrDefault()=="--reference-prediction-review")
    return ReferencePredictionChecks.Review(args[1],args[2]);

if(args.FirstOrDefault()=="--reference-tracking")
    return ReferenceTrackingChecks.Run(args[1]);

if(args.FirstOrDefault()=="--reference-tracking-review")
    return ReferenceTrackingChecks.Review(args[1],args[2],args.ElementAtOrDefault(3));

if(args.FirstOrDefault()=="--prepare-station-return")
{
    var profile=System.Text.Json.JsonSerializer.Deserialize<StationReturnProfile>(File.ReadAllText(args[1]))!;
    File.WriteAllText(args[2],StationReturnNcProgram.Generate(profile),System.Text.Encoding.ASCII);
    Console.WriteLine("Generated station return NC for review only; no connection, upload, or motion.");return 0;
}

if(args.FirstOrDefault()=="--station-return")
    return await StationReturnChecks.Run();

if(args.FirstOrDefault()=="--prepare-master-review")
{
    var profile=System.Text.Json.JsonSerializer.Deserialize<ProductionStationProfile>(File.ReadAllText(args[1]))!;
    profile.Validate();
    File.WriteAllText(args[2],ContinuousNcProgram.Generate(profile.Envelope!,profile.SessionToken),System.Text.Encoding.ASCII);
    Console.WriteLine("Generated review text only; no device connection, upload, or motion.");return 0;
}

if(args.FirstOrDefault()=="--spot-recenter-review")
{
    try{return SpotRecenterReview.Run(args[1],args[2],args[3]);}
    catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
}

if(args.FirstOrDefault()=="--laser-spot-review")
{
    try{return LaserSpotReview.Run(args[1],args[2]);}
    catch(Exception ex){Console.Error.WriteLine(ex.Message);return 1;}
}

if(args.FirstOrDefault()=="--xy-evidence-review")
    return XyEvidenceReview.Run(args[1],args[2],args.ElementAtOrDefault(3));

if(args.FirstOrDefault()=="--ring-exposure")
    return RingExposureChecks.Run(args[1],args[2]);

if(args.FirstOrDefault()=="--field-capture-review")
    return FieldCaptureReview.Run(args[1],args[2],args.ElementAtOrDefault(3));

if(args.FirstOrDefault()=="--connected-production")
    return await ConnectedProductionChecks.Run(args.ElementAtOrDefault(1));

if(args.FirstOrDefault()=="--field-evidence")
    return FieldEvidenceChecks.Run(args.ElementAtOrDefault(1));

if(args.FirstOrDefault()=="--continuous-production")
    return await ContinuousProductionChecks.Run(args.ElementAtOrDefault(1));

if (args.FirstOrDefault() == "--xy-micro")
    return await XyMicroChecks.Run();

if (args.FirstOrDefault() == "--exposure-selection")
    return await ExposureSelectionChecks.Run();

if (args.FirstOrDefault() == "--guided-capture")
    return await GuidedCaptureChecks.Run();

if (args.FirstOrDefault() == "--face-seed-review")
{
    var data=RingScanSession.Load(Path.GetFullPath(args[1]));
    var frame=data.Frames.Single(f=>f.Role==RingFrameRole.Reference1);
    var initial=frame.FaceSeed??throw new InvalidDataException("缺少人工初选范围。");
    var reviews=new List<DatumHoleInspection>();
    foreach(var (dx,dy) in new[]{(0.0,0.0),(5.0,0.0),(0.0,-5.0),(0.0,5.0)})
    foreach(double ratio in new[]{1.0,1.05,1.10,1.15})
    {
        var candidate=frame with {FaceSeed=initial with {Px=initial.Px+dx,Py=initial.Py+dy,RadiusPx=initial.RadiusPx*ratio,
            SelectionSource="OfflineRoiReview_NotOperatorConfirmed"}};
        var review=new DatumDirectionSolver().Inspect(candidate,data.Settings,data.DirectionOptions);reviews.Add(review);
        Console.WriteLine($"ROI ({candidate.FaceSeed!.Px:F1},{candidate.FaceSeed.Py:F1}) r={candidate.FaceSeed.RadiusPx:F1} {review.Status} "
            +string.Join(";",review.Candidates.Select(d=>$"center({d.Px:F2},{d.Py:F2}) support{d.AngularSupportRatio:P1} spread{d.CenterSpreadMm*1000:F2}um")));
    }
    File.WriteAllText(args[2],System.Text.Json.JsonSerializer.Serialize(new {Kind="FaceSeedReview",PreviewOnly=true,
        OperatorConfirmed=false,Reviews=reviews},new System.Text.Json.JsonSerializerOptions {WriteIndented=true}));
    return 0;
}

if (args.FirstOrDefault() == "--alignment-preview")
{
    // 离线报告工具：不创建设备，不进行任何轴运动或激光读写。
    try
    {
        var data=RingScanSession.Load(Path.GetFullPath(args[1]));
        var ring=new RingScanSolver().Analyze(new RingScanSession {Settings=data.Settings,
            Frames=data.Frames.Where(f=>f.Role==RingFrameRole.RingArc).ToList()});
        var inspections=data.Frames.Where(f=>f.Role!=RingFrameRole.RingArc)
            .Select(f=>new DatumDirectionSolver().Inspect(f,data.Settings,data.DirectionOptions)).ToArray();
        DialAlignmentPreview? result=null;string? rejection=null;
        try {result=new DialAlignmentSolver().Analyze(data,HolePatternTemplate.LoadJson("templates/dial_18holes.json"));}
        catch(InvalidDataException ex) {rejection=ex.Message;}
        LaserCompensationPreview? compensation=args.Length>3 && result is not null ? LaserSpotCompensation.Preview(
            LaserSpotCalibration.Load(Path.GetFullPath(args[3])),data.Settings,result.Ring.ReferenceZ,result.Ring.ReferenceC,result.Targets) : null;
        File.WriteAllText(Path.GetFullPath(args[2]),System.Text.Json.JsonSerializer.Serialize(new {
            Kind="DialAlignmentInspection",GeneratedAt=DateTimeOffset.Now,PreviewOnly=true,AlignmentCompleted=result is not null,
            Settings=data.Settings,SourceFrames=data.Frames,Ring=ring,ReferenceInspections=inspections,
            Result=result,Rejection=rejection,LaserCompensation=compensation
        },new System.Text.Json.JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine(FormattableString.Invariant($"OFFLINE ring center=({ring.CenterX:F6},{ring.CenterY:F6}) R={ring.RadiusMm:F6} RMS={ring.RmsMm:F6}; alignment completed={result is not null}"));
        foreach(var item in inspections)Console.WriteLine(item.Frame.Name+": "+item.Message+" "+string.Join("; ",item.Diagnostics));
        return 0;
    }
    catch(Exception ex) {Console.Error.WriteLine(ex);return 1;}
}

if (args.FirstOrDefault() == "--alignment")
    return AlignmentChecks.Run(args.ElementAtOrDefault(1));

if (args.FirstOrDefault() == "--face-boundary")
    return FaceBoundaryChecks.Run(args.ElementAtOrDefault(1));

if (args.FirstOrDefault() == "--hole-quality")
    return HoleQualityChecks.Run(args.ElementAtOrDefault(1));

if (args.FirstOrDefault() == "--laser-calibration")
    return await LaserCalibrationChecks.Run();

if (args.FirstOrDefault() == "--spot-trial")
    return await SpotTrialChecks.Run();

if (args.FirstOrDefault() == "--laser-compensation")
    return LaserCompensationChecks.Run(args.ElementAtOrDefault(1));

if (args.FirstOrDefault() == "--ring-scan")
    return RingScanChecks.Run(args.ElementAtOrDefault(1));

Console.Error.WriteLine("请指定检查项，例如 --ring-scan、--alignment、--continuous-production；完整列表见 README。");
return 2;
