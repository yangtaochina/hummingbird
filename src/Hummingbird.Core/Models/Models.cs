using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hummingbird.Core;

/// <summary>
/// 应用日志接口：Info（正常流程）/ Warn（可恢复异常）/ Error（失败，需要人处理）。
/// 所有模块统一走它，实现方决定输出到哪里（界面、文件……）。
/// </summary>
public interface IAppLogger
{
    void Info(string message);
    void Warn(string message);
    void Error(string message);
    /// <summary>level: INFO/WARN/ERROR</summary>
    event Action<string, string>? LineAdded;
}

/// <summary>
/// 默认日志器：界面事件（LineAdded）+ 文件落盘 双输出。
/// 文件位置：exe 目录下 logs\hummingbird_yyyy-MM-dd.log（每天一个，追加写）。
/// 文件写失败（磁盘满/权限）只吞掉异常——日志不能把主程序搞崩。
/// </summary>
public sealed class MemoryLogger : IAppLogger
{
    private readonly object _gate = new();
    public event Action<string, string>? LineAdded;

    private readonly string _logRoot;
    public string? LastWriteError {get;private set;}
    public MemoryLogger(string? logRoot=null) => _logRoot=logRoot ?? Path.Combine(AppContext.BaseDirectory,"logs");
    public static string Category(string message) =>
        message.Contains("光学标定") || message.Contains("激光补偿") ? "光学标定" :
        message.Contains("补加工") || message.Contains("整件") || message.Contains("[Production") ? "加工" :
        message.Contains("定位") || message.Contains("采图") || message.Contains("圆环") || message.Contains("曝光") ? "视觉" :
        message.Contains("激光") || message.Contains("Laser") || message.Contains("RTU") || message.Contains("Modbus") || message.Contains("试打") ? "激光" :
        message.Contains("OPC") || message.Contains("数控") || message.Contains("相机") || message.Contains("工位恢复") ? "设备" : "系统";
    // 惰性初始化的日志文件路径（首次写日志时确定，exe 目录\logs\）
    private string? _logFile;

    private void Emit(string lvl, string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{lvl,-5}] {msg}";
        lock (_gate)
        {
            try
            {
                WriteFile(line);
                string day=Path.Combine(_logRoot,DateTime.Now.ToString("yyyy-MM-dd"));Directory.CreateDirectory(day);
                File.AppendAllText(Path.Combine(day,Category(msg)+".log"),line+Environment.NewLine);
                if(lvl is "WARN" or "ERROR")File.AppendAllText(Path.Combine(day,"异常.log"),line+Environment.NewLine);
                LastWriteError=null;
            }
            catch(Exception ex) { LastWriteError=ex.Message; }
            LineAdded?.Invoke(lvl, msg);
        }
    }

    /// <summary>追加写入日志文件（调用方已持锁）。文件按天滚动，方便按日期回溯现场问题。</summary>
    private void WriteFile(string line)
    {
        if (_logFile is null || !Path.GetFileName(_logFile).Contains(DateTime.Now.ToString("yyyy-MM-dd")))
        {
            string dir = _logRoot;
            Directory.CreateDirectory(dir);
            _logFile = Path.Combine(dir, $"hummingbird_{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(_logFile, $"===== 飞秒创芯启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");
        }
        File.AppendAllText(_logFile, line + Environment.NewLine);
    }

    public void Info(string m) => Emit("INFO", m);
    public void Warn(string m) => Emit("WARN", m);
    public void Error(string m) => Emit("ERROR", m);
}

/// <summary>一帧灰度图（8bit/像素）。</summary>
public sealed record CameraFrame(byte[] Gray, int Width, int Height, DateTime Timestamp);

/// <summary>定位结果：中心（像素）+ 零件旋转偏角（顺时针为正，单位度）。</summary>
public sealed record PartPose(
    bool Success,
    double CenterPxX,
    double CenterPxY,
    double OuterRadiusPx,
    double DatumClockDeg,
    double AngleOffsetDeg,
    double Confidence,
    string Detail)
{
    /// <summary>
    /// 角向候选值（度）。当基准特征本身具旋转对称性时（如 8 孔 30° 均布 → 90° 周期），
    /// 单张图无法唯一确定转角，只能给出若干候选；由"人工装夹粗对齐角 / 上次成功值"择一。
    /// 为空表示转角唯一确定。
    /// </summary>
    public IReadOnlyList<double> AngleCandidatesDeg { get; init; } = Array.Empty<double>();
}

public sealed record HoleTemplate(
    [property: JsonPropertyName("id")] int Id,
    [property: JsonPropertyName("x_mm")] double X,
    [property: JsonPropertyName("y_mm")] double Y,
    [property: JsonPropertyName("diameter_mm")] double DiameterMm,
    [property: JsonPropertyName("clock")] string Clock = "");

public sealed record HoleTarget(int Id, double MachineX, double MachineY, double DiameterMm, string Clock = "");

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
    /// <summary>角向基准模式：notch=外圈缺口 / offsetCircle=偏心小圆 / datumHoles=外环自带定位孔组</summary>
    [JsonPropertyName("datumMode")] public string DatumMode { get; set; } = "notch";
    /// <summary>基准小圆预期直径（mm），0=未知</summary>
    [JsonPropertyName("datumCircleDiameterMm")] public double DatumCircleDiameterMm { get; set; }
    /// <summary>
    /// "datumHoles" 模式用：外环自带定位孔组的**名义时钟角**列表（度，12H=0，顺时针）。
    /// 视觉侧检出孔组后与该列表做圆周平移匹配，取残差最小的平移量 = 工件装卡转角。
    /// 数据来源：P26009G打孔图纸（2026.11.2）.dwg 中 8 个工件自带双环孔（R14.45，30° 均布）。
    /// </summary>
    [JsonPropertyName("datumHoleAnglesDeg")] public List<double> DatumHoleAnglesDeg { get; set; } = new();
    /// <summary>"datumHoles" 模式用：定位孔组的名义半径（mm），图纸标称 14.45</summary>
    [JsonPropertyName("datumHoleRadiusMm")] public double DatumHoleRadiusMm { get; set; }
    /// <summary>工件外径（mm），用于视觉尺寸校验</summary>
    [JsonPropertyName("partOuterDiameterMm")] public double PartOuterDiameterMm { get; set; }
    /// <summary>基准特征的人类可读说明（写进模板便于现场人员理解）</summary>
    [JsonPropertyName("datumFeature")] public string DatumFeature { get; set; } = "";
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
