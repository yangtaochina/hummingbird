using System.Runtime.InteropServices;
using Hummingbird.Core;
using OpenCvSharp;

namespace Hummingbird.Vision;

/// <summary>
/// 现场诊断工具：反算实际像素尺寸（mm/px）与视野，判断"角向基准孔是否在画面内"。
///
/// ============================================================
/// 2026-11-05 真图实测后的重大修正（重要，别回退）
/// ============================================================
/// 初版用"中央孔（Ø2.5/Ø2.9mm）"当标尺，实测证明这是错的：
///   真图 Image__2026-11-05__14-04-13.bmp（曝光 3114μs）中心 500×500 区域
///   **完全看不到中央孔**，只有一个漫射光斑（中央孔应该是一个 ~100px 的圆）。
///   → 中央孔在当前红光照明下不可见，不能当标尺，也不能用它判断"在不在视野内"。
///
/// 真图实测结论（离线定量分析，RANSAC + 多核径向峰值交叉验证）：
///   · 画面中央的大椭圆是**工件盘面**，不是孔
///   · 四周的白色亮弧是**工件白色外环**（图纸 Ø3420 DIAL）
///   · 外环半径实测 **R ≈ 700px**（blur 3/7/11/15 多核中位数 699~704，标准差 ~47px）
///   · 由此 mm/px = 17.1mm / 700px = **0.0244 mm/px**
///   · 视野 = 1920×0.0244 = **46.9mm** 宽 × 1200×0.0244 = **29.3mm** 高
///   · 基准孔 R14.45mm = 592px，画面垂直半高 600px → **勉强在视野内（余量仅 8px）**
///     ⇒ 垂直方向极度边缘，任何装夹偏移都会让基准孔出画
///   · 18 个待加工孔 R11.1~13.6mm = 454~557px → 同样在视野内
///   · 中央孔 Ø2.5/Ø2.9mm 应为 102/119px，但画面中看不到
///
/// 因此本工具改为：
///   ① **主标尺 = 白色外环**（Ø34.2mm，图纸已知，视觉上最可靠）
///      用径向峰值法逐方位找外环半径 → 中位数 → 反算 mm/px
///   ② **副标尺 = 中央孔**（保留但降级）：只有真的量到圆环才用，并明确标注是次要依据
///   ③ 输出"基准孔在视野内的余量（px 与 mm）"，而不只是布尔值
///      —— 余量 8px 这种边缘情况必须让人一眼看到，不能只说"在视野内"
///
/// ============================================================
/// 另一个必须修正的判据：垂直方向才是约束
/// ============================================================
/// 之前用"半宽"判断视野够不够，错了——图幅是 1920×1200（横图），
/// 垂直半高 600px 才是限制（1200/2 < 1920/2）。基准孔在 R592px 时，
/// 水平方向余量 368px 很充裕，垂直方向余量只有 8px。
/// </summary>
public sealed class FieldDiagnosis
{
    private readonly IAppLogger? _log;

    public FieldDiagnosis(IAppLogger? log = null) { _log = log; }

    /// <summary>白色外环标称外径（mm）：图纸 Ø3420 DIAL = Ø34.2mm。这是现役主标尺。</summary>
    public const double OuterRingDiameterMm = 34.2;

    /// <summary>中央孔候选标称直径（mm）：沉孔边 Ø2.9 与通孔 Ø2.5。仅作次要参考。</summary>
    private static readonly double[] CenterHoleNominalMm = { 2.90, 2.50 };

    /// <summary>
    /// 诊断结果。
    /// </summary>
    /// <param name="Success">是否成功量到标尺特征。</param>
    /// <param name="ScalePx">标尺特征的像素尺寸（外环直径，或中央孔直径）。</param>
    /// <param name="MmPerPx">反算的像素尺寸（mm/px）。</param>
    /// <param name="FovWidthMm">视野宽（mm）。</param>
    /// <param name="FovHeightMm">视野高（mm）。</param>
    /// <param name="DatumRadiusMm">角向基准孔所在半径（mm）。</param>
    /// <param name="DatumInView">基准孔是否落在视野内。</param>
    /// <param name="MarginMm">基准孔到画面上下边界的余量（mm）。负数=出画。</param>
    /// <param name="ScaleSource">标尺来源说明（外环 / 中央孔）。</param>
    /// <param name="Detail">人可读结论。</param>
    public sealed record Result(
        bool Success,
        double ScalePx,
        double MmPerPx,
        double FovWidthMm,
        double FovHeightMm,
        double DatumRadiusMm,
        bool DatumInView,
        double MarginMm,
        string ScaleSource,
        string Detail);

