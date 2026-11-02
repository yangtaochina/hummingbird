using System.Text.Json;

namespace Hummingbird.App;

/// <summary>
/// 应用配置（config.json，随程序目录，改完重启程序生效）。
/// 现场调试速查：
///   - 联不上数控 → 查 CncEndpoint；程序下发失败 → 查 CncProgramShare
///   - 激光器无响应 → 查 LaserComPort / LaserBaud（波特率协议文档没给，现场试）
///   - 视觉找不到盘 → 查 ExpectedOuterDiameterMm 与实际盘径
///   - 孔位整体偏 → 查 Conv* 四项（表盘系→机床系轴向约定，标定时定）
/// </summary>
public sealed class AppConfig
{
    /// <summary>Sim=全模拟（无硬件跑流程）；Real=真机（OPC UA + 串口 + pylon）。</summary>
    public string DeviceMode { get; set; } = "Sim";

    /// <summary>Basler 相机序列号；空 = 使用发现的第一台相机（现场只挂一台时留空即可）。</summary>
    public string CameraSerialNumber { get; set; } = "";

    /// <summary>828D OPC UA 端点。X130 口默认 IP 192.168.214.1，端口 4840。</summary>
    public string CncEndpoint { get; set; } = "opc.tcp://192.168.214.1:4840";

    /// <summary>NC 程序下发的网络共享路径（828D 侧需开 SMB 共享并给写权限）。</summary>
    public string CncProgramShare { get; set; } = @"\\192.168.214.1\nc";

    /// <summary>变量命名空间匹配关键字（连接时自动在服务器命名空间表里找含此关键字的 URI）。
    /// 现场核实（2026-11-02）：这台 828D 的变量节点在 ns=2 SinumerikVarProvider，不是文档常写的 ns=3。</summary>
    public string CncVarNamespace { get; set; } = "SinumerikVarProvider";

    /// <summary>轴名（须与 aaPos[u1, 轴名] 一致，UaExpert 展开 MachineAxis 可核实；Z 常见为 Z1 或 Z）。</summary>
    public string CncAxisXName { get; set; } = "X";
    public string CncAxisYName { get; set; } = "Y";
    public string CncAxisZName { get; set; } = "Z1";

    /// <summary>激光器 232转USB 的串口号（设备管理器里查，如 COM3）。</summary>
    public string LaserComPort { get; set; } = "COM3";

    /// <summary>激光器串口波特率。⚠ 协议文档未给出，默认 115200，现场确认后改。</summary>
    public int LaserBaud { get; set; } = 115200;

    /// <summary>激光功率 0~10000（从站09/寄存器0x0172）。调试时先低功率！</summary>
    public int LaserPower { get; set; } = 6000;

    /// <summary>激光重频 50~1000 kHz（寄存器0x001C）。</summary>
    public int LaserFrequencyKhz { get; set; } = 200;

    /// <summary>单孔脉冲数 1~10（寄存器0x001D）。</summary>
    public int LaserPulseCount { get; set; } = 5;

    /// <summary>表盘外缘预期直径（mm），视觉定位的尺寸校验用（偏差>30% 报失败）。</summary>
    public double ExpectedOuterDiameterMm { get; set; } = 34.5;

    /// <summary>表盘系→机床系：是否交换 X/Y 轴（标定时确定）。</summary>
    public bool ConvSwapXY { get; set; }

    /// <summary>表盘系→机床系：X 是否取反。</summary>
    public bool ConvNegateX { get; set; }

    /// <summary>表盘系→机床系：Y 是否取反。</summary>
    public bool ConvNegateY { get; set; }

    /// <summary>表盘系→机床系：附加旋转角（°，轴向约定之外的固定转角，一般为0）。</summary>
    public double ConvExtraRotationDeg { get; set; }

    private static readonly JsonSerializerOptions Opt = new() { PropertyNameCaseInsensitive = true };

    /// <summary>读配置；文件不存在/损坏时返回默认值（不抛异常，保证程序能起来）。</summary>
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
