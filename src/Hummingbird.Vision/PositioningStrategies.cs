using System.Runtime.InteropServices;
using Hummingbird.Core;
using OpenCvSharp;

namespace Hummingbird.Vision;

/// <summary>
/// 定位策略集合（策略模式的具体实现）。
///
/// ============================================================
/// 为什么要有策略模式（2026-11-05 客户提议）
/// ============================================================
/// 现役方案要求「整个工件 + 8 个双环基准孔同时入画且可见」，但真机验证表明：
///   · 视野短边 28.1mm &lt; 工件 Ø34.2mm → 整盘放不进画面
///   · 8 个基准孔在红光下环带对比度仅 4.9/255 → 完全不可见
/// 客户因此提议：相机视野缩小到**只看一个标记孔**（客户自己放置），
/// 工件中心由夹具定位销锁死，其余 18 个孔由 DWG 模板几何算出来。
///
/// 数学上的关键约束（务必看懂，否则会用错）：
///   平面刚体变换有 3 个自由度：中心 X、中心 Y、转角 θ。
///   一个孔的孔心只提供 2 个约束（像素 x,y）→ 欠定，解不出。
///   策略②必须用**夹具锁死中心**（2 个自由度交给机械），视觉只求转角 θ。
///   ⚠ 若标记孔不随工件转（固定在夹具上），则转角永远测不出，Check() 会直接拒绝。
///
/// ⚠ 2026-11-05 现场照片复核结论（推翻了策略②的前提）：
///   客户现有「夹具」只是三根金属条拼出的托板，随手摆在回转台上靠一根挡块限位，
///   既没有定位销锁中心、也没有角向定位 —— 策略②在现场**根本用不了**。
///   但照片同时证明：白光下工件自带的那圈双环小孔清晰可见（红光下才看不见）。
///   于是新增策略④：同时看 **3 个**相邻基准孔 → 中心、转角、mm/px 全部由图像实时解出，
///   **夹具什么都不用锁**。为什么是 3 个不是 2 个：孔组有 180° 旋转对称，
///   只看两个孔时弦两侧各有一个自洽圆心（相距 27.9mm），第三孔才能消掉这个镜像。
///
/// 四个策略（按工程优先级）：
///   ① FullFieldDatumHolesStrategy：现役整盘视觉（包装 DialPositioning，零改动复用）
///   ④ TwoFiducialStrategy：多孔自主定位（推荐 3 孔），不需夹具（当前现场最推荐）
///   ② SingleFiducialStrategy：单标记孔 + 夹具锁心（夹具改造后才可用）
///   ③ ManualFixtureStrategy：纯机械对刀兜底（不用视觉，转角靠人工）
///
/// 共同点：都实现 IPositioningStrategy（继承 IVisionService），
/// 因此 AutoRunWorkflow 完全不用改，界面换 Vision 指向哪个策略即可。
/// </summary>

// ==================================================================
// ① 现役：整盘 + 8 基准孔组（包装现有 DialPositioning）
// ==================================================================

/// <summary>
/// 现役整盘视觉定位。内部委托 DialPositioning（三遍定位），不改一行旧代码。
/// 适用前提最苛刻：整盘入画 + 基准孔可见 + 已标定。
/// </summary>
public sealed class FullFieldDatumHolesStrategy : IPositioningStrategy
{
    private readonly DialPositioning _inner;

    public FullFieldDatumHolesStrategy(IAppLogger? log = null) => _inner = new DialPositioning(log);

    public string Id => StrategyIds.FullFieldDatumHoles;
    public string DisplayName => "① 整盘视觉定位（8 基准孔）";
    public string Description =>
        "拍下整个表盘，用外轮廓定心 + 工件自带的 8 个双环孔组定角；中心与转角全部由视觉实测，精度最高。";

    public IReadOnlyList<string> Prerequisites => new[]
    {
        "视野短边 ≥ 工件外径 34.2mm（当前约 28.1mm，不满足）",
        "8 个双环基准孔在画面里清晰可见（当前红光下完全不可见）",
        "已完成像素→机床九点标定",
        "提供人工粗对齐角，消解 8 孔 90° 对称造成的 4 个等效转角"
    };

    public StrategyReadiness Check(StrategyContext ctx)
    {
        var missing = new List<string>();
        if (!ctx.FullPartInView) missing.Add("整盘放不进画面（视野短边不足）");
        if (!ctx.DatumHolesVisible) missing.Add("8 个基准孔在画面里不可见");
        if (!ctx.CalibrationValid) missing.Add("九点标定未完成");
        if (!ctx.CoarseAlignProvided) missing.Add("未提供粗对齐角（无法消解 90° 对称）");

        return missing.Count == 0
            ? StrategyReadiness.Ok("全部前提满足，整盘视觉定位可用（精度最高）。")
            : StrategyReadiness.Block("缺 " + missing.Count + " 项：" + string.Join("；", missing) + "。");
    }

    public Task<PartPose> LocateAsync(CameraFrame frame, VisionOptions opt, CancellationToken ct = default)
        => _inner.LocateAsync(frame, opt, ct);
}

// ==================================================================
// ② 客户新方案：单标记孔 + 夹具锁心
// ==================================================================