    /// <summary>
    /// 分析一帧图：优先用白色外环反算 mm/px → 判断基准孔是否在视野内。
    /// </summary>
    /// <param name="frame">灰度帧</param>
    /// <param name="datumRadiusMm">角向基准孔半径（mm），图纸标称 14.45</param>
    /// <param name="outerRingDiameterMm">白色外环标称外径（mm），图纸 Ø34.2</param>
    public Result Analyze(CameraFrame frame, double datumRadiusMm = 14.45, double outerRingDiameterMm = OuterRingDiameterMm)
    {
        using var src = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
        Marshal.Copy(frame.Gray, 0, src.Data, frame.Gray.Length);

        Cv2.MinMaxIdx(src, out _, out double maxGray);
        double meanGray = Cv2.Mean(src).Val0;

        // ---------- 采样前先判断画面是否可用 ----------
        if (maxGray < 20)
        {
            string whyDark = $"画面全黑（最亮灰度仅 {maxGray:F0}/255）。" +
                         "→ 先解决照明/曝光：点「②自动曝光扫描」找到够亮的曝光档，" +
                         "若所有曝光档都暗，说明是照明光强不足，需要加装高功率照明（不是调参数能解决的）";
            _log?.Warn("[诊断] " + whyDark);
            return new Result(false, 0, 0, 0, 0, datumRadiusMm, false, 0, "无", whyDark);
        }

        // 整体过曝检测（必须在量外环之前做）：
        // 全白画面没有结构，任何"边缘"都会被高斯模糊的边界效应伪造成圆弧 → 会算出假的外环半径。
        using (var check = new Mat())
        {
            Cv2.GaussianBlur(src, check, new Size(9, 9), 0);
            Cv2.MeanStdDev(check, out Scalar m0s, out Scalar s0s);
            double m0 = m0s.Val0, s0 = s0s.Val0;
            if (m0 > 245 && s0 < 8)
            {
                string whyOver = $"画面**整体过曝**（均值 {m0:F0}/255、标准差 {s0:F1}）—— 全白一片、没有结构，" +
                             "量不到任何尺寸特征。\n" +
                             "→ 把曝光调低（当前扫描显示 50000μs 起就全白了，建议 3000~8000μs 区间试）" +
                             "或把增益降到 0 附近。";
                _log?.Warn("[诊断] " + whyOver);
                return new Result(false, 0, 0, 0, 0, datumRadiusMm, false, 0, "无", whyOver);
            }
        }

        // ---------- 主标尺：白色外环半径（径向峰值法） ----------
        var ring = MeasureOuterRingRadius(src, frame.Width, frame.Height);
        if (ring.RadiusPx > 0)
        {
            double mmPerPx = (outerRingDiameterMm / 2.0) / ring.RadiusPx;
            double fovW = mmPerPx * frame.Width;
            double fovH = mmPerPx * frame.Height;
            // 判据用**垂直方向**（图幅 1920×1200，垂直半高 600px 才是限制）
            double halfH = frame.Height / 2.0;
            double datumPx = datumRadiusMm / mmPerPx;
            bool inView = datumPx <= halfH;
            double marginMm = (halfH - datumPx) * mmPerPx;

            string detail =
                $"【主标尺】白色外环 Ø{outerRingDiameterMm:F1}mm（图纸 DIAL）\n" +
                $"  外环半径实测 R = {ring.RadiusPx:F1}px（{ring.Samples} 个方位角采样，中位 {ring.MedianRadiusPx:F1}px，标准差 {ring.StdRadiusPx:F1}px）\n" +
                $"  → mm/px = {mmPerPx:F6}\n" +
                $"  视野 = {fovW:F1} × {fovH:F1} mm（图幅 {frame.Width}×{frame.Height}，垂直半高 {halfH:F0}px = {halfH * mmPerPx:F2}mm）\n" +
                $"【判据】角向基准孔 R{datumRadiusMm:F2}mm = {datumPx:F0}px\n" +
                (inView
                    ? (marginMm < 1.5
                        ? $"  ⚠ 在视野内，但余量仅 {marginMm:F2}mm（{halfH - datumPx:F0}px）—— **垂直方向处于边缘**！\n" +
                          $"     → 装夹时工件一旦上下偏移，基准孔立刻出画。\n" +
                          $"     → 建议：①相机改为竖装（1200×1920）或加宽图幅 ②改用 Binning=1 并确认 ROI 覆盖 ③" +
                          $"把工件在 Y 方向做机械限位，确保每件都精确落在同一位置"
                        : $"  ✅ 在视野内，余量 {marginMm:F2}mm（{halfH - datumPx:F0}px），datumHoles 方案可行")
                    : $"  ❌ **不在视野内**（垂直半高仅 {halfH * mmPerPx:F2}mm < {datumRadiusMm:F2}mm，超出 {Math.Abs(marginMm):F2}mm）\n" +
                      $"     → datumHoles 角向基准方案失效！退路：①加大视野（换短焦/加中继镜/相机竖装让 1200px 当长边）" +
                      $"②或把相机移到能同时看到中心孔与基准孔的位置 ③或改用纯人工对刀（粗对齐角需准到 ±0.5°）");

            detail += "\n" + MeasureCenterHoleNote(src, frame.Width, frame.Height, mmPerPx);
            _log?.Info("[诊断] " + detail.Replace("\n", " | "));
            return new Result(true, ring.RadiusPx * 2, mmPerPx, fovW, fovH, datumRadiusMm, inView, marginMm,
                              $"白色外环 Ø{outerRingDiameterMm:F1}mm", detail);
        }

        // ---------- 副标尺：中央孔（外环量不到时的退路） ----------
        var hole = MeasureCenterHole(src, frame.Width, frame.Height);
        if (hole.DiameterPx > 0)
        {
            var pick = CenterHoleNominalMm.OrderBy(n => n).First();   // 保守：取 Ø2.5
            double mmPerPx = pick / hole.DiameterPx;
            double fovW = mmPerPx * frame.Width;
            double fovH = mmPerPx * frame.Height;
            double halfH = frame.Height / 2.0;
            double datumPx = datumRadiusMm / mmPerPx;
            bool inView = datumPx <= halfH;
            double marginMm = (halfH - datumPx) * mmPerPx;

            string detail =
                $"【副标尺】未找到白色外环，退回用中央孔 Ø{pick:F2}mm 反算（**精度低于外环**）\n" +
                $"  中央孔像素直径 = {hole.DiameterPx:F1}px → mm/px = {mmPerPx:F6}\n" +
                $"  视野 = {fovW:F1} × {fovH:F1} mm\n" +
                $"  基准孔 R{datumRadiusMm:F2}mm = {datumPx:F0}px → " +
                (inView ? $"✅ 在视野内（余量 {marginMm:F2}mm）" : $"❌ 不在视野内（超 {Math.Abs(marginMm):F2}mm）");

            _log?.Info("[诊断] " + detail.Replace("\n", " | "));
            return new Result(true, hole.DiameterPx, mmPerPx, fovW, fovH, datumRadiusMm, inView, marginMm,
                              $"中央孔 Ø{pick:F2}mm（次要）", detail);
        }

        string why = $"既没找到白色外环，也没找到中央孔亮环（全图最亮 {maxGray:F0}，全图均值 {meanGray:F1}）。\n" +
                     "→ 画面里没有可用的尺寸标尺：检查①工件是否在视野内 ②红光是否已开 ③曝光是否过曝（全白也量不到结构）";
        _log?.Warn("[诊断] " + why);
        return new Result(false, 0, 0, 0, 0, datumRadiusMm, false, 0, "无", why);
    }

