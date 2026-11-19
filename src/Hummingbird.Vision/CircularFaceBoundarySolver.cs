using Hummingbird.Core;
using OpenCvSharp;
using System.Security.Cryptography;

namespace Hummingbird.Vision;

/// <summary>由框选初值提取实体圆面的局部亮度边界。与暗孔/亮缘模型分开，不用阴影外轮廓替代实体边缘。</summary>
internal sealed class CircularFaceBoundarySolver
{
    private sealed record EdgeFit(double Fraction,RotatedRect Ellipse,double Rms,double Support,double Core,double Wall)
    {
        public double SeedDx {get;init;}
        public double SeedDy {get;init;}
        public double SeedRadiusRatio {get;init;}=1;
    }

    public DatumHoleInspection Inspect(RingScanFrame frame,RingScanSettings settings,DatumDirectionOptions options)
    {
        using var image=Cv2.ImDecode(File.ReadAllBytes(frame.ImagePath),ImreadModes.Grayscale);
        if(image.Empty() || image.Width!=settings.ImageWidth || image.Height!=settings.ImageHeight)
            throw new InvalidDataException("实体圆边图像尺寸与标定不一致。");
        double scale=Math.Sqrt(Math.Abs(settings.Jxx*settings.Jyy-settings.Jxy*settings.Jyx));
        var seeds=new List<CircleSegment>();
        if(frame.FaceSeed is { } input)
        {
            if(new[]{input.Px,input.Py,input.RadiusPx}.Any(v=>!double.IsFinite(v)) || input.RadiusPx<=0 ||
                input.Px<0 || input.Py<0 || input.Px>=image.Width || input.Py>=image.Height ||
                string.IsNullOrWhiteSpace(input.SelectionSource) ||
                !string.Equals(input.ImageSha256,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(frame.ImagePath))),StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("实体圆边框选初值无效或不属于当前原图，请重新框选。");
            if(input.Ellipse is {} ellipse && (new[]{ellipse.WidthPx,ellipse.HeightPx,ellipse.AngleDeg}.Any(v=>!double.IsFinite(v)) ||
                ellipse.WidthPx<=0 || ellipse.HeightPx<=0 || Math.Max(ellipse.WidthPx,ellipse.HeightPx)/Math.Min(ellipse.WidthPx,ellipse.HeightPx)>1.4 ||
                Math.Abs((ellipse.WidthPx+ellipse.HeightPx)/4-input.RadiusPx)>input.RadiusPx*.02))
                throw new InvalidDataException("椭圆搜索初值的轴长、角度或平均半径不一致。");
            seeds.Add(new CircleSegment(new Point2f((float)input.Px,(float)input.Py),(float)input.RadiusPx));
        }
        var detections=new List<DatumHoleDetection>();var details=new List<(int Support,string Text)>();
        int tested=0;
        foreach(var seed in seeds)
        {
            var world=settings.ToMachine(frame,seed.Center.X,seed.Center.Y);
            if(double.Hypot(world.X-frame.X,world.Y-frame.Y)>options.MaxImageCenterDistanceMm)
            {details.Add((0,"框选中心距离画面中心超过允许范围，请核对是否选中目标实体圆面。"));continue;}
            var shape=frame.FaceSeed?.Ellipse;
            double extentX=seed.Radius,extentY=seed.Radius;
            if(shape is not null)
            {
                double t=shape.AngleDeg*Math.PI/180,a=shape.WidthPx/2,b=shape.HeightPx/2;
                extentX=Math.Sqrt(Math.Pow(a*Math.Cos(t),2)+Math.Pow(b*Math.Sin(t),2));
                extentY=Math.Sqrt(Math.Pow(a*Math.Sin(t),2)+Math.Pow(b*Math.Cos(t),2));
            }
            if(seed.Center.X-extentX*1.7<8 || seed.Center.Y-extentY*1.7<8 ||
                seed.Center.X+extentX*1.7>=image.Width-8 || seed.Center.Y+extentY*1.7>=image.Height-8)
            {details.Add((0,"框选范围及其外侧检查区域超出原图，请核对是否框入了外围阴影或选框过大。"));continue;}
            tested++;
            var fits=new List<EdgeFit>();
            int bestSupport=0;
            var rejections=new List<string>();
            int completeVariants=0;
            var perturbations=new List<(double Dx,double Dy,double Ratio,double WidthRatio,double HeightRatio,double AngleDelta)>
            { (0,0,1,1,1,0),(2,0,1,1,1,0),(-2,0,1,1,1,0),(0,2,1,1,1,0),(0,-2,1,1,1,0),(0,0,.98,1,1,0),(0,0,1.02,1,1,0) };
            if(shape is not null)perturbations.AddRange(new[]{(0.0,0.0,1.0,.98,1.0,0.0),(0.0,0.0,1.0,1.02,1.0,0.0),
                (0.0,0.0,1.0,1.0,.98,0.0),(0.0,0.0,1.0,1.0,1.02,0.0),(0.0,0.0,1.0,1.0,1.0,-2.0),(0.0,0.0,1.0,1.0,1.0,2.0)});
            foreach(var (seedDx,seedDy,ratio,widthRatio,heightRatio,angleDelta) in perturbations)
            {
                var variant=new CircleSegment(new Point2f(seed.Center.X+(float)seedDx,seed.Center.Y+(float)seedDy),seed.Radius*(float)ratio);
                var localFits=new List<EdgeFit>();
                foreach(double fraction in new[]{.35,.50,.65})
                {
                    var variedShape=shape is null?null:shape with {WidthPx=shape.WidthPx*ratio*widthRatio,HeightPx=shape.HeightPx*ratio*heightRatio,AngleDeg=shape.AngleDeg+angleDelta};
                    var fit=Fit(image,variant,fraction,scale,options,out int support,out string rejection,variedShape);
                    bestSupport=Math.Max(bestSupport,support);
                    if(fit is not null)localFits.Add(fit with {SeedDx=seedDx,SeedDy=seedDy,SeedRadiusRatio=ratio});
                    else rejections.Add(rejection);
                }
                if(localFits.Count>=options.MinimumThresholdCount){completeVariants++;fits.AddRange(localFits);}
                else if(seedDx==0 && seedDy==0 && ratio==1 && widthRatio==1 && heightRatio==1 && angleDelta==0)break;
            }
            if(completeVariants<(shape is null?3:perturbations.Count))
            {
                details.Add((bestSupport,$"种子({seed.Center.X:F1},{seed.Center.Y:F1}) R={seed.Radius:F1}px：仅{fits.Count}组参数通过，最多{bestSupport}/120条射线支持；{string.Join("/",rejections.Distinct())}。"));
                continue;
            }
            double px=Median(fits.Select(f=> (double)f.Ellipse.Center.X));
            double py=Median(fits.Select(f=> (double)f.Ellipse.Center.Y));
            var position=settings.ToMachine(frame,px,py);
            double spread=fits.Max(f=>
            {
                var q=settings.ToMachine(frame,f.Ellipse.Center.X,f.Ellipse.Center.Y);
                return double.Hypot(q.X-position.X,q.Y-position.Y);
            });
            if(spread>Math.Min(options.MaxCenterSpreadMm,options.MaxFaceCenterSpreadMm))
            {details.Add((bestSupport,$"框选扰动后的圆心变化{spread*1000:F1}µm，超过{Math.Min(options.MaxCenterSpreadMm,options.MaxFaceCenterSpreadMm)*1000:F1}µm上限。"));continue;}
            var representative=fits.MinBy(f=>double.Hypot(f.Ellipse.Center.X-px,f.Ellipse.Center.Y-py))!;
            double diameter=Math.Sqrt(representative.Ellipse.Size.Width*representative.Ellipse.Size.Height)/scale;
            if(diameter<options.MinHoleDiameterMm || diameter>options.MaxHoleDiameterMm ||
                double.Hypot(position.X-frame.X,position.Y-frame.Y)>options.MaxImageCenterDistanceMm)
            {details.Add((bestSupport,$"拟合实体直径{diameter:F3}mm或圆心位置超出允许范围（直径{options.MinHoleDiameterMm:F3}～{options.MaxHoleDiameterMm:F3}mm）。"));continue;}
            var detection=new DatumHoleDetection(frame.Name,frame.ImagePath,px,py,position.X,position.Y,
                representative.Ellipse.Size.Width,representative.Ellipse.Size.Height,representative.Ellipse.Angle,
                spread,representative.Core,representative.Wall,fits.Select(f=>new DatumHoleSample(0,f.Ellipse.Center.X,f.Ellipse.Center.Y)
                {Criterion=shape is null?"LocalEntityBoundaryStepFraction":"EllipseAwareEntityBoundaryStepFraction",EdgeFraction=f.Fraction,
                    SeedDx=f.SeedDx,SeedDy=f.SeedDy,SeedRadiusRatio=f.SeedRadiusRatio}).ToArray())
            {
                BoundaryMode=DatumBoundaryMode.CircularFaceEdge,AngularSupportRatio=fits.Min(f=>f.Support),EdgeRmsPx=fits.Max(f=>f.Rms)
            };
            if(!detections.Any(d=>double.Hypot(d.Px-px,d.Py-py)<20))detections.Add(detection);
        }
        var diagnostics=new List<string> {$"实体圆边框选初值{tested}个；通过圆周支持、亮面/暗壁过渡及参数稳定性筛选{detections.Count}个。",
            "本模型拟合中间实体圆面的外缘，外围黑影不作为边界；不代表加工精度验收。"};
        if(frame.FaceSeed is null)diagnostics.Add("请先框选当前原图中完整的实体小圆面，程序再自动细化圆心；框选初值不是人工孔心结果。");
        diagnostics.AddRange(details.OrderByDescending(d=>d.Support).Take(6).Select(d=>d.Text));
        var status=detections.Count switch {0=>DatumHoleInspectionStatus.Rejected,1=>DatumHoleInspectionStatus.Detected,_=>DatumHoleInspectionStatus.Ambiguous};
        string message=status switch
        {
            DatumHoleInspectionStatus.Detected=>$"检出1个实体圆边，参数敏感性{detections[0].CenterSpreadMm*1000:F1}µm；仅供预览。",
            DatumHoleInspectionStatus.Ambiguous=>$"有{detections.Count}个实体圆边候选，不能唯一指定定位特征。",
            _=>frame.FaceSeed is null ? "尚未框选实体圆边；未输出圆心。" : "实体圆边检查未通过；未输出圆心，具体原因见诊断。"
        };
        return new(frame,status,detections,message,diagnostics) {BoundaryMode=DatumBoundaryMode.CircularFaceEdge};
    }

