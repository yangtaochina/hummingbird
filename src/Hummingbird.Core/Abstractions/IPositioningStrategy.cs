namespace Hummingbird.Core;

/// <summary>
/// 定位策略 ID 常量。界面下拉框、config.json、日志统一用这些字符串。
/// 新增策略时：① 这里加常量 ② 在 Vision 层实现 IPositioningStrategy
/// ③ 在 PositioningStrategySelector 注册 —— 界面与加工流程无需改动。
/// </summary>
public static class StrategyIds
{
    /// <summary>现役：整盘入画 + 8 个工件自带双环孔组定角（DatumHoles 三遍定位）。</summary>
    public const string FullFieldDatumHoles = "fullFieldDatumHoles";

    /// <summary>
    /// 单标记孔方案（2026-11-05 客户提议）：相机视野只看**一个**客户指定的标记孔，
    /// 工件中心由夹具定位销/止口锁死，用「孔心相对夹具中心的方位角变化」求装卡转角，
    /// 其余 18 孔由 DWG 模板几何 + 中心 + 转角算出来。
    /// </summary>
    public const string SingleFiducial = "singleFiducial";

    /// <summary>
    /// 多孔自主定位（2026-11-05 现场照片复核后新增）：相机视野同时看 3 个工件自带的双环基准孔，
    /// 由「孔间距 ↔ 图纸名义弦长」反算 mm/px，由弦中垂线 + 已知半径解出工件中心，
    /// 再由孔心方位角求转角，第 3 个孔用来消掉孔组 180° 对称造成的镜像圆心二义性。
    /// **中心、转角、缩放全部由图像实时解出，夹具不需要锁任何东西**。
    /// 适用：客户现有托板只是随手摆放、既无定位销也无角向基准（照片已证实）。
    /// ID 沿用 twoFiducials 仅为兼容已写出的 config.json，实际推荐 3 孔入画。
    /// </summary>
    public const string TwoFiducials = "twoFiducials";

    /// <summary>兜底：纯机械对刀，不用视觉。中心与转角全部由夹具/人工粗对齐角给定。</summary>
    public const string ManualFixture = "manualFixture";
}

/// <summary>
/// 现场条件快照：界面在切换策略 / 点「策略自检」时组装，交给各策略判断自己能不能用。
/// 这些全是"硬前提"，不是偏好 —— 前提不满足时策略会明确拒绝执行，避免现场误用。
/// </summary>
/// <param name="FixtureCentersPart">夹具是否能把工件中心重复锁死（定位销/止口）。</param>
/// <param name="FiducialAvailable">工件/夹具上是否有一个相机能看清的标记孔。</param>
/// <param name="FiducialRotatesWithPart">标记孔是否随工件一起转动（固定在夹具上则不能定角）。</param>
/// <param name="FullPartInView">当前视野是否能装下整个工件（短边 ≥ 外径）。</param>
/// <param name="DatumHolesVisible">8 个双环基准孔在画面里是否真的看得见。</param>
/// <param name="CalibrationValid">九点标定是否有效。</param>
/// <param name="FiducialTaught">单标记孔策略的示教值（夹具中心像素 + 零位孔心）是否已录入。</param>
/// <param name="CoarseAlignProvided">是否提供了人工粗对齐角（消解 90° 对称多解）。</param>
/// <param name="TwoHolesInView">当前视野能否同时看清 **3 个相邻**基准孔（多孔策略的唯一视野前提，约需 6×20mm）。</param>
public sealed record StrategyContext(
    bool FixtureCentersPart,
    bool FiducialAvailable,
    bool FiducialRotatesWithPart,
    bool FullPartInView,
    bool DatumHolesVisible,
    bool CalibrationValid,
    bool FiducialTaught,
    bool CoarseAlignProvided,
    bool TwoHolesInView = true)
{
    /// <summary>默认：按 2026-11-05 真机诊断的最坏情况（整盘不入画、基准孔不可见、无夹具信息）。</summary>
    public static StrategyContext WorstCase => new(
        FixtureCentersPart: false,
        FiducialAvailable: false,
        FiducialRotatesWithPart: false,
        FullPartInView: false,
        DatumHolesVisible: false,
        CalibrationValid: false,
        FiducialTaught: false,
        CoarseAlignProvided: false,
        TwoHolesInView: false);
}

/// <summary>策略可用性判定结果。</summary>
/// <param name="CanRun">false = 硬前提缺失，界面应禁用该策略。</param>
/// <param name="Level">OK / WARN / BLOCK 三档，界面用来上色。</param>
/// <param name="Reason">人话：为什么能用 / 缺什么，直接显示给现场人员。</param>
public sealed record StrategyReadiness(bool CanRun, string Level, string Reason)
{
    public static StrategyReadiness Ok(string why) => new(true, "OK", why);
    public static StrategyReadiness Warn(string why) => new(true, "WARN", why);
    public static StrategyReadiness Block(string why) => new(false, "BLOCK", why);
}

/// <summary>
/// 定位策略：在「现役整盘视觉定位」与「单标记孔 + 夹具锁心」等方案之间切换。
///
/// 继承 IVisionService 是为了让 AutoRunWorkflow 零改动 ——
/// 它只知道"有个东西能 LocateAsync"，不知道背后用的是哪种策略。
/// 界面只负责换 Vision 属性指向的策略实例。
/// </summary>
public interface IPositioningStrategy : IVisionService
{
    /// <summary>稳定 ID（写入 config.json，见 StrategyIds）。</summary>
    string Id { get; }

    /// <summary>界面下拉框显示名。</summary>
    string DisplayName { get; }

    /// <summary>一句话说明原理，界面选中时展示。</summary>
    string Description { get; }

    /// <summary>现场硬前提清单（每条一行），界面展示给操作工确认。</summary>
    IReadOnlyList<string> Prerequisites { get; }

    /// <summary>按当前现场条件判断本策略能否执行。</summary>
    StrategyReadiness Check(StrategyContext ctx);
}
