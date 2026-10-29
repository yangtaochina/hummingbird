using System.Text.Json;

namespace Hummingbird.App;

/// <summary>应用配置（config.json，随程序目录）。</summary>
public sealed class AppConfig
{
    public string DeviceMode { get; set; } = "Sim";   // Sim | Real
    public string CameraSerialNumber { get; set; } = "";
    public string CncEndpoint { get; set; } = "opc.tcp://192.168.214.1:4840";
    public string CncProgramShare { get; set; } = @"\\192.168.214.1\nc";
    public string LaserComPort { get; set; } = "COM3";
    public int LaserBaud { get; set; } = 115200;      // TODO 现场确认
    public int LaserPower { get; set; } = 6000;
    public int LaserFrequencyKhz { get; set; } = 200;
    public int LaserPulseCount { get; set; } = 5;
    public double ExpectedOuterDiameterMm { get; set; } = 34.5;
    public bool ConvSwapXY { get; set; }
    public bool ConvNegateX { get; set; }
    public bool ConvNegateY { get; set; }
    public double ConvExtraRotationDeg { get; set; }

    private static readonly JsonSerializerOptions Opt = new() { PropertyNameCaseInsensitive = true };

    public static AppConfig LoadOrDefault(string path)
    {
        try
        {
            if (File.Exists(path))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Opt) ?? new AppConfig();
        }
        catch { /* 配置损坏时用默认值 */ }
        return new AppConfig();
    }
}
