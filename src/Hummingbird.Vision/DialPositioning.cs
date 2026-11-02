using System.Runtime.InteropServices;
using Hummingbird.Core;
using OpenCvSharp;

namespace Hummingbird.Vision;

/// <summary>
/// 表盘定位（OpenCV），实现 IVisionService。三遍式：
///
///   第一遍：全体外轮廓 → 椭圆拟合 粗定心；建"半径-角度"剖面找出**全部**凹陷扇区
///           （现场核实 2026-10-30：外缘有 8 个渠口、不均匀分布——不止一个）
///   第二遍：剔除全部渠口扇区后重新拟合 → 精定心（任何一个渠口都会把拟合中心拉偏，必须全剔）
///   第三遍：角向基准，按 DatumMode 二选一：
///     - offsetCircle（现场确认的正解）：检测盘面既有偏心小圆，中心→小圆方向 = 12H。
///       实物上没有"12H"印刷标记——12H 是客户自定义方向，靠这个偏心小圆定义。
///     - notch（旧方案兼容）：全体点剖面重建，最长凹陷带深度加权质心定角向。
///
/// 为什么这么绕（一个坑的教训）：
///   - 直接拿全体轮廓拟合：缺口那几度的"缺口弦"把中心拉偏 3~4px（约 0.07mm）→ 超差
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
            bool useOffsetCircle = !string.Equals(opt.DatumMode, "notch", StringComparison.OrdinalIgnoreCase);

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

            // ---- 第一遍：全体轮廓粗定心 + 找全部渠口扇区 ----
            var fit1 = FitCircle(contour);
            var (bins1, mean1, std1) = BuildProfile(contour, fit1.Cx, fit1.Cy);
            var dips = FindDipRuns(bins1, mean1, std1);
            _log?.Info($"[视觉] 一遍：粗中心=({fit1.Cx:F1},{fit1.Cy:F1}) 找到 {dips.Count} 个渠口扇区" +
                        string.Concat(dips.Select(d => $" [{d.Start}°+{d.Len}°]")));
            if (dips.Count == 0 && !useOffsetCircle)
                return Fail("未找到任何外缘缺口（渠口不可见？），角向基准无法建立");

            // ---- 第二遍：剔除全部渠口扇区后精定心（仅用于椭圆拟合）----
            // 每段渠口外扩 2°+半宽（弦过渡区也带偏差）。8 段渠口下用旧的 ±25° 会把整圈剔光。
            var kept = contour.Where(p =>
            {
                double clock = ClockAngle(p.X - fit1.Cx, p.Y - fit1.Cy);
                foreach (var d in dips)
                {
                    double center = (d.Start + d.Len / 2.0) % 360;
                    if (AngleDist(clock, center) < d.Len / 2.0 + 2 + d.Len * 0.1)
                        return false;
                }
                return true;
            }).ToArray();
            // 8 渠口最多可剔掉 ~25% 轮廓点；剩余太少说明渠道异常宽，宁可退回粗定心并警告
            var fit = kept.Length > contour.Length / 3 ? FitCircle(kept) : fit1;
            if (kept.Length <= contour.Length / 3 && dips.Count > 0)
                _log?.Warn($"[视觉] 渠口扇区剔除后仅剩 {kept.Length}/{contour.Length} 点，精定心退回粗拟合（渠口是否异常宽？）");
            _log?.Info($"[视觉] 二遍：精中心=({fit.Cx:F1},{fit.Cy:F1})（剔除渠口后用 {kept.Length}/{contour.Length} 点）");

            // ---- 第三遍：角向基准 ----
            double datumClock, datumQuality;
            if (useOffsetCircle)
            {
                // 中心 → 既有偏心小圆 的方向 = 12H（现场 2026-10-30 确认）
                var datum = FindOffsetCircle(blur, fit, dips, opt);
                if (datum is null)
                    return Fail("未找到盘面基准小圆（12H 方向特征）。检查：小圆是否在视野内/对比度是否足够/" +
                                "模板 datumCircleDiameterMm 是否与实物一致");
                datumClock = ClockAngle(datum.X - fit.Cx, datum.Y - fit.Cy);
                datumQuality = datum.Quality;
                _log?.Info($"[视觉] 三遍：基准小圆@({datum.X:F1},{datum.Y:F1}) " +
                           $"直径≈{datum.DiameterPx:F0}px → 12H 方位={datumClock:F2}°（圆度{datumQuality:F2}）");
            }
            else
            {
                // 旧方案：全体点重建剖面，最长凹陷带的深度加权质心
                var (bins, mean, std) = BuildProfile(contour, fit.Cx, fit.Cy);
                var dips2 = FindDipRuns(bins, mean, std);
                if (dips2.Count == 0)
                    return Fail("未找到可靠的零位缺口（外缘无凹陷带），请确认缺口可见");
                var run = dips2.OrderByDescending(d => d.Len).First();
                double notchFrom = run.Start, notchTo = run.Start + run.Len;
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
                datumClock = wSum > 0
                    ? ((Math.Atan2(sSin, sCos) * 180 / Math.PI) % 360 + 360) % 360
                    : notchFrom % 360;
                double dipDepth = std / mean;
                datumQuality = Math.Min(dipDepth / 0.05, 1);
                _log?.Info($"[视觉] 三遍：缺口角向={datumClock:F2}°（加权质心，权重和{wSum:F0}）");
            }

            // ---- 尺寸校验：测得外缘半径 vs 预期（防拿错件/视野不对）----
            double expectedR = opt.ExpectedOuterDiameterMm / 2 / Math.Max(opt.PixelSizeMm, 1e-6);
            double sizeErr = expectedR > 0 ? Math.Abs(fit.R - expectedR) / expectedR : 0;
            if (sizeErr > 0.3)
                return Fail($"外缘半径偏差过大：测到{fit.R:F0}px vs 预期{expectedR:F0}px（{sizeErr:P0}）。" +
                            $"若上件确为本料号表盘，说明像素尺寸配置不符：实际≈{opt.ExpectedOuterDiameterMm / 2 / Math.Max(fit.R, 1):F4} mm/px，" +
                            $"请把 config.json 的 VisionPixelSizeMm 改为该值后重试");

            // ---- 置信度：椭圆圆度 + 尺寸吻合度 + 基准特征质量 三项加权 ----
            double conf = Math.Max(0, Math.Min(1,
                0.5 * fit.AxesRatio + 0.3 * (1 - Math.Min(sizeErr / 0.3, 1)) + 0.2 * datumQuality));

            // 装卡转角 = 实测基准方位 - 名义零位角，归一化到 ±180°
            double angleOffset = ((datumClock - opt.NominalDatumAngleDeg) % 360 + 540) % 360 - 180;
            string datumName = useOffsetCircle ? "基准小圆" : "缺口";
            return new PartPose(true, fit.Cx, fit.Cy, fit.R, datumClock, angleOffset, conf,
                $"中心=({fit.Cx:F1},{fit.Cy:F1})px 外缘R={fit.R:F0}px {datumName}@{datumClock:F1}° 转角={angleOffset:F2}°");
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

    // ==================== 偏心基准小圆检测（12H 方向特征） ====================

    /// <summary>基准小圆检测结果：亚像素中心（椭圆拟合）+ 实测直径 + 质量分（0~1）。</summary>
    private sealed record DatumCircle(double X, double Y, double DiameterPx, double Quality);

    /// <summary>
    /// 在盘面内部找"既有偏心小圆"（12H 基准特征）。现场口述：白色外环（大圈圈）上有小圆圈，
    /// 中心→小圆方向 = 12H。要点：
    ///   - 以盘面（0.9R 圆盘）的平均灰度为基准电平，亮斑 = 均值+25、暗斑 = 均值-25 的全局阈值。
    ///     不用自适应阈值：小圆大小未知，窗口与特征同量级时阈值会被特征自身抬高导致碎裂；
    ///     视野仅 Ø34mm，光照均匀性由环形光源保证，全局阈值更可预期、现场好调。
    ///   - 两种极性都找（小圆是亮点还是暗点现场未确认），取质量分最高的
    ///   - 搜索区 0.12R ~ 0.96R：小圆在白色外环上（≈0.9R），只排除中心孔（&lt;0.12R）
    ///     和最外缘渠口区（&gt;0.96R）；直径过滤再挡一层渠口/中心孔这类"尺寸不对"的假候选
    ///   - 渠口扇区排除：渠口切入外缘可达 0.9R（白色外环也在其范围内），其暗块是天然假候选，
    ///     贴近基准小圆时还会粘连成"长条"使圆度判定失效 → 候选中心落在渠口扇区内一律拒绝
    ///   - 已知预期直径时按直径过滤（±45%），未知时按"面积够大 + 圆度够圆"的分数挑
    /// </summary>
    private DatumCircle? FindOffsetCircle(Mat gray, CircleFit face,
        List<(int Start, int Len)> dips, VisionOptions opt)
    {
        double r = face.R;
        double expectedPx = opt.DatumCircleDiameterMm > 0
            ? opt.DatumCircleDiameterMm / Math.Max(opt.PixelSizeMm, 1e-6) : 0;

        // 基准电平掩膜（0.9R 圆盘）+ 搜索掩膜（0.98R 圆盘，罩住白色外环上的基准小圆）
        using var levelMask = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(levelMask, new Point((int)face.Cx, (int)face.Cy), Math.Max(4, (int)(r * 0.9)), Scalar.All(255), -1);
        using var searchMask = new Mat(gray.Size(), MatType.CV_8UC1, Scalar.All(0));
        Cv2.Circle(searchMask, new Point((int)face.Cx, (int)face.Cy), Math.Max(4, (int)(r * 0.98)), Scalar.All(255), -1);
        double level = Cv2.Mean(gray, levelMask).Val0;

        DatumCircle? best = null;
        var rejections = new List<string>();   // 候选诊断：只记过了面积关的候选，远程排障用
        // 亮斑 / 暗斑 两种极性
        var polarities = new (double Offset, ThresholdTypes Mode)[]
        { (+25.0, ThresholdTypes.Binary), (-25.0, ThresholdTypes.BinaryInv) };
        foreach (var (offset, mode) in polarities)
        {
            using var mask = new Mat();
            Cv2.Threshold(gray, mask, level + offset, 255, mode);
            Cv2.BitwiseAnd(mask, searchMask, mask);   // 背景必须剔除（暗极性下背景会整体过阈值）
            Cv2.FindContours(mask, out var cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);

            foreach (var ct in cs)
            {
                double area = Cv2.ContourArea(ct);
                if (area < 30 || area > Math.PI * r * r * 0.05) continue;      // 太小=噪声，太大=不是特征（整条白环在此被挡掉）
                if (ct.Length < 5) { rejections.Add($"area={area:F0} 点数<5 椭圆拟不了"); continue; }
                var ell = Cv2.FitEllipse(ct);
                double dPx = (ell.Size.Width + ell.Size.Height) / 2;            // 实测直径
                double ratio = Math.Min(ell.Size.Width, ell.Size.Height)
                             / Math.Max(ell.Size.Width, ell.Size.Height);
                double dist = Math.Sqrt(Math.Pow(ell.Center.X - face.Cx, 2) + Math.Pow(ell.Center.Y - face.Cy, 2));
                string tag = $"{(offset > 0 ? "亮" : "暗")}斑 area={area:F0} dPx={dPx:F0} 圆度={ratio:F2} " +
                             $"距心={dist:F0}px({dist / r:F2}R) 时钟角={ClockAngle(ell.Center.X - face.Cx, ell.Center.Y - face.Cy):F0}°";

                if (ratio < 0.55) { rejections.Add(tag + " → 拒:不圆(可能渠口粘连)"); continue; }
                if (dist < r * 0.12 || dist > r * 0.96) { rejections.Add(tag + " → 拒:超出0.12R~0.96R搜索环带"); continue; }
                if (expectedPx > 0 && Math.Abs(dPx - expectedPx) / expectedPx > 0.45)
                { rejections.Add(tag + $" → 拒:直径与预期{expectedPx:F0}px差>45%"); continue; }

                // 渠口扇区排除：渠口切入外缘可达 0.9R，其暗块/亮环缺口是天然假候选，
                // 且贴近基准小圆时会把小圆粘连成长条（圆度判定失效）——候选中心落在
                // 任一渠口扇区（外扩候选自身角宽度+3°）内的一律拒绝
                double clock = ClockAngle(ell.Center.X - face.Cx, ell.Center.Y - face.Cy);
                double halfAng = dist > 1 ? (dPx / 2 / dist) * 180 / Math.PI : 90;
                bool inNotch = dips.Any(d =>
                    AngleDist(clock, (d.Start + d.Len / 2.0) % 360) < d.Len / 2.0 + halfAng + 3);
                if (inNotch) { rejections.Add(tag + " → 拒:落在渠口扇区内"); continue; }

                // 分数：圆度为主，特征大小做弱加权（预期已知时命中直径即满分）
                double score = ratio * (expectedPx > 0 ? 1.0 : Math.Min(1, dPx / 100));
                if (best is null || score > best.Quality)
                    best = new DatumCircle(ell.Center.X, ell.Center.Y, dPx, score);
            }
        }
        if (best is null)
        {
            _log?.Warn("[视觉] 基准小圆：两种极性均未找到合格候选（调整阈值/确认特征在 0.12R~0.96R 环带内）");
            foreach (var line in rejections.Take(12))
                _log?.Info("[视觉][基准诊断] " + line);
            _log?.Info($"[视觉][基准诊断] 盘面电平={level:F1} 亮阈={level + 25:F1} 暗阈={level - 25:F1} " +
                       $"预期直径={expectedPx:F0}px 共{rejections.Count}个候选被拒（把dumps图+这些行发给开发）");
        }
        return best;
    }

    // ==================== 通用几何/剖面工具 ====================

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
    /// 渠口在剖面上表现为一段"半径凹陷带"。
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
    /// 找**全部**连续凹陷带（半径 &lt; mean-0.6σ，环形搜索）。
    /// 现场核实：外缘有 8 个渠口且不均匀分布 → 必须把每个渠口都找出来逐个剔除。
    /// 单段长度限制 2°~90°：太短=噪声，太长=拿错特征（都不是渠口）。
    /// </summary>
    private static List<(int Start, int Len)> FindDipRuns(double[] bins, double mean, double std)
    {
        var runs = new List<(int, int)>();
        bool IsDip(int i) => bins[i] > 0 && bins[i] < mean - 0.6 * std;
        for (int start = 0; start < BinCount; start++)
        {
            if (!IsDip(start)) continue;
            int len = 0;
            while (len < BinCount && IsDip((start + len) % BinCount)) len++;
            if (len is >= 2 and <= 90)
                runs.Add((start, len));
            start += len;
        }
        return runs;
    }

    /// <summary>取指定角度（可为小数/越界）所在桶的半径值。</summary>
    private static double BinAt(double[] bins, double angle)
    {
        int i = ((int)Math.Floor(angle) % BinCount + BinCount) % BinCount;
        return bins[i];
    }
}