    // ==================================================================
    // 白色外环半径测量（径向峰值法）
    // ==================================================================

    /// <summary>外环测量结果。</summary>
    private sealed record RingResult(double RadiusPx, double MedianRadiusPx, double StdRadiusPx, int Samples);

    /// <summary>
    /// 径向峰值法测外环半径：
    ///   从画面中心出发，沿每个方位角向外扫描，找**局部亮度峰值**（外环是白色反光环），
    ///   收集各方位角测得的半径，取中位数（抗单方向噪声）。
    ///
    /// 为什么不用霍夫圆 / 轮廓拟合：
    ///   实测真图里外环**只露出几段弧**（其余部分在画面外或被暗背景吞掉），
    ///   霍夫圆会在弧段上拟合出偏心的大圆（实测 center=(765,483) R=923px，明显偏），
    ///   而径向峰值法只用"该方位角上最亮的环"这一局部信息，对弧段缺失天然鲁棒。
    /// </summary>
    private RingResult MeasureOuterRingRadius(Mat src, int w, int h)
    {
        // 多核模糊取中位数，避免单次噪声影响
        var radii = new List<double>();
        int rMin = (int)(Math.Min(w, h) * 0.20);
        // 上限放到图幅对角线的一半：外环可能大到只露出几段弧（实测真图 R=703px，
        // 而短边 1200px 的一半只有 600px —— 用短边做上限会把真正的外环切掉）
        int rMax = (int)(Math.Sqrt((double)w * w + (double)h * h) / 2.0);
        int win = 25;   // 峰搜索半窗

        foreach (var k in new[] { 3, 7, 11 })
        {
            using var blur = new Mat();
            Cv2.GaussianBlur(src, blur, new Size(k, k), 0);

            int cx = w / 2, cy = h / 2;

            // 逐方位角取径向剖面（**每个方位角独立找峰**，不能合并成一个剖面，
            // 否则只会在全图最亮的那一个方位角上找到峰）
            for (int deg = 0; deg < 360; deg += 2)
            {
                double a = deg * Math.PI / 180.0;
                double ca = Math.Cos(a), sa = Math.Sin(a);

                var prof = new float[rMax - rMin + 1];
                for (int r = rMin; r <= rMax; r++)
                {
                    int x = (int)(cx + r * ca), y = (int)(cy + r * sa);
                    if (x < 0 || x >= w || y < 0 || y >= h) continue;
                    // ⚠ 必须用 At<byte>()：blur 是 CV_8UC1 矩阵，
                    //   用 At<float>() 会把字节重新解释成 float，读到的是垃圾值
                    //   （实测 r=541 处读出 -570423317435539088851599360 这种数）。
                    prof[r - rMin] = blur.At<byte>(y, x);
                }

                // 找局部峰：比窗口内其他点都亮，且比窗口均值高 15
                for (int i = win; i < prof.Length - win; i++)
                {
                    if (prof[i] <= 40) continue;
                    float mx = 0, sum = 0;
                    for (int j = i - win; j <= i + win; j++)
                    {
                        if (prof[j] > mx) mx = prof[j];
                        sum += prof[j];
                    }
                    if (prof[i] < mx) continue;                       // 不是窗口内最亮
                    if (prof[i] <= sum / (2 * win + 1) + 15) continue; // 突出度不够

                    // **取亮带的中心**，而不是第一个亮点。
                    // 白色外环在图上是几十像素宽的亮带（平台），若取"第一个达到峰值的像素"，
                    // 会系统性偏到亮带的内边缘（实测合成 R=560 被测成 539，偏 21px）。
                    // 这里沿亮带向两侧扩展到亮度降到峰值一半以下，取扩展段的中心。
                    int lo = i, hi = i;
                    float half = prof[i] * 0.5f;
                    while (lo > 0 && prof[lo - 1] >= half) lo--;
                    while (hi < prof.Length - 1 && prof[hi + 1] >= half) hi++;

                    radii.Add(rMin + (lo + hi) / 2);
                    break;   // 每个方位角只取最内层的那个峰（外环内侧还有盘面反光）
                }
            }
        }

        if (radii.Count < 12) return new RingResult(0, 0, 0, radii.Count);

        radii.Sort();
        double median = radii[radii.Count / 2];
        double mean = radii.Average();
        double std = Math.Sqrt(radii.Select(r => (r - mean) * (r - mean)).Average());

        // 剔除明显离群（径向毛刺），再做一次中位数
        var inliers = radii.Where(r => Math.Abs(r - median) < Math.Max(30, 3 * std)).ToList();
        if (inliers.Count >= 8)
        {
            inliers.Sort();
            median = inliers[inliers.Count / 2];
            mean = inliers.Average();
            std = Math.Sqrt(inliers.Select(r => (r - mean) * (r - mean)).Average());
        }

        return new RingResult(median, median, std, inliers.Count);
    }

