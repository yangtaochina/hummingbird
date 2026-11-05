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
/// 真图实测结论（离线定量分析，Kasa 圆拟合 + 阈值敏感性 + 极坐标展开交叉验证）：
///   · 画面中央的大椭圆是**工件盘面**，不是孔
///   · 四周的白色亮弧是**工件白色外环**（图纸 Ø3420 DIAL）
///   · 亮弧圆心**不在画面中心**：Kasa 拟合 center=(935,591)，R≈720px
///     （阈值敏感性：th=180/200/215 → R=732/730/720px；th=235/245 → R=615/608px，
///       那是拟合到了错误的更小弧段，不可信）
///   · 由此 mm/px = 17.1mm / 720px = **0.0237 mm/px**（保守取 0.0234）
///   · 视野 = 1920×0.0234 = **44.9mm** 宽 × 1200×0.0234 = **28.1mm** 高
///   · 垂直半高 = 600px = **14.0mm** < 基准孔 R14.45mm
///     ⇒ 基准孔**其实出画**（边缘情况，按保守口径说"在视野内但余量 <1mm"）
///   · ⚠ **视野短边 28.1mm < 工件外径 Ø34.2mm** —— 工件根本放不进画面
///   · 18 个待加工孔 R11.1~13.6mm = 474~581px → 在视野内
///   · 中央孔 Ø2.5/Ø2.9mm 应为 107/124px，但画面中看不到
///
/// 🔴 最关键的发现：**8 个角向基准孔在红光下完全不可见**
///   把图按 (角度,半径) 极坐标展开后，基准孔环带（R600~660px）灰度标准差仅
///   **4.9/255**，看不到任何周期性结构；而外环亮弧处标准差 54.5。
///   ⇒ "基准孔在视野内"是**假象**。在视野内 ≠ 看得见。
///   ⇒ 当前红光 + 现有视野下，datumHoles 角向基准方案**实际不可用**。
///   ⇒ 程序新增 `DatumVisible` 字段把这两件事分开报，避免现场误判。
///
/// 因此本工具改为：
///   ① **主标尺 = 白色外环**（Ø34.2mm，图纸已知，视觉上最可靠）
///      用径向峰值法逐方位找外环半径 → 中位数 → 反算 mm/px
///   ② **副标尺 = 中央孔**（保留但降级）：只有真的量到圆环才用，并明确标注是次要依据
///   ③ 输出"基准孔在视野内的余量（px 与 mm）"，而不只是布尔值
///      —— 余量 8px 这种边缘情况必须让人一眼看到，不能只说"在视野内"
///   ④ 新增**基准孔可见性检测**：区分"在视野内"与"看得见"（见上）
///
/// ============================================================
/// 两个必须修正的判据
/// ============================================================
/// ① 垂直方向才是约束：图幅是 1920×1200（横图），垂直半高 600px 才是限制
///    （1200/2 < 1920/2）。
/// ② ⚠ **相机旋转 90° 竖装对余量零增益**（曾有此误判，勿再犯）：
///    旋转只改变画面方向，**物理短边仍是 28.1mm**，基准孔依然放不进画面。
///    正确解法是**加大视野**（短焦镜头/拉远物距，短边 ≥36mm）或做机械限位。
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
    /// <param name="DatumVisible">
    /// 角向基准孔在环带上**是否真的看得见**（不是"在不在视野内"）。
    /// 2026-11-05 真图实测：在视野内 ≠ 看得见 —— 红光下 8 个基准孔在极坐标展开图上
    /// 完全不可见（环带标准差仅 4.9/255），角向基准方案实际不可用。
    /// </param>
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
        string Detail,
        bool DatumVisible = true);

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
                          $"     → 建议：①**加大视野**（换短焦镜头或拉远物距，使垂直半高 ≥ 18mm）" +
                          $"②把工件在 Y 方向做机械限位，确保每件都精确落在同一位置 ③Binning 保持 1 并确认 ROI 覆盖全图\n" +
                          $"     → ⚠ 旋转相机 90° **无效**（只换画面方向，物理短边不变）"
                        : $"  ✅ 在视野内，余量 {marginMm:F2}mm（{halfH - datumPx:F0}px），datumHoles 方案可行")
                    : $"  ❌ **不在视野内**（垂直半高仅 {halfH * mmPerPx:F2}mm < {datumRadiusMm:F2}mm，超出 {Math.Abs(marginMm):F2}mm）\n" +
                      $"     → 视野短边 {fovH:F1}mm **小于工件外径 {outerRingDiameterMm:F1}mm**，工件根本放不进画面。\n" +
                      $"     → 必须加大视野（换短焦/加中继镜/拉远物距，短边 ≥ {datumRadiusMm * 1.25:F0}mm）" +
                      $"或把相机移到能同时看到中心孔与基准孔的位置，或改用纯机械对刀（粗对齐角需准到 ±0.5°）");

            detail += "\n" + MeasureCenterHoleNote(src, frame.Width, frame.Height, mmPerPx);

            // ---------- 基准孔"可见性"检测（关键：在视野内 ≠ 看得见） ----------
            var vis = MeasureDatumVisibility(src, frame.Width, frame.Height, datumPx, ring.RadiusPx);
            detail += "\n" + vis.Note;

            _log?.Info("[诊断] " + detail.Replace("\n", " | "));
            return new Result(true, ring.RadiusPx * 2, mmPerPx, fovW, fovH, datumRadiusMm, inView, marginMm,
                              $"白色外环 Ø{outerRingDiameterMm:F1}mm", detail, vis.Visible);
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
    // 基准孔可见性检测（极坐标展开 + 角度投票）
    // ==================================================================

    /// <summary>基准孔可见性检测结果。</summary>
    private sealed record VisibilityResult(bool Visible, int Peaks, double RingStd, string Note);

    /// <summary>
    /// 检测角向基准孔在环带上是否真的成像可见。
    ///
    /// 2026-11-05 真图实测（决定性）：
    ///   把相机图按极坐标展开（横轴=角度，纵轴=半径）后，基准孔所在环带（R600~680px）
    ///   **几乎全黑，标准差仅 4.9/255**，看不到任何周期性结构。
    ///   而外环亮弧所在半径（R700~780）标准差高达 54.5 —— 有强信号。
    ///   ⇒ "基准孔在视野内" 是假象：**在视野内 ≠ 看得见**。
    ///   红光下工件自带的双环孔不反光，角向基准方案不可用。
    ///
    /// 检测方法：极坐标展开 → 对基准孔环带做角度方向的高通 → 统计显著峰数量。
    ///   有孔时，8 个孔（30° 均布）会在展开图上形成 8 组周期性暗点。
    /// </summary>
    private VisibilityResult MeasureDatumVisibility(Mat src, int w, int h, double datumPx, double ringPx)
    {
        // 圆心估计：外环拟合的圆心未知，用画面中心近似会偏。
        // 这里直接从亮弧反推：用外环半径 + 图像重心二次修正代价高，
        // 改用"以画面中心为起点、在外环内侧 15% 带宽内找最亮弧"的方式，
        // 代价是圆心有几十像素误差 —— 但本检测判据是"环带有没有周期结构"，
        // 对几十像素的圆心误差不敏感（环带取 ±60px 宽）。
        var (cx, cy) = EstimateRingCenter(src, w, h, ringPx);

        // 基准孔环带：目标半径 ± 60px，且不能越过外环亮弧的内边缘
        int r0 = Math.Max(2, (int)(datumPx - 60));
        int r1 = (int)(datumPx + 60);
        int rCap = (int)(ringPx - 30);      // 外环亮弧内侧，环带上界不能进到这里
        if (r1 > rCap) r1 = rCap;
        if (r1 - r0 < 10) return new VisibilityResult(false, 0, 0, "");

        const int nAng = 720;   // 0.5°/列
        var band = new double[r1 - r0 + 1, nAng];

        using var blur = new Mat();
        Cv2.GaussianBlur(src, blur, new Size(3, 3), 0);

        for (int ri = r0; ri <= r1; ri++)
        {
            for (int ai = 0; ai < nAng; ai++)
            {
                double ang = 2 * Math.PI * ai / nAng;
                int x = (int)Math.Round(cx + ri * Math.Cos(ang));
                int y = (int)Math.Round(cy + ri * Math.Sin(ang));
                if (x < 0 || x >= w || y < 0 || y >= h) { band[ri - r0, ai] = double.NaN; continue; }
                band[ri - r0, ai] = blur.At<byte>(y, x);   // ⚠ 必须 At<byte>()
            }
        }

        // 每行（每个半径）沿角度方向做去均值 → 抑制大尺度照明起伏，保留局部特征
        int rows = r1 - r0 + 1;
        var valid = new bool[nAng];
        for (int ri = 0; ri < rows; ri++)
        {
            double sum = 0; int n = 0;
            for (int ai = 0; ai < nAng; ai++) if (!double.IsNaN(band[ri, ai])) { sum += band[ri, ai]; n++; }
            if (n < 20) { for (int ai = 0; ai < nAng; ai++) valid[ai] = false; continue; }
            double mean = sum / n;
            for (int ai = 0; ai < nAng; ai++) if (!double.IsNaN(band[ri, ai])) band[ri, ai] -= mean;
            for (int ai = 0; ai < nAng; ai++) valid[ai] = valid[ai] && true;
        }
        // valid[i] 表示该角度列在环带内有足够采样
        for (int ai = 0; ai < nAng; ai++)
        {
            int cnt = 0;
            for (int ri = 0; ri < rows; ri++) if (!double.IsNaN(band[ri, ai])) cnt++;
            valid[ai] = cnt >= Math.Max(5, rows / 3);
        }

        // 逐角度聚合（对该半径区间内的所有行取平方和）→ 得到角度方向能量曲线
        var energy = new double[nAng];
        for (int ai = 0; ai < nAng; ai++)
        {
            double e = 0; int n = 0;
            for (int ri = 0; ri < rows; ri++)
            {
                if (double.IsNaN(band[ri, ai])) continue;
                e += band[ri, ai] * band[ri, ai]; n++;
            }
            energy[ai] = n > 0 ? Math.Sqrt(e / n) : 0;
        }

        double[] validEnergy = energy.Where((_, i) => valid[i]).ToArray();
        if (validEnergy.Length < 50) return new VisibilityResult(false, 0, 0, "");
        double avg = validEnergy.Average();
        double std = Math.Sqrt(validEnergy.Select(v => (v - avg) * (v - avg)).Average());

        // 角度方向能量的峰谷比：孔会产生局部凹陷（暗）或反光凸起（亮）
        // 统计显著偏离均值的角度段数
        int peaks = CountPeaks(energy, avg - Math.Max(3, std * 1.2), valid);

        // 环带整体对比度（灰度域）
        double ringStd = BandStdDev(band, r0, r1, nAng);

        bool visible = ringStd >= 12 && peaks >= 4;

        string note;
        if (visible)
        {
            note = $"【基准孔可见性】✅ 环带 R{r0}~{r1}px 上检出 {peaks} 个显著特征（环带对比度 {ringStd:F1}/255），" +
                   $"角向基准方案可用。";
        }
        else
        {
            note = $"【基准孔可见性】❌ **基准孔在视野内但完全不可见**" +
                   $"（环带 R{r0}~{r1}px 对比度仅 {ringStd:F1}/255，显著特征 {peaks} 个，需 ≥12 且 ≥4 个）。\n" +
                   $"     → 极坐标展开验证：该环带**看不到任何周期性结构**，8 个双环孔在红光下不反光。\n" +
                   $"     → **\"基准孔在视野内\"是假象**——在视野内 ≠ 看得见。datumHoles 角向基准方案**实际不可用**。\n" +
                   $"     → 退路：①换成**白光/蓝光**照明或加窄带滤光片（红光对工件金属不形成反差）" +
                   $"②或改用**双相机/一次两工位**方案：一次拍中心定位、一次拍角向基准" +
                   $"③或客户确认：打孔孔位本身是否对称（18 孔不具 90° 对称，必须有角向基准）";
        }
        return new VisibilityResult(visible, peaks, ringStd, note);
    }

    /// <summary>估计白色外环的圆心（用最亮弧的三点圆拟合）。</summary>
    private static (double Cx, double Cy) EstimateRingCenter(Mat src, int w, int h, double ringPx)
    {
        // 取亮度最高的一批像素（外环最亮处），做 Kasa 圆拟合
        var pts = new List<(double X, double Y)>();
        using var blur = new Mat();
        Cv2.GaussianBlur(src, blur, new Size(3, 3), 0);
        Cv2.MinMaxIdx(blur, out _, out double maxGray);
        double th = Math.Max(60, maxGray * 0.75);
        for (int y = 0; y < h; y += 2)
            for (int x = 0; x < w; x += 2)
                if (blur.At<byte>(y, x) > th) pts.Add((x, y));

        if (pts.Count < 30) return (w / 2.0, h / 2.0);

        // Kasa 最小二乘圆拟合
        double sx = 0, sy = 0, sxx = 0, syy = 0, sxy = 0, sz = 0, sxz = 0, syz = 0;
        foreach (var (x, y) in pts)
        {
            double z = x * x + y * y;
            sx += x; sy += y; sxx += x * x; syy += y * y; sxy += x * y;
            sz += z; sxz += x * z; syz += y * z;
        }
        int n = pts.Count;
        double[,] A =
        {
            { sxx, sxy, sx },
            { sxy, syy, sy },
            { sx,  sy,  n  }
        };
        double[] b = { sxz, syz, sz };
        var sol = Solve3(A, b);
        if (sol == null) return (w / 2.0, h / 2.0);
        double cx = sol[0] / 2.0, cy = sol[1] / 2.0;
        double r = Math.Sqrt(Math.Max(1, sol[2] + cx * cx + cy * cy));

        // 拟合半径与传入的外环半径差太多 → 不可信，回退画面中心
        if (r < ringPx * 0.6 || r > ringPx * 1.6) return (w / 2.0, h / 2.0);
        return (cx, cy);
    }

    /// <summary>解 3×3 线性方程组（高斯消元）。解不出返回 null。</summary>
    private static double[]? Solve3(double[,] a, double[] b)
    {
        var m = new double[4, 4];
        for (int i = 0; i < 3; i++)
        {
            for (int j = 0; j < 3; j++) m[i, j] = a[i, j];
            m[i, 3] = b[i];
        }
        for (int col = 0; col < 3; col++)
        {
            int piv = col;
            for (int r = col + 1; r < 3; r++)
                if (Math.Abs(m[r, col]) > Math.Abs(m[piv, col])) piv = r;
            if (Math.Abs(m[piv, col]) < 1e-9) return null;
            for (int j = col; j < 4; j++) (m[col, j], m[piv, j]) = (m[piv, j], m[col, j]);
            for (int r = 0; r < 3; r++)
            {
                if (r == col) continue;
                double f = m[r, col] / m[col, col];
                for (int j = col; j < 4; j++) m[r, j] -= f * m[col, j];
            }
        }
        return new[] { m[0, 3] / m[0, 0], m[1, 3] / m[1, 1], m[2, 3] / m[2, 2] };
    }

    /// <summary>统计能量曲线中低于阈值的连续段数量（暗点 = 孔）。</summary>
    private static int CountPeaks(double[] energy, double threshold, bool[] valid)
    {
        int count = 0; bool inSeg = false;
        for (int i = 0; i < energy.Length; i++)
        {
            if (!valid[i]) { inSeg = false; continue; }
            bool low = energy[i] < threshold;
            if (low && !inSeg) { count++; inSeg = true; }
            else if (!low) inSeg = false;
        }
        return count;
    }

    /// <summary>环带的灰度标准差（衡量该环带有没有结构）。</summary>
    private static double BandStdDev(double[,] band, int r0, int r1, int nAng)
    {
        var vals = new List<double>();
        for (int ri = 0; ri < band.GetLength(0); ri++)
            for (int ai = 0; ai < nAng; ai++)
                if (!double.IsNaN(band[ri, ai])) vals.Add(band[ri, ai]);
        if (vals.Count < 20) return 0;
        double m = vals.Average();
        return Math.Sqrt(vals.Select(v => (v - m) * (v - m)).Average());
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