/// <summary>
/// 单标记孔定位（StrategyIds.SingleFiducial）。
///
/// 现场做法：
///   1. 夹具加定位销/止口，把工件中心重复锁死（机械保证）。
///   2. 客户在工件上放一个**随工件转动**、相机看得清的标记孔
///      （可用现成的 8 个双环孔之一，名义半径 14.45mm）。
///   3. 相机视野缩小到只框住这一个孔 → 放大倍数高、对比度好，红光问题局部化。
///   4. 一次标定：把标准件装夹到位，记录「工件中心在相机像素系的位置」
///      (FixtureCenterPxX/Y) 和标记孔的名义时钟角。
///   5. 每次装夹拍一张，检测孔心 → 求方位角 → 装卡转角。
///   6. 18 个待加工孔的机床坐标 = 夹具中心(像素→标定→机床) + 模板坐标按转角旋转。
///
/// 为什么这个方案更稳：不管视野多小、不管其它孔看不看得见，
/// 只要这一个孔能稳定检出，就能出坐标。
/// </summary>
public sealed class SingleFiducialStrategy : IPositioningStrategy
{
    private readonly IAppLogger? _log;

    public SingleFiducialStrategy(IAppLogger? log = null) => _log = log;

    public string Id => StrategyIds.SingleFiducial;
    public string DisplayName => "② 单标记孔 + 夹具锁心（客户新方案）";
    public string Description =>
        "相机只看一个客户放置的标记孔；工件中心由夹具定位销锁死，视觉只测转角；其余 18 孔由模板几何算出。";

    public IReadOnlyList<string> Prerequisites => new[]
    {
        "夹具用定位销/止口把工件中心重复锁死（关键：单孔无法自定中心）",
        "标记孔必须随工件一起转动（固定在夹具上则测不出转角）",
        "标记孔在缩小后的视野里清晰可见、对比度足够",
        "已标定「工件中心在当前相机像素系的位置」(FixtureCenterPxX/Y)",
        "已完成像素→机床九点标定",
        "已知标记孔相对中心的名义半径与名义时钟角"
    };

    public StrategyReadiness Check(StrategyContext ctx)
    {
        var missing = new List<string>();
        if (!ctx.FixtureCentersPart) missing.Add("夹具未锁死工件中心（单孔数学上欠定）");
        if (!ctx.FiducialAvailable) missing.Add("没有可用的标记孔");
        if (!ctx.FiducialRotatesWithPart) missing.Add("标记孔不随工件转动（转角测不出）");
        if (!ctx.FiducialTaught) missing.Add("未标定夹具中心像素 / 未示教标记孔");
        if (!ctx.CalibrationValid) missing.Add("九点标定未完成（孔位无法换算到机床系）");

        if (missing.Count > 0)
            return StrategyReadiness.Block("缺 " + missing.Count + " 项：" + string.Join("；", missing) + "。");

        return StrategyReadiness.Warn(
            "前提齐备，可用。注意：中心精度现在取决于**夹具重复定位精度**，不再由视觉实时修正；" +
            "建议每次换夹具/刀具后复核一次中心像素标定。");
    }

