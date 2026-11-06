namespace Hummingbird.Core;

/// <summary>静默日志器：单元测试 / 离线工具不传 logger 时用，避免日志器实现散落在各设备工程里。</summary>
public sealed class NoOpLogger : IAppLogger
{
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message) { }
#pragma warning disable CS0067
    public event Action<string, string>? LineAdded;
#pragma warning restore CS0067
}

/// <summary>
/// 转台（机床回转工作台）的三种工作方式。
/// 关键设计：**转台控制与 OPC UA 网络解耦** —— 现场 OPC UA 还没打通时，
/// 用 ManualEntry（人工转盘 + 界面抄角度）照样能采集多帧做定位，不会被网络卡死。
/// </summary>
public enum RotaryTableMode
{
    /// <summary>人工转动转台，操作工从 HMI 抄实际角度填进界面。不依赖任何通讯。</summary>
    ManualEntry = 0,

    /// <summary>离线仿真：程序自己按速度模拟转到目标角，用于开发/测试。</summary>
    Simulated = 1,

    /// <summary>
    /// OPC UA 自动：写 R 参数传目标角 + 写 PLC 触发位，由机床侧预置的 NC 子程序执行转位，
    /// 再读回实际角度/完成信号确认到位。所有节点路径现场用 UaExpert 核实后填 config.json。
    /// </summary>
    OpcUaRPlc = 2
}

/// <summary>转台动作结果。</summary>
/// <param name="Success">是否确认到位。</param>
/// <param name="ActualAngleDeg">读回的实际角度（人工模式为操作工抄入的值；读不到为 NaN）。</param>
/// <param name="Detail">人话结论，直接显示/写日志。</param>
public sealed record RotaryMoveResult(bool Success, double ActualAngleDeg, string Detail);

/// <summary>
/// 转台参数（Core 层，不依赖 App/具体设备）。
/// ⚠ 所有 OPC UA 节点路径默认**留空**：绝不预设臆造的 NodeId，
/// 必须在现场用 UaExpert 展开确认后填入，否则服务会明确拒绝动作。
/// </summary>
public sealed class RotaryTableOptions
{
    /// <summary>数控轴名（如 C / A / SP）。仅用于日志与现场核实，不参与寻址。</summary>
    public string AxisName { get; set; } = "C";

    /// <summary>传目标角用的 R 参数号。默认 R90，需机床侧子程序读同一个 R 参数。</summary>
    public int TargetRNumber { get; set; } = 90;

    /// <summary>目标角写入的 R 参数节点（留空则用 /Channel/Parameter/R[u1, n] 标准路径）。</summary>
    public string TargetRNode { get; set; } = "";

    /// <summary>PLC 触发位节点（写 true 启动转位，写 false 复位）。必须现场核实。</summary>
    public string TriggerNode { get; set; } = "";

    /// <summary>PLC 到位完成位节点（可选；配了就优先用它判到位）。</summary>
    public string DoneNode { get; set; } = "";

    /// <summary>转台实际角度的 OPC UA 节点（如 C 轴实际位置）。用于读回与到位校验。</summary>
    public string ActualAngleNode { get; set; } = "";

    /// <summary>角度归一化：true = 目标角归一到 0~360；false = 允许多圈累计。</summary>
    public bool Normalize360 { get; set; } = true;

    /// <summary>允许的最小角度（度）。</summary>
    public double MinAngleDeg { get; set; } = -3600;

    /// <summary>允许的最大角度（度）。</summary>
    public double MaxAngleDeg { get; set; } = 3600;

    /// <summary>单次最大转角（度）。防误输入 3000° 之类导致转台飞车，默认 90°。</summary>
    public double MaxStepDeg { get; set; } = 90;

    /// <summary>到位容差（度）。</summary>
    public double ToleranceDeg { get; set; } = 0.05;

    /// <summary>等待到位超时（毫秒）。</summary>
    public int TimeoutMs { get; set; } = 15000;

    /// <summary>轮询间隔（毫秒）。</summary>
    public int PollIntervalMs { get; set; } = 200;

    /// <summary>仿真模式角速度（度/秒）。</summary>
    public double SimSpeedDegPerSec { get; set; } = 30;
}

/// <summary>
/// SINUMERIK 变量读写能力（由 OpcUaCncService 实现，转台服务通过它复用同一个 OPC UA 会话）。
/// 单独抽成接口是为了让转台服务能用假实现做离线测试，不依赖真机床。
/// </summary>
public interface ISinumerikVarAccess
{
    bool IsConnected { get; }
    Task<bool> WriteVarAsync(string nodePath, object value, CancellationToken ct = default);
    Task<object?> ReadVarAsync(string nodePath, CancellationToken ct = default);
}

/// <summary>
/// 转台服务。上层（多帧定位/分度打孔）只依赖这个接口，
/// 不关心是人工转盘、仿真还是 OPC UA 自动 —— 现场条件变了换实现即可。
/// </summary>
public interface IRotaryTableService
{
    RotaryTableMode Mode { get; }
    double CurrentAngleDeg { get; }
    bool IsReady { get; }
    string ReadinessDetail { get; }
    RotaryTableOptions Options { get; }

    /// <summary>转到目标角并等待确认到位。</summary>
    Task<RotaryMoveResult> MoveToAsync(double targetDeg, CancellationToken ct = default);

    /// <summary>人工模式专用：操作工已把转台转到某角度，把该值告知程序。</summary>
    Task<bool> SetAngleAsync(double actualDeg, CancellationToken ct = default);

    /// <summary>读当前实际角度（人工模式返回操作工抄入值；读不到返回 null）。</summary>
    Task<double?> ReadAngleAsync(CancellationToken ct = default);
}

// ======================================================================
// OPC UA 现场只读探测（2026-11-06：客户确认 OPC UA 已联通后新增）
// ======================================================================

/// <summary>
/// OPC UA 现场探测报告。
/// ⚠ 探测全程**只读**：只读取变量值、节点 AccessLevel/UserAccessLevel 属性和地址空间目录，
/// 绝不写任何机床变量、绝不触发任何运动。报告文本可直接存档/发回开发侧定死节点路径。
/// </summary>
/// <param name="Connected">探测时 OPC UA 会话是否可用。</param>
/// <param name="Endpoint">探测所用的服务器端点。</param>
/// <param name="Text">中文报告全文（含结论与下一步）。</param>
/// <param name="CandidateAngleNodes">扫描命中的、可能是转台实际角度的节点路径列表。</param>
public sealed record OpcUaProbeReport(
    bool Connected,
    string Endpoint,
    string Text,
    IReadOnlyList<string> CandidateAngleNodes);

/// <summary>
/// SINUMERIK 变量只读探测能力（由 OpcUaCncService 实现，复用同一个 OPC UA 会话）。
/// 抽成接口是为了让界面/测试不依赖具体设备实现。
/// </summary>
public interface ISinumerikVarProbe
{
    /// <summary>
    /// 只读探测转台自动分度所需的前置条件：命名空间、目标角 R 参数及其写权限、
    /// 轴实际位置候选节点、PLC 目录。要求调用前已建立会话。
    /// </summary>
    /// <param name="targetRNumber">机床侧预置子程序约定读取的 R 参数号（默认 R90）。</param>
    Task<OpcUaProbeReport> ProbeRotaryAsync(int targetRNumber, CancellationToken ct = default);
}
