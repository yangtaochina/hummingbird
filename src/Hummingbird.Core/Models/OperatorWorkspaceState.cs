using System.Text.Json;

namespace Hummingbird.Core;

/// <summary>只保存操作偏好与文件路径，不保存连接凭据，也不保存装夹/参考有效确认。</summary>
public sealed class OperatorWorkspaceState
{
    public int Version { get; set; } = 1;
    public double? CameraExposureUs { get; set; }
    public double? CameraGain { get; set; }
    public bool AutoExposureSelection { get; set; } = true;
    public Dictionary<string, double> StepExposureUs { get; set; } = new();
    public string CaptureRoot { get; set; } = "";
    public string LastSessionPath { get; set; } = "";
    public string LastRouteSessionPath { get; set; } = "";
    public string LaserCalibrationPath { get; set; } = "";
    public DateTimeOffset? OpticalChangedAt {get;set;}
    public bool OpticalRecalculationRequired {get;set;}
    public string InvalidatedLaserTrialSha256 {get;set;}="";
    public string LastManualC { get; set; } = "";
    public int? LaserRequestMs { get; set; }
    public ushort? LaserPowerRaw { get; set; }
    public string LastProductionProfilePath {get;set;}="";
    public string LastStationReturnProfilePath {get;set;}="";
}

public sealed class OperatorWorkspaceStore(string path)
{
    public string Path { get; } = System.IO.Path.GetFullPath(path);
    public OperatorWorkspaceState Load()
    {
        if (!File.Exists(Path)) return new();
        var value = JsonSerializer.Deserialize<OperatorWorkspaceState>(File.ReadAllText(Path)) ?? throw new InvalidDataException("操作状态为空。");
        if (value.Version != 1) throw new InvalidDataException("操作状态版本不受支持。");
        if (value.CameraExposureUs is { } exp && (!double.IsFinite(exp) || exp < 20 || exp > 1000000) ||
            value.CameraGain is { } gain && (!double.IsFinite(gain) || gain < 0 || gain > 20))
            throw new InvalidDataException("已保存的相机参数无效。");
        if (value.StepExposureUs is null || value.StepExposureUs.Values.Any(v => !double.IsFinite(v) || v < 20 || v > 1000000))
            throw new InvalidDataException("已保存的分步曝光参数无效。");
        if (value.LaserRequestMs is { } ms && (ms < LaserRequestLimits.MinimumExposureMs || ms > LaserRequestLimits.MaximumExposureMs))
            throw new InvalidDataException("保存的激光请求时长无效。");
        if(value.LaserPowerRaw is 0 or >10000)throw new InvalidDataException("保存的激光功率设定值无效。");
        return value;
    }
    public void Save(OperatorWorkspaceState value)
    {
        string temporary = Path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true })); File.Move(temporary, Path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