    public async Task<PartPose> LocateAsync(CameraFrame frame, VisionOptions opt, CancellationToken ct = default)
    {
        await Task.Yield();

        // ---- 前置校验：夹具中心必须已经标定 ----
        if (double.IsNaN(opt.FixtureCenterPxX) || double.IsNaN(opt.FixtureCenterPxY))
            return Fail("未标定夹具中心像素（FixtureCenterPxX/Y 为 NaN）。" +
                        "请先在界面点『标定夹具中心』：装标准件 → 拍整盘/对刀 → 记录中心像素。");
        if (opt.PixelSizeMm <= 0)
            return Fail("像素尺寸 PixelSizeMm 未设置，无法做孔半径校验。");

        // ---- 检测画面里唯一的标记孔 ----
        var hole = DetectFiducialHole(frame, opt, _log);
        if (hole is null)
            return Fail("画面中未找到标记孔。检查：①光源/曝光（画面是否全黑或全白）" +
                        "②相机视野是否对准该孔 ③标记孔是否被反光淹没 " +
                        "④FiducialDiameterMm 与实际孔径是否差太多");

        // ---- 方位角 → 装卡转角 ----
        double dx = hole.Cx - opt.FixtureCenterPxX;
        double dy = hole.Cy - opt.FixtureCenterPxY;
        double measuredClock = ClockAngle(dx, dy);          // 12H=0°，顺时针
        double angleOffset = Norm180(measuredClock - opt.FiducialNominalClockDeg);

        // ---- 距离校验：孔心到夹具中心的距离应等于名义半径 ----
        double distPx = Math.Sqrt(dx * dx + dy * dy);
        double distMm = distPx * opt.PixelSizeMm;
        double radiusErr = Math.Abs(distMm - opt.FiducialRadiusMm);
        if (radiusErr > opt.FiducialRadiusToleranceMm)
            return Fail($"孔心到夹具中心距离 {distMm:F2}mm，与名义半径 {opt.FiducialRadiusMm:F2}mm " +
                        $"偏差 {radiusErr:F2}mm > 容差 {opt.FiducialRadiusToleranceMm:F2}mm。" +
                        "可能原因：①工件没装夹到位 ②夹具中心像素标定失效（相机/镜头动过）" +
                        "③检出的不是标记孔而是污点/反光 ④像素尺寸 mm/px 填错。" +
                        "⚠ 已拒绝输出坐标，避免打废工件。");

        // 置信度：孔检测质量 × 距离校验吻合度
        double radiusScore = 1.0 - Math.Min(1.0, radiusErr / Math.Max(0.01, opt.FiducialRadiusToleranceMm)) * 0.3;
        double confidence = Math.Clamp(hole.Quality * radiusScore, 0, 1);

        string detail =
            $"单标记孔：孔心=({hole.Cx:F1},{hole.Cy:F1})px 直径={hole.DiameterPx:F1}px 圆度={hole.Circularity:F2} | " +
            $"夹具中心=({opt.FixtureCenterPxX:F1},{opt.FixtureCenterPxY:F1})px | " +
            $"实测时钟角={measuredClock:F2}° 名义={opt.FiducialNominalClockDeg:F2}° → 装卡转角={angleOffset:F2}° | " +
            $"孔心距={distMm:F2}mm（名义 {opt.FiducialRadiusMm:F2}mm，偏差 {radiusErr:F2}mm）| " +
            $"中心由夹具保证，18 孔由模板几何+本转角算出";

        _log?.Info("[策略②] " + detail);
        if (confidence < 0.55)
            _log?.Warn($"[策略②] 置信度偏低 {confidence:F2}：检查孔成像质量与夹具中心标定");

        // CenterPxX/Y 直接给夹具中心 —— ComputeTargets 会用它经标定换成机床坐标，
        // 再把模板 18 孔按 angleOffset 旋转叠加。中心虽不在当前小视野画面内也没关系，
        // 它只是参与坐标换算的数值，不要求出现在图里。
        return new PartPose(
            Success: true,
            CenterPxX: opt.FixtureCenterPxX,
            CenterPxY: opt.FixtureCenterPxY,
            OuterRadiusPx: distPx,
            DatumClockDeg: measuredClock,
            AngleOffsetDeg: angleOffset,
            Confidence: confidence,
            Detail: detail);
    }

    private PartPose Fail(string why)
    {
        _log?.Warn("[策略②] " + why);
        return new PartPose(false, 0, 0, 0, 0, 0, 0, why);
    }

    /// <summary>时钟角：+Y=12H 为 0°，顺时针为正（与 DialPositioning 保持一致）。</summary>
    private static double ClockAngle(double dx, double dy)
        => ((90 - Math.Atan2(-dy, dx) * 180 / Math.PI) % 360 + 360) % 360;

    /// <summary>归一化到 ±180°。</summary>
    private static double Norm180(double deg) => ((deg % 360) + 540) % 360 - 180;

    // ------------------------------------------------------------------
    // 标记孔检测：小视野里找**唯一的圆孔**
    // ------------------------------------------------------------------

    public sealed record HoleResult(double Cx, double Cy, double DiameterPx, double Circularity, double Quality);

    /// <summary>
    /// 单孔版：在视野里找**质量最好的那个**标记孔（策略②用）。
    /// 若画面里出现多个圆会告警 —— 策略②的视野本应只框住一个孔。
    /// </summary>
    public static HoleResult? DetectFiducialHole(CameraFrame frame, VisionOptions opt, IAppLogger? log = null)
    {
        var all = DetectFiducialHoles(frame, opt, log);
        if (all.Count == 0) return null;
        if (all.Count > 3)
            log?.Warn($"[孔检测] 画面检出 {all.Count} 个圆形候选，已取质量最高者。" +
                      "策略②建议把视野缩小到只框住一个孔；策略④（多孔）推荐同时框住 3 个相邻孔。");
        return all[0];
    }

