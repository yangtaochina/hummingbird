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
///   所以必须由**夹具锁死中心**（2 个自由度交给机械），视觉只求转角 θ（1 个自由度）。
///   此时：「夹具中心 → 孔心」的方向角 − 标记孔名义时钟角 = 装卡转角。
///   ⚠ 若标记孔不随工件转（固定在夹具上），则转角永远测不出，Check() 会直接拒绝。
///
/// 三个策略：
///   ① FullFieldDatumHolesStrategy：现役整盘视觉（包装 DialPositioning，零改动复用）
///   ② SingleFiducialStrategy：单标记孔 + 夹具锁心（客户新方案）
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
    /// 在（可能很小的）视野里找那个标记孔。
    /// 思路：Otsu 二值化 → 轮廓 → 按「圆度 + 尺寸 + 面积」打分选最佳。
    /// 同时兼容**亮孔**（孔反光成白点）和**暗孔**（孔是黑洞）：两种极性都试一遍。
    /// </summary>
    public static HoleResult? DetectFiducialHole(CameraFrame frame, VisionOptions opt, IAppLogger? log = null)
    {
        using var src = new Mat(frame.Height, frame.Width, MatType.CV_8UC1);
        Marshal.Copy(frame.Gray, 0, src.Data, frame.Gray.Length);

        using var blur = new Mat();
        Cv2.GaussianBlur(src, blur, new Size(5, 5), 0);

        Cv2.MinMaxIdx(blur, out double minGray, out double maxGray);
        double meanGray = Cv2.Mean(blur).Val0;
        if (maxGray < 20)
        {
            log?.Warn("[策略②] 画面全黑（最亮仅 " + maxGray.ToString("F0") + "/255），先解决照明/曝光");
            return null;
        }
        if (meanGray > 245 && maxGray - minGray < 12)
        {
            log?.Warn("[策略②] 画面整体过曝（全白无结构），把曝光降到 5000~8000μs");
            return null;
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
            log?.Warn($"[策略②] 二值化后没有符合圆形+尺寸的轮廓（期望孔径 {expectDiaPx:F1}px，允许 {minDia:F1}~{maxDia:F1}px）");
            return null;
        }

        // 取质量最高的；若画面里有多个圆，明确告警（视野应只框住一个孔）
        var best = candidates.OrderByDescending(p => p.Quality).First();
        if (candidates.Count > 3)
            log?.Warn($"[策略②] 画面检出 {candidates.Count} 个圆形候选，已取质量最高者。" +
                      "建议把视野再缩小、只框住一个标记孔，避免误检。");
        return best;
    }
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
