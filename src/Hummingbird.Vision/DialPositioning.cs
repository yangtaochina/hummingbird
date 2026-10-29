using System.Runtime.InteropServices;
using Hummingbird.Core;
using OpenCvSharp;

namespace Hummingbird.Vision;

/// <summary>
/// 表盘定位（OpenCV），实现 IVisionService。算法三遍式：
///
///   第一遍：全体外轮廓 → 椭圆拟合 粗定心；建"半径-角度"剖面找出缺口扇区
///   第二遍：剔除缺口 ±25° 扇区后重新拟合 → 精定心（缺口会把拟合中心拉偏，必须剔）
///   第三遍：全体轮廓点 + 精修中心重建剖面 → 缺口深度加权质心定角向（亚度级）
///
/// 为什么这么绕（一个坑的教训）：
///   - 直接拿全体轮廓拟合：缺口那 3° 的"缺口弦"把中心拉偏 3~4px（约 0.07mm）→ 超差
///   - 剔除缺口后在剩余点上找缺口：缺口区域没数据，凹陷检测跑到别处 → 角度全错
///   - 所以：剔缺口只为定心，找缺口必须回到全体点
///
/// 可注入日志器（可选），三遍各自报进度，现场调光/调曝光时能看到卡在哪一步。
/// </summary>
public sealed class DialPositioning : IVisionService
{
    private const int BinCount = 360; // 1°一个角度桶

    private readonly IAppLogger? _log;   // 可空：不注入也能跑（SmokeTest）

    public DialPositioning(IAppLogger? log = null) { _log = log; }

