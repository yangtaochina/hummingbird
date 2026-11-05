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

    // 惰性初始化的日志文件路径（首次写日志时确定，exe 目录\logs\）
    private string? _logFile;

    private void Emit(string lvl, string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] [{lvl,-5}] {msg}";
        lock (_gate)
        {
            try { WriteFile(line); } catch { /* 磁盘问题不影响运行 */ }
            LineAdded?.Invoke(lvl, msg);
        }
    }

    /// <summary>追加写入日志文件（调用方已持锁）。文件按天滚动，方便按日期回溯现场问题。</summary>
    private void WriteFile(string line)
    {
        if (_logFile is null)
        {
            string dir = Path.Combine(AppContext.BaseDirectory, "logs");
            Directory.CreateDirectory(dir);
            _logFile = Path.Combine(dir, $"hummingbird_{DateTime.Now:yyyy-MM-dd}.log");
            File.AppendAllText(_logFile, $"===== 蜂鸟上位机启动 {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}");
        }
        File.AppendAllText(_logFile, line + Environment.NewLine);
    }

    public void Info(string m) => Emit("INFO", m);
    public void Warn(string m) => Emit("WARN", m);
    public void Error(string m) => Emit("ERROR", m);
}

/// <summary>一帧灰度图（8bit/像素）。</summary>
public sealed record CameraFrame(byte[] Gray, int Width, int Height, DateTime Timestamp);

public sealed record VisionOptions
{
    /// <summary>表盘外缘预期直径（mm），用于轮廓校验</summary>
    public double ExpectedOuterDiameterMm { get; init; } = 34.2;

    /// <summary>
    /// 角向基准模式（现场 2026-10-30 / 11-05 核实结论）：
    ///   "notch"       = 外圈缺口定角向（旧方案，留作兼容）
    ///   "offsetCircle" = 盘面既有偏心小圆定角向：中心→小圆方向 = 12H（实物无 12H 标记，
    ///                    客户以偏心小圆自定义 12 点方向；表盘对称，靠它消解角向歧义）
    ///   "datumHoles"  = **现役方案**：白色外环上自带的定位小孔组（工件自带、随工件转动，
    ///                    客户 2026-11-05 确认）。一组多孔的**孔组朝向**作为角向基准，
    ///                    比单个小圆更抗干扰（任一孔被反光淹没仍可用其余孔定角）。
    ///                    需要 DatumHoleAngles 提供该组各孔的名义时钟角。
    /// </summary>
    public string DatumMode { get; init; } = "notch";

    /// <summary>基准小圆预期直径（mm），0=未知（检测时仅按形状/尺寸范围找，现场量得后填模板）</summary>
    public double DatumCircleDiameterMm { get; init; } = 0.0;

    /// <summary>
    /// "datumHoles" 模式用：外环自带定位孔组的**名义时钟角**列表（度，12H=0，顺时针）。
    /// 视觉侧在白环带内找出所有小孔，检出角与该列表做**圆周平移匹配**（整体旋转一个角度），
    /// 取残差最小的那个平移量作为"工件装卡转角"。
    /// 图纸数据（P26009G 2026.11.2）：自带双环孔在 30/60/120/150/210/240/300/330°，
    /// 即 90° 周期 → 单张图有 4 个等效解，需 CoarseAlignDeg 消歧。
    /// </summary>
    public IReadOnlyList<double> DatumHoleAngles { get; init; } = Array.Empty<double>();

    /// <summary>
    /// 人工装夹**粗对齐角**（度，顺时针为正）。操作工把工件大致按 12H 方向放好（±10° 内即可），
    /// 上位机用它来消解"基准特征旋转对称"造成的多解（见 PartPose.AngleCandidatesDeg）。
    /// NaN = 未提供（此时若多解则定位失败并要求人工介入）。
    /// </summary>
    public double CoarseAlignDeg { get; init; } = double.NaN;

    /// <summary>
    /// "datumHoles" 模式用：定位孔组的**名义半径**（mm，图样中心到孔心）。
    /// 图纸标称 14.45mm（工件自带双环孔，R40 倒角口）。0=不限制半径。
    /// 有此值时按 ±12% 过滤候选，防止把"待加工孔"误当基准孔（两者半径不同）。
    /// </summary>
    public double DatumHoleRadiusMm { get; init; } = 0.0;

    /// <summary>零位特征的名义时钟角：0=12H</summary>
    public double NominalDatumAngleDeg { get; init; } = 0.0;
    /// <summary>像素尺寸（mm/px），仅用于预期尺寸校验</summary>
    public double PixelSizeMm { get; init; } = 0.02;

    /// <summary>派生一份新选项，只改粗对齐角（其余字段保留）。供 UI 实时调整用。</summary>
    public VisionOptions WithCoarseAlign(double deg) => this with { CoarseAlignDeg = deg };
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
