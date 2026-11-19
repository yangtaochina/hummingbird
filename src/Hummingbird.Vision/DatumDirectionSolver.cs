using Hummingbird.Core;
using OpenCvSharp;

namespace Hummingbird.Vision;

/// <summary>固定C/Z，从两张完整参考孔图中提取孔心，按选定11/1孔的角平分线求12点方向。</summary>
public sealed class DatumDirectionSolver
{
    private sealed record Observation(int Threshold, double X, double Y, double Width, double Height, double Angle,
        double CoreMedian, double AnnulusMedian);

    public DatumDirectionResult Analyze(RingScanSettings settings, RingScanResult ring,
        RingScanFrame eleven, RingScanFrame one, DatumDirectionOptions? options = null)
    {
        options ??= new DatumDirectionOptions();
        options.Validate(); settings.Validate();
        foreach (var f in new[] { eleven, one })
        {
            if (new[] { f.X, f.Y, f.Z, f.C }.Any(v => !double.IsFinite(v)) ||
                Math.Abs(Math.IEEERemainder(f.C - ring.ReferenceC, 360)) > 0.005 || Math.Abs(f.Z - ring.ReferenceZ) > 0.010)
                throw new InvalidDataException("圆环与参考孔必须为同次装夹、固定C/Z的采图记录；当前不能混合旋转采图。");
            if (f.Capture?.Purpose == "RotaryScan") throw new InvalidDataException("旋转采图不能用于当前固定C定向。");
            RingCaptureArchive.VerifyImage(f);
        }
        if (File.ReadAllBytes(eleven.ImagePath).AsSpan().SequenceEqual(File.ReadAllBytes(one.ImagePath)))
            throw new InvalidDataException("11点与1点使用了同一张原图，请检查用途及图片对应关系。");

        var a = Candidates(eleven, settings, options);
        var b = Candidates(one, settings, options);
        var eligible = new List<(DatumHoleDetection A, DatumHoleDetection B)>();
        foreach (var x in a)
        foreach (var y in b)
        {
            if (Math.Abs(double.Hypot(x.MachineX-ring.CenterX, x.MachineY-ring.CenterY)-settings.ExpectedRadius) > options.MaxRadialErrorMm ||
                Math.Abs(double.Hypot(y.MachineX-ring.CenterX, y.MachineY-ring.CenterY)-settings.ExpectedRadius) > options.MaxRadialErrorMm) continue;
            try
            {
                DatumDirectionGeometry.Compute(ring.CenterX, ring.CenterY, x.MachineX, x.MachineY,
                    y.MachineX, y.MachineY, options);
                eligible.Add((x,y));
            }
            catch (InvalidDataException) { }
        }
        if (eligible.Count == 0)
            throw new InvalidDataException($"没有符合参考半径与{options.ExpectedGapDeg:F0}°夹角的孔对。候选孔数：11点={a.Count}，1点={b.Count}。请检查用途、坐标、C/Z和装夹是否一致。");
        if (eligible.Count > 1)
            throw new InvalidDataException("检测到多个可能的参考孔对，不能唯一确定方向。请让指定小孔位于画面中部并排除其他相似孔。");
        var pair = eligible.Single();
        var direction = DatumDirectionGeometry.Compute(ring.CenterX, ring.CenterY, pair.A.MachineX,
            pair.A.MachineY, pair.B.MachineX, pair.B.MachineY, options);
        double spread = 0;
        foreach (var s11 in pair.A.Samples)
        foreach (var s1 in pair.B.Samples)
        {
            var p11 = settings.ToMachine(eleven,s11.Px,s11.Py);
            var p1 = settings.ToMachine(one,s1.Px,s1.Py);
            var check = DatumDirectionGeometry.Compute(ring.CenterX,ring.CenterY,p11.X,p11.Y,p1.X,p1.Y,options);
            spread = Math.Max(spread, Math.Abs(Math.IEEERemainder(check.Twelve-direction.Twelve,360)));
        }
        if (spread > options.MaxDirectionSpreadDeg)
            throw new InvalidDataException($"孔缘参数导致方向变化{spread:F3}°，超过本次预览筛选值，请改善参考孔成像。");
        return new DatumDirectionResult(direction.Twelve,direction.Eleven,direction.One,direction.Gap,spread,pair.A,pair.B);
    }

    private static List<DatumHoleDetection> Candidates(RingScanFrame frame, RingScanSettings settings, DatumDirectionOptions options)
    {
        var inspection = new DatumDirectionSolver().Inspect(frame, settings, options);
        if (inspection.Candidates.Count == 0)
            throw new InvalidDataException(inspection.Message + " " + string.Join("；", inspection.Diagnostics));
        return inspection.Candidates.ToList();
    }

