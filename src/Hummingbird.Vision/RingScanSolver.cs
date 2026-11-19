using System.Runtime.InteropServices;
using System.Text.Json;
using Hummingbird.Core;
using OpenCvSharp;

namespace Hummingbird.Vision;

/// <summary>固定C轴，用不同X/Y位置的局部亮圆环求机床平面圆心。结果供定位试验使用。</summary>
public sealed partial class RingScanSolver
{
    private sealed record Profile(int FrameIndex, double Along, double Nx, double Ny,
        double[] Values, int Peak, int HalfLength);
    private sealed record Circle(double X, double Y, double R, double[] Residuals);

    public static CameraFrame LoadFrame(string path)
    {
        using var image = Cv2.ImDecode(File.ReadAllBytes(path), ImreadModes.Grayscale);
        if (image.Empty()) throw new InvalidDataException("图像读取失败 " + path);
        var gray = new byte[checked(image.Width * image.Height)];
        Marshal.Copy(image.Data, gray, 0, gray.Length);
        return new CameraFrame(gray, image.Width, image.Height, File.GetLastWriteTime(path));
    }

    public static void SaveFrame(string path, CameraFrame frame)
    {
        ValidateFrame(frame);
        using var image = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
        Marshal.Copy(frame.Gray, 0, image.Data, frame.Gray.Length);
        Cv2.ImEncode(".bmp", image, out byte[] data);
        File.WriteAllBytes(path, data);
    }

    public RingScanResult Analyze(RingScanSession session)
    {
        if(session.Frames.Any(f=>f.Role!=RingFrameRole.RingArc))
            throw new InvalidDataException("参考孔照片不能参与圆环拟合，请先按采图用途分组。");
        var settings = session.Settings;
        settings.Validate();
        ValidatePoses(session.Frames, settings);
        var initialization = ResolveInitialization(session.Frames, settings);
        // 粗中心只用于取边方向和覆盖检查；克隆计算参数，不改写采图时的参数快照或输入记录。
        settings = JsonSerializer.Deserialize<RingScanSettings>(JsonSerializer.Serialize(settings))!;
        settings.HintX = initialization.HintX; settings.HintY = initialization.HintY;
        session = new RingScanSession { Settings=settings, Frames=session.Frames.ToList() };
        var profiles = new List<Profile>();
        for (int i = 0; i < session.Frames.Count; i++)
        {
            RingCaptureArchive.VerifyImage(session.Frames[i]);
            var frame = LoadFrame(session.Frames[i].ImagePath);
            ValidateFrame(frame);
            if (frame.Width != settings.ImageWidth || frame.Height != settings.ImageHeight)
                throw new InvalidDataException($"{session.Frames[i].Name} 的图像尺寸与像素标定不一致。");
            profiles.AddRange(ExtractProfiles(frame, session.Frames[i], i, settings));
        }

        var edges = new[] { .30, .20, .40, .50 }.Select(level => ExtractEdges(profiles, session, level)).ToArray();
        return FitPrepared(session, initialization, edges);
    }

    private static RingScanResult FitPrepared(RingScanSession session,RingScanInitialization initialization,IReadOnlyList<RingEdgePoint>[] edges,bool allowRecovery=true)
    {
        try{return FitPreparedStrict(session,initialization,edges);}
        catch(InvalidDataException original) when(allowRecovery)
        {
            // 原合格结果完全保留；只在失败后考虑每处至多20%的局部异常点。
            var cleaned=edges.Select(e=>TrimLocalOutliers(e,session)).ToArray();
            if(cleaned[0].Count==edges[0].Count)throw;
            var result=FitPreparedStrict(session,initialization,cleaned);
            double leaveShift=0;
            for(int view=0;view<session.Frames.Count;view++)
            {
                var fit=FitCircle(cleaned[0].Where(p=>p.FrameIndex!=view).ToArray());
                leaveShift=Math.Max(leaveShift,double.Hypot(fit.X-result.CenterX,fit.Y-result.CenterY));
            }
            if(leaveShift>Math.Min(.015,session.Settings.MaxSensitivity))
                throw new InvalidDataException($"圆环兼容拟合的留一方位圆心变化{leaveShift*1000:F1}µm，拒绝输出孔位。");
            return result with {CompatibilityRecovery=new(Enumerable.Range(0,session.Frames.Count).Select(i=>
                edges[0].Count(p=>p.FrameIndex==i)-cleaned[0].Count(p=>p.FrameIndex==i)).ToArray(),leaveShift,original.Message)};
        }
    }
    private static IReadOnlyList<RingEdgePoint> TrimLocalOutliers(IReadOnlyList<RingEdgePoint> points,RingScanSession session)
    {
        var kept=points.ToArray();
        double Median(IEnumerable<double> values){var a=values.Order().ToArray();return (a[(a.Length-1)/2]+a[a.Length/2])/2;}
        for(int iteration=0;iteration<3;iteration++)
        {
            var circle=FitCircle(kept);var next=new List<RingEdgePoint>();
            for(int view=0;view<session.Frames.Count;view++)
            {
                var all=points.Where(p=>p.FrameIndex==view).ToArray();var group=kept.Where(p=>p.FrameIndex==view).ToArray();
                double Residual(RingEdgePoint p)=>double.Hypot(p.X-circle.X,p.Y-circle.Y)-circle.R;
                double middle=Median(group.Select(Residual));
                double limit=Math.Max(.010,4*Median(group.Select(p=>Math.Abs(Residual(p)-middle))));
                var valid=group.Where(p=>Math.Abs(Residual(p)-middle)<=limit).ToArray();
                if(valid.Length<20 || valid.Length<Math.Ceiling(all.Length*.8))
                    throw new InvalidDataException("圆环局部异常超过每方位20%上限或有效点不足，未作兼容拟合。");
                var pose=session.Frames[view];double dx=pose.X-session.Settings.HintX,dy=pose.Y-session.Settings.HintY;
                double Project(RingEdgePoint p)=>-dy*p.X+dx*p.Y;
                double span=all.Max(Project)-all.Min(Project);
                if(span<=0 || valid.Max(Project)-valid.Min(Project)<span*.8)
                    throw new InvalidDataException("圆环异常剔除后覆盖范围不足，拒绝靠局部短弧外推圆心。");
                next.AddRange(valid);
            }
            if(next.Count==kept.Length)break;
            kept=next.ToArray();
        }
        return kept;
    }

