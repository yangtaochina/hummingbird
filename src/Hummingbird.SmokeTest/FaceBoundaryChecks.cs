using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

internal static class FaceBoundaryChecks
{
    public static int Run(string? sitePath)
    {
        int passed=0,failed=0;
        void Check(bool ok,string name){Console.WriteLine($"[{(ok?"PASS":"FAIL")}] {name}");if(ok)passed++;else failed++;}
        string folder=Path.Combine(Path.GetTempPath(),"hummingbird-face-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var settings=new RingScanSettings();var options=new DatumDirectionOptions {BoundaryMode=DatumBoundaryMode.CircularFaceEdge};
            var solver=new DatumDirectionSolver();
            var frame=Face(settings,folder,"face",RingFrameRole.Reference11,90,800,500);
            var check=solver.Inspect(frame,settings,options);
            Check(check.Status==DatumHoleInspectionStatus.Detected,"实体圆面在外侧非对称阴影下仍识别");
            var d=check.Candidates.Single();
            Check(double.Hypot(d.Px-800,d.Py-500)<1.5,"恢复独立生成的实体圆心，未使用偏移阴影中心");
            Check(d.BoundaryMode==DatumBoundaryMode.CircularFaceEdge && d.Samples.Count>=9 &&
                d.Samples.All(s=>s.Criterion=="LocalEntityBoundaryStepFraction" && s.EdgeFraction is not null),
                "记录局部过渡参数及框选扰动，不伪装为旧灰度阈值");
            Check(d.AngularSupportRatio>=.75 && d.EdgeRmsPx<3 && d.CenterSpreadMm<.015,
                "至少75%圆周支持并通过残差及初值敏感性筛选");
            var shifted=frame with {FaceSeed=frame.FaceSeed! with {Px=803,Py=498,RadiusPx=81}};
            var moved=solver.Inspect(shifted,settings,options);
            Check(moved.Status==DatumHoleInspectionStatus.Detected &&
                double.Hypot(moved.Candidates.Single().Px-d.Px,moved.Candidates.Single().Py-d.Py)<1.5,
                "框选初值小幅变化不直接变成输出孔心偏移");
            Check(solver.Inspect(frame with {FaceSeed=null},settings,options).Status==DatumHoleInspectionStatus.Rejected,
                "实体模型缺少框选时拒绝，不静默退回阴影识别");
            Check(solver.Inspect(frame with {FaceSeed=frame.FaceSeed! with {ImageSha256=new string('0',64)}},settings,options).Status==DatumHoleInspectionStatus.InputError,
                "框选绑定原图SHA256，不能跨图复用");
            var bright=Face(settings,folder,"bright",RingFrameRole.Reference11,90,800,500,plainBright:true);
            Check(solver.Inspect(bright,settings,options).Status==DatumHoleInspectionStatus.Rejected,
                "没有独立暗壁的亮岛不作为实体参考边界");
            var partial=Face(settings,folder,"partial",RingFrameRole.Reference11,90,60,500);
            Check(solver.Inspect(partial,settings,options).Status==DatumHoleInspectionStatus.Rejected,
                "原图边缘不完整圆面拒绝外推");
            var half=Face(settings,folder,"half",RingFrameRole.Reference11,90,800,500,halfOnly:true);
            Check(solver.Inspect(half,settings,options).Status==DatumHoleInspectionStatus.Rejected,
                "只有半圈过渡时不靠拟合补出完整圆心");
            var weak=Face(settings,folder,"weak",RingFrameRole.Reference11,90,800,500,weak:true);
            Check(solver.Inspect(weak,settings,options).Status==DatumHoleInspectionStatus.Rejected,
                "过弱实体边界不因框选正确而强行通过");
            Check(solver.Inspect(frame with {FaceSeed=frame.FaceSeed! with {RadiusPx=140}},settings,options).Status==DatumHoleInspectionStatus.Rejected,
                "把外侧阴影圈进大框不能替代实体圆面");
            Check(solver.Inspect(frame with {FaceSeed=frame.FaceSeed! with {Px=double.NaN}},settings,options).Status==DatumHoleInspectionStatus.InputError,
                "框选非法数值拒绝");
            Check(solver.Inspect(frame with {FaceSeed=frame.FaceSeed! with {Px=-1}},settings,options).Status==DatumHoleInspectionStatus.InputError,
                "图像范围外的框选输入拒绝");
            Check(solver.Inspect(frame with {FaceSeed=frame.FaceSeed! with {SelectionSource=""}},settings,options).Status==DatumHoleInspectionStatus.InputError,
                "框选必须记录来源，不能伪造为完全自动发现");
            var noise=Face(settings,folder,"noise",RingFrameRole.Reference11,90,800,500,noise:true);
            var noisy=solver.Inspect(noise,settings,options);
            Check(noisy.Status==DatumHoleInspectionStatus.Detected && double.Hypot(noisy.Candidates[0].Px-800,noisy.Candidates[0].Py-500)<2,
                "小幅传感器噪声下仍恢复已知几何真值");
            var session=RingScanChecks.SyntheticSession(folder);session.DirectionOptions=options;
            session.Frames.Add(Face(settings,folder,"eleven",RingFrameRole.Reference11,90,800,500));
            session.Frames.Add(Face(settings,folder,"one",RingFrameRole.Reference1,30,820,540));
            var alignment=new DialAlignmentSolver().Analyze(session,HolePatternTemplate.LoadJson("templates/dial_18holes.json"));
            Check(Math.Abs(alignment.Direction.TwelveAngleDeg-60)<.03 && alignment.Targets.Count==18,
                "两点使用相同实体边界口径后恢复已知12点方向及18孔");
            Check(alignment.Direction.Reference11.BoundaryMode==DatumBoundaryMode.CircularFaceEdge &&
                alignment.Direction.Reference1.BoundaryMode==DatumBoundaryMode.CircularFaceEdge,
                "1点和11点不混用实体边缘与暗孔阴影模型");
            string saved=Path.Combine(folder,"session.json");session.Save(saved);var restored=RingScanSession.Load(saved);
            Check(restored.DirectionOptions.BoundaryMode==DatumBoundaryMode.CircularFaceEdge && restored.Frames[^1].FaceSeed?.ImageSha256==session.Frames[^1].FaceSeed?.ImageSha256,
                "整组记录保存模型和框选来源，重启后可恢复");
            Check(Math.Abs(new DialAlignmentSolver().Analyze(restored,HolePatternTemplate.LoadJson("templates/dial_18holes.json")).Direction.TwelveAngleDeg-60)<.03,
                "保存重载后重复计算保持方向结果");
            if(sitePath is not null)
            {
                var site=RingScanSession.Load(Path.GetFullPath(sitePath));
                var eleven=solver.Inspect(site.Frames.Single(f=>f.Role==RingFrameRole.Reference11),site.Settings,site.DirectionOptions);
                Check(eleven.Status==DatumHoleInspectionStatus.Detected &&
                    double.Hypot(eleven.Candidates.Single().Px-804.64,eleven.Candidates.Single().Py-477.85)<8,
                    "本次客户确认的11点实体圆边通过独立圆周及初值稳定性检查");
                var one=solver.Inspect(site.Frames.Single(f=>f.Role==RingFrameRole.Reference1),site.Settings,site.DirectionOptions);
                Check(one.Status==DatumHoleInspectionStatus.Rejected,"本次暗1点原图仍拒绝，不以旧阴影中心凑18孔");
                if(eleven.Candidates.Count>0)Console.WriteLine($"SITE11 center=({eleven.Candidates[0].Px:F3},{eleven.Candidates[0].Py:F3}) support={eleven.Candidates[0].AngularSupportRatio:P1} spread={eleven.Candidates[0].CenterSpreadMm*1000:F2}um");
            }
        }
        catch(Exception ex){Console.WriteLine(ex);failed++;}
        finally{foreach(string f in Directory.EnumerateFiles(folder))File.Delete(f);Directory.Delete(folder);}
        Console.WriteLine($"Face boundary: {passed} passed, {failed} failed; no hardware accessed.");return failed==0?0:1;
    }

