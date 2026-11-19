namespace Hummingbird.Devices.LaserRtu;

public sealed record LaserPresetCheck(DateTimeOffset Started,LaserProcessPreset Requested,
    LaserCalibrationState? Before,LaserCalibrationState? After,bool AllWritesAcknowledged,bool Matched,
    IReadOnlyList<string> Differences,string? Error);

/// <summary>可重复使用的设备原始设置；来源试打不是人工合格孔配方的替代证明。</summary>
public sealed record LaserProcessPreset(string Name, int RequestedExposureMs, LaserCalibrationState Settings, string Source)
{
    public static LaserProcessPreset CustomerConfirmed20261116 {get;}=new("客户确认60秒／AOM2 12%",60000,
        RecordedSettings4420(),"2026-11-16客户明确确认每孔连续60秒，厂家显示AOM2 12%(4420)；仅记录本机此设定对应关系，不推导其他百分比或瓦数。");
    private static LaserCalibrationState RecordedSettings4420()=>new(null,4420,0,0,0,null,100,1,LaserCalibrationProtocol.ExcelLegacy);
    public static LaserProcessPreset CustomerConfigured(int milliseconds,ushort power)
    {
        LaserCalibrationController.ParseRequestMilliseconds(milliseconds.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if(power is 0 or >10000)throw new ArgumentOutOfRangeException(nameof(power),"功率设定值须为1～10000。");
        if(milliseconds==60000 && power==4420)return CustomerConfirmed20261116;
        return new("客户设置",milliseconds,Recorded20261113.Settings with {Power=power},
            "客户在界面设置时长及功率；重频100kHz、Burst1及内部控制沿用历史设置。不是合格孔配方认证。");
    }

    public static LaserProcessPreset Recorded20261113 { get; } = new(
        "2026-11-13 已执行出光", 2000,
        new(null, 3950, 0, 0, 0, null, 100, 1, LaserCalibrationProtocol.ExcelLegacy),
        "单点试打_20261113_121007417/trial.json；6%为现场备注，功率使用原始值3950；未记录脉宽");

    public bool Matches(LaserCalibrationState state) => state.Protocol == Settings.Protocol &&
        state.Power == Settings.Power && state.FrequencyKhz == Settings.FrequencyKhz &&
        state.BurstCount == Settings.BurstCount && state.PowerMode == Settings.PowerMode &&
        state.ControlMode == Settings.ControlMode && state.PulseWidthFs == Settings.PulseWidthFs;

    public IReadOnlyList<string> Differences(LaserCalibrationState state)
    {
        var result=new List<string>();
        void Compare<T>(string name,T requested,T actual)
        {if(!EqualityComparer<T>.Default.Equals(requested,actual))result.Add($"{name}：请求{requested}，读回{actual}");}
        Compare("协议",Settings.Protocol,state.Protocol);
        Compare("功率设定值",Settings.Power,state.Power);
        Compare("重频(kHz)",Settings.FrequencyKhz,state.FrequencyKhz);
        Compare("Burst",Settings.BurstCount,state.BurstCount);
        Compare("功率模式",Settings.PowerMode,state.PowerMode);
        Compare("控制模式",Settings.ControlMode,state.ControlMode);
        Compare("脉宽(fs)",Settings.PulseWidthFs,state.PulseWidthFs);
        Compare("出光状态(0=关光)",(ushort)0,state.Emission);
        if(state.Fault is >0)result.Add($"故障状态读回{state.Fault}");
        return result;
    }
}