    private static RingScanResult FitPreparedStrict(RingScanSession session, RingScanInitialization initialization,
        IReadOnlyList<RingEdgePoint>[] edges)
    {
        var settings = session.Settings;
        var points = edges[0];
        var circle = FitCircle(points);
        var variants=new List<RingFitVariant> {new(circle.X,circle.Y,circle.R)};
        double sensitivity = 0;
        foreach (var comparisonEdges in edges.Skip(1))
        {
            var comparison = FitCircle(comparisonEdges);
            variants.Add(new(comparison.X,comparison.Y,comparison.R));
            sensitivity = Math.Max(sensitivity, double.Hypot(comparison.X - circle.X, comparison.Y - circle.Y));
        }
        double rms = Math.Sqrt(circle.Residuals.Average(r => r * r));
        var views = session.Frames.Select((f, i) =>
        {
            var residuals = points.Select((p, k) => (p, k)).Where(v => v.p.FrameIndex == i)
                .Select(v => circle.Residuals[v.k]).ToArray();
            return new RingViewFit(f.Name, residuals.Length, Math.Sqrt(residuals.Average(r => r * r)));
        }).ToList();

        if (Math.Abs(circle.R - settings.ExpectedRadius) > settings.RadiusTolerance)
            throw new InvalidDataException($"拟合半径 {circle.R:F3}mm 与圆环预期半径不符，请核对边界、标定和图片坐标配对。");
        if (rms > settings.MaxRms || views.Any(v => v.RmsMm > settings.MaxRms * 2) || sensitivity > settings.MaxSensitivity)
            throw new InvalidDataException($"圆边一致性未通过试验筛选。RMS={rms * 1000:F1}µm，参数敏感性={sensitivity * 1000:F1}µm。请复核成像和坐标。");
        return new RingScanResult(circle.X, circle.Y, circle.R, rms, sensitivity,
            session.Frames[0].Z, session.Frames[0].C, points, views) { Initialization=initialization,FitVariants=variants };
    }

    private static void ValidateFrame(CameraFrame frame)
    {
        if (frame.Width < 32 || frame.Height < 32 || frame.Gray.Length != (long)frame.Width * frame.Height)
            throw new InvalidDataException("灰度图像尺寸或数据长度无效。");
    }

    private static void ValidatePoses(IReadOnlyList<RingScanFrame> frames, RingScanSettings settings)
    {
        if (frames.Count < 4 || frames.Count > 32)
            throw new InvalidDataException("请登记4至32个分散在圆周上的采图位置。");
        foreach (var f in frames)
        {
            if (new[] { f.X, f.Y, f.Z, f.C }.Any(v => !double.IsFinite(v)))
                throw new InvalidDataException("采图X/Y/Z/C必须为有限数值。");
            double angle = Math.Abs(Math.IEEERemainder(f.C - frames[0].C, 360));
            if (angle > 0.005 || Math.Abs(f.Z - frames[0].Z) > 0.010)
                throw new InvalidDataException("本流程要求固定C和Z采图。C差须≤0.005°，Z差须≤0.010mm。");
        }
    }