    // ==================================================================
    // 中央孔测量（次要标尺 / 补充说明）
    // ==================================================================

    /// <summary>中央孔测量结果。</summary>
    private sealed record HoleResult(double DiameterPx, double Cx, double Cy);

    private HoleResult MeasureCenterHole(Mat src, int w, int h)
    {
        using var blur = new Mat();
        Cv2.GaussianBlur(src, blur, new Size(5, 5), 0);

        int roi = Math.Max(20, Math.Min(w, h) / 2);
        int cx = w / 2, cy = h / 2;
        int rx = Math.Max(0, cx - roi), ry = Math.Max(0, cy - roi);
        int rw = Math.Min(roi * 2, w - rx), rh = Math.Min(roi * 2, h - ry);
        if (rw <= 10 || rh <= 10) return new HoleResult(0, 0, 0);

        using var roiMat = new Mat(blur, new Rect(rx, ry, rw, rh));
        double roiMean = Cv2.Mean(roiMat).Val0;
        using var bright = new Mat();
        Cv2.Threshold(roiMat, bright, roiMean + 40, 255, ThresholdTypes.Binary);
        Cv2.FindContours(bright, out var cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);

        double maxDia = Math.Min(w, h) * 0.6;
        var cands = cs
            .Where(c => c.Length >= 8 && Cv2.ContourArea(c) > 20)
            .Select(c =>
            {
                var e = Cv2.FitEllipse(c);
                double d = (e.Size.Width + e.Size.Height) / 2;
                double ratio = Math.Min(e.Size.Width, e.Size.Height) / Math.Max(e.Size.Width, e.Size.Height);
                double x = e.Center.X + rx, y = e.Center.Y + ry;
                double dist = Math.Sqrt((x - w / 2.0) * (x - w / 2.0) + (y - h / 2.0) * (y - h / 2.0));
                return (D: d, Ratio: ratio, Dist: dist);
            })
            .Where(t => t.Ratio > 0.65 && t.D >= 10 && t.D <= maxDia)
            .OrderBy(t => Math.Round(t.Dist / 10))
            .ThenBy(t => t.D)
            .ToList();

        var best = cands.FirstOrDefault();
        return best.D <= 0 ? new HoleResult(0, 0, 0) : new HoleResult(best.D, 0, 0);
    }

