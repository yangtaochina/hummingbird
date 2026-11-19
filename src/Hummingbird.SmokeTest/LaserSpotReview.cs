using System.Text.Json;
using Hummingbird.Core;
using OpenCvSharp;
using Point = OpenCvSharp.Point;
using Size = OpenCvSharp.Size;

/// <summary>从前后原图求落点预览及可审阅的无出光复核文本；不连接或启动设备。</summary>
internal static class LaserSpotReview
{
    internal sealed record Fit(string Image,double SeedX,double SeedY,double SeedRadius,double X,double Y,double Radius,
        double Rms,int Rays,double CircleX,double CircleY,double CircleRms,double EllipseWidth,double EllipseHeight);
    private sealed record Circle(double X,double Y,double R);
    internal static IReadOnlyList<Fit> MeasureExistingMark(string path,double sx,double sy,double radius)
    {
        using var original=Cv2.ImDecode(File.ReadAllBytes(path),ImreadModes.Grayscale);
        using var blurred=new Mat();Cv2.GaussianBlur(original,blurred,new Size(5,5),0);
        using var image=new Mat();blurred.ConvertTo(image,MatType.CV_32FC1);
        var fits=new List<Fit>();
        foreach(var (dx,dy,ratio) in new[]{(0.0,0.0,1.0),(3.0,0.0,1.0),(-3.0,0.0,1.0),(0.0,3.0,1.0),(0.0,-3.0,1.0),(0.0,0.0,.95),(0.0,0.0,1.05)})
        foreach(double fraction in new[]{.35,.50,.65})fits.Add(Radial(image,"After_Step"+fraction,sx+dx,sy+dy,radius*ratio,fraction));
        return fits;
    }
    public static int Run(string trialPath,string outputFolder)
    {
        trialPath=Path.GetFullPath(trialPath);string output=Path.GetFullPath(outputFolder);Directory.CreateDirectory(output);
        using var doc=JsonDocument.Parse(File.ReadAllText(trialPath));var trial=doc.RootElement;
        var settings=trial.GetProperty("SoftwareOpticalSettings").Deserialize<RingScanSettings>()!;
        string source=Path.GetDirectoryName(trialPath)!;
        string ImagePath(string field)
        {
            string name=trial.GetProperty(field).GetProperty("FileName").GetString()!;
            if(Path.GetFileName(name)!=name)throw new InvalidDataException("图像路径必须为同目录文件名。");
            return Path.Combine(source,name);
        }
        var calibration=new LaserSpotCalibration {SourceTrialPath=trialPath,SourceTrialSha256=LaserSpotCalibration.Hash(trialPath),
            BeforeImagePath=ImagePath("BeforeImage"),BeforeImageSha256=trial.GetProperty("BeforeImage").GetProperty("Sha256").GetString()!,
            AfterImagePath=ImagePath("AfterImage"),AfterImageSha256=trial.GetProperty("AfterImage").GetProperty("Sha256").GetString()!,
            OpticalSettings=settings,ReferenceZ=trial.GetProperty("StartPosition").GetProperty("Z").GetDouble(),
            ReferenceC=trial.GetProperty("StartPosition").GetProperty("C").GetDouble(),PixelX=0,PixelY=0};
        calibration.VerifySources();
        using var syntheticByte=new Mat(1200,1920,MatType.CV_8UC1,Scalar.All(160));
        Cv2.Ellipse(syntheticByte,new Point(900,650),new Size(100,80),20,0,360,Scalar.All(20),-1,LineTypes.AntiAlias);
        Cv2.GaussianBlur(syntheticByte,syntheticByte,new Size(5,5),0);
        using var synthetic=new Mat();syntheticByte.ConvertTo(synthetic,MatType.CV_32FC1);
        var selfCheck=Radial(synthetic,"SyntheticEllipse",903,648,90,.5);
        double selfError=double.Hypot(selfCheck.X-900,selfCheck.Y-650);
        if(selfError>.75)throw new InvalidDataException($"已知中心的椭圆自检误差{selfError:F3}px。");
        using var before=Cv2.ImDecode(File.ReadAllBytes(calibration.BeforeImagePath),ImreadModes.Grayscale);
        using var after=Cv2.ImDecode(File.ReadAllBytes(calibration.AfterImagePath),ImreadModes.Grayscale);
        if(before.Empty()||after.Empty()||before.Size()!=after.Size()||after.Width!=settings.ImageWidth||after.Height!=settings.ImageHeight)
            throw new InvalidDataException("前后图实际尺寸与来源标定不符。");
        using var a=new Mat();using var b=new Mat();Cv2.GaussianBlur(before,a,new Size(5,5),0);Cv2.GaussianBlur(after,b,new Size(5,5),0);
        using var darkChange=new Mat();using var changed=new Mat();using var darkAfter=new Mat();using var mask=new Mat();
        Cv2.Subtract(a,b,darkChange);Cv2.Threshold(darkChange,changed,40,255,ThresholdTypes.Binary);
        Cv2.Threshold(b,darkAfter,85,255,ThresholdTypes.BinaryInv);Cv2.BitwiseAnd(changed,darkAfter,mask);
        using var kernel=Cv2.GetStructuringElement(MorphShapes.Ellipse,new Size(7,7));Cv2.MorphologyEx(mask,mask,MorphTypes.Close,kernel);
        Cv2.FindContours(mask,out Point[][] contours,out _,RetrievalModes.External,ContourApproximationModes.ApproxSimple);
        var candidates=contours.Where(c=>Cv2.ContourArea(c)>1000).OrderByDescending(c=>Cv2.ContourArea(c)).ToArray();
        if(candidates.Length==0||candidates.Length>1&&Cv2.ContourArea(candidates[1])>Cv2.ContourArea(candidates[0])*.25)
            throw new InvalidDataException("前后图没有唯一的新暗色打痕，不输出落点。");
        var moment=Cv2.Moments(candidates[0]);double sx=moment.M10/moment.M00,sy=moment.M01/moment.M00;
        double radius=Math.Sqrt(Cv2.ContourArea(candidates[0])/Math.PI);
        if(sx-radius*1.8<8||sy-radius*1.8<8||sx+radius*1.8>=after.Width-8||sy+radius*1.8>=after.Height-8)
            throw new InvalidDataException("新打痕靠近边界，无法完整检查圆周。");
        using var af=new Mat();using var bf=new Mat();a.ConvertTo(af,MatType.CV_32FC1);b.ConvertTo(bf,MatType.CV_32FC1);
        using var difference=new Mat();Cv2.Subtract(bf,af,difference);
        var fits=new List<Fit>();
        foreach(var (name,frame) in new[]{("After",bf),("AfterMinusBefore",difference)})
        foreach(var (dx,dy,ratio) in new[]{(0.0,0.0,1.0),(3.0,0.0,1.0),(-3.0,0.0,1.0),(0.0,3.0,1.0),(0.0,-3.0,1.0),(0.0,0.0,.95),(0.0,0.0,1.05)})
        foreach(double fraction in new[]{.35,.50,.65})
            fits.Add(Radial(frame,name+"_Step"+fraction,sx+dx,sy+dy,radius*ratio,fraction));
        double Median(IEnumerable<double> values){var sorted=values.Order().ToArray();return (sorted[(sorted.Length-1)/2]+sorted[sorted.Length/2])/2;}
        double px=Median(fits.Select(f=>f.X)),py=Median(fits.Select(f=>f.Y));
        double spread=fits.Max(f=>double.Hypot(f.X-px,f.Y-py));
        if(spread>5)throw new InvalidDataException($"前后图/初值扰动导致中心变化{spread:F2}px，未生成落点标定。");
        calibration=calibration with {PixelX=px,PixelY=py,
            Method="New dark feature from before/after; first inner-boundary local step crossing (35/50/65%); robust ellipse in after and signed difference; 42 seed/radius/fraction variants; circle diagnostic",
            Notes=$"当前局部XY响应；圆心参数变化最大{spread:F3}px。未完成独立位置复核；不证明孔径/孔深/实际光学时长。"};
        string package=Path.Combine(output,"供现场导入",Path.GetFileName(source));Directory.CreateDirectory(package);
        foreach(string file in new[]{trialPath,calibration.BeforeImagePath,calibration.AfterImagePath,
            Path.ChangeExtension(calibration.BeforeImagePath,".capture.json"),Path.ChangeExtension(calibration.AfterImagePath,".capture.json")})
            File.Copy(file,Path.Combine(package,Path.GetFileName(file)),true);
        calibration=calibration with {SourceTrialPath=Path.Combine(package,"trial.json"),BeforeImagePath=Path.Combine(package,"before.bmp"),AfterImagePath=Path.Combine(package,"after.bmp")};
        string calibrationPath=Path.Combine(package,"激光落点_预览标定.json");calibration.Save(calibrationPath);
        var verified=LaserSpotCalibration.Load(calibrationPath);var offset=LaserSpotCompensation.Offset(verified,settings);
        File.WriteAllText(Path.Combine(output,"HM_SPOT_CHECK.MPF"),LaserSpotCompensation.RecenterTrial(verified,settings),System.Text.Encoding.ASCII);
        using var annotated=new Mat();Cv2.CvtColor(after,annotated,ColorConversionCodes.GRAY2BGR);
        Cv2.Circle(annotated,new Point((int)Math.Round(px),(int)Math.Round(py)),(int)Math.Round(Median(fits.Select(f=>f.Radius))),new Scalar(0,255,255),2);
        Cv2.DrawMarker(annotated,new Point((int)Math.Round(px),(int)Math.Round(py)),new Scalar(0,255,255),MarkerTypes.Cross,30,2);
        Cv2.DrawMarker(annotated,new Point((int)((after.Width-1)/2.0),(int)((after.Height-1)/2.0)),new Scalar(0,165,255),MarkerTypes.Cross,30,2);
        using var reduced=new Mat();Cv2.Resize(annotated,reduced,new Size(960,600));Cv2.ImEncode(".png",reduced,out byte[] image);File.WriteAllBytes(Path.Combine(output,"落点标注_黄色落点_橙色画面中心.png"),image);
        File.WriteAllText(Path.Combine(output,"落点分析.json"),JsonSerializer.Serialize(new {Kind="LaserSpotOfflineReview",PreviewOnly=true,
            HardwareAccess=false,ApprovedForMachining=false,IndependentPositionVerified=false,SourceTrial=trialPath,
            CandidatePixelX=px,CandidatePixelY=py,RadiusPx=Median(fits.Select(f=>f.Radius)),MaxCenterSpreadPx=spread,
            ImageCenterX=(after.Width-1)/2.0,ImageCenterY=(after.Height-1)/2.0,
            CalibrationPath=calibrationPath,CameraToLaserOffsetMm=new {offset.X,offset.Y},Fits=fits,
            KnownEllipseSelfCheck=new {Synthetic=true,ExpectedX=900,ExpectedY=650,MeasuredX=selfCheck.X,MeasuredY=selfCheck.Y,ErrorPx=selfError}},
            new JsonSerializerOptions {WriteIndented=true}));
        Console.WriteLine(FormattableString.Invariant($"Verified source; laser pixel=({px:F6},{py:F6}); spread={spread:F4}px; offset=({offset.X:F6},{offset.Y:F6})mm"));
        return 0;
    }