    private static double MaximumGap(IReadOnlyList<RingScanFrame> frames, double cx, double cy)
    {
        var angles = frames.Select(f => Math.Atan2(f.Y - cy, f.X - cx))
            .OrderBy(a => a).ToArray();
        double maxGap = angles[0] + 2 * Math.PI - angles[^1];
        for (int i = 1; i < angles.Length; i++) maxGap = Math.Max(maxGap, angles[i] - angles[i - 1]);
        return maxGap * 180 / Math.PI;
    }

    private static RingScanInitialization ResolveInitialization(IReadOnlyList<RingScanFrame> frames, RingScanSettings settings)
    {
        bool Usable(double x, double y) => frames.All(f => double.Hypot(f.X-x,f.Y-y)>=settings.ExpectedRadius*0.5)
            && MaximumGap(frames,x,y)<=150;
        double currentGap=MaximumGap(frames,settings.HintX,settings.HintY);
        if(Usable(settings.HintX,settings.HintY))
            return new(settings.HintX,settings.HintY,false,currentGap,null);

        // 默认(0,0)是机床原点，不一定接近表盘。仅用本次圆环采图位置初始化，随后仍执行原覆盖/图像筛选。
        double mx=frames.Average(f=>f.X),my=frames.Average(f=>f.Y);
        double xx=frames.Sum(f=>(f.X-mx)*(f.X-mx)),yy=frames.Sum(f=>(f.Y-my)*(f.Y-my));
        double xy=frames.Sum(f=>(f.X-mx)*(f.Y-my)),trace=xx+yy;
        if(trace<1e-6 || xx*yy-xy*xy<=trace*trace*1e-4)
            throw new InvalidDataException("采图位置重复或接近一条直线，无法自动估计粗中心；请核对坐标和圆周分布。");
        var coarse=FitCircle(frames.Select((f,i)=>new RingEdgePoint(i,0,0,f.X,f.Y)).ToArray());
        double matrixTrace=settings.Jxx*settings.Jxx+settings.Jxy*settings.Jxy+settings.Jyx*settings.Jyx+settings.Jyy*settings.Jyy;
        double det=settings.Jxx*settings.Jyy-settings.Jxy*settings.Jyx;
        double minSingular=Math.Sqrt(2*det*det/(matrixTrace+Math.Sqrt(Math.Max(0,matrixTrace*matrixTrace-4*det*det))));
        double halfFieldDiagonal=double.Hypot(settings.ImageWidth,settings.ImageHeight)/(2*minSingular);
        if(Math.Abs(coarse.R-settings.ExpectedRadius)>settings.RadiusTolerance+halfFieldDiagonal ||
            !Usable(coarse.X,coarse.Y))
            throw new InvalidDataException("采图位置覆盖不足或位置尺度不符：自动粗中心初始化后仍未通过圆周覆盖检查，请核对方位和坐标。");
        return new(coarse.X,coarse.Y,true,MaximumGap(frames,coarse.X,coarse.Y),coarse.R);
    }

    private static List<Profile> ExtractProfiles(CameraFrame image, RingScanFrame pose, int index, RingScanSettings settings)
    {
        double dx = pose.X - settings.HintX, dy = pose.Y - settings.HintY;
        double nx = -(settings.Jxx * dx + settings.Jxy * dy);
        double ny = -(settings.Jyx * dx + settings.Jyy * dy);
        double length = double.Hypot(nx, ny);
        nx /= length; ny /= length;
        int extent = Math.Min(500, image.Height * 5 / 12);
        var profiles = new List<Profile>();
        for (int along = -extent; along <= extent; along += 20)
        {
            double baseX=(image.Width-1)/2.0-ny*along,baseY=(image.Height-1)/2.0+nx*along;
            // 每条剖面取传感器内可用的长度，避免固定±450px漏掉已入画但偏离中心的圆环。
            double availableX=Math.Abs(nx)<1e-9 ? double.PositiveInfinity : Math.Min(baseX-8,image.Width-9-baseX)/Math.Abs(nx);
            double availableY=Math.Abs(ny)<1e-9 ? double.PositiveInfinity : Math.Min(baseY-8,image.Height-9-baseY)/Math.Abs(ny);
            int half=(int)Math.Floor(Math.Min(900,Math.Min(availableX,availableY)));
            if(half<200)continue;
            var raw = new double[half * 2 + 1];
            bool inside = true;
            for (int k = 0; k < raw.Length; k++)
            {
                double x = (image.Width - 1) / 2.0 - ny * along + nx * (k - half);
                double y = (image.Height - 1) / 2.0 + nx * along + ny * (k - half);
                if (x < 0 || y < 0 || x >= image.Width - 1 || y >= image.Height - 1)
                { inside = false; break; }
                raw[k] = Bilinear(image, x, y);
            }
            if (!inside) continue;
            var smoothed = new double[raw.Length];
            for (int k = 7; k < raw.Length - 7; k++)
            {
                double sum = 0;
                for (int j = -7; j <= 7; j++) sum += raw[k + j];
                smoothed[k] = sum / 15;
            }
            int margin = Math.Min(120, half / 2), peak = margin;
            for (int k = margin; k < smoothed.Length - margin; k++)
                if (smoothed[k] > smoothed[peak]) peak = k;
            if (smoothed[peak] >= 80)
                profiles.Add(new Profile(index, along, nx, ny, smoothed, peak, half));
        }
        return profiles;
    }