    /// <summary>
    /// 补充说明：中央孔是否可见，以及"过曝"检测。
    /// 2026-11-05 真图实测：中央孔在红光照明下**不可见**（画面中心是一个 ~220px 的漫射亮斑，
    /// 是灯光反射，不是孔）。这一点必须明确告诉现场，
    /// 否则他们会一直以为"程序没找到孔是程序的问题"。
    /// </summary>
    private string MeasureCenterHoleNote(Mat src, int w, int h, double mmPerPx)
    {
        var hole = MeasureCenterHole(src, w, h);
        double expectPx = 2.5 / mmPerPx;   // 中央孔 Ø2.5mm 应有的像素直径

        // 漫射亮斑检测：面积巨大的亮斑 = 灯光反射（不是孔）
        string reflection = DetectDiffuseReflection(src, w, h);
        bool hasReflection = reflection.Contains("漫射亮斑");

        // ⚠ 关键：若中心存在漫射反射辉光，MeasureCenterHole 量到的"圆"其实是那团光，
        //   不能当中央孔（2026-11-05 真图实测：204px 的"孔"其实是 ~220px 的反射光斑）。
        if (hasReflection)
            return $"【副标尺】中央孔：**被灯光反射辉光淹没，无法可靠识别**" +
                   $"（Ø2.5mm 应表现为约 {expectPx:F0}px 的圆，中心区只找到 {hole.DiameterPx:F0}px 的漫射光斑）。" +
                   $"\n           {reflection}" +
                   "\n           → 中央孔在当前红光照明下不反光，不能当定位/标尺特征；" +
                   "角向基准只能靠工件自带的 8 个双环孔。";

        // 尺寸自检：量到的直径与 Ø2.5mm 预期差太多 → 大概率是误检（反光/暗斑）
        if (hole.DiameterPx > 0 && expectPx > 0)
        {
            double ratio = hole.DiameterPx / expectPx;
            if (ratio > 1.6 || ratio < 0.5)
                return $"【副标尺】中央孔：**疑似误检，尺寸对不上**" +
                       $"（量到 {hole.DiameterPx:F0}px，Ø2.5mm 应为 {expectPx:F0}px，偏差 {ratio:F2}×）。" +
                       "\n           → 中心区最亮的圆不是中央孔（多半是灯光反射或压圈边缘），" +
                       "中央孔在当前红光照明下不可用；角向基准只能靠工件自带的 8 个双环孔。";
        }

        if (hole.DiameterPx <= 0)
            return $"【副标尺】中央孔：**画面中不可见**（Ø2.5mm 应表现为约 {expectPx:F0}px 的圆，实测中心区找不到）。" +
                   "\n           → 中央孔在当前红光照明下不反光，不能当定位/标尺特征；" +
                   "角向基准只能靠工件自带的 8 个双环孔。";

        return $"【副标尺】中央孔可见，像素直径 {hole.DiameterPx:F1}px" +
               $"（若按 Ø2.5mm 标定 → {2.5 / hole.DiameterPx:F6} mm/px，与外环标尺 {mmPerPx:F6} 对比可自检）。" +
               (reflection != "" ? $"\n           {reflection}" : "");
    }

