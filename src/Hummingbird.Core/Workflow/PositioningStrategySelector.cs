namespace Hummingbird.Core;

/// <summary>
/// 策略选择器：界面用它做两件事
///   ① 列出全部策略 + 每个策略在当前现场条件下「能不能用、缺什么」
///   ② 自动推荐一个（按优先级：整盘 → 多孔自主 → 单孔+夹具锁心 → 纯机械对刀）
///
/// 放在 Core 层、只依赖 IPositioningStrategy 抽象：
/// 将来加新策略，只要注册进构造器，推荐逻辑与界面都不用改。
/// </summary>
public sealed class PositioningStrategySelector
{
    private readonly IReadOnlyList<IPositioningStrategy> _strategies;

    public PositioningStrategySelector(IEnumerable<IPositioningStrategy> strategies)
    {
        _strategies = strategies.ToArray();
        if (_strategies.Count == 0)
            throw new ArgumentException("至少要注册一个定位策略", nameof(strategies));
    }

    /// <summary>全部策略（界面下拉框数据源，顺序 = 显示顺序）。</summary>
    public IReadOnlyList<IPositioningStrategy> All => _strategies;

    /// <summary>按 ID 取策略；找不到返回 null（配置文件里写了旧 ID 时不会崩）。</summary>
    public IPositioningStrategy? Find(string id)
        => _strategies.FirstOrDefault(s => string.Equals(s.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 自动推荐：不是"看哪个策略自己说能用"，而是按**工程优先级**挑。
    /// 理由：整盘定位精度最高（中心+转角都由视觉实测）；单标记孔把中心交给夹具，
    /// 精度受夹具重复定位限制；纯机械对刀最差（连转角都靠人工）。
    /// 所以只有在高优先级方案硬前提不满足时才降级。
    /// </summary>
    public (IPositioningStrategy? Strategy, string Reason) Recommend(StrategyContext ctx)
    {
        // 优先级 1：整盘视野 + 基准孔可见 + 已标定 → 现役方案，精度最好
        var full = Find(StrategyIds.FullFieldDatumHoles);
        if (full is not null && full.Check(ctx).CanRun)
            return (full, "整盘能入画且 8 个基准孔可见 → 用现役整盘视觉定位，中心与转角都由视觉实测，精度最高。");

        // 优先级 2：整盘不行，但视野能同时看清 3 个相邻孔 → 多孔自主定位。
        // 排在单标记孔之前，是因为它的中心与转角都由视觉实测，
        // 不把精度交给夹具重复定位 —— 现场照片已证实该夹具没有定位销。
        var two = Find(StrategyIds.TwoFiducials);
        if (two is not null && two.Check(ctx).CanRun)
            return (two,
                "整盘视野/基准孔不可用 → 改用多孔自主定位：相机同时看 3 个工件自带的双环孔，" +
                "由孔间距反算 mm/px、由弦中垂线解中心、由孔心方位角求转角，第三孔消掉镜像二义性。" +
                "中心与转角都实时实测，**不需要夹具锁心或锁角**，正适合现场现有托板随手摆放的情况。");

        // 优先级 3：夹具能锁心 + 有随工件转的标记孔 + 已示教 → 客户原提议的单孔方案
        var fiducial = Find(StrategyIds.SingleFiducial);
        if (fiducial is not null && fiducial.Check(ctx).CanRun)
            return (fiducial,
                "整盘与多孔都不可用，但夹具能锁死中心且有标记孔 → 改用单标记孔方案：" +
                "相机只测一个孔的方位角求转角，中心由夹具保证，其余孔由模板几何算出。");

        // 优先级 4：什么视觉特征都没有 → 纯机械对刀兜底（需人工保证 ±0.5°）
        var manual = Find(StrategyIds.ManualFixture);
        if (manual is not null && manual.Check(ctx).CanRun)
            return (manual,
                "当前没有任何可用视觉特征 → 只剩纯机械对刀：中心与转角全靠夹具/人工粗对齐角。" +
                "⚠ 精度最差，须确认夹具重复定位精度与粗对齐角准到 ±0.5°。");

        return (null, "所有策略的硬前提都未满足。至少要先完成：九点标定 + 换白光让孔可见 + 填粗对齐角。" +
                      "详见下方每个策略的缺口说明。");
    }

    /// <summary>界面「策略自检」用：把每个策略的可用性拼成人话清单。</summary>
    public string DescribeAll(StrategyContext ctx)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("定位策略自检（依据当前现场条件）");
        sb.AppendLine(new string('-', 60));
        foreach (var s in _strategies)
        {
            var r = s.Check(ctx);
            string mark = r.Level switch { "OK" => "[可用]", "WARN" => "[可用·有限制]", _ => "[不可用]" };
            sb.AppendLine($"{mark} {s.DisplayName}");
            sb.AppendLine($"       {r.Reason}");
        }
        var (rec, why) = Recommend(ctx);
        sb.AppendLine(new string('-', 60));
        sb.AppendLine(rec is null
            ? "推荐：暂无可用策略"
            : $"推荐：{rec.DisplayName}");
        sb.AppendLine(why);
        return sb.ToString();
    }
}
