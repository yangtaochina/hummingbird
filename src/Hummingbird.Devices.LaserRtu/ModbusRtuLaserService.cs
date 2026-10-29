using System.IO.Ports;
using Hummingbird.Core;
using NModbus;
using NModbus.Serial;

namespace Hummingbird.Devices.LaserRtu;

/// <summary>
/// 激光器控制（RS232 Modbus RTU，现场经 232转USB 接上位机串口）。
///
/// 协议要点（来自《激光器协议说明_V1.0》）：
///   - 功能码 06（写单寄存器），帧格式：[从站地址][06][寄存器地址][数据][CRC16]
///   - 大部分寄存器走从站地址 01；唯独"功率"走从站 09 / 寄存器 0x0172（厂家如此定义，别改错）
///   - 波特率协议文档未给出，默认 115200；现场用激光器自带软件试出后改 config.json
///
/// ⚠️ 安全提醒：LaserOnAsync 之后激光真实出光！调试时务必先降功率+遮挡光路。
/// </summary>
public sealed class ModbusRtuLaserService : ILaserService
{
    // ---- 寄存器表（协议 V1.0）----
    public const ushort REG_CTRL_MODE = 0x0001;   // 控制模式：内控/外Gate/外Trigger
    public const ushort REG_POWER_MODE = 0x0002;  // 功率模式：内部/8bit/模拟量
    public const ushort REG_LASER_ON = 0x0007;    // 出光/关光（1/0）
    public const ushort REG_POWER_ON = 0x000F;    // 整机开机/关机
    public const ushort REG_SAVE = 0x0013;        // 保存参数到Flash
    public const ushort REG_FREQ_KHZ = 0x001C;    // 重频 50~1000 KHz
    public const ushort REG_PULSES = 0x001D;      // 单串脉冲数 1~10
    public const ushort REG_POWER = 0x0172;       // 功率 0~10000（⚠ 从站09！）
    public const byte SLAVE_MAIN = 1;             // 常规寄存器从站地址
    public const byte SLAVE_POWER = 9;            // 功率寄存器从站地址

    private readonly IAppLogger _log;
    private SerialPort? _port;
    private IModbusSerialMaster? _master;
    private int _freqKhz = 200;   // 记住当前参数，FireBurst 计算脉冲串时长要用
    private int _pulses = 5;

    public ModbusRtuLaserService(IAppLogger? log = null)
    {
        _log = log ?? new SilentLogger();
    }

    public string DeviceName => "激光器（Modbus RTU/RS232）";
    public bool IsOpen => _port?.IsOpen == true;

    /// <summary>打开串口并建 Modbus 主站。串口被占用/不存在会抛异常（上层捕获记日志）。</summary>
    public Task OpenAsync(string comPort, int baudRate)
    {
        CloseAsync().GetAwaiter().GetResult();   // 先关旧的，保证可重复调用
        _log.Info($"[激光] 打开串口 {comPort}@{baudRate} 8N1 ...");
        _port = new SerialPort(comPort, baudRate, Parity.None, 8, StopBits.One)
        { ReadTimeout = 800, WriteTimeout = 800, Handshake = Handshake.None };
        _port.Open();
        _master = new ModbusFactory().CreateRtuMaster(new SerialPortAdapter(_port));
        _log.Info("[激光] 串口已打开（Modbus RTU 主站就绪）");
        return Task.CompletedTask;
    }

    /// <summary>关闭串口（先释放主站再关口，顺序反了会卡）。</summary>
    public Task CloseAsync()
    {
        _master?.Dispose(); _master = null;
        if (_port?.IsOpen == true) _port.Close();
        _port = null;
        return Task.CompletedTask;
    }

    /// <summary>
    /// 写单个寄存器（功能码06）。所有控制动作的底层出口。
    /// 写失败（超时/CRC错/异常码）会抛 Modbus 异常，上层捕获后能从日志看出是哪条指令失败。
    /// </summary>
    private async Task WriteRegAsync(byte slave, ushort reg, ushort value)
    {
        if (_master is null) throw new InvalidOperationException("[激光] 串口未打开（请先\"连接设备\"）");
        await _master.WriteSingleRegisterAsync(slave, reg, value);
    }

    /// <summary>设定功率 0~10000。⚠ 走从站09/寄存器0x0172，与其他指令不同。</summary>
    public Task SetPowerAsync(int power)
    {
        int p = Math.Clamp(power, 0, 10000);
        _log.Info($"[激光] 设功率 {p}/10000（从站{SLAVE_POWER} 寄存器0x{REG_POWER:X4}）");
        return WriteRegAsync(SLAVE_POWER, REG_POWER, (ushort)p);
    }

    /// <summary>设定重频 50~1000 kHz（决定单孔脉冲间隔）。</summary>
    public Task SetFrequencyKhzAsync(int khz)
    {
        _freqKhz = Math.Clamp(khz, 50, 1000);
        _log.Info($"[激光] 设重频 {_freqKhz} kHz");
        return WriteRegAsync(SLAVE_MAIN, REG_FREQ_KHZ, (ushort)_freqKhz);
    }

    /// <summary>设定单串脉冲数 1~10（打一个孔用几发脉冲）。</summary>
    public Task SetPulseCountAsync(int pulses)
    {
        _pulses = Math.Clamp(pulses, 1, 10);
        _log.Info($"[激光] 设脉冲数 {_pulses}");
        return WriteRegAsync(SLAVE_MAIN, REG_PULSES, (ushort)_pulses);
    }

    /// <summary>出光（寄存器0x0007=1）。⚠ 真实出光！</summary>
    public Task LaserOnAsync()
    {
        _log.Warn("[激光] 出光 ON");
        return WriteRegAsync(SLAVE_MAIN, REG_LASER_ON, 1);
    }

    /// <summary>关光（寄存器0x0007=0）。</summary>
    public Task LaserOffAsync()
    {
        _log.Info("[激光] 出光 OFF");
        return WriteRegAsync(SLAVE_MAIN, REG_LASER_ON, 0);
    }

    /// <summary>
    /// 打一个孔的完整动作：出光 → 等脉冲串打完 → 关光。
    /// 等待时长 = 脉冲数/重频 + 20ms 余量，最少 50ms。
    /// 例：5 脉冲 @ 200kHz = 25μs 的光，但串口/电路响应有余量，取 50ms 稳妥。
    /// </summary>
    public async Task FireBurstAsync(CancellationToken ct = default)
    {
        await LaserOnAsync();
        int ms = Math.Max(50, (int)(_pulses * 1000.0 / _freqKhz) + 20);
        await Task.Delay(ms, ct);
        await LaserOffAsync();
    }

    public void Dispose() => CloseAsync().GetAwaiter().GetResult();

    /// <summary>静默日志器：调用方不传 logger 时用（单元测试/SmokeTest）。</summary>
    private sealed class SilentLogger : IAppLogger
    {
        public void Info(string m) { }
        public void Warn(string m) { }
        public void Error(string m) { }
#pragma warning disable CS0067
        public event Action<string, string>? LineAdded;
#pragma warning restore CS0067
    }
}
