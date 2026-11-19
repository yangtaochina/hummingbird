using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

internal static class EllipticalReferenceChecks
{
    public static int Run(string folder)
    {
        folder=Path.GetFullPath(folder);Directory.CreateDirectory(folder);int count=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
        var settings=new RingScanSettings();var options=new DatumDirectionOptions{BoundaryMode=DatumBoundaryMode.CircularFaceEdge};
        var results=new List<ReferenceRegionReview>();
        foreach(double angle in new[]{0.0,25,60,110})
        {
            var frame=Face("ellipse-"+angle,1.39,angle,RingFrameRole.Reference1,30);
            var original=new DatumDirectionSolver().Inspect(frame,settings,options);
            var found=ReferenceRegionTracking.Inspect(frame,settings,options,reacquireInView:true);
            File.WriteAllText(Path.Combine(folder,"review-"+angle+".json"),JsonSerializer.Serialize(found,new JsonSerializerOptions{WriteIndented=true}));
            Check(original.Status==DatumHoleInspectionStatus.Rejected,"圆半径取边方式复现椭圆参考面拒绝："+angle);
            Check(found.Inspection.Status==DatumHoleInspectionStatus.Detected && found.Frame.FaceSeed?.Ellipse is not null,"椭圆轮廓引导取边后恢复完整实体边界："+angle);
            var d=found.Inspection.Candidates.Single();
            Check(double.Hypot(d.Px-800,d.Py-500)<2 && d.AngularSupportRatio>=.75 && d.CenterSpreadMm<.015,
                "独立真值中心及原圆周／中心稳定性条件通过："+angle);
            Check(found.Frame.X==frame.X && found.Frame.Y==frame.Y && !found.MachineMotionRequested,"椭圆补偿仅用于图像取边，不改机器坐标："+angle);
            results.Add(found);
        }
        var edge=ReferenceRegionTracking.Inspect(Face("edge-ellipse",1.39,0,RingFrameRole.Reference1,30,cy:300),settings,options,reacquireInView:true);
        Check(edge.Inspection.Status==DatumHoleInspectionStatus.Detected && double.Hypot(edge.Inspection.Candidates.Single().Px-800,edge.Inspection.Candidates.Single().Py-300)<2,
            "按椭圆实际方向核对边界，完整入画的椭圆不被外接大圆误拒绝");
        Check(ReferenceRegionTracking.Inspect(Face("cropped-ellipse",1.39,0,RingFrameRole.Reference1,30,cy:80),settings,options,reacquireInView:true).Inspection.Status!=DatumHoleInspectionStatus.Detected,
            "实际超出画幅的椭圆仍拒绝，不外推中心");
        var half=Face("half-ellipse",1.39,25,RingFrameRole.Reference1,30,half:true);
        Check(ReferenceRegionTracking.Inspect(half,settings,options,reacquireInView:true).Inspection.Status!=DatumHoleInspectionStatus.Detected,"半圈椭圆仍拒绝，不外推参考中心");
        var bright=Face("bright-ellipse",1.39,25,RingFrameRole.Reference1,30,bright:true);
        Check(ReferenceRegionTracking.Inspect(bright,settings,options,reacquireInView:true).Inspection.Status!=DatumHoleInspectionStatus.Detected,"无独立暗壁的椭圆高光岛仍拒绝");
        var extreme=Face("extreme-ellipse",1.8,25,RingFrameRole.Reference1,30);
        Check(ReferenceRegionTracking.Inspect(extreme,settings,options,reacquireInView:true).Inspection.Status!=DatumHoleInspectionStatus.Detected,"明显拉长超出原1.4轴比的特征不自动放行");
        var left=Face("left-ellipse",1.39,25,RingFrameRole.Reference1,30,cx:650);
        var right=Face("right-ellipse",1.39,25,RingFrameRole.Reference1,30,cx:1270);
        var leftImage=RingScanSolver.LoadFrame(left.ImagePath);var rightImage=RingScanSolver.LoadFrame(right.ImagePath);
        byte[] pairPixels=leftImage.Gray.ToArray();
        for(int y=0;y<1200;y++)for(int x=960;x<1920;x++)pairPixels[y*1920+x]=rightImage.Gray[y*1920+x];
        string pairPath=Path.Combine(folder,"two-ellipses.bmp");RingScanSolver.SaveFrame(pairPath,leftImage with{Gray=pairPixels});
        foreach(double cx in new[]{650.0,1270.0})
        {
            var input=left with{ImagePath=pairPath,FaceSeed=new(cx,500,180,LaserSpotCalibration.Hash(pairPath),"SyntheticTwoEllipses")
                {Ellipse=new(720*1.39/2.39,720/2.39,25)}};
            Check(new DatumDirectionSolver().Inspect(input,settings,options).Status==DatumHoleInspectionStatus.Detected,"双椭圆用例中每个椭圆独立通过原边界检查");
        }
        Check(ReferenceRegionTracking.Inspect(left with{ImagePath=pairPath,FaceSeed=new(960,500,180,LaserSpotCalibration.Hash(pairPath),"SyntheticAmbiguousEllipses")},
            settings,options,reacquireInView:true).Inspection.Status!=DatumHoleInspectionStatus.Detected,"两个合格椭圆不能自动选择一个作为参考孔");
        var bad=results[0].Frame with{FaceSeed=results[0].Frame.FaceSeed! with{Ellipse=new(160,double.NaN,0)}};
        Check(new DatumDirectionSolver().Inspect(bad,settings,options).Status==DatumHoleInspectionStatus.InputError,"非法椭圆先验不能绕过输入检查");
        var ring=RingScanChecks.SyntheticSession(folder);ring.DirectionOptions=options;
        var eleven=ReferenceRegionTracking.Inspect(Face("eleven-ellipse",1.39,60,RingFrameRole.Reference11,90),settings,options,reacquireInView:true);
        ring.Frames.Add(eleven.Frame);ring.Frames.Add(results[1].Frame);
        var template=HolePatternTemplate.LoadJson("templates/dial_18holes.json");
        var alignment=new DialAlignmentSolver().Analyze(ring,template);
        var expected=RingScanPreview.Targets(new RingScanSolver().Analyze(new(){Settings=ring.Settings,Frames=ring.Frames.Where(f=>f.Role==RingFrameRole.RingArc).ToList()}),template,60);
        double shift=alignment.Targets.Zip(expected,(a,b)=>double.Hypot(a.MachineX-b.MachineX,a.MachineY-b.MachineY)).Max();
        Check(alignment.TargetStability is{RecoveryUsed:true} && shift<.010,"1点和11点均为椭圆时18孔与独立CAD位姿一致，执行兼容结果稳定性筛选");
        string saved=Path.Combine(folder,"ellipse-session.json");ring.Save(saved);
        Check(RingScanSession.Load(saved).Frames.Last().FaceSeed?.Ellipse is not null &&
            new DialAlignmentSolver().Analyze(RingScanSession.Load(saved),template).Targets.Count==18,"保存／重载保留椭圆初值及原图绑定，不丢失取边模型");
        File.WriteAllText(Path.Combine(folder,"ellipse-checks.json"),JsonSerializer.Serialize(new{Passed=count,MaxKnownTargetErrorMm=shift,alignment.TargetStability},new JsonSerializerOptions{WriteIndented=true}));
        Console.WriteLine($"Elliptical references: {count} passed; image-only synthetic ground truth.");return 0;

        RingScanFrame Face(string name,double ratio,double angle,RingFrameRole role,double ray,bool half=false,bool bright=false,int cx=800,int cy=500)
        {
            double a=360*ratio/(1+ratio),b=360/(1+ratio),theta=angle*Math.PI/180,co=Math.Cos(theta),si=Math.Sin(theta);
            byte[] gray=new byte[1920*1200];
            for(int y=0;y<1200;y++)for(int x=0;x<1920;x++)
            {
                double dx=x-cx,dy=y-cy,u=co*dx+si*dy,v=-si*dx+co*dy;
                double r=180*Math.Sqrt(u*u/(a*a)+v*v/(b*b));
                double value=20+80*(.5-.5*Math.Tanh((r-180)/2))+140*(.5+.5*Math.Tanh((r-252)/3));
                if(bright)value=50+140*(.5-.5*Math.Tanh((r-180)/2));
                if(half && y<cy)value=100;
                gray[y*1920+x]=(byte)Math.Clamp(Math.Round(value),0,255);
            }
            string path=Path.Combine(folder,name+".bmp");RingScanSolver.SaveFrame(path,new(gray,1920,1200,DateTime.Now));
            double t=ray*Math.PI/180,targetX=-12.3+14.45*Math.Cos(t),targetY=45.6+14.45*Math.Sin(t),det=settings.Jxx*settings.Jyy-settings.Jxy*settings.Jyx;
            double px=cx-959.5,py=cy-599.5;
            return new(name,path,targetX+(settings.Jyy*px-settings.Jxy*py)/det,targetY+(-settings.Jyx*px+settings.Jxx*py)/det,0,0)
            {Role=role,FaceSeed=new(cx+40,cy-30,180,LaserSpotCalibration.Hash(path),"SyntheticEllipseKnownCenter")};
        }
    }
}