    /// <summary>离线单图质量检查；不依赖用途列、不推断12点、不改变已保存的定位结果。</summary>
    public DatumHoleInspection Inspect(RingScanFrame frame, RingScanSettings settings, DatumDirectionOptions? options = null)
    {
        options ??= new DatumDirectionOptions();
        try
        {
            options.Validate(); settings.Validate();
            if (new[] { frame.X, frame.Y, frame.Z, frame.C }.Any(v => !double.IsFinite(v)))
                throw new InvalidDataException("采图坐标无效，请核对该张原图的配套记录。");
            RingCaptureArchive.VerifyImage(frame);
            return options.BoundaryMode==DatumBoundaryMode.CircularFaceEdge
                ? new CircularFaceBoundarySolver().Inspect(frame,settings,options) : InspectImage(frame, settings, options);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or OpenCVException or ArgumentException)
        {
            return new(frame, DatumHoleInspectionStatus.InputError, [], "无法检查：" + ex.Message,
                ["没有输出孔心，请先核对原图、图像尺寸、坐标和配套记录。"]);
        }
    }

    private static DatumHoleInspection InspectImage(RingScanFrame frame, RingScanSettings settings, DatumDirectionOptions options)
    {
        using var image = Cv2.ImDecode(File.ReadAllBytes(frame.ImagePath),ImreadModes.Grayscale);
        if (image.Empty() || image.Width != settings.ImageWidth || image.Height != settings.ImageHeight)
            throw new InvalidDataException($"{frame.Name} 图像尺寸无效或与像素标定不同。");
        int mx=image.Width/10, my=image.Height/10;
        using var roi = new Mat(image,new Rect(mx,my,image.Width-2*mx,image.Height-2*my));
        using var smooth = new Mat();
        Cv2.GaussianBlur(roi,smooth,new Size(5,5),0);
        double scale = Math.Sqrt(Math.Abs(settings.Jxx*settings.Jyy-settings.Jxy*settings.Jyx));
        var groups = new List<List<Observation>>();
        var contrastDiagnostics = new List<string>();
        int closedShapes = 0, rejectedContrast = 0, rejectedSpread = 0;
        for (int threshold=8; threshold<=80; threshold+=2)
        {
            using var binary = new Mat();
            Cv2.Threshold(smooth,binary,threshold,255,ThresholdTypes.BinaryInv);
            Cv2.FindContours(binary,out Point[][] contours,out _,RetrievalModes.List,ContourApproximationModes.ApproxNone);
            foreach (var contour in contours)
            {
                if (contour.Length < 12) continue;
                var bounds=Cv2.BoundingRect(contour);
                if (bounds.Left<=1 || bounds.Top<=1 || bounds.Right>=roi.Width-1 || bounds.Bottom>=roi.Height-1) continue;
                double area=Cv2.ContourArea(contour);
                if (area < Math.PI*Math.Pow(options.MinHoleDiameterMm*scale/2,2)*0.5) continue;
                var ellipse=Cv2.FitEllipse(contour);
                double ew=ellipse.Size.Width,eh=ellipse.Size.Height;
                double diameter=Math.Sqrt(ew*eh)/scale;
                double fill=area/(Math.PI*ew*eh/4);
                if (!double.IsFinite(diameter) || diameter<options.MinHoleDiameterMm || diameter>options.MaxHoleDiameterMm ||
                    Math.Max(ew,eh)/Math.Min(ew,eh)>1.8 || fill<0.60 || fill>1.15) continue;
                double px=ellipse.Center.X+mx,py=ellipse.Center.Y+my;
                var p=settings.ToMachine(frame,px,py);
                if (double.Hypot(p.X-frame.X,p.Y-frame.Y)>options.MaxImageCenterDistanceMm) continue;
                closedShapes++;
                // 每个阈值的轮廓都需先验证极性，不能只检查合并后的代表轮廓。
                var contrast = Contrast(image, px, py, ew, eh, ellipse.Angle);
                if (contrast is null || contrast.Value.Annulus - contrast.Value.Core < options.MinAnnulusContrast)
                {
                    rejectedContrast++;
                    if(contrast is not null && contrastDiagnostics.Count<6)
                        contrastDiagnostics.Add($"拒绝轮廓T={threshold}，中心({px:F1},{py:F1})，椭圆{ew:F1}×{eh:F1}px："
                            +$"孔心中位灰度{contrast.Value.Core:F1}，外围中位灰度{contrast.Value.Annulus:F1}；"
                            +RadialDiagnostic(image,px,py,ew,eh,ellipse.Angle));
                    continue;
                }
                var observation=new Observation(threshold,px,py,ew,eh,ellipse.Angle,contrast.Value.Core,contrast.Value.Annulus);
                var group=groups.FirstOrDefault(g=>double.Hypot(Median(g.Select(v=>v.X))-px,Median(g.Select(v=>v.Y))-py)<30);
                if (group==null) groups.Add([observation]);
                else if (group.All(v=>v.Threshold!=threshold)) group.Add(observation);
            }
        }
        var result = new List<DatumHoleDetection>();
        foreach (var group in groups.Where(g=>g.Count>=options.MinimumThresholdCount))
        {
            double px=Median(group.Select(v=>v.X)),py=Median(group.Select(v=>v.Y));
            var p=settings.ToMachine(frame,px,py);
            double spread=group.Max(v=>
            {
                var q=settings.ToMachine(frame,v.X,v.Y);
                return double.Hypot(q.X-p.X,q.Y-p.Y);
            });
            if (spread>options.MaxCenterSpreadMm) { rejectedSpread++; continue; }
            var representative=group.OrderBy(v=>double.Hypot(v.X-px,v.Y-py)).First();
            result.Add(new DatumHoleDetection(frame.Name,frame.ImagePath,px,py,p.X,p.Y,
                representative.Width,representative.Height,representative.Angle,spread,representative.CoreMedian,representative.AnnulusMedian,
                group.Select(v=>new DatumHoleSample(v.Threshold,v.X,v.Y)).ToArray()));
        }
        int support=groups.Select(g=>g.Count).DefaultIfEmpty(0).Max();
        var diagnostics = new List<string> {
            $"形状与位置初筛轮廓{closedShapes}个；暗孔/亮缘对比不足排除{rejectedContrast}个。",
            $"同一候选最多获得{support}个有效阈值支持，至少需要{options.MinimumThresholdCount}个。",
            "阈值敏感性是图像稳定性指标，不能作为机床精度或实际加工误差。" };
        if(result.Count==0)diagnostics.AddRange(contrastDiagnostics);
        if (rejectedSpread > 0) diagnostics.Add($"{rejectedSpread}组候选孔心随阈值变化过大，已拒绝输出。");
        if (result.Count==0)
            return new(frame, DatumHoleInspectionStatus.Rejected, [],
                $"{frame.Name} 未检出稳定、完整的参考孔。请检查孔缘、阴影和曝光；不要放宽条件强行取心。", diagnostics);
        if (result.Count > 1)
            return new(frame, DatumHoleInspectionStatus.Ambiguous, result,
                $"检出{result.Count}个候选孔，单张照片不能唯一指定目标。", diagnostics);
        var hole=result[0];
        if (hole.CenterSpreadMm > options.CenterSpreadWarningMm)
            diagnostics.Add($"孔心阈值敏感性{hole.CenterSpreadMm*1000:F1}µm，超过图像预警值{options.CenterSpreadWarningMm*1000:F1}µm；仅用于预览，需改善识别稳定性。");
        return new(frame, DatumHoleInspectionStatus.Detected, result,
            $"检出1个参考孔，阈值敏感性{hole.CenterSpreadMm*1000:F1}µm；通过试验筛选不等于加工精度合格。", diagnostics);
    }

