using System.Diagnostics;
using System.Runtime.ExceptionServices;

namespace Hummingbird.Devices.LaserRtu;

public enum LaserCalibrationProtocol { Pdf, ExcelLegacy }

public sealed record LaserCalibrationState(ushort? Fault, ushort Power, ushort Emission,
    ushort PowerMode, ushort ControlMode, ushort? PulseWidthFs, ushort FrequencyKhz, ushort BurstCount,
    LaserCalibrationProtocol Protocol = LaserCalibrationProtocol.Pdf)
{
    public bool IsLegacy => Protocol == LaserCalibrationProtocol.ExcelLegacy;
    public bool IsPlausible => Emission <= 1 && PowerMode <= 2 && ControlMode <= 2
        && FrequencyKhz is >= 50 and <= 1000 && (IsLegacy
            ? Power <= 10000 && BurstCount is >= 1 and <= 10 && Fault == null && PulseWidthFs == null
            : Power <= 1000 && BurstCount is >= 1 and <= 30 && Fault != null && PulseWidthFs is >= 100 and <= 10000);
    public bool CanExpose => IsPlausible && (IsLegacy || Fault == 0) && Emission == 0
        && PowerMode == (IsLegacy ? 0 : 2) && ControlMode == 0 && Power > 0;
    public string Description => IsLegacy
        ? $"Excel旧协议：功率原始值={Power}（不换算百分比），重频={FrequencyKhz}kHz，Burst={BurstCount}，"
            + $"功率模式={PowerMode}(0=内部)，控制模式={ControlMode}(0=内控)，出光={Emission}；本协议未读取故障及脉宽，保留设备设置"
        : $"PDF协议：故障={Fault}，功率={Power / 10.0:F1}%，重频={FrequencyKhz}kHz，"
            + $"脉宽={PulseWidthFs}fs，Burst={BurstCount}，功率模式={PowerMode}(2=软件)，控制模式={ControlMode}(0=软件)，出光={Emission}";
}

