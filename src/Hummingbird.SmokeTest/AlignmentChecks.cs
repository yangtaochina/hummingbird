using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

internal static class AlignmentChecks
{
    public static int Run(string? sitePath)
    {
        int failed=0,passed=0;
        void Check(bool ok,string name) { Console.WriteLine($"[{(ok?"PASS":"FAIL")}] {name}"); if(ok)passed++;else failed++; }
        void Reject(Action action,string name) { try { action(); Check(false,name); } catch(InvalidDataException) { Check(true,name); } }
        string folder=Path.Combine(Path.GetTempPath(),"hummingbird-alignment-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var session=RingScanChecks.SyntheticSession(folder);
            const double angle=59.9;
            var eleven=Hole(session.Settings,folder,"eleven",RingFrameRole.Reference11,angle+30,760,590);
            var one=Hole(session.Settings,folder,"one",RingFrameRole.Reference1,angle-30,780,560);
            session.Frames.AddRange([eleven,one]);
            var template=HolePatternTemplate.LoadJson("templates/dial_18holes.json");
            var solver=new DialAlignmentSolver();
            RingScanSession Copy()=>JsonSerializer.Deserialize<RingScanSession>(JsonSerializer.Serialize(session))!;
            var result=solver.Analyze(session,template);
            Check(double.Hypot(result.Ring.CenterX+12.3,result.Ring.CenterY-45.6)<.003,"圆环和参考孔分组后恢复已知圆心");
            Check(Math.Abs(result.Direction.TwelveAngleDeg-angle)<.02,"从离心像素位置及XYZ自动还原已知12点方向");
            Check(result.Targets.Count==18 && result.PreviewOnly && result.Warnings.Count>0,"输出18孔及预览标记，不宣称加工精度通过");
            Check(Math.Abs(result.Direction.Reference11.Px-760)<.2 && Math.Abs(result.Direction.Reference1.Py-560)<.2,
                "孔心由图像识别，未把画面中心当孔心");
            Check(result.Direction.Reference11.AnnulusMedian>result.Direction.Reference11.CoreMedian+15,
                "参考孔为暗孔心/亮外围，识别结果包含成像质量数据");
            Check(result.Direction.GapDeg>59.9 && result.Direction.GapDeg<60.1,"11/1孔夹角独立核对");

            var changed=Copy(); changed.Frames.RemoveAt(5);
            Reject(()=>solver.Analyze(changed,template),"缺少1点参考孔时拒绝自动定位");
            changed=Copy(); changed.Frames.Add(eleven with{Name="duplicate11"});
            Reject(()=>solver.Analyze(changed,template),"重复参考孔用途时拒绝定位");
            changed=Copy(); changed.Frames[4]=eleven with{Role=RingFrameRole.Reference1};changed.Frames[5]=one with{Role=RingFrameRole.Reference11};
            Reject(()=>solver.Analyze(changed,template),"11/1用途选反时拒绝方向翻转");
            changed=Copy(); changed.Frames[5]=one with{C=1};
            Reject(()=>solver.Analyze(changed,template),"参考孔C与圆环不同则拒绝混算");
            changed=Copy(); changed.Frames[5]=one with{Z=.02};
            Reject(()=>solver.Analyze(changed,template),"参考孔Z与圆环不同则拒绝混算");
            changed=Copy(); changed.Frames[5]=one with{C=359.999};
            Check(Math.Abs(solver.Analyze(changed,template).Direction.TwelveAngleDeg-angle)<.02,"C反馈跨360度零点按实际角差比较");
            changed=Copy(); changed.Frames[4]=RingCaptureArchive.BindImage(eleven,changed.Settings,DateTime.Now,1920,1200,
                AxisCoordinateSource.ManualPanelEntry,AxisCoordinateSource.ManualPanelEntry,null,"RotaryScan");
            Reject(()=>solver.Analyze(changed,template),"即使C值被重复手填，旋转采图标记仍阻止固定C定位");
            changed=Copy();changed.Frames[4]=eleven with{Role=(RingFrameRole)99};
            Reject(()=>solver.Analyze(changed,template),"无效用途不静默当圆环处理");
            Reject(()=>new RingScanSolver().Analyze(session),"直接圆环拟合拒绝混入参考孔图片");
            changed=Copy();changed.Frames=changed.Frames.OrderByDescending(f=>(int)f.Role).ToList();
            Check(Math.Abs(solver.Analyze(changed,template).Direction.TwelveAngleDeg-result.Direction.TwelveAngleDeg)<1e-6,
                "行顺序变化不影响按用途分组的定位");

            changed=Copy();changed.Frames[5]=one with{ImagePath=eleven.ImagePath};
            Reject(()=>solver.Analyze(changed,template),"两个用途指向同一原图时拒绝");
            string duplicate=Path.Combine(folder,"renamed.bmp"); File.Copy(eleven.ImagePath,duplicate);
            changed=Copy();changed.Frames[5]=one with{ImagePath=duplicate};
            Reject(()=>solver.Analyze(changed,template),"同图换文件名仍拒绝作为两个参考孔");
            changed=Copy();changed.Frames[5]=Hole(changed.Settings,folder,"partial",RingFrameRole.Reference1,angle-30,20,560);
            Reject(()=>solver.Analyze(changed,template),"孔不完整或位于边缘时拒绝猜测孔心");
            changed=Copy();changed.Frames[5]=Hole(changed.Settings,folder,"bright",RingFrameRole.Reference1,angle-30,780,560,brightCore:true);
            Reject(()=>solver.Analyze(changed,template),"亮斑内轮廓不被当成暗孔");
            changed=Copy();changed.Frames[5]=Hole(changed.Settings,folder,"multiple",RingFrameRole.Reference1,angle-30,780,560,secondHole:true);
            Reject(()=>solver.Analyze(changed,template),"两个几何上均可行的候选孔时拒绝歧义");
            changed=Copy();changed.Frames[5]=one with{X=one.X+3};
            Reject(()=>solver.Analyze(changed,template),"图像与机床坐标错配时由半径/夹角筛选拒绝");
            changed=Copy();
            double t=(angle-30)*Math.PI/180;
            changed.Frames[5]=one with{X=one.X+.2*Math.Cos(t),Y=one.Y+.2*Math.Sin(t)};
            var imperfect=solver.Analyze(changed,template);
            double cadDistance=double.Hypot(template.Holes[0].X-template.Holes[1].X,template.Holes[0].Y-template.Holes[1].Y);
            double targetDistance=double.Hypot(imperfect.Targets[0].MachineX-imperfect.Targets[1].MachineX,
                imperfect.Targets[0].MachineY-imperfect.Targets[1].MachineY);
            Check(Math.Abs(cadDistance-targetDistance)<1e-10,"参考孔径向制造偏差不缩放CAD加工孔距");
            string json=Path.Combine(folder,"alignment.json");session.Save(json);
            var restored=RingScanSession.Load(json);
            Check(restored.Frames.Count(f=>f.Role==RingFrameRole.Reference11)==1 &&
                Math.Abs(solver.Analyze(restored,template).Direction.TwelveAngleDeg-angle)<.02,
                "用途和相对图片路径保存重载后可直接自动定位");
            string report=JsonSerializer.Serialize(result);
            Check(JsonSerializer.Deserialize<DialAlignmentPreview>(report)!.Targets.Count==18,
                "定位预览报告完整保存圆心、方向、孔心和目标孔位");

            foreach(double deg in new[]{0.0,359.0,181.0,90.0})
            {
                double a=(deg+30)*Math.PI/180,b=(deg-30)*Math.PI/180;
                var d=DatumDirectionGeometry.Compute(0,0,Math.Cos(a),Math.Sin(a),Math.Cos(b),Math.Sin(b),new());
                Check(Math.Abs(Math.IEEERemainder(d.Twelve-deg,360))<1e-8,$"方向跨角度边界 {deg}° 不取反向平分线");
            }
            Reject(()=>DatumDirectionGeometry.Compute(0,0,1,0,-1,0,new()),"相反两孔不能冒充11/1参考孔");
            if(sitePath!=null)
            {
                var site=solver.Analyze(RingScanSession.Load(Path.GetFullPath(sitePath)),template);
                Console.WriteLine($"SITE center=({site.Ring.CenterX:F6},{site.Ring.CenterY:F6}) angle={site.Direction.TwelveAngleDeg:F6} gap={site.Direction.GapDeg:F6} spread={site.Direction.DirectionSpreadDeg:F6}");
                Check(Math.Abs(site.Direction.TwelveAngleDeg-59.9)<.15 &&
                    double.Hypot(site.Ring.CenterX+147.384943,site.Ring.CenterY+196.615172)<.02 && site.Targets.Count==18,
                    "历史现场6帧自动结果与先前独立离线分析一致");
            }
        }
        catch(Exception ex) { Console.WriteLine(ex); failed++; }
        finally
        {
            foreach(string file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
        Console.WriteLine($"Alignment: {passed} passed, {failed} failed; no hardware accessed.");
        return failed==0?0:1;
    }

    internal static RingScanFrame Hole(RingScanSettings settings,string folder,string name,RingFrameRole role,
        double rayDeg,int px,int py,bool brightCore=false,bool secondHole=false)
    {
        double t=rayDeg*Math.PI/180;
        double targetX=-12.3+14.45*Math.Cos(t),targetY=45.6+14.45*Math.Sin(t);
        // 独立构造q，使指定像素处的真实孔心在已知机床位置，不依赖检测器输出。
        double dx=px-959.5,dy=py-599.5,det=settings.Jxx*settings.Jyy-settings.Jxy*settings.Jyx;
        double qx=targetX+(settings.Jyy*dx-settings.Jxy*dy)/det;
        double qy=targetY+(-settings.Jyx*dx+settings.Jxx*dy)/det;
        byte[] gray=new byte[1920*1200];Array.Fill(gray,(byte)50);
        for(int y=0;y<1200;y++)
        for(int x=0;x<1920;x++)
        {
            double r=double.Hypot(x-px,y-py);
            if(secondHole)r=Math.Min(r,double.Hypot(x-(px+240),y-py));
            if(r<90) gray[y*1920+x]=(byte)(brightCore?190:16);
            else if(r<135)gray[y*1920+x]=(byte)(brightCore?50:190);
        }
        string path=Path.Combine(folder,name+".bmp");
        RingScanSolver.SaveFrame(path,new CameraFrame(gray,1920,1200,DateTime.Now));
        return new RingScanFrame(name,path,qx,qy,0,0){Role=role};
    }
}
