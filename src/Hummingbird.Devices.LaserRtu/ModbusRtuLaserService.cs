using System.IO.Ports;
using Hummingbird.Core;
using NModbus;
using NModbus.Serial;

namespace Hummingbird.Devices.LaserRtu;

/// <summary>
/// 激光器控制（RS232 Modbus RTU，现场经 232转USB 接上位机）。
/// 寄存器表来自《激光器协议说明_V1.0》：功能码06写单寄存器。
/// 注意：功率设置走从站地址 09 / 寄存器 0x0172，其余走从站 01。
/// 波特率协议未给，默认115200，现场用激光器软件试出后改配置。
/// </summary>
public sealed class ModbusRtuLaserService : ILaserService
{
    public const ushort REG_CTRL_MODE = 0x0001;   // 控制模式：内控/外Gate/外Trigger
    public const ushort REG_POWER_MODE = 0x0002;  // 功率模式：内部/8bit/模拟量
    public const ushort REG_LASER_ON = 0x0007;    // 出光/关光
    public const ushort REG_POWER_ON = 0x000F;    // 开机/关机
    public const ushort REG_SAVE = 0x0013;        // 保存参数
    public const ushort REG_FREQ_KHZ = 0x001C;    // 重频 50~1000 KHz
    public const ushort REG_PULSES = 0x001D;      // 脉冲数 1~10
    public const ushort REG_POWER = 0x0172;       // 功率 0~10000（从站09！）
    public const byte SLAVE_MAIN = 1;
    public const byte SLAVE_POWER = 9;

    private SerialPort? _port;
    private IModbusSerialMaster? _master;
    private int _freqKhz = 200;
    private int _pulses = 5;

    public string DeviceName => "激光器（Modbus RTU/RS232）";
    public bool IsOpen => _port?.IsOpen == true;

    public Task OpenAsync(string comPort, int baudRate)
    {
        CloseAsync().GetAwaiter().GetResult();
        _port = new SerialPort(comPort, baudRate, Parity.None, 8, StopBits.One)
        { ReadTimeout = 800, WriteTimeout = 800, Handshake = Handshake.None };
        _port.Open();
        _master = new ModbusFactory().CreateRtuMaster(new SerialPortAdapter(_port));
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        _master?.Dispose(); _master = null;
        if (_port?.IsOpen == true) _port.Close();
        _port = null;
        return Task.CompletedTask;
    }

    private async Task WriteRegAsync(byte slave, ushort reg, ushort value)
    {
        if (_master is null) throw new InvalidOperationException("串口未打开");
        await _master.WriteSingleRegisterAsync(slave, reg, value);
    }

    public Task SetPowerAsync(int power)
        => WriteRegAsync(SLAVE_POWER, REG_POWER, (ushort)Math.Clamp(power, 0, 10000));

    public Task SetFrequencyKhzAsync(int khz)
    { _freqKhz = Math.Clamp(khz, 50, 1000); return WriteRegAsync(SLAVE_MAIN, REG_FREQ_KHZ, (ushort)_freqKhz); }

    public Task SetPulseCountAsync(int pulses)
    { _pulses = Math.Clamp(pulses, 1, 10); return WriteRegAsync(SLAVE_MAIN, REG_PULSES, (ushort)_pulses); }

    public Task LaserOnAsync() => WriteRegAsync(SLAVE_MAIN, REG_LASER_ON, 1);
    public Task LaserOffAsync() => WriteRegAsync(SLAVE_MAIN, REG_LASER_ON, 0);

    public async Task FireBurstAsync(CancellationToken ct = default)
    {
        await LaserOnAsync();
        // 脉冲串时长 = 脉冲数/重频，加余量；不足50ms按50ms算
        int ms = Math.Max(50, (int)(_pulses * 1000.0 / _freqKhz) + 20);
        await Task.Delay(ms, ct);
        await LaserOffAsync();
    }

    public void Dispose() => CloseAsync().GetAwaiter().GetResult();
}