    /// <summary>
    /// 多孔版：返回视野里**全部**圆孔候选（按质量降序，已按中心距离去重）。
    /// 思路：Otsu 二值化 → 轮廓 → 按「圆度 + 尺寸 + 面积」打分。
    /// 同时兼容**亮孔**（孔反光成白点）和**暗孔**（孔是黑洞）：两种极性都试一遍。
    /// 策略④（多孔自主定位）靠它拿到 3 个孔心，从而解出中心与转角并消掉镜像。
    /// </summary>
    public static List<HoleResult> DetectFiducialHoles(CameraFrame frame, VisionOptions opt, IAppLogger? log = null)
    {
        using var src = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
        Marshal.Copy(frame.Gray, 0, src.Data, frame.Gray.Length);

        using var blur = new Mat();
        Cv2.GaussianBlur(src, blur, new Size(5, 5), 0);

        Cv2.MinMaxIdx(blur, out double minGray, out double maxGray);
        double meanGray = Cv2.Mean(blur).Val0;
        if (maxGray < 20)
        {
            log?.Warn("[孔检测] 画面全黑（最亮仅 " + maxGray.ToString("F0") + "/255），先解决照明/曝光");
            return new List<HoleResult>();
        }
        if (meanGray > 245 && maxGray - minGray < 12)
        {
            log?.Warn("[孔检测] 画面整体过曝（全白无结构），把曝光降到 5000~8000μs");
            return new List<HoleResult>();
        }

        // 期望孔径（像素）：名义直径 / mm每像素。放宽到 0.35~3 倍，
        // 因为缩小视野后 mm/px 可能还没重新标定，不要让尺寸过滤太苛刻。
        double expectDiaPx = Math.Max(4, opt.FiducialDiameterMm / Math.Max(0.0001, opt.PixelSizeMm));
        double minDia = expectDiaPx * 0.35, maxDia = expectDiaPx * 3.0;

        using var bw = new Mat();
        Cv2.Threshold(blur, bw, 0, 255, ThresholdTypes.Otsu | ThresholdTypes.Binary);

        // ⚠ 两种极性各自独立建 Mat，别用 "using var work = invert ? new Mat() : bw"：
        //   那样第一次循环结束会把 bw 一起 Dispose，第二次 BitwiseNot 直接访问野指针崩溃（0xC0000005）。
        using var bwInverted = new Mat();
        Cv2.BitwiseNot(bw, bwInverted);

        var candidates = new List<HoleResult>();
        foreach (var work in new[] { bw, bwInverted })
        {
            Cv2.FindContours(work, out var contours, out _,
                RetrievalModes.External, ContourApproximationModes.ApproxNone);

            foreach (var c in contours)
            {
                double area = Cv2.ContourArea(c);
                if (area < 9) continue;
                double peri = Cv2.ArcLength(c, true);
                if (peri < 1) continue;
                double circularity = 4 * Math.PI * area / (peri * peri);   // 1.0=完美圆
                if (circularity < 0.45) continue;

                var rect = Cv2.BoundingRect(c);
                double dia = (rect.Width + rect.Height) / 2.0;
                if (dia < minDia || dia > maxDia) continue;
                double aspect = (double)Math.Min(rect.Width, rect.Height) / Math.Max(1, Math.Max(rect.Width, rect.Height));
                if (aspect < 0.6) continue;

                var m = Cv2.Moments(c);
                if (Math.Abs(m.M00) < 1e-6) continue;
                double cx = m.M10 / m.M00, cy = m.M01 / m.M00;

                // 对比度：孔内均值 vs 全图均值，差越大越可信
                using var mask = new Mat(frame.Height, frame.Width, MatType.CV_8UC1, Scalar.All(0));
                Cv2.DrawContours(mask, new[] { c }, -1, Scalar.All(255), -1);
                double inner = Cv2.Mean(blur, mask).Val0;
                double contrast = Math.Abs(inner - meanGray) / 255.0;

                double quality = Math.Clamp(circularity * 0.55 + aspect * 0.20 + Math.Min(1, contrast * 3) * 0.25, 0, 1);
                candidates.Add(new HoleResult(cx, cy, dia, circularity, quality));
            }
        }

        if (candidates.Count == 0)
        {
            log?.Warn($"[孔检测] 二值化后没有符合圆形+尺寸的轮廓（期望孔径 {expectDiaPx:F1}px，允许 {minDia:F1}~{maxDia:F1}px）");
            return new List<HoleResult>();
        }

        // 同一个实体孔在两种极性下可能被检出两次 → 按中心距离合并，保留质量高者。
        // 不做这步的话，策略④会把「一个孔的正反两种二值化结果」当成两个孔，解出荒唐的中心。
        double mergePx = Math.Max(4, expectDiaPx * 0.6);
        var merged = new List<HoleResult>();
        foreach (var c in candidates.OrderByDescending(p => p.Quality))
        {
            bool dup = false;
            foreach (var m in merged)
            {
                double dd = Math.Sqrt((m.Cx - c.Cx) * (m.Cx - c.Cx) + (m.Cy - c.Cy) * (m.Cy - c.Cy));
                if (dd < mergePx) { dup = true; break; }
            }
            if (!dup) merged.Add(c);
        }
        return merged;   // 已按质量降序
    }
}

// ==================================================================
// ④ 多孔自主定位（2026-11-05 现场照片复核后新增 —— 当前最推荐）
// ==================================================================