    public async Task<PartPose> LocateAsync(CameraFrame frame, VisionOptions opt, CancellationToken ct = default)
    {
        await Task.Yield();
        try
        {
            // ---- 预处理：去噪（5x5 高斯）→ Otsu 自动二值化（盘亮/背景暗自动分）----
            using var src = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
            Marshal.Copy(frame.Gray, 0, src.Data, frame.Gray.Length);
            using var blur = new Mat();
            Cv2.GaussianBlur(src, blur, new Size(5, 5), 0);

            using var bw = new Mat();
            Cv2.Threshold(blur, bw, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);

            // ---- 外轮廓 = 最大面积轮廓（比盘小的噪声块自然被排除）----
            Cv2.FindContours(bw, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
            var contour = contours.OrderByDescending(c => Cv2.ContourArea(c)).FirstOrDefault();
            if (contour is not { Length: > 60 })
                return Fail("画面中未找到工件轮廓（检查光源/曝光/视野）");
            _log?.Info($"[视觉] 外轮廓 {contour.Length} 点");

            // ---- 第一遍：全体轮廓粗定心 + 找缺口扇区 ----
            var fit1 = FitCircle(contour);
            var (bins1, mean1, std1) = BuildProfile(contour, fit1.Cx, fit1.Cy);
            var run1 = FindDipRun(bins1, mean1, std1);
            if (run1 is null)
                return Fail("未找到可靠的零位缺口（外缘无凹陷带），请确认缺口可见");
            _log?.Info($"[视觉] 一遍：粗中心=({fit1.Cx:F1},{fit1.Cy:F1}) 缺口扇区@{(run1.Value.Start + run1.Value.Len / 2.0) % 360:F0}° 宽{run1.Value.Len}°");

            // ---- 第二遍：剔除缺口 ±25° 扇区后精定心（仅用于椭圆拟合）----
            double notchApprox = (run1.Value.Start + run1.Value.Len / 2.0) % 360;
            var kept = contour.Where(p =>
            {
                double clock = ClockAngle(p.X - fit1.Cx, p.Y - fit1.Cy);
                return AngleDist(clock, notchApprox) > 25;
            }).ToArray();
            var fit = kept.Length > contour.Length / 2 ? FitCircle(kept) : fit1;
            _log?.Info($"[视觉] 二遍：精中心=({fit.Cx:F1},{fit.Cy:F1})（剔除缺口后用 {kept.Length}/{contour.Length} 点）");

            // ---- 第三遍：全体轮廓点 + 精修中心重建剖面，凹陷深度加权质心定角向 ----
            var (bins, mean, std) = BuildProfile(contour, fit.Cx, fit.Cy);
            var run = FindDipRun(bins, mean, std) ?? run1;
            double notchFrom = run.Value.Start, notchTo = run.Value.Start + run.Value.Len;
            double wSum = 0, sSin = 0, sCos = 0;
            for (double a = notchFrom; a <= notchTo; a += 0.25)
            {
                double r = BinAt(bins, a);
                double w = r > 0 ? Math.Max(0, mean - r) : 0;   // 凹陷深度作权重
                w = w * w;                                       // 平方权重：突出缺口底部
                if (w <= 0) continue;
                double rad = a * Math.PI / 180;
                wSum += w; sSin += w * Math.Sin(rad); sCos += w * Math.Cos(rad);
            }
            // 角度质心 = 加权向量的方向（环形平均，避免 359°/1° 跨零问题）
            double notchClock = wSum > 0
                ? ((Math.Atan2(sSin, sCos) * 180 / Math.PI) % 360 + 360) % 360
                : notchFrom % 360;
            _log?.Info($"[视觉] 三遍：缺口角向={notchClock:F2}°（加权质心，权重和{wSum:F0}）");

            // ---- 尺寸校验：测得外缘半径 vs 预期（防拿错件/视野不对）----
            double expectedR = opt.ExpectedOuterDiameterMm / 2 / Math.Max(opt.PixelSizeMm, 1e-6);
            double sizeErr = expectedR > 0 ? Math.Abs(fit.R - expectedR) / expectedR : 0;
            if (sizeErr > 0.3)
                return Fail($"外缘半径偏差过大：测到{fit.R:F0}px vs 预期{expectedR:F0}px（{sizeErr:P0}）");

            // ---- 置信度：椭圆圆度 + 尺寸吻合度 + 缺口深度 三项加权 ----
            double dipDepth = std / mean;
            double conf = Math.Max(0, Math.Min(1,
                0.5 * fit.AxesRatio + 0.3 * (1 - Math.Min(sizeErr / 0.3, 1)) + 0.2 * Math.Min(dipDepth / 0.05, 1)));

            // 装卡转角 = 实测缺口角 - 名义零位角，归一化到 ±180°
            double angleOffset = ((notchClock - opt.NominalDatumAngleDeg) % 360 + 540) % 360 - 180;
            return new PartPose(true, fit.Cx, fit.Cy, fit.R, notchClock, angleOffset, conf,
                $"中心=({fit.Cx:F1},{fit.Cy:F1})px 外缘R={fit.R:F0}px 缺口@{notchClock:F1}° 转角={angleOffset:F2}°");
        }
        catch (Exception ex)
        {
            return Fail("定位异常：" + ex.Message);
        }
    }

    /// <summary>统一失败出口（Success=false + 原因文本，UI/日志直接显示）。</summary>
    private PartPose Fail(string why)
    {
        _log?.Warn("[视觉] " + why);
        return new PartPose(false, 0, 0, 0, 0, 0, 0, why);
    }

    /// <summary>
    /// 图像坐标 → 时钟角（12H=0，顺时针）。
    /// 注意图像 y 轴向下，所以数学角要翻转：clock = 90° - atan2(-dy, dx)。
    /// 全代码的角度体系以此时钟角为基准（和表盘图纸的 12H 表述一致）。
    /// </summary>
    private static double ClockAngle(double dx, double dy)
        => ((90 - Math.Atan2(-dy, dx) * 180 / Math.PI) % 360 + 360) % 360;

    /// <summary>环形角度差（0~180°，正确处理 359° vs 1° 这种跨零）。</summary>
    private static double AngleDist(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    /// <summary>椭圆拟合结果：圆心/平均半径/长短轴比（圆度，1.0=完美圆）。</summary>
    private sealed record CircleFit(double Cx, double Cy, double R, double AxesRatio);

    /// <summary>最小二乘椭圆拟合（OpenCv FitEllipse），比 Hough 圆稳、且给亚像素中心。</summary>
    private static CircleFit FitCircle(Point[] pts)
    {
        var ell = Cv2.FitEllipse(pts);
        double r = (ell.Size.Width + ell.Size.Height) / 4;
        double ratio = Math.Min(ell.Size.Width, ell.Size.Height) / Math.Max(ell.Size.Width, ell.Size.Height);
        return new CircleFit(ell.Center.X, ell.Center.Y, r, ratio);
    }

    /// <summary>
    /// 径向半径剖面：把轮廓点按角度扔进 360 个桶，桶值=该角度的平均半径。
    /// 再做环形平滑（3桶滑窗，跨 359°/0° 正确回绕）。
    /// 缺口在剖面上表现为一段"半径凹陷带"。
    /// </summary>
    private static (double[] Bins, double Mean, double Std) BuildProfile(Point[] pts, double cx, double cy)
    {
        var sum = new double[BinCount];
        var cnt = new int[BinCount];
        foreach (var p in pts)
        {
            double dx = p.X - cx, dy = p.Y - cy;
            double r = Math.Sqrt(dx * dx + dy * dy);
            int b = (int)ClockAngle(dx, dy) % BinCount;
            sum[b] += r; cnt[b]++;
        }
        var raw = new double[BinCount];
        for (int i = 0; i < BinCount; i++) if (cnt[i] > 0) raw[i] = sum[i] / cnt[i];
        var bins = new double[BinCount];
        for (int i = 0; i < BinCount; i++)
        {
            double a = raw[(i - 1 + BinCount) % BinCount], b = raw[i], c = raw[(i + 1) % BinCount];
            int n = (a > 0 ? 1 : 0) + (b > 0 ? 1 : 0) + (c > 0 ? 1 : 0);
            bins[i] = n > 0 ? (a + b + c) / n : 0;
        }
        var vals = bins.Where(v => v > 0).ToArray();
        double mean = vals.Average();
        double std = Math.Sqrt(vals.Average(v => (v - mean) * (v - mean)));
        return (bins, mean, std);
    }

    /// <summary>
    /// 找最长连续凹陷带（半径 &lt; mean-0.6σ，环形搜索）。
    /// 长度限制 2°~90°：太短=噪声，太长=拿错特征（都不是缺口）。
    /// </summary>
    private static (int Start, int Len)? FindDipRun(double[] bins, double mean, double std)
    {
        bool IsDip(int i) => bins[i] > 0 && bins[i] < mean - 0.6 * std;
        int bestLen = 0, bestStart = -1;
        for (int start = 0; start < BinCount; start++)
        {
            if (!IsDip(start)) continue;
            int len = 0;
            while (len < BinCount && IsDip((start + len) % BinCount)) len++;
            if (len > bestLen) { bestLen = len; bestStart = start; }
            start += len;
        }
        return (bestStart < 0 || bestLen is < 2 or > 90) ? null : (bestStart, bestLen);
    }

    /// <summary>取指定角度（可为小数/越界）所在桶的半径值。</summary>
    private static double BinAt(double[] bins, double angle)
    {
        int i = ((int)Math.Floor(angle) % BinCount + BinCount) % BinCount;
        return bins[i];
    }
}