    private static EdgeFit? Fit(Mat image,CircleSegment seed,double fraction,double scale,DatumDirectionOptions options,out int support,out string rejection,DatumEllipseSeed? shape=null)
    {
        const int rays=120;
        support=0;
        rejection="局部过渡或圆周支持不足";
        double Radius(double angle)
        {
            if(shape is null)return seed.Radius;
            double a=shape.WidthPx/2,b=shape.HeightPx/2,t=angle-shape.AngleDeg*Math.PI/180;
            return 1/Math.Sqrt(Math.Pow(Math.Cos(t)/a,2)+Math.Pow(Math.Sin(t)/b,2));
        }
        var points=new List<Point2f>();var angles=new List<double>();var cores=new List<double>();var walls=new List<double>();
        for(int ray=0;ray<rays;ray++)
        {
            double angle=2*Math.PI*ray/rays,co=Math.Cos(angle),si=Math.Sin(angle);
            double Read(double r)=>Bilinear(image,seed.Center.X+co*r,seed.Center.Y+si*r);
            double Smoothed(double r)=>(Read(r-1)+Read(r-.5)+Read(r)+Read(r+.5)+Read(r+1))/5;
            // 实体圆面一侧可为暗壁、另一侧可为反光；允许局部过渡极性不同，但必须落在同一条圆边上。
            double expectedRadius=Radius(angle);
            double start=expectedRadius*.90,end=expectedRadius*1.10;
            var gradient=new List<(double R,double Strength)>();
            for(double r=start;r<=end;r+=.5)
                gradient.Add((r,Math.Abs(Smoothed(r+.5)-Smoothed(r-.5))));
            double maxStrength=gradient.Max(g=>g.Strength);
            var peaks=gradient.Where(g=>g.Strength>=maxStrength*.65).OrderBy(g=>Math.Abs(g.R-expectedRadius)).ToArray();
            if(peaks.Length==0)continue;
            double peak=peaks[0].R;
            double core=Median(new[]{3.0,5.0,7.0}.Select(d=>Smoothed(peak-d)));
            double outer=Median(new[]{3.0,5.0,7.0}.Select(d=>Smoothed(peak+d)));
            if(Math.Abs(core-outer)<options.MinFaceWallContrast)continue;
            double wall=Math.Min(core,outer),level=core+fraction*(outer-core),sign=Math.Sign(outer-core);
            double bestRadius=double.NaN,bestDistance=double.PositiveInfinity;
            double previous=Smoothed(peak-5);
            for(double r=peak-4.5;r<=peak+5;r+=.5)
            {
                double current=Smoothed(r);
                if((previous-level)*sign<0 && (current-level)*sign>=0 && (current-previous)*sign>.15)
                {
                    double crossing=r-.5+.5*(previous-level)/(previous-current);
                    double distance=Math.Abs(crossing-peak);
                    if(distance<bestDistance) {bestDistance=distance;bestRadius=crossing;}
                }
                previous=current;
            }
            if(!double.IsFinite(bestRadius))continue;
            points.Add(new Point2f((float)(seed.Center.X+co*bestRadius),(float)(seed.Center.Y+si*bestRadius)));
            angles.Add(angle);cores.Add(core);walls.Add(wall);
        }
        support=points.Count;
        if(points.Count<rays*options.MinFaceAngularSupport)return null;
        rejection="椭圆拟合或离群点过多";
        var kept=Enumerable.Range(0,points.Count).ToList();RotatedRect ellipse=default;
        double[] residuals=[];
        for(int iteration=0;iteration<3;iteration++)
        {
            if(kept.Count<rays*options.MinFaceAngularSupport)return null;
            ellipse=Cv2.FitEllipse(kept.Select(i=>points[i]).ToArray());
            if(ellipse.Size.Width<=0 || ellipse.Size.Height<=0 ||
                Math.Max(ellipse.Size.Width,ellipse.Size.Height)/Math.Min(ellipse.Size.Width,ellipse.Size.Height)>1.4)return null;
            residuals=points.Select(p=>Residual(p,ellipse)).ToArray();
            double limit=Math.Min(6,Math.Max(3,Median(kept.Select(i=>Math.Abs(residuals[i])))*3));
            kept=kept.Where(i=>Math.Abs(residuals[i])<=limit).ToList();
        }
        if(kept.Count<rays*options.MinFaceAngularSupport)return null;
        rejection="角度缺口或边缘残差过大";
        double[] supported=kept.Select(i=>angles[i]).OrderBy(a=>a).ToArray();
        double gap=supported[0]+2*Math.PI-supported[^1];
        for(int i=1;i<supported.Length;i++)gap=Math.Max(gap,supported[i]-supported[i-1]);
        double rms=Math.Sqrt(kept.Average(i=>residuals[i]*residuals[i]));
        if(gap>Math.PI/3 || rms/scale>options.MaxFaceEdgeRmsMm)return null;
        // 外侧必须还有与暗壁不同的实体亮度，排除单纯高光岛；不要求圆面自身为黑色。
        var outside=new List<double>();var band=new List<double>();
        foreach(int i in kept)
        {
            double co=Math.Cos(angles[i]),si=Math.Sin(angles[i]);
            outside.Add(Bilinear(image,seed.Center.X+1.6*Radius(angles[i])*co,seed.Center.Y+1.6*Radius(angles[i])*si));
            band.Add(walls[i]);
        }
        double outsideMedian=Median(outside),bandMedian=Median(band);
        rejection=$"外侧亮度{outsideMedian:F1}与暗壁{bandMedian:F1}对比不足";
        if(!double.IsFinite(outsideMedian) || !double.IsFinite(bandMedian) || outsideMedian-bandMedian<options.MinAnnulusContrast)return null;
        rejection="";
        return new(fraction,ellipse,rms,(double)kept.Count/rays,Median(cores),Median(walls));
    }

