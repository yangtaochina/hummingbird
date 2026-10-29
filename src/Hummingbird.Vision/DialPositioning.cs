using System.Runtime.InteropServices;
using Hummingbird.Core;
using OpenCvSharp;

namespace Hummingbird.Vision;

/// <summary>
/// 表盘定位（OpenCV）：
/// 1) Otsu 二值化 + 最大外轮廓 → 椭圆拟合定心
/// 2) 轮廓半径-角度剖面 → 外缘凹陷带 = 零位缺口
/// 3) 剔除缺口扇区后二次拟合（消除缺口对定心的拉偏），凹陷最深点定角向
/// </summary>
public sealed class DialPositioning : IVisionService
{
    private const int BinCount = 360; // 1°一个角度桶

    public async Task<PartPose> LocateAsync(CameraFrame frame, VisionOptions opt, CancellationToken ct = default)
    {
        await Task.Yield();
        try
        {
            using var src = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
            Marshal.Copy(frame.Gray, 0, src.Data, frame.Gray.Length);
            using var blur = new Mat();
            Cv2.GaussianBlur(src, blur, new Size(5, 5), 0);

            using var bw = new Mat();
            Cv2.Threshold(blur, bw, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);

            Cv2.FindContours(bw, out var contours, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);
            var contour = contours.OrderByDescending(c => Cv2.ContourArea(c)).FirstOrDefault();
            if (contour is not { Length: > 60 })
                return Fail("画面中未找到工件轮廓（检查光源/曝光/视野）");

            // ---- 第一遍：全体轮廓粗定心 + 找缺口扇区 ----
            var fit1 = FitCircle(contour);
            var (bins1, mean1, std1) = BuildProfile(contour, fit1.Cx, fit1.Cy);
            var run1 = FindDipRun(bins1, mean1, std1);
            if (run1 is null)
                return Fail("未找到可靠的零位缺口（外缘无凹陷带），请确认缺口可见");

            // ---- 第二遍：剔除缺口 ±25° 扇区后精定心（仅用于椭圆拟合）----
            double notchApprox = (run1.Value.Start + run1.Value.Len / 2.0) % 360;
            var kept = contour.Where(p =>
            {
                double clock = ClockAngle(p.X - fit1.Cx, p.Y - fit1.Cy);
                return AngleDist(clock, notchApprox) > 25;
            }).ToArray();
            var fit = kept.Length > contour.Length / 2 ? FitCircle(kept) : fit1;

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
            double notchClock = wSum > 0
                ? ((Math.Atan2(sSin, sCos) * 180 / Math.PI) % 360 + 360) % 360
                : notchFrom % 360;

            // ---- 尺寸校验 ----
            double expectedR = opt.ExpectedOuterDiameterMm / 2 / Math.Max(opt.PixelSizeMm, 1e-6);
            double sizeErr = expectedR > 0 ? Math.Abs(fit.R - expectedR) / expectedR : 0;
            if (sizeErr > 0.3)
                return Fail($"外缘半径偏差过大：测到{fit.R:F0}px vs 预期{expectedR:F0}px（{sizeErr:P0}）");

            // ---- 置信度 ----
            double dipDepth = std / mean;
            double conf = Math.Max(0, Math.Min(1,
                0.5 * fit.AxesRatio + 0.3 * (1 - Math.Min(sizeErr / 0.3, 1)) + 0.2 * Math.Min(dipDepth / 0.05, 1)));

            double angleOffset = ((notchClock - opt.NominalDatumAngleDeg) % 360 + 540) % 360 - 180;
            return new PartPose(true, fit.Cx, fit.Cy, fit.R, notchClock, angleOffset, conf,
                $"中心=({fit.Cx:F1},{fit.Cy:F1})px 外缘R={fit.R:F0}px 缺口@{notchClock:F1}° 转角={angleOffset:F2}°");
        }
        catch (Exception ex)
        {
            return Fail("定位异常：" + ex.Message);
        }
    }

    private static PartPose Fail(string why) => new(false, 0, 0, 0, 0, 0, 0, why);

    /// <summary>图像坐标 → 时钟角（12H=0，顺时针）。图像 y 向下。</summary>
    private static double ClockAngle(double dx, double dy)
        => ((90 - Math.Atan2(-dy, dx) * 180 / Math.PI) % 360 + 360) % 360;

    private static double AngleDist(double a, double b)
    {
        double d = Math.Abs(a - b) % 360;
        return d > 180 ? 360 - d : d;
    }

    private sealed record CircleFit(double Cx, double Cy, double R, double AxesRatio);

    private static CircleFit FitCircle(Point[] pts)
    {
        var ell = Cv2.FitEllipse(pts);
        double r = (ell.Size.Width + ell.Size.Height) / 4;
        double ratio = Math.Min(ell.Size.Width, ell.Size.Height) / Math.Max(ell.Size.Width, ell.Size.Height);
        return new CircleFit(ell.Center.X, ell.Center.Y, r, ratio);
    }

    /// <summary>径向半径剖面：每个角度桶的平均半径，再做环形平滑（3桶滑窗）。</summary>
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

    /// <summary>找最长连续凹陷带（半径 &lt; mean-0.6σ，环形）。</summary>
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

    private static double BinAt(double[] bins, double angle)
    {
        int i = ((int)Math.Floor(angle) % BinCount + BinCount) % BinCount;
        return bins[i];
    }
}