/// <summary>
/// 多孔自主定位：视野同时看 3 个工件自带的双环基准孔，
/// **中心、转角、mm/px 全部由图像实时解出，夹具不需要锁任何东西**。
///
/// 为什么需要它：2026-11-05 客户回传的现场照片显示，现有「夹具」只是三根金属条拼出的
/// 托板，随手摆在回转台上靠一根挡块限位 —— 既没有定位销锁中心，也没有角向基准。
/// 于是策略②「夹具锁死中心」的硬前提在现场不成立，必须有不依赖机械精度的退路。
///
/// 数学（关键）：
///   两个孔心 = 4 个约束 &gt; 刚体变换 3 个自由度 → 中心(X,Y) 与转角 θ 全部可解，
///   且两孔像素弦长 : 图纸名义弦长 还顺带把 mm/px 反算出来（可用来校验标定是否失效）。
///   ① 名义角 αi→αj 顺时针差 Δ，名义弦长 d_mm = 2R·sin(Δ/2)
///   ② 实测像素弦长 d_px → mm/px = d_mm / d_px
///   ③ 圆心在弦的中垂线上，离弦中点 h = R·cos(Δ/2)；两个镜像候选里，
///      只有一个满足「B 的时钟角 − A 的时钟角 ≡ +Δ」，据此消掉镜像
///   ④ θ = ClockAngle(A − 圆心) − αi
///   ⑤ 孔组有 90° 旋转对称 → θ / θ+90 / θ+180 / θ+270 四个等效解，
///      用人工粗对齐角 CoarseAlignDeg 挑最接近的那个（必填，否则拒绝出坐标）
///
/// ⑥ **镜像二义性（务必看懂）**：8 个基准孔的分布有 180° 旋转对称，
///   只看两个孔时，弦的两侧各有一个自洽圆心（相距 2R·cos(Δ/2) ≈ 27.9mm），
///   两者都能解释观察到的两个孔，选错会把 18 个孔整体打偏。
///   破解：让**第三个**相邻的基准孔一起入画 —— 它不在弦上，只有真圆心能让它
///   同时落在半径圈且方位角吻合模板。只有 2 个孔时，必须给一个粗略中心提示，否则拒绝。
///
/// 视野需求：3 个相邻孔（30/60/120）的包围盒约 **6 × 20mm**，现有视野 28.1mm 完全够，
/// 不用改镜头、不用改夹具。工件中心此时在画面外（离孔 14.45mm），但 ComputeTargets
/// 只用中心像素的**数值**经九点标定换算，不要求它出现在图里 —— 18 孔坐标照样算得出。
/// </summary>
public sealed class TwoFiducialStrategy : IPositioningStrategy
{
    private readonly IAppLogger? _log;

    public TwoFiducialStrategy(IAppLogger? log = null) => _log = log;

    public string Id => StrategyIds.TwoFiducials;
    public string DisplayName => "④ 多孔自主定位（不需夹具，推荐 3 孔）";
    public string Description =>
        "相机同时看 3 个工件自带的双环基准孔；由孔间距反算 mm/px、由弦中垂线解出工件中心、由孔心方位角求转角，" +
        "第三个孔用来消掉孔组 180° 对称造成的镜像圆心二义性。" +
        "中心/转角/缩放全由图像实时解出，夹具随便放、工件随手摆都不影响 —— 适合现场现有托板。";

    public IReadOnlyList<string> Prerequisites => new[]
    {
        "【推荐】视野同时看清 3 个相邻基准孔（约需 6×20mm，现有 28.1mm 够）—— 第三孔用于消镜像二义性",
        "若只能看到 2 个孔：需填一个粗略的工件中心像素（±10mm 内即可，仅用于选镜像）",
        "孔必须随工件转动（直接用工件的 8 个双环孔，别用夹具上的孔）",
        "已完成像素→机床九点标定（缩小视野后必须重做）",
        "已填人工粗对齐角（孔组 90° 对称，不填会拒绝出坐标，防打废工件）",
        "⚠ 不需要定位销、不需要止口、不需要精确示教夹具中心 —— 本策略不依赖夹具"
    };

    public StrategyReadiness Check(StrategyContext ctx)
    {
        var missing = new List<string>();
        if (!ctx.TwoHolesInView) missing.Add("视野还没同时看清 3 个相邻基准孔（换白光后先拍一张确认）");
        if (!ctx.FiducialRotatesWithPart) missing.Add("孔不随工件转动（用工件自带的双环孔就没这问题）");
        if (!ctx.CalibrationValid) missing.Add("九点标定未完成（孔位无法换算到机床系）");
        if (!ctx.CoarseAlignProvided) missing.Add("未填人工粗对齐角（90° 对称无法消歧）");

        if (missing.Count > 0)
            return StrategyReadiness.Block("缺 " + missing.Count + " 项：" + string.Join("；", missing) + "。");

        return StrategyReadiness.Ok(
            "前提齐备。**本策略完全不依赖夹具**：中心、转角、mm/px 都从当前这一帧图像解出，" +
            (ctx.FixtureCentersPart ? "现场若有定位销也无妨（仅作冗余校验）。" : "现场没有定位销/止口也照样能打。") +
            " 注意粗对齐角要准到 ±15° 以内，否则 90° 对称消歧会选错。");
    }

    private sealed record PairSolution(
        double Cx, double Cy, double MmPerPx, double Theta,
        double ScaleErr, double CoarseErr, double Quality,
        double ChordMm, double DeltaDeg, double AlphaI,
        int HoleA, int HoleB, int Consistent, int ExtraOnRing);

