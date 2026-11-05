using System.Runtime.InteropServices;
using Hummingbird.Core;
using OpenCvSharp;

namespace Hummingbird.Vision;

/// <summary>
/// 现场诊断工具：用**已知尺寸的工件自带特征**（中央孔）反算实际像素尺寸与视野，
/// 判断"角向基准孔是否在画面内"。
///
/// 为什么必须做这件事（2026-11-05 现场）：
///   相机是 1920×1200 / 160µm 像素，但现场看到的是"像显微镜一样小"的视野，
///   而且画面里只能看到中央孔。如果视野实际只有几毫米，
///   那么图纸上 R14.45mm 处的 8 个自带基准孔**根本不在画面里** →
///   datumHoles 角向基准方案直接失效，必须改方案（加大视野 / 二次拍照 / 纯人工对刀）。
///   这个风险在办公室靠猜是发现不了的，必须拿真图量。
///
/// 量法：中央孔是工件自带、尺寸已知（图纸 Ø250+0.5/-0 = Ø2.5mm，沉孔 Ø290 = Ø2.9mm），
///   且是画面里唯一稳定可见的亮环 → 测出它的像素直径，反算 mm/px。
/// </summary>
public sealed class FieldDiagnosis
{
    private readonly IAppLogger? _log;

    public FieldDiagnosis(IAppLogger? log = null) { _log = log; }

    /// <summary>中央孔候选标称直径（mm）：沉孔边 Ø2.9 与通孔 Ø2.5，二者都可能成为可见亮环。</summary>
    private static readonly double[] CenterHoleNominalMm = { 2.90, 2.50 };

    /// <summary>
    /// 诊断结果。
    /// </summary>
    /// <param name="Success">是否量到了中央孔。</param>
    /// <param name="HoleDiameterPx">量得的中央孔像素直径。</param>
    /// <param name="MmPerPx">反算的像素尺寸（mm/px）。</param>
    /// <param name="FovWidthMm">视野宽（mm）。</param>
    /// <param name="FovHeightMm">视野高（mm）。</param>
    /// <param name="DatumRadiusMm">角向基准孔所在半径（mm）。</param>
    /// <param name="DatumInView">基准孔是否落在视野内。</param>
    /// <param name="Detail">人可读结论。</param>
    public sealed record Result(
        bool Success,
        double HoleDiameterPx,
        double MmPerPx,
        double FovWidthMm,
        double FovHeightMm,
        double DatumRadiusMm,
        bool DatumInView,
        string Detail);

