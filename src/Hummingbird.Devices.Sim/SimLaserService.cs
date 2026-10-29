using Hummingbird.Core;

namespace Hummingbird.Devices.Sim;

/// <summary>模拟激光器：不出真光，只记录动作轨迹。</summary>
public sealed class SimLaserService : ILaserService
{
    private readonly Action<string> _trace;

    public SimLaserService(Action<string>? trace = null) { _trace = trace ?? (_ => { }); }

    public string DeviceName => "模拟激光器（Modbus RTU）";
    public bool IsOpen { get; private set; }
    public int Power { get; private set; } = 6000;
    public int FrequencyKhz { get; private set; } = 200;
    public int PulseCount { get; private set; } = 5;
    public int FireCount { get; private set; }

    public Task OpenAsync(string comPort, int baudRate)
    { IsOpen = true; _trace($"激光器串口打开（模拟）：{comPort}@{baudRate}"); return Task.CompletedTask; }

    public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }

    public Task SetPowerAsync(int power) { Power = Math.Clamp(power, 0, 10000); _trace($"功率={Power}/10000"); return Task.CompletedTask; }
    public Task SetFrequencyKhzAsync(int khz) { FrequencyKhz = Math.Clamp(khz, 50, 1000); _trace($"重频={FrequencyKhz}kHz"); return Task.CompletedTask; }
    public Task SetPulseCountAsync(int p) { PulseCount = Math.Clamp(p, 1, 10); _trace($"脉冲数={PulseCount}"); return Task.CompletedTask; }
    public Task LaserOnAsync() { _trace("出光 ON"); return Task.CompletedTask; }
    public Task LaserOffAsync() { _trace("出光 OFF"); return Task.CompletedTask; }

    public async Task FireBurstAsync(CancellationToken ct = default)
    {
        FireCount++;
        _trace($"[打孔] 功率{Power}/10000 {FrequencyKhz}kHz×{PulseCount}脉冲");
        await Task.Delay(80, ct);
    }

    public void Dispose() { }
}
