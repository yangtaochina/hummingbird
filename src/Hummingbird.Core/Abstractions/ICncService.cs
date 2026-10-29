namespace Hummingbird.Core;

/// <summary>
/// 数控系统服务（SINUMERIK 828D，经 OPC UA）。
/// 职责：连接、状态读取、R参数写入、刻孔程序下发、轴位置订阅。
/// </summary>
public interface ICncService : IDisposable
{
    string Endpoint { get; }
    bool IsConnected { get; }

    Task<bool> ConnectAsync(string endpoint, CancellationToken ct = default);
    Task DisconnectAsync();

    Task<MachineStatus> GetStatusAsync(CancellationToken ct = default);

    /// <summary>写 R 参数（如 R1=25.3）。NC 参数化子程序可读取。</summary>
    Task<bool> WriteRAsync(int rNumber, double value, CancellationToken ct = default);

    /// <summary>下发刻孔程序（SPF 文本）到数控。</summary>
    Task<bool> UploadProgramAsync(string name, string content, CancellationToken ct = default);

    /// <summary>订阅轴位置（UI 实时显示用）。</summary>
    IDisposable SubscribePosition(Action<AxisPosition> onChange, int intervalMs = 200);
}