    public async Task<PartPose> LocateAsync(CameraFrame frame, VisionOptions opt, CancellationToken ct = default)
    {
        await Task.Yield();

        // ---- 前置校验 ----
        if (opt.DatumHoleRadiusMm <= 0)
            return Fail("模板未给基准孔名义半径 DatumHoleRadiusMm（应为 14.45mm）。");
        if (opt.DatumHoleAngles.Count < 2)
            return Fail("模板未给基准孔角度表 DatumHoleAngles（应为 30/60/120/150/210/240/300/330）。");
        if (opt.PixelSizeMm <= 0)
            return Fail("未设置像素尺寸 VisionPixelSizeMm，无法做弦长尺度自检。");
        if (double.IsNaN(opt.CoarseAlignDeg))
            return Fail("未填人工粗对齐角 CoarseAlignDeg。孔组有 90° 旋转对称，" +
                        "不消歧有 3/4 概率打错方位，已拒绝出坐标。请在界面填一个 ±45° 内的估计角。");

        var holes = SingleFiducialStrategy.DetectFiducialHoles(frame, opt, _log);
        if (holes.Count < 2)
            return Fail($"画面只检出 {holes.Count} 个孔，多孔定位至少需要 2 个孔（推荐 3 个）。检查：" +
                        "①换白光/手机手电照明（红光下这些孔不反光，真机图已证实看不见）" +
                        "②曝光 5000~8000μs、增益 0 ③视野是否同时框住相邻两个孔 " +
                        "④FiducialDiameterMm 与实际孔径是否差太多");
        if (holes.Count > 4)
            _log?.Warn($"[策略④] 画面检出 {holes.Count} 个圆形候选（可能混入了划痕/污点），" +
                       "已只用质量最高的前 6 个做配对，并用第三孔一致性做校验。");

        double R = opt.DatumHoleRadiusMm;
        double pxSize = opt.PixelSizeMm;
        double tol = Math.Max(0.01, opt.TwoHoleScaleTolerancePct);
        double coarse = opt.CoarseAlignDeg;
        var angles = opt.DatumHoleAngles.Where(a => !double.IsNaN(a)).Distinct().ToArray();
        var sols = new List<PairSolution>();

        // 枚举：孔的有序对 (A→B) × 名义角的有序对 (αi→αj)
        int n = Math.Min(holes.Count, 6);
        for (int hi = 0; hi < n; hi++)
        for (int hj = 0; hj < n; hj++)
        {
            if (hi == hj) continue;
            var A = holes[hi]; var B = holes[hj];
            double dx = B.Cx - A.Cx, dy = B.Cy - A.Cy;
            double dPx = Math.Sqrt(dx * dx + dy * dy);
            if (dPx < 10) continue;

            double ux = dx / dPx, uy = dy / dPx;
            double nx = -uy, ny = ux;                    // 弦的法向
            double mx = (A.Cx + B.Cx) / 2, my = (A.Cy + B.Cy) / 2;

            for (int ai = 0; ai < angles.Length; ai++)
            for (int aj = 0; aj < angles.Length; aj++)
            {
                if (ai == aj) continue;
                double delta = CwDiff(angles[ai], angles[aj]);   // 顺时针角差 0~360
                if (delta <= 1 || delta > 120) continue;         // 小视野只容得下相邻近的孔对

                double half = delta * Math.PI / 360.0;           // Δ/2（弧度）
                double dMm = 2 * R * Math.Sin(half);             // 名义弦长
                double mmpp = dMm / dPx;                         // 反算 mm/px
                double scaleErr = Math.Abs(mmpp / pxSize - 1);
                if (scaleErr > tol) continue;                    // 尺度对不上 → 配对错误或标定失效

                double hPx = R * Math.Cos(half) / mmpp;          // 圆心离弦中点的距离

                // 中垂线上有两个镜像圆心；只有满足「B−A 的时钟差 ≡ +Δ」的那个是对的
                foreach (int sign in new[] { 1, -1 })
                {
                    double cx = mx + sign * hPx * nx;
                    double cy = my + sign * hPx * ny;
                    double ca = ClockAngle(A.Cx - cx, A.Cy - cy);
                    double cb = ClockAngle(B.Cx - cx, B.Cy - cy);
                    if (Math.Abs(CwDiff(ca, cb) - delta) > 2.0) continue;

                    double theta = Norm180(ca - angles[ai]);

                    // 90° 旋转对称：4 个等效解，用人工粗对齐角挑最接近的
                    double bestTheta = theta, bestDiff = double.MaxValue;
                    for (int k = 0; k < 4; k++)
                    {
                        double t = theta + 90 * k;
                        double dd = Math.Abs(Norm180(t - coarse));
                        if (dd < bestDiff) { bestDiff = dd; bestTheta = t; }
                    }

                    // 第三孔一致性：这是消掉「镜像圆心」的关键判据。
                    // 8 个基准孔的分布有 180° 旋转对称，只看两个孔时，弦两侧各有一个
                    // 自洽的圆心（相距 2R·cos(Δ/2) ≈ 27.9mm），会把 18 孔整体打偏。
                    // 第三个孔不在弦上，只有真圆心能让它同时落在半径圈且角度吻合。
                    var (extra, ok) = CountConsistentExtraHoles(
                        holes, hi, hj, cx, cy, mmpp, R, bestTheta, angles);

                    sols.Add(new PairSolution(
                        cx, cy, mmpp, bestTheta, scaleErr, bestDiff,
                        (A.Quality + B.Quality) / 2, dMm, delta, angles[ai], hi, hj, ok, extra));
                }
            }
        }

        if (sols.Count == 0)
            return Fail("孔与模板角度表配不上（没有任何组合能同时满足弦长尺度和方位约束）。" +
                        "常见原因：①缩小视野后 VisionPixelSizeMm 没重测（当前 " + pxSize.ToString("F5") +
                        " mm/px）②检出的不是孔而是反光/污点 ③画面里的孔不是同一半径圈上的基准孔 " +
                        "④DatumHoleRadiusMm 填错。⚠ 已拒绝输出坐标。");

        // ---- 消歧：≥3 孔用第三孔自动消镜像；恰好 2 孔必须给一个粗略中心提示 ----
        bool hasHint = !double.IsNaN(opt.FixtureCenterPxX) && !double.IsNaN(opt.FixtureCenterPxY);
        List<PairSolution> usable;

        if (holes.Count >= 3)
        {
            usable = sols.Where(s => s.Consistent > 0).ToList();
            if (usable.Count == 0)
                return Fail("画面里有 3 个以上圆，但没有任何一组解能让第三个孔同时满足" +
                            "「落在半径圈上」和「方位角吻合模板」。可能混入了划痕/污点，或配对被反光干扰。" +
                            "⚠ 已拒绝输出坐标，请换白光重拍或缩小视野只框住相邻的 3 个基准孔。");
        }
        else                                     // 恰好 2 孔
        {
            if (!hasHint)
                return Fail("画面只有 2 个孔：**存在镜像圆心二义性**。8 个基准孔的分布有 180° 旋转对称，" +
                            "两孔弦的两侧各有一个自洽圆心（相距约 27.9mm），选错会把 18 个孔整体打偏。\n" +
                            "两条路任选其一：\n" +
                            "  ①【推荐】把视野稍微放大，让第 3 个相邻基准孔一起入画 —— " +
                            "3 孔约需 6×20mm 视野，现有 28.1mm 完全够，不用改镜头；\n" +
                            "  ②在界面填一个**粗略**的工件中心像素 FixtureCenterPxX/Y" +
                            "（误差 ±10mm 内即可，只用来选镜像，不参与最终坐标）。\n" +
                            "⚠ 已拒绝输出坐标，避免打废工件。");
            usable = sols;
        }

        double diag = Math.Sqrt((double)frame.Width * frame.Width + (double)frame.Height * frame.Height);

        // 综合打分：第三孔一致性（权重最大）+ 尺度 + 粗对齐 + 成像质量 + 粗略中心提示
        var best = usable.OrderBy(s =>
            s.ScaleErr * 3
            + s.CoarseErr / 180.0
            + (1 - s.Quality) * 0.2
            - s.Consistent * 2
            + (hasHint
                ? Math.Sqrt(Math.Pow(s.Cx - opt.FixtureCenterPxX, 2) +
                            Math.Pow(s.Cy - opt.FixtureCenterPxY, 2)) / diag * 2
                : 0)).First();

        double ringPx = R / best.MmPerPx;
        int consistent = best.Consistent, extraOnRing = best.ExtraOnRing;

        double confidence = Math.Clamp(
            best.Quality * (1 - Math.Min(1, best.ScaleErr / tol) * 0.3) * (consistent > 0 ? 1.0 : 0.85), 0, 1);

        string detail =
            $"多孔自主定位：孔A=({holes[best.HoleA].Cx:F1},{holes[best.HoleA].Cy:F1})px " +
            $"孔B=({holes[best.HoleB].Cx:F1},{holes[best.HoleB].Cy:F1})px | " +
            $"配对名义角 {best.AlphaI:F0}°→{best.AlphaI + best.DeltaDeg:F0}°（Δ={best.DeltaDeg:F0}°）| " +
            $"像素弦长 {Math.Sqrt(Math.Pow(holes[best.HoleB].Cx - holes[best.HoleA].Cx, 2) + Math.Pow(holes[best.HoleB].Cy - holes[best.HoleA].Cy, 2)):F1}px " +
            $"↔ 名义 {best.ChordMm:F2}mm → 反算 mm/px={best.MmPerPx:F6}" +
            $"（配置 {pxSize:F6}，偏差 {best.ScaleErr * 100:F1}%）| " +
            $"解出工件中心=({best.Cx:F1},{best.Cy:F1})px（可在画面外）| " +
            $"装卡转角={best.Theta:F2}°（粗对齐 {coarse:F1}°，偏差 {best.CoarseErr:F1}°）| " +
            $"第三孔校验：半径圈上 {extraOnRing} 个、角度吻合 {consistent} 个" +
            (consistent > 0 ? " ✅" : "（视野只有两孔时无此项）") +
            " | 中心/转角/缩放均由本帧图像解出，不依赖夹具";

        _log?.Info("[策略④] " + detail);
        if (best.CoarseErr > 20)
            _log?.Warn($"[策略④] 选中解与粗对齐角差 {best.CoarseErr:F1}° > 20°。" +
                       "请复核人工粗对齐角是否填错，否则可能选到 90° 等效解打错方位。");
        if (extraOnRing > 0 && consistent == 0)
            _log?.Warn("[策略④] 画面里还有圆落在基准孔半径圈上，但角度对不上模板 —— 可能是污点或配对错误，请人工看一眼图。");

        return new PartPose(
            Success: true,
            CenterPxX: best.Cx,
            CenterPxY: best.Cy,
            OuterRadiusPx: ringPx,
            DatumClockDeg: ClockAngle(holes[best.HoleA].Cx - best.Cx, holes[best.HoleA].Cy - best.Cy),
            AngleOffsetDeg: best.Theta,
            Confidence: confidence,
            Detail: detail);
    }

