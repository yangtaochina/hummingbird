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
    public const ushort REG_POWER = 0x0172;       // 功率原始值 0~10000，不代表百分比（⚠ 从站09！）
    public const byte SLAVE_MAIN = 1;             // 常规寄存器从站地址
    public const byte SLAVE_POWER = 9;            // 功率寄存器从站地址

    private readonly IAppLogger _log;
    private SerialPort? _port;
    private IModbusSerialMaster? _master;
    private int _freqKhz = 200;   // 记住当前参数，FireBurst 计算脉冲串时长要用
    private int _pulses = 5;
    private readonly SemaphoreSlim _io = new(1, 1);
    private bool _legacyVerified;
    private ushort? _confirmedEmissionRegister;
    private readonly SemaphoreSlim _probe = new(1, 1);
    public LaserCalibrationController Calibration { get; }

    public ModbusRtuLaserService(IAppLogger? log = null)
    {
        _log = log ?? new SilentLogger();
        Calibration = new LaserCalibrationController(
            async reg => (await ReadRegsAsync(1, reg, 1))[0],
            (reg, value) => WriteRawAsync(1, reg, value), m => _log.Info("[激光标定] " + m),
            async () => (await ReadRegsAsync(SLAVE_POWER, REG_POWER, 1))[0]);
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
        if (Calibration.IsRunning) throw new InvalidOperationException("定时试打中，请先停止并等待关光确认。");
        if (!Calibration.ShutdownConfirmed) throw new InvalidOperationException("设备关光尚未确认，请先使用设备自身关光并只读核实状态。");
        Calibration.Reset();
        _legacyVerified = false;
        _confirmedEmissionRegister = null;
        _master?.Dispose(); _master = null;
        if (_port?.IsOpen == true) _port.Close();
        _port = null;
        return Task.CompletedTask;
    }

    /// <summary>写单个寄存器（功能码06）。所有控制动作的底层出口。
    /// 写失败（超时/CRC错/异常码）会抛 Modbus 异常，上层捕获后能从日志看出是哪条指令失败。</summary>
    private async Task WriteRegAsync(byte slave, ushort reg, ushort value)
    {
        if (Calibration.IsRunning) throw new InvalidOperationException("定时试打中，不能同时更改激光参数或开光。");
        if (!_legacyVerified) throw new InvalidOperationException("旧协议写指令未确认。请先做只读测试；试片测试请使用定时试打功能。");
        await WriteRawAsync(slave, reg, value);
    }

    private async Task WriteRawAsync(byte slave, ushort reg, ushort value)
    {
        await _io.WaitAsync();
        try
        {
            if (_master is null) throw new InvalidOperationException("[激光] 串口未打开（请先\"连接设备\"）");
            await _master.WriteSingleRegisterAsync(slave, reg, value);
        }
        finally { _io.Release(); }
    }

    // ------------------------------------------------------------------
    // 连接测试（只读，绝不出光）
    // ------------------------------------------------------------------

    /// <summary>读保持寄存器（功能码03）。仅诊断用：读任何寄存器都不会让激光出光。</summary>
    public async Task<ushort[]> ReadRegsAsync(byte slave, ushort start, ushort count)
    {
        await _io.WaitAsync();
        try
        {
            if (_master is null) throw new InvalidOperationException("[激光] 串口未打开");
            return await _master.ReadHoldingRegistersAsync(slave, start, count);
        }
        finally { _io.Release(); }
    }

    /// <summary>
    /// 分别读取PDF和旧Excel协议已定义的参数，验证完整参数范围。
    /// 不读PDF中的只写寄存器；异常应答不能视为协议命中。整个过程不写设备。
    /// </summary>
    public async Task<string> ProbeAsync()
    {
        if (!await _probe.WaitAsync(0)) throw new InvalidOperationException("只读测试正在进行，请等待完成。");
        try
        {
            if (Calibration.IsRunning) throw new InvalidOperationException("试打中不能重新探测协议。");
            var order = _legacyVerified
                ? new[] { LaserCalibrationProtocol.ExcelLegacy, LaserCalibrationProtocol.Pdf }
                : new[] { LaserCalibrationProtocol.Pdf, LaserCalibrationProtocol.ExcelLegacy };
            _legacyVerified = false;
            foreach (var protocol in order)
            {
                try
                {
                    Calibration.SelectProtocol(protocol);
                    var state = await Calibration.ReadStateAsync();
                    _legacyVerified = state.IsLegacy;
                    _confirmedEmissionRegister = Calibration.EmissionRegister;
                    _log.Info("[激光] " + state.Description);
                    return state.Description + "；支持定时试片标定，请核对读回值。";
                }
                catch (Exception ex) { _log.Info($"[激光] {protocol}协议未确认：{ex.Message}"); }
            }
            return "";
        }
        finally { _probe.Release(); }
    }

    public async Task<LaserCalibrationState> ReadRecordedProtocolAsync()
    {
        if (!await _probe.WaitAsync(0)) throw new InvalidOperationException("激光参数正在读取。");
        try
        {
            Calibration.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy);
            _legacyVerified = false;
            var state = await Calibration.ReadStateAsync();
            _legacyVerified = true; _confirmedEmissionRegister = Calibration.EmissionRegister;
            return state;
        }
        finally { _probe.Release(); }
    }
    public async Task VerifyEmissionOffAsync(CancellationToken ct=default)
    {
        ct.ThrowIfCancellationRequested();
        if(!IsOpen || _confirmedEmissionRegister is not { } register)throw new InvalidOperationException("请先连接激光器并读回已确认协议。");
        if((await ReadRegsAsync(SLAVE_MAIN,register,1))[0]!=0)throw new IOException("激光关光读回不为0，拒绝移动。");
        ct.ThrowIfCancellationRequested();
    }

    public async Task<LaserCalibrationState> RestoreRecordedPresetAsync(LaserProcessPreset preset)
    {
        if (preset.Settings.Protocol != LaserCalibrationProtocol.ExcelLegacy || !_legacyVerified)
            throw new InvalidOperationException("先自动读回已记录的旧协议，才能恢复预设。");
        return await Calibration.RestorePresetAsync(preset, async () =>
        {
            await WriteRawAsync(SLAVE_MAIN, REG_CTRL_MODE, preset.Settings.ControlMode);
            await WriteRawAsync(SLAVE_MAIN, REG_POWER_MODE, preset.Settings.PowerMode);
            await WriteRawAsync(SLAVE_MAIN, REG_FREQ_KHZ, preset.Settings.FrequencyKhz);
            await WriteRawAsync(SLAVE_MAIN, REG_PULSES, preset.Settings.BurstCount);
            _log.Info($"[激光参数] 写入功率设定值{preset.Settings.Power}：从站{SLAVE_POWER}，寄存器0x{REG_POWER:X4}，功能码06。");
            await WriteRawAsync(SLAVE_POWER, REG_POWER, preset.Settings.Power);
            _log.Info("[激光参数] 功率写入收到应答，随后独立读回核对；应答不代表实际功率已确认。");
        });
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
        if (_confirmedEmissionRegister is { } emissionRegister)
        {
            Calibration.RequestStop();
            return WriteRawAsync(1, emissionRegister, 0);
        }
        return WriteRegAsync(SLAVE_MAIN, REG_LASER_ON, 0);
    }

    /// <summary>
    /// 旧协议短时开关光兼容入口（50ms软件等待），并非硬件单脉冲触发。
    /// 不能由脉冲串个数推算总出光时长；工艺参数仍需实机验证。
    /// </summary>
    public async Task FireBurstAsync(CancellationToken ct = default)
    {
        // 此旧入口不是单脉冲触发；未经工艺确认不得用于自动加工。
        ct.ThrowIfCancellationRequested();
        try
        {
            await LaserOnAsync();
            await Task.Delay(50, ct);
        }
        finally { await LaserOffAsync(); }
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