/// <summary>仅用于固定轴试片标定。协议来自机型说明书第64至66页；不是硬件定时脉冲触发器。</summary>
public sealed class LaserCalibrationController(
    Func<ushort, Task<ushort>> read, Func<ushort, ushort, Task> write, Action<string> trace,
    Func<Task<ushort>>? readLegacyPower = null)
{
    public const int MinimumRequestMs = Hummingbird.Core.LaserRequestLimits.MinimumExposureMs;
    public const int MaximumRequestMs = Hummingbird.Core.LaserRequestLimits.MaximumExposureMs;

    public static int ParseRequestMilliseconds(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new InvalidOperationException("尚未填写请求出光时长；当前只能执行无出光演练。");
        if (!int.TryParse(text.Trim(), System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out int value) ||
            value < MinimumRequestMs || value > MaximumRequestMs)
            throw new ArgumentOutOfRangeException(nameof(text), $"请求时长须为{MinimumRequestMs}至{MaximumRequestMs}的整数毫秒；这是软件范围，不是工艺推荐值。");
        return value;
    }
    private readonly SemaphoreSlim _operation = new(1, 1);
    private CancellationTokenSource? _stop;
    private LaserCalibrationProtocol _protocol;
    public ushort EmissionRegister => _protocol == LaserCalibrationProtocol.ExcelLegacy ? (ushort)0x0007 : (ushort)1543;
    public LaserCalibrationState? LastState { get; private set; }
    public LaserPresetCheck? LastPresetCheck { get; private set; }
    public bool IsRunning { get; private set; }
    public bool ProtocolConfirmed { get; private set; }
    public bool ShutdownConfirmed { get; private set; } = true;
    public void Reset() { LastState = null; ProtocolConfirmed = false; }
    public void SelectProtocol(LaserCalibrationProtocol protocol)
    {
        if (!_operation.Wait(0)) throw new InvalidOperationException("试打/读回中不能切换协议。");
        try { _protocol = protocol; Reset(); }
        finally { _operation.Release(); }
    }
    public void RequestStop() => _stop?.Cancel();

    private async Task<LaserCalibrationState> ReadStateCore()
    {
        LaserCalibrationState state;
        if (_protocol == LaserCalibrationProtocol.ExcelLegacy)
        {
            if (readLegacyPower == null) throw new InvalidOperationException("旧协议功率读回接口未配置。");
            var mode = await read(0x0001);
            var powerMode = await read(0x0002);
            var emission = await read(0x0007);
            var freq = await read(0x001C);
            var burst = await read(0x001D);
            state = new LaserCalibrationState(null, await readLegacyPower(), emission, powerMode, mode,
                null, freq, burst, _protocol);
        }
        else state = new LaserCalibrationState(await read(1541), await read(1542), await read(1543),
            await read(1547), await read(1548), await read(1504), await read(1575), await read(1507));
        if (!state.IsPlausible) throw new InvalidDataException($"{_protocol}协议读回值不合理，尚未确认协议，禁止定时出光。");
        return state;
    }

    public async Task<LaserCalibrationState> ReadStateAsync()
    {
        if (!await _operation.WaitAsync(0)) throw new InvalidOperationException("定时试打/读回正在进行。");
        try
        {
            LastState = null;
            var state = await ReadStateCore();
            ProtocolConfirmed = true;
            ShutdownConfirmed = state.Emission == 0;
            return LastState = state;
        }
        finally { _operation.Release(); }
    }

    public async Task<LaserCalibrationState> RestorePresetAsync(LaserProcessPreset preset, Func<Task> writeParameters)
    {
        if(!preset.Settings.CanExpose)throw new InvalidDataException("预设参数无效。");
        if (!await _operation.WaitAsync(0)) throw new InvalidOperationException("激光操作正在进行。");
        try
        {
            LastState = null;
            LastPresetCheck=new(DateTimeOffset.Now,preset,null,null,false,false,[],null);
            trace($"应用参数请求：功率设定值={preset.Settings.Power}，重频={preset.Settings.FrequencyKhz}kHz，Burst={preset.Settings.BurstCount}，请求时长={preset.RequestedExposureMs}ms；不出光。");
            var before = await ReadStateCore();
            LastPresetCheck=LastPresetCheck with {Before=before};
            ShutdownConfirmed=before.Emission==0;
            if (before.Emission != 0 || before.Protocol != preset.Settings.Protocol)
                throw new InvalidOperationException("恢复参数要求已关光且协议与预设一致。");
            await writeParameters();
            LastPresetCheck=LastPresetCheck with {AllWritesAcknowledged=true};
            var after = await ReadStateCore();
            ShutdownConfirmed = after.Emission == 0;
            var differences=preset.Differences(after);
            LastPresetCheck=LastPresetCheck with {After=after,Differences=differences};
            trace("应用后实际读回："+after.Description);
            if (!preset.Matches(after) || !after.CanExpose)
                throw new IOException("参数未通过核对："+(differences.Count==0?"当前状态不满足出光条件":string.Join("；",differences))+"；未启用出光。");
            LastPresetCheck=LastPresetCheck with {Matched=true};
            ProtocolConfirmed = true; return LastState = after;
        }
        catch(Exception ex)
        {
            if(LastPresetCheck is not null)LastPresetCheck=LastPresetCheck with {Error=ex.Message};
            trace("应用参数未完成："+ex.Message);throw;
        }
        finally { _operation.Release(); }
    }

    public async Task<double> ExposeAsync(int milliseconds, CancellationToken ct = default, Func<Task>? verifyStationary = null)
    {
        if (milliseconds is < MinimumRequestMs or > MaximumRequestMs) throw new ArgumentOutOfRangeException(nameof(milliseconds), $"软件请求时长范围{MinimumRequestMs}至{MaximumRequestMs}ms，不等于实际光学出光时长。");
        if (!await _operation.WaitAsync(0)) throw new InvalidOperationException("已有试打/读回正在进行，不能重复触发。");
        bool attempted = false;
        Exception? failure = null;
        var timer = new Stopwatch();
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _stop = stop;
        IsRunning = true;
        try
        {
            var expected = LastState ?? throw new InvalidOperationException("请先读回参数，核对与厂家界面一致。");
            LastState = null; // 每次试打后必须重新读回，不允许沿用旧参数连续触发。
            var current = await ReadStateCore();
            if (current != expected) throw new InvalidOperationException("参数或状态已变化，请重新读回核对。");
            if (!current.CanExpose) throw new InvalidOperationException("需要当前关光、软件内控、内部功率模式且功率非零；PDF协议还需读回无故障。");
            stop.Token.ThrowIfCancellationRequested();
            trace($"定时试打请求 {milliseconds}ms；{current.Description}；保持现有激光参数，不移动机床。");
            attempted = true; // ON应答丢失也必须尝试OFF。
            ShutdownConfirmed = false;
            timer.Start();
            await write(EmissionRegister, 1);
            var hold=Stopwatch.StartNew();
            while(hold.Elapsed.TotalMilliseconds<milliseconds)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(0,Math.Min(250,milliseconds-hold.Elapsed.TotalMilliseconds))),stop.Token);
                stop.Token.ThrowIfCancellationRequested();
                if(hold.Elapsed.TotalMilliseconds>=milliseconds)break;
                if(verifyStationary is not null)await verifyStationary();
                if(await read(EmissionRegister)!=1)throw new IOException("出光期间状态已改变，停止本孔并关光。");
                if(_protocol==LaserCalibrationProtocol.Pdf && await read(1541)!=0)throw new IOException("出光期间设备报告故障，停止本孔并关光。");
            }
        }
        catch (Exception ex) { failure = ex; }
        finally
        {
            if (attempted)
            {
                Exception? offError = null;
                for (int attempt = 1; attempt <= 3; attempt++)
                {
                    try { await write(EmissionRegister, 0); offError = null; break; }
                    catch (Exception ex) { offError = ex; trace($"关光命令第{attempt}次失败：{ex.Message}"); }
                }
                timer.Stop();
                if (offError != null)
                {
                    var error = new IOException("关光未确认，请立即使用设备自身关光/急停功能。", offError);
                    failure = failure == null ? error : new AggregateException(failure, error);
                }
                else
                {
                    try
                    {
                        if (await read(EmissionRegister) != 0) throw new IOException("设备仍报告出光状态，请立即使用设备自身关光/急停功能。");
                        ShutdownConfirmed = true;
                        trace($"关光读回=0。ON命令发出至OFF应答 {timer.Elapsed.TotalMilliseconds:F1}ms（含通信延迟，不代表实际光学出光时长）。");
                    }
                    catch (Exception ex) { failure = failure == null ? ex : new AggregateException(failure, ex); }
                }
            }
            _stop = null;
            IsRunning = false;
            _operation.Release();
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
        return timer.Elapsed.TotalMilliseconds;
    }
}