    private static double Residual(Point2f p,RotatedRect ellipse)
    {
        double angle=ellipse.Angle*Math.PI/180,co=Math.Cos(angle),si=Math.Sin(angle);
        double dx=p.X-ellipse.Center.X,dy=p.Y-ellipse.Center.Y;
        double x=co*dx+si*dy,y=-si*dx+co*dy,a=ellipse.Size.Width/2,b=ellipse.Size.Height/2;
        return (Math.Sqrt(x*x/(a*a)+y*y/(b*b))-1)*Math.Sqrt(a*b);
    }
    private static double Bilinear(Mat image,double x,double y)
    {
        int ix=(int)Math.Floor(x),iy=(int)Math.Floor(y);
        if(ix<0 || iy<0 || ix>=image.Width-1 || iy>=image.Height-1)return double.NaN;
        double fx=x-ix,fy=y-iy;
        return image.At<byte>(iy,ix)*(1-fx)*(1-fy)+image.At<byte>(iy,ix+1)*fx*(1-fy)
            +image.At<byte>(iy+1,ix)*(1-fx)*fy+image.At<byte>(iy+1,ix+1)*fx*fy;
    }
    private static double Median(IEnumerable<double> values)
    {
        var data=values.OrderBy(x=>x).ToArray();return (data[(data.Length-1)/2]+data[data.Length/2])/2;
    }
}
