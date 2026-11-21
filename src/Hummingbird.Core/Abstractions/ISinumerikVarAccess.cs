namespace Hummingbird.Core;

/// <summary>
/// SINUMERIK 变量读写能力（由 OpcUaCncService 实现，流程编排通过它复用同一个 OPC UA 会话）。
/// 单独抽成接口是为了能用假实现做离线测试，不依赖真机床。
/// </summary>
public interface ISinumerikVarAccess
{
    bool IsConnected { get; }
    Task<bool> WriteVarAsync(string nodePath, object value, CancellationToken ct = default);
    Task<object?> ReadVarAsync(string nodePath, CancellationToken ct = default);
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