    /// <summary>
    /// 第三孔一致性：除 A、B 外，还有几个圆落在「以候选圆心为心、半径 R」的圈上，
    /// 且方位角与「模板角 + 装卡转角」吻合。这是排除镜像圆心的决定性判据。
    /// </summary>
    private static (int OnRing, int Consistent) CountConsistentExtraHoles(
        List<SingleFiducialStrategy.HoleResult> holes, int ia, int ib,
        double cx, double cy, double mmpp, double R, double theta, double[] angles)
    {
        double ringPx = R / mmpp;
        int onRing = 0, ok = 0;
        for (int i = 0; i < holes.Count; i++)
        {
            if (i == ia || i == ib) continue;
            double d = Math.Sqrt(Math.Pow(holes[i].Cx - cx, 2) + Math.Pow(holes[i].Cy - cy, 2));
            if (Math.Abs(d - ringPx) / ringPx > 0.08) continue;   // 不在半径圈上
            onRing++;
            double clock = ClockAngle(holes[i].Cx - cx, holes[i].Cy - cy);
            double err = angles.Min(a => Math.Abs(Norm180(clock - (a + theta))));
            if (err < 6) ok++;
        }
        return (onRing, ok);
    }

    private PartPose Fail(string why)
    {
        _log?.Warn("[策略④] " + why);
        return new PartPose(false, 0, 0, 0, 0, 0, 0, why);
    }