    internal static RingScanFrame Face(RingScanSettings settings,string folder,string name,RingFrameRole role,
        double ray,int cx,int cy,bool plainBright=false,bool halfOnly=false,bool weak=false,bool noise=false)
    {
        const double radius=80;
        byte[] gray=new byte[1920*1200];var random=new Random(17);
        for(int y=0;y<1200;y++)for(int x=0;x<1920;x++)
        {
            double r=double.Hypot(x-cx,y-cy);
            double value=20+80*(.5-.5*Math.Tanh((r-radius)/2))+140*(.5+.5*Math.Tanh((r-radius-14)/3));
            if(x>cx && y>cy && r>radius+16)value=55;
            if(plainBright)value=50+140*(.5-.5*Math.Tanh((r-radius)/2));
            if(halfOnly && y<cy)value=100;
            if(weak)value=56+4*(.5-.5*Math.Tanh((r-radius)/2))+104*(.5+.5*Math.Tanh((r-radius-14)/3));
            if(noise)value+=random.Next(-2,3);
            gray[y*1920+x]=(byte)Math.Clamp(Math.Round(value),0,255);
        }
        string image=Path.Combine(folder,name+".bmp");RingScanSolver.SaveFrame(image,new CameraFrame(gray,1920,1200,DateTime.Now));
        double angle=ray*Math.PI/180,targetX=-12.3+14.45*Math.Cos(angle),targetY=45.6+14.45*Math.Sin(angle);
        double px=cx-959.5,py=cy-599.5,det=settings.Jxx*settings.Jyy-settings.Jxy*settings.Jyx;
        return new(name,image,targetX+(settings.Jyy*px-settings.Jxy*py)/det,
            targetY+(-settings.Jyx*px+settings.Jxx*py)/det,0,0)
        {Role=role,FaceSeed=new(cx,cy,radius,LaserSpotCalibration.Hash(image),"SyntheticKnownGeometry")};
    }
}