    /// <summary>
    /// 分析一帧图：找中央孔 → 反算 mm/px → 判断基准孔是否在视野内。
    /// </summary>
    /// <param name="frame">灰度帧</param>
    /// <param name="datumRadiusMm">角向基准孔半径（mm），图纸标称 14.45</param>
    public Result Analyze(CameraFrame frame, double datumRadiusMm = 14.45)
    {
        using var src = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
        Marshal.Copy(frame.Gray, 0, src.Data, frame.Gray.Length);

        Cv2.MinMaxIdx(src, out _, out double maxGray);
        double meanGray = Cv2.Mean(src).Val0;

        // 中央孔在画面中心附近，且是**亮环**（金属反光）——先取中心区域内的亮斑
        using var blur = new Mat();
        Cv2.GaussianBlur(src, blur, new Size(5, 5), 0);

        int roi = Math.Max(20, Math.Min(frame.Width, frame.Height) / 2);   // 中心半区
        int cx = frame.Width / 2, cy = frame.Height / 2;
        using var roiMat = new Mat(blur, new Rect(
            Math.Max(0, cx - roi), Math.Max(0, cy - roi),
            Math.Min(roi * 2, frame.Width), Math.Min(roi * 2, frame.Height)));

        // 亮斑：比中心区平均亮 40 以上
        double roiMean = Cv2.Mean(roiMat).Val0;
        using var bright = new Mat();
        Cv2.Threshold(roiMat, bright, roiMean + 40, 255, ThresholdTypes.Binary);
        Cv2.FindContours(bright, out var cs, out _, RetrievalModes.External, ContourApproximationModes.ApproxNone);

        // 候选筛选：
        //   ① 必须是"圆"，直径 ≥10px（太小是噪点）且 ≤画面短边的 60%
        //   ② 圆度足够
        //   ③ **优先取最靠近画面中心的那个**；若同样靠中（外环与中心孔同心），
        //      再取**直径小**的 —— 中央孔就是画面正中唯一的小圆
        //      （白色大外环 Ø34.2mm 与中心孔同心，靠中程度相同，直径大得多）
        double maxDia = Math.Min(frame.Width, frame.Height) * 0.6;
        var cands = cs
            .Where(c => c.Length >= 8 && Cv2.ContourArea(c) > 20)
            .Select(c =>
            {
                var e = Cv2.FitEllipse(c);
                double d = (e.Size.Width + e.Size.Height) / 2;
                double ratio = Math.Min(e.Size.Width, e.Size.Height) / Math.Max(e.Size.Width, e.Size.Height);
                double x = e.Center.X + Math.Max(0, cx - roi);
                double y = e.Center.Y + Math.Max(0, cy - roi);
                double dist = Math.Sqrt((x - frame.Width / 2.0) * (x - frame.Width / 2.0)
                                     + (y - frame.Height / 2.0) * (y - frame.Height / 2.0));
                return (D: d, Ratio: ratio, Cx: x, Cy: y, Dist: dist);
            })
            .Where(t => t.Ratio > 0.65 && t.D >= 10 && t.D <= maxDia)
            .OrderBy(t => Math.Round(t.Dist / 10))   // 先按"离画面中心多远"分档（10px 一档）
            .ThenBy(t => t.D)                        // 同档内取更小的圆
            .ToList();

        var best = cands.FirstOrDefault();

        if (best.D <= 0)
        {
            string why = $"画面中心区未找到中央孔亮环（中心区均值{roiMean:F0}，全图最亮{maxGray:F0}，" +
                         $"允许直径范围 10~{maxDia:F0}px）。" +
                         "画面太暗 → 先解决照明/曝光；或中央孔不在画面中心 → 手动调整工件位置";
            _log?.Warn("[诊断] " + why);
            return new Result(false, 0, 0, 0, 0, datumRadiusMm, false, why);
        }

        // 反算 mm/px：用两个标称值分别算，取"视野能覆盖基准孔"的那个（物理上更可信）
        double px = best.D;
        // 两个标称假设（沉孔 Ø2.9 / 通孔 Ø2.5），都可能成为可见亮环
        var options = CenterHoleNominalMm.Select(n => new
        {
            Nominal = n,
            MmPerPx = n / px,
            FovW = n / px * frame.Width,
            InView = (n / px * frame.Width / 2.0) >= datumRadiusMm,
        }).ToList();

        // 选假设的原则：**取保守的那个**——用较小的标称（Ø2.5）得到较小的 mm/px 与较小的视野。
        // 理由：亮环到底是 Ø2.9 沉孔边还是 Ø2.5 通孔边，在现场是分不清的；
        //   而"基准孔在不在视野内"这个问题必须给一个**不会误判为可行**的答案。
        //   若保守假设下基准孔都在视野内 → 一定成立；
        //   若保守假设下不在视野内 → 保守报"不在视野内"，宁可多叫一次现场验证，也不要漏掉硬件问题。
        var pick = options.OrderBy(o => o.Nominal).First();
        double fovW = pick.FovW;
        double fovH = pick.MmPerPx * frame.Height;
        bool inView = pick.InView;
        var other = options.First(o => o.Nominal != pick.Nominal);

        string detail =
            $"中央孔亮环 {px:F1}px（按标称 Ø{pick.Nominal:F2}mm 反算）→ {pick.MmPerPx:F5} mm/px\n" +
            $"视野 = {fovW:F1} × {fovH:F1} mm（图幅 {frame.Width}×{frame.Height}）\n" +
            $"角向基准孔在 R{datumRadiusMm:F2}mm → " +
            (inView
                ? $"✅ 在视野内（画面半宽 {fovW / 2:F1}mm ≥ {datumRadiusMm:F2}mm），datumHoles 方案可行"
                : $"❌ **不在视野内**（画面半宽仅 {fovW / 2:F1}mm < {datumRadiusMm:F2}mm）\n" +
                  $"   → datumHoles 角向基准方案失效！必须：①加大视野（换短焦/加中继镜/降 Binning）" +
                  $"②或把相机移到能同时看到中心孔与基准孔的位置 ③或改用纯人工对刀（粗对齐角需准到 ±0.5°）") +
            $"\n（另一标称假设：Ø{other.Nominal:F2}mm → {other.MmPerPx:F5} mm/px，视野 {other.FovW:F1}mm，" +
            $"基准孔{(other.InView ? "在视野内" : "也不在视野内")}）";

        _log?.Info("[诊断] " + detail.Replace("\n", " | "));
        return new Result(true, px, pick.MmPerPx, fovW, fovH, datumRadiusMm, inView, detail);
    }
}
