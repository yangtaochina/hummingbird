using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hummingbird.Core;

public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    /// <summary>level: INFO/WARN/ERROR</summary>
    event Action<string, string>? LineAdded;
}

public sealed class MemoryLogger : IAppLogger
{
    private readonly object _gate = new();
    public event Action<string, string>? LineAdded;
    private void Emit(string lvl, string msg) { lock (_gate) LineAdded?.Invoke(lvl, msg); }
    public void Info(string m) => Emit("INFO", m);
    public void Warn(string m) => Emit("WARN", m);
    public void Error(string m) => Emit("ERROR", m);
}

/// <summary>一帧灰度图（8bit/像素）。</summary>
public sealed record CameraFrame(byte[] Gray, int Width, int Height, DateTime Timestamp);

public sealed record VisionOptions
{
    /// <summary>表盘外缘预期直径（mm），用于轮廓校验</summary>
    public double ExpectedOuterDiameterMm { get; init; } = 34.5;
    /// <summary>零位特征（缺口）的名义时钟角：0=12H</summary>
    public double NominalDatumAngleDeg { get; init; } = 0.0;
    /// <summary>像素尺寸（mm/px），仅用于预期尺寸校验</summary>
    public double PixelSizeMm { get; init; } = 0.02;
}

/// <summary>定位结果：中心（像素）+ 零件旋转偏角（顺时针为正，单位度）。</summary>
public sealed record PartPose(
    bool Success,
    double CenterPxX,
    double CenterPxY,
    double OuterRadiusPx,
    double DatumClockDeg,
    double AngleOffsetDeg,
    double Confidence,
    string Detail);

public sealed record HoleTemplate(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("x_mm")] double X,
    [property: JsonPropertyName("y_mm")] double Y,
    [property: JsonPropertyName("diameter_mm")] double DiameterMm,
    [property: JsonPropertyName("clock")] string Clock = "");

public sealed record HoleTarget(int Id, double MachineX, double MachineY, double DiameterMm, string Clock = "");

public sealed record CalibPointPair(double Px, double Py, double Mx, double My);

/// <summary>
/// 像素→机床平面坐标的仿射标定结果：
/// Mx = A*px + B*py + C ；My = D*px + E*py + F
/// </summary>
public sealed record CalibrationResult(
    bool Valid, double RmsErrorMm,
    double A, double B, double C,
    double D, double E, double F)
{
    public string? Note { get; init; }
    public (double Mx, double My) Apply(double px, double py)
        => (A * px + B * py + C, D * px + E * py + F);
}

public sealed record MachineStatus(
    bool Connected, string Mode, string ProgramName,
    double PosX, double PosY, double PosZ, string Message);

public sealed record AxisPosition(double X, double Y, double Z);

/// <summary>模板（表盘坐标系）→机床坐标系的轴向约定，现场标定时确定。</summary>
public sealed record TransformConvention
{
    public bool SwapXY { get; init; }
    public bool NegateX { get; init; }
    public bool NegateY { get; init; }
    public double ExtraRotationDeg { get; init; }

    public (double X, double Y) Apply(double x, double y)
    {
        if (SwapXY) (x, y) = (y, x);
        if (NegateX) x = -x;
        if (NegateY) y = -y;
        if (Math.Abs(ExtraRotationDeg) > 1e-9)
        {
            double r = Math.PI / 180 * ExtraRotationDeg, c = Math.Cos(r), s = Math.Sin(r);
            (x, y) = (x * c - y * s, x * s + y * c);
        }
        return (x, y);
    }
}

/// <summary>孔位几何模板（以表盘中心为原点，+Y=12H，角度顺时针）。</summary>
public sealed class HolePatternTemplate
{
    [JsonPropertyName("partName")] public string PartName { get; set; } = "";
    [JsonPropertyName("note")] public string Note { get; set; } = "";
    [JsonPropertyName("nominalDatumAngleDeg")] public double NominalDatumAngleDeg { get; set; }
    [JsonPropertyName("holes")] public List<HoleTemplate> Holes { get; set; } = new();

    private static readonly JsonSerializerOptions Opt = new() { PropertyNameCaseInsensitive = true };

    public static HolePatternTemplate LoadJson(string path)
        => JsonSerializer.Deserialize<HolePatternTemplate>(ReadAllTextNearExe(path), Opt)
           ?? throw new InvalidDataException("模板文件为空: " + path);

    /// <summary>先按给定路径找；找不到再按 exe 所在目录找（双击运行时 cwd 不可靠）。</summary>
    internal static string ReadAllTextNearExe(string path)
    {
        if (File.Exists(path)) return File.ReadAllText(path);
        string near = Path.Combine(AppContext.BaseDirectory, path);
        if (File.Exists(near)) return File.ReadAllText(near);
        throw new FileNotFoundException("找不到文件（含 exe 目录）: " + path);
    }
}