    /// <summary>
    /// 检测画面中心是否存在"大面积漫射亮斑"（灯光反射），
    /// 以及画面是否整体过曝（所有像素都接近 255）。
    /// </summary>
    private string DetectDiffuseReflection(Mat src, int w, int h)
    {
        using var blur = new Mat();
        Cv2.GaussianBlur(src, blur, new Size(9, 9), 0);

        // 过曝检测：均值很高且标准差很小 = 全白一片，没有结构
        double mean = Cv2.Mean(blur).Val0;
        Cv2.MeanStdDev(blur, out Scalar ms, out Scalar ss);
        double std = ss.Val0;
        if (mean > 245 && std < 8)
            return "⚠ 画面疑似**整体过曝**（均值 " + mean.ToString("F0") + "、标准差 " + std.ToString("F1") +
                   "）→ 曝光需调低，否则任何结构都分辨不出来";

        // 漫射亮斑：中心区里面积占比很大的亮连通域
        int roi = Math.Max(20, Math.Min(w, h) / 3);
        int cx = w / 2, cy = h / 2;
        int rx = Math.Max(0, cx - roi), ry = Math.Max(0, cy - roi);
        int rw = Math.Min(roi * 2, w - rx), rh = Math.Min(roi * 2, h - ry);
        if (rw <= 10 || rh <= 10) return "";

        using var roiMat = new Mat(blur, new Rect(rx, ry, rw, rh));
        double roiMean = Cv2.Mean(roiMat).Val0;
        using var bright = new Mat();
        Cv2.Threshold(roiMat, bright, roiMean + 40, 255, ThresholdTypes.Binary);
        Cv2.FindContours(bright, out var cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);

        foreach (var c in cs.OrderByDescending(c => Cv2.ContourArea(c)))
        {
            double area = Cv2.ContourArea(c);
            if (area < rw * rh * 0.10) break;      // 亮斑占中心区 10% 以上才算"漫射反射"
            var e = Cv2.FitEllipse(c);
            double d = (e.Size.Width + e.Size.Height) / 2;
            double x = e.Center.X + rx, y = e.Center.Y + ry;
            double dist = Math.Sqrt((x - w / 2.0) * (x - w / 2.0) + (y - h / 2.0) * (y - h / 2.0));
            return $"画面中心有一个 **直径约 {d:F0}px 的漫射亮斑**（距中心 {dist:F0}px），" +
                   "这是灯光反射形成的辉光，**不是中央孔**——孔被这团光淹没了。";
        }
        return "";
    }
}