    private static double Bilinear(CameraFrame f, double x, double y)
    {
        int ix = (int)x, iy = (int)y, offset = iy * f.Width + ix;
        double fx = x - ix, fy = y - iy;
        return f.Gray[offset] * (1 - fx) * (1 - fy) + f.Gray[offset + 1] * fx * (1 - fy)
            + f.Gray[offset + f.Width] * (1 - fx) * fy + f.Gray[offset + f.Width + 1] * fx * fy;
    }

    private static List<RingEdgePoint> ExtractEdges(List<Profile> profiles, RingScanSession session, double fraction)
    {
        var points = new List<RingEdgePoint>();
        foreach (var profile in profiles)
        {
            var v = profile.Values;
            var sorted = v.Skip(80).Take(v.Length - 160).OrderBy(value => value).ToArray();
            double baseline = sorted[(int)((sorted.Length - 1) * 0.15)];
            double level = baseline + fraction * (v[profile.Peak] - baseline);
            int left = profile.Peak, right = profile.Peak;
            while (left > 25 && v[left] > level) left--;
            while (right < v.Length - 26 && v[right] > level) right++;
            if (left <= 25 || right >= v.Length - 26 || right - left < 12 || right - left > 450) continue;
            double l = left + (level - v[left]) / (v[left + 1] - v[left]);
            double r = right - 1 + (level - v[right - 1]) / (v[right] - v[right - 1]);
            double middle = (l + r) / 2 - profile.HalfLength;
            double px = (session.Settings.ImageWidth - 1) / 2.0 - profile.Ny * profile.Along + profile.Nx * middle;
            double py = (session.Settings.ImageHeight - 1) / 2.0 + profile.Nx * profile.Along + profile.Ny * middle;
            var (x, y) = session.Settings.ToMachine(session.Frames[profile.FrameIndex], px, py);
            points.Add(new RingEdgePoint(profile.FrameIndex, px, py, x, y));
        }
        for (int i = 0; i < session.Frames.Count; i++)
            if (points.Count(p => p.FrameIndex == i) < 20)
                throw new InvalidDataException($"{session.Frames[i].Name} 可用圆边不足20处，请检查亮带、曝光、粗中心和坐标。");
        return points;
    }

    private static Circle FitCircle(IReadOnlyList<RingEdgePoint> points)
    {
        double ox = points.Average(p => p.X), oy = points.Average(p => p.Y);
        using var a = new Mat(points.Count, 3, MatType.CV_64FC1);
        using var b = new Mat(points.Count, 1, MatType.CV_64FC1);
        using var solution = new Mat();
        for (int i = 0; i < points.Count; i++)
        {
            double x = points[i].X - ox, y = points[i].Y - oy;
            a.Set(i, 0, 2 * x); a.Set(i, 1, 2 * y); a.Set(i, 2, 1.0);
            b.Set(i, 0, x * x + y * y);
        }
        if (!Cv2.Solve(a, b, solution, DecompTypes.SVD)) throw new InvalidDataException("圆拟合求解失败。");
        double cx = solution.At<double>(0), cy = solution.At<double>(1);
        double radius = Math.Sqrt(solution.At<double>(2) + cx * cx + cy * cy);
        cx += ox; cy += oy;
        for (int iteration = 0; iteration < 12; iteration++)
        {
            for (int i = 0; i < points.Count; i++)
            {
                double x = points[i].X - cx, y = points[i].Y - cy, distance = double.Hypot(x, y);
                if (distance < 1e-9) throw new InvalidDataException("圆拟合几何退化。");
                a.Set(i, 0, -x / distance); a.Set(i, 1, -y / distance); a.Set(i, 2, -1.0);
                b.Set(i, 0, radius - distance);
            }
            if (!Cv2.Solve(a, b, solution, DecompTypes.SVD)) throw new InvalidDataException("圆拟合迭代失败。");
            cx += solution.At<double>(0); cy += solution.At<double>(1); radius += solution.At<double>(2);
        }
        if (!double.IsFinite(cx) || !double.IsFinite(cy) || !double.IsFinite(radius) || radius <= 0)
            throw new InvalidDataException("圆拟合结果无效。");
        return new Circle(cx, cy, radius, points.Select(p => double.Hypot(p.X - cx, p.Y - cy) - radius).ToArray());
    }
}