    /// <summary>时钟角：+Y=12H 为 0°，顺时针为正（与 DialPositioning 一致）。</summary>
    private static double ClockAngle(double dx, double dy)
        => ((90 - Math.Atan2(-dy, dx) * 180 / Math.PI) % 360 + 360) % 360;

    /// <summary>从 a 顺时针走到 b 的角度差，返回 0~360。</summary>
    private static double CwDiff(double a, double b) => ((b - a) % 360 + 360) % 360;

    /// <summary>归一化到 ±180°。</summary>
    private static double Norm180(double deg) => ((deg % 360) + 540) % 360 - 180;
}

// ==================================================================
// ③ 兜底：纯机械对刀（不用视觉）
// ==================================================================

/// <summary>
/// 纯机械对刀：夹具同时保证中心与转角，视觉不参与。
/// 用于视觉彻底不可用时的最低成本兜底 —— 精度完全取决于夹具与人工摆正。
/// </summary>
public sealed class ManualFixtureStrategy : IPositioningStrategy
{
    private readonly IAppLogger? _log;

    public ManualFixtureStrategy(IAppLogger? log = null) => _log = log;

    public string Id => StrategyIds.ManualFixture;
    public string DisplayName => "③ 纯机械对刀（无视觉，兜底）";
    public string Description =>
        "夹具同时保证中心与转角，跳过视觉定位直接按模板坐标下发。精度最差，仅在视觉方案全失效时用。";

    public IReadOnlyList<string> Prerequisites => new[]
    {
        "夹具用定位销/止口锁死工件中心，且重复定位精度满足孔位公差",
        "夹具/人工把工件按 12H 摆正到 ±0.5° 以内（18 孔不具 90° 对称，转角错了全废）",
        "已标定夹具中心像素 + 完成九点标定",
        "⚠ 无任何视觉复核，装夹错误不会被发现"
    };

    public StrategyReadiness Check(StrategyContext ctx)
    {
        var missing = new List<string>();
        if (!ctx.FixtureCentersPart) missing.Add("夹具未锁死中心");
        if (!ctx.CalibrationValid) missing.Add("九点标定未完成");
        if (!ctx.FiducialTaught) missing.Add("夹具中心像素未标定");
        if (missing.Count > 0)
            return StrategyReadiness.Block("缺 " + missing.Count + " 项：" + string.Join("；", missing) + "。");

        return StrategyReadiness.Warn(
            "可用，但**没有视觉复核**：装夹歪了、工件放反了都不会被发现。" +
            "18 个孔不具 90° 对称，粗对齐角必须准到 ±0.5°。");
    }

    public Task<PartPose> LocateAsync(CameraFrame frame, VisionOptions opt, CancellationToken ct = default)
    {
        if (double.IsNaN(opt.FixtureCenterPxX) || double.IsNaN(opt.FixtureCenterPxY))
            return Task.FromResult(new PartPose(false, 0, 0, 0, 0, 0, 0,
                "未标定夹具中心像素（FixtureCenterPxX/Y 为 NaN），纯机械对刀无法给出中心。"));

        string detail = $"纯机械对刀：夹具中心=({opt.FixtureCenterPxX:F1},{opt.FixtureCenterPxY:F1})px，" +
                        $"转角取人工假设值 {opt.FixtureAssumedAngleDeg:F2}°。" +
                        "⚠ 无视觉实测、无复核，装夹错误不会被发现。";
        _log?.Warn("[策略③] " + detail);

        return Task.FromResult(new PartPose(
            Success: true,
            CenterPxX: opt.FixtureCenterPxX,
            CenterPxY: opt.FixtureCenterPxY,
            OuterRadiusPx: 0,
            DatumClockDeg: opt.FixtureAssumedAngleDeg,
            AngleOffsetDeg: opt.FixtureAssumedAngleDeg,
            Confidence: 0.30,     // 故意压低：提醒这是保底方案
            Detail: detail));
    }
}
