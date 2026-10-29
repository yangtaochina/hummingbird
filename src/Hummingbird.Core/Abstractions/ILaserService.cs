namespace Hummingbird.Core;

/// <summary>
/// 激光器服务（脉冲光纤激光器，RS232 Modbus RTU）。
/// 寄存器语义见《激光器协议说明_V1.0》。
/// </summary>
public interface ILaserService : IDisposable
{
    string DeviceName { get; }
    bool IsOpen { get; }

    Task OpenAsync(string comPort, int baudRate);
    Task CloseAsync();

    Task SetPowerAsync(int power);         // 0..10000（从站09，寄存器0x0172）
    Task SetFrequencyKhzAsync(int khz);    // 50..1000 KHz（0x001C）
    Task SetPulseCountAsync(int pulses);   // 1..10（0x001D）
    Task LaserOnAsync();                   // 出光（0x0007=1）
    Task LaserOffAsync();                  // 关光（0x0007=0）

    /// <summary>按当前"重频+脉冲数"打一串脉冲后关光（打一个孔的完整动作）。</summary>
    Task FireBurstAsync(CancellationToken ct = default);
}