    private static (double Core, double Annulus)? Contrast(Mat image, double px, double py, double width, double height, double degrees)
    {
        var core=new List<double>(); var annulus=new List<double>();
        double angle=degrees*Math.PI/180,co=Math.Cos(angle),si=Math.Sin(angle);
        for(int i=0;i<96;i++)
        foreach(double radius in new[]{0.15,0.30,0.45,1.15,1.30,1.45,1.60})
        {
            double t=2*Math.PI*i/96,dx=width/2*radius*Math.Cos(t),dy=height/2*radius*Math.Sin(t);
            int x=(int)Math.Round(px+co*dx-si*dy),y=(int)Math.Round(py+si*dx+co*dy);
            if(x<0 || y<0 || x>=image.Width || y>=image.Height) continue;
            (radius<1?core:annulus).Add(image.At<byte>(y,x));
        }
        return core.Count<230 || annulus.Count<300 ? null : (Median(core),Median(annulus));
    }

    private static double Median(IEnumerable<double> values)
    {
        var a=values.OrderBy(v=>v).ToArray();
        return (a[(a.Length-1)/2]+a[a.Length/2])/2;
    }

    private static string RadialDiagnostic(Mat image,double px,double py,double width,double height,double degrees)
    {
        double angle=degrees*Math.PI/180,co=Math.Cos(angle),si=Math.Sin(angle);
        var samples=new List<string>();
        foreach(double radius in new[]{0.45,0.80,1.00,1.15,1.30,1.60,1.90})
        {
            var values=new List<double>();
            for(int i=0;i<96;i++)
            {
                double t=2*Math.PI*i/96,dx=width/2*radius*Math.Cos(t),dy=height/2*radius*Math.Sin(t);
                int x=(int)Math.Round(px+co*dx-si*dy),y=(int)Math.Round(py+si*dx+co*dy);
                if(x>=0 && y>=0 && x<image.Width && y<image.Height)values.Add(image.At<byte>(y,x));
            }
            if(values.Count>0)samples.Add($"r{radius:F2}={Median(values):F1}");
        }
        return string.Join("，",samples)+"（半径相对候选轮廓；仅诊断，不改变筛选）。";
    }
}