    private static Fit Radial(Mat image,string name,double sx,double sy,double radius,double fraction)
    {
        double Value(double x,double y)
        {
            int ix=(int)x,iy=(int)y;double fx=x-ix,fy=y-iy;
            return image.At<float>(iy,ix)*(1-fx)*(1-fy)+image.At<float>(iy,ix+1)*fx*(1-fy)+
                image.At<float>(iy+1,ix)*(1-fx)*fy+image.At<float>(iy+1,ix+1)*fx*fy;
        }
        var points=new List<Point2d>();
        for(int ray=0;ray<180;ray++)
        {
            double angle=ray*2*Math.PI/180,ux=Math.Cos(angle),uy=Math.Sin(angle);
            double Sample(double r)=>Value(sx+r*ux,sy+r*uy);
            var core=Enumerable.Range((int)(radius*.2),Math.Max(4,(int)(radius*.3))).Select(r=>Sample(r)).Order().ToArray();
            double baseline=core[core.Length/2];
            int first=(int)Math.Ceiling(radius*.6),last=(int)Math.Floor(radius*1.45);
            double wall=Enumerable.Range((int)(radius*.8),last-(int)(radius*.8)+1).Max(r=>Sample(r));
            if(wall-baseline<30)continue;
            double level=baseline+fraction*(wall-baseline);int crossing=first;
            while(crossing<=last && !(Sample(crossing)>=level && Sample(crossing+1)>=level && Sample(crossing+2)>=level))crossing++;
            if(crossing<=first||crossing>last)continue;
            double low=Sample(crossing-1),high=Sample(crossing);
            double rEdge=crossing-1+(level-low)/(high-low);
            points.Add(new(sx+rEdge*ux,sy+rEdge*uy));
        }
        if(points.Count<135)throw new InvalidDataException("新打痕不足75%圆周支持。");
        Circle circle=CircleFit(points);
        for(int pass=0;pass<3;pass++)
        {
            var errors=points.Select(p=>Math.Abs(double.Hypot(p.X-circle.X,p.Y-circle.Y)-circle.R)).Order().ToArray();
            double cutoff=Math.Max(2.5,errors[errors.Length/2]*4.4478);
            points=points.Where(p=>Math.Abs(double.Hypot(p.X-circle.X,p.Y-circle.Y)-circle.R)<=cutoff).ToList();
            if(points.Count<135)throw new InvalidDataException("新打痕圆周离群点过多。");
            circle=CircleFit(points);
        }
        var ellipse=Cv2.FitEllipse(points.Select(p=>new Point2f((float)p.X,(float)p.Y)).ToArray());
        double Residual(Point2d p,RotatedRect fit)
        {
            double angle=fit.Angle*Math.PI/180,ca=Math.Cos(angle),sa=Math.Sin(angle),a=fit.Size.Width/2,b=fit.Size.Height/2;
            double x=ca*(p.X-fit.Center.X)+sa*(p.Y-fit.Center.Y),y=-sa*(p.X-fit.Center.X)+ca*(p.Y-fit.Center.Y);
            double distance=double.Hypot(x,y),expected=distance/Math.Sqrt(x*x/(a*a)+y*y/(b*b));
            return distance-expected;
        }
        for(int pass=0;pass<3;pass++)
        {
            var errors=points.Select(p=>Math.Abs(Residual(p,ellipse))).Order().ToArray();
            double cutoff=Math.Max(2.5,errors[errors.Length/2]*4.4478);
            points=points.Where(p=>Math.Abs(Residual(p,ellipse))<=cutoff).ToList();
            if(points.Count<135)throw new InvalidDataException("椭圆模型有效圆周不足75%。");
            ellipse=Cv2.FitEllipse(points.Select(p=>new Point2f((float)p.X,(float)p.Y)).ToArray());
        }
        circle=CircleFit(points);
        double rms=Math.Sqrt(points.Average(p=>Math.Pow(double.Hypot(p.X-circle.X,p.Y-circle.Y)-circle.R,2)));
        double ellipseRms=Math.Sqrt(points.Average(p=>Math.Pow(Residual(p,ellipse),2)));
        double a=ellipse.Size.Width/2,b=ellipse.Size.Height/2;
        if(ellipseRms>5 || Math.Max(a,b)/Math.Min(a,b)>1.4)throw new InvalidDataException($"新打痕椭圆残差{ellipseRms:F2}px或形状比例不符，未生成落点。");
        return new(name,sx,sy,radius,ellipse.Center.X,ellipse.Center.Y,Math.Sqrt(a*b),ellipseRms,points.Count,circle.X,circle.Y,rms,ellipse.Size.Width,ellipse.Size.Height);
    }

    private static Circle CircleFit(IReadOnlyList<Point2d> points)
    {
        double ox=points.Average(p=>p.X),oy=points.Average(p=>p.Y);
        using var a=new Mat(points.Count,3,MatType.CV_64FC1);using var b=new Mat(points.Count,1,MatType.CV_64FC1);using var solution=new Mat();
        for(int i=0;i<points.Count;i++){double x=points[i].X-ox,y=points[i].Y-oy;a.Set(i,0,2*x);a.Set(i,1,2*y);a.Set(i,2,1.0);b.Set(i,0,x*x+y*y);}
        if(!Cv2.Solve(a,b,solution,DecompTypes.SVD))throw new InvalidDataException("落点圆拟合退化。");
        return new(ox+solution.At<double>(0),oy+solution.At<double>(1),Math.Sqrt(solution.At<double>(2)+Math.Pow(solution.At<double>(0),2)+Math.Pow(solution.At<double>(1),2)));
    }
}
