using System.Text.Json;
using System.IO.Compression;
using System.Text;
using Hummingbird.Core;
using Hummingbird.Vision;

internal static class VisionCompatibilityChecks
{
    public static int Run(string folder)
    {
        folder=Path.GetFullPath(folder);Directory.CreateDirectory(folder);int count=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
        bool Fails(Action action){try{action();return false;}catch(InvalidDataException){return true;}}
        var s=new RingScanSettings();var options=new DatumDirectionOptions{BoundaryMode=DatumBoundaryMode.CircularFaceEdge};
        var normal=FaceBoundaryChecks.Face(s,folder,"normal-reference",RingFrameRole.Reference1,30,800,500);
        var before=new DatumDirectionSolver().Inspect(normal,s,options);
        var normalTracked=ReferenceRegionTracking.Inspect(normal,s,options,reacquireInView:true);
        Check(!normalTracked.Adjusted && before.Candidates.Single().Px==normalTracked.Inspection.Candidates.Single().Px,"既有合格参考圆结果保持逐值一致");
        var pixels=RingScanSolver.LoadFrame(normal.ImagePath);
        string compressed=Path.Combine(folder,"mid-gray-reference.bmp");
        RingScanSolver.SaveFrame(compressed,pixels with{Gray=pixels.Gray.Select(v=>(byte)Math.Clamp(Math.Round(156+.13*(v-50)),0,255)).ToArray()});
        var mid=normal with{ImagePath=compressed,FaceSeed=normal.FaceSeed! with{Px=860,Py=450,ImageSha256=LaserSpotCalibration.Hash(compressed)}};
        var recovered=ReferenceRegionTracking.Inspect(mid,s,options,reacquireInView:true);
        Check(new DatumDirectionSolver().Inspect(mid,s,options).Status==DatumHoleInspectionStatus.Rejected,"窄灰度与偏移初值复现原框识别失败");
        File.WriteAllText(Path.Combine(folder,"reference-review.json"),JsonSerializer.Serialize(recovered,new JsonSerializerOptions{WriteIndented=true}));
        Check(recovered.Adjusted && recovered.Inspection.Status==DatumHoleInspectionStatus.Detected && recovered.Frame.FaceSeed!.SelectionSource.Contains("Compatibility"),"窄灰度候选可由自适应灰度补充发现，仍在原图严格复核");
        var center=recovered.Inspection.Candidates.Single();
        Check(double.Hypot(center.Px-800,center.Py-500)<2.5 && center.AngularSupportRatio>=.75 && center.CenterSpreadMm<.015,"独立已知真值误差受限，原75%圆周及稳定性门槛保留");
        Check(mid.X==recovered.Frame.X && mid.Y==recovered.Frame.Y && !recovered.MachineMotionRequested,"补充候选只改变图像初值，不改变机器坐标或请求运动");
        var half=FaceBoundaryChecks.Face(s,folder,"half",RingFrameRole.Reference1,30,800,500,halfOnly:true);
        Check(ReferenceRegionTracking.Inspect(half with{FaceSeed=half.FaceSeed! with{Px=860}},s,options,reacquireInView:true).Inspection.Status!=DatumHoleInspectionStatus.Detected,"缺半圈仍拒绝，不因兼容搜索外推圆心");
        var bright=FaceBoundaryChecks.Face(s,folder,"island",RingFrameRole.Reference1,30,800,500,plainBright:true);
        Check(ReferenceRegionTracking.Inspect(bright with{FaceSeed=bright.FaceSeed! with{Px=860}},s,options,reacquireInView:true).Inspection.Status!=DatumHoleInspectionStatus.Detected,"高光岛没有实体暗壁仍拒绝");
        var other=FaceBoundaryChecks.Face(s,folder,"second-reference",RingFrameRole.Reference1,30,1120,500);
        var otherPixels=RingScanSolver.LoadFrame(other.ImagePath);
        string pair=Path.Combine(folder,"two-mid-gray-faces.bmp");
        var pairPixels=pixels.Gray.ToArray();
        for(int y=350;y<650;y++)for(int x=970;x<1270;x++)pairPixels[y*1920+x]=otherPixels.Gray[y*1920+x];
        RingScanSolver.SaveFrame(pair,pixels with{Gray=pairPixels.Select(v=>(byte)Math.Clamp(Math.Round(156+.13*(v-50)),0,255)).ToArray()});
        foreach(double seedX in new[]{800.0,1120.0})
            Check(new DatumDirectionSolver().Inspect(normal with{ImagePath=pair,FaceSeed=normal.FaceSeed! with{Px=seedX,ImageSha256=LaserSpotCalibration.Hash(pair)}},s,options).Status==DatumHoleInspectionStatus.Detected,
                "歧义用例的两个圆分别通过原实体边界检查，真值有效");
        Check(ReferenceRegionTracking.Inspect(normal with{ImagePath=pair,FaceSeed=normal.FaceSeed! with{Px=960,ImageSha256=LaserSpotCalibration.Hash(pair)}},s,options,reacquireInView:true).Inspection.Status!=DatumHoleInspectionStatus.Detected,
            "自适应灰度下两个可行圆仍拒绝，不取最近候选冒充唯一");
        var clean=RingScanChecks.SyntheticSession(folder,prefix:"clean-");
        var ring=new RingScanSolver().Analyze(clean);
        Check(ring.CompatibilityRecovery is null && ring.FitVariants.Count==4,"合格四方位保留原拟合，记录原参数扰动结果");
        var dirty=RingScanSession.Load(Save(clean,"dirty-session.json"));
        var image=RingScanSolver.LoadFrame(dirty.Frames[0].ImagePath);var gray=image.Gray.ToArray();
        for(int y=480;y<600;y++)for(int x=700;x<750;x++)gray[y*image.Width+x]=255;
        string contaminated=Path.Combine(folder,"localized-reflection.bmp");RingScanSolver.SaveFrame(contaminated,image with{Gray=gray});
        dirty.Frames[0]=dirty.Frames[0] with{ImagePath=contaminated};
        var robust=new RingScanSolver().Analyze(dirty);
        Check(robust.CompatibilityRecovery is not null && robust.CompatibilityRecovery.RemovedPerView[0]>0,"少量局部亮斑异常能被受限剔除，不再拖偏整圆");
        Check(double.Hypot(robust.CenterX+12.3,robust.CenterY-45.6)<.005 && robust.CompatibilityRecovery!.LeaveOneViewCenterShiftMm<.015,"圆环恢复独立真值且留一方位圆心稳定");
        Check(robust.CompatibilityRecovery!.RemovedPerView.Skip(1).All(n=>n==0),"不丢弃完整方位或改写其他良好方位");
        var wrong=RingScanSession.Load(Save(clean,"wrong-session.json"));wrong.Frames[0]=wrong.Frames[0] with{X=wrong.Frames[0].X+.5};
        Check(Fails(()=>new RingScanSolver().Analyze(wrong)),"整处坐标错配不作为局部噪声掩盖");
        var template=HolePatternTemplate.LoadJson("templates/dial_18holes.json");
        clean.DirectionOptions=options;clean.Frames.Add(FaceBoundaryChecks.Face(s,folder,"eleven",RingFrameRole.Reference11,90,800,500));clean.Frames.Add(recovered.Frame);
        var alignment=new DialAlignmentSolver().Analyze(clean,template);
        Check(alignment.TargetStability is{RecoveryUsed:true,EvaluatedVariants:>0} && alignment.TargetStability.MaxTargetShiftMm<.025,"兼容参考圆通过最终18孔坐标稳定性筛选");
        var expected=RingScanPreview.Targets(ring,template,60);
        double maxError=alignment.Targets.Zip(expected,(a,b)=>double.Hypot(a.MachineX-b.MachineX,a.MachineY-b.MachineY)).Max();
        Check(maxError<.010,"实际输出18孔与独立已知CAD位姿偏差小于10µm，非加工精度验收");
        Check(Fails(()=>new DialAlignmentSolver().Analyze(clean,template,options with{MaxRecoveredTargetShiftMm=.000001})),"兼容拟合即使参考圆通过，18孔扰动超限仍拒绝");
        string firstArchive=Path.Combine(folder,"historical-first");
        if(!Directory.Exists(firstArchive))
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            ZipFile.ExtractToDirectory("资料收集/本件资料.zip",firstArchive,Encoding.GetEncoding(936));
        }
        var historical=new List<object>();
        foreach(string archive in new[]{firstArchive,"资料收集/工件_20261117_123839174/整件_6c6b896ad7b745a1a068317702e4ff72/本件资料"})
        {
            string sessionPath=Path.Combine(archive,"定位","整组记录.json");
            var session=RingScanSession.Load(sessionPath);var hashes=session.Frames.Select(f=>LaserSpotCalibration.Hash(f.ImagePath)).ToArray();
            var result=new DialAlignmentSolver().Analyze(session,template);
            using var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(archive,"整件","定位与补偿.json")));
            var prior=saved.RootElement.GetProperty("Targets").EnumerateArray().ToDictionary(e=>e.GetProperty("Id").GetInt32());
            double drift=result.Targets.Max(t=>double.Hypot(t.MachineX-prior[t.Id].GetProperty("CameraX").GetDouble(),t.MachineY-prior[t.Id].GetProperty("CameraY").GetDouble()));
            Check(drift<1e-9 && result.Ring.CompatibilityRecovery is null,"已完成18孔真实原图回放，所有相机孔位与原归档保持一致："+Path.GetFileName(archive));
            Check(session.Frames.Select(f=>LaserSpotCalibration.Hash(f.ImagePath)).SequenceEqual(hashes),"历史原图只读复核，指纹不变");
            historical.Add(new{Archive=archive,MaxTargetDriftMm=drift,result.Ring.CenterX,result.Ring.CenterY,result.Ring.RadiusMm,result.TargetStability});
        }
        File.WriteAllText(Path.Combine(folder,"historical-coordinate-replay.json"),JsonSerializer.Serialize(historical,new JsonSerializerOptions{WriteIndented=true}));
        File.WriteAllText(Path.Combine(folder,"compatibility-results.json"),JsonSerializer.Serialize(new{Passed=count,Reference=center,Ring=robust,alignment.TargetStability,MaxKnownTargetErrorMm=maxError},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Vision compatibility: {count} passed; synthetic ground truth and local files only.");return 0;
        string Save(RingScanSession session,string name){string path=Path.Combine(folder,name);session.Save(path);return path;}
    }
}
