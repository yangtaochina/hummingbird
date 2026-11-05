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

    /// <summary>相机曝光时间（微秒）。0 = 不设置（沿用相机内部当前值/pylon Viewer 里调的值）。
    /// 现场调光流程：pylon Viewer 里手动调到清晰、亮度合适后，把该数值抄到这里——
    /// 程序每次连接相机时强制下发，避免相机参数被 pylon 改乱。调光目标：盘面约 120~150、特征 200+。
    /// ✅ 2026-11-05 真机曝光扫描：100us 均值0.7 / 500us 6.3 / 1000us 13.2 / 5000us 63.2（可用）
    /// / 20000us 202（临界过曝）/ 50000us 起全白。默认取 5000us（留 1~2 倍余量）。</summary>
    public double CameraExposureUs { get; set; } = 5000;

    /// <summary>相机增益。0 = 不设置。曝光拉满仍偏暗时再加增益（增益加噪声，优先加曝光）。</summary>
    public double CameraGain { get; set; } = 0;

    /// <summary>828D OPC UA 端点。X130 口默认 IP 192.168.214.1，端口 4840。</summary>
    public string CncEndpoint { get; set; } = "opc.tcp://192.168.214.1:4840";

    /// <summary>OPC UA 登录用户名（空 = 匿名）。现场核实（2026-11-02）：该 828D 匿名可建会话但读 NC 变量
    /// 被拒（BadUserAccessDenied）——SW4.7+ 必须用户名密码。需在 HMI【调试→网络→OPC UA→设置】
    /// 创建用户（8 位以上强密码）并赋变量读写权限。</summary>
    public string CncUserName { get; set; } = "";

    /// <summary>OPC UA 登录密码（配合 CncUserName）。</summary>
    public string CncPassword { get; set; } = "";

    /// <summary>是否用加密端点（Basic256Sha256 - Sign）。用户名登录时若 None 通道被拒（密码明文传输部分服务器禁止），
    /// 改 true；注意加密端点需要在 828D 信任客户端证书。</summary>
    public bool CncUseSecurity { get; set; } = false;

    /// <summary>NC 程序下发的网络共享路径（828D 侧需开 SMB 共享并给写权限）。</summary>
    public string CncProgramShare { get; set; } = @"\\192.168.214.1\nc";

    /// <summary>变量命名空间匹配关键字（连接时自动在服务器命名空间表里找含此关键字的 URI）。
    /// 现场核实（2026-11-02）：这台 828D 的变量节点在 ns=2 SinumerikVarProvider，不是文档常写的 ns=3。</summary>
    public string CncVarNamespace { get; set; } = "SinumerikVarProvider";

    /// <summary>轴位置节点整路径（不带 ns 前缀，ns 连接时自动解析）。
    /// 现场核实（2026-11-02）：该 828D VarProvider 无 aaPos 变量（原预设错误），
    /// 轴位置正式变量为 actToolBasePos（刀具基准点机床系实际位置）。
    /// 最终以 UaExpert 里能读到、且摇轴会变的节点为准——右键 Copy NodeId，取 s= 后面的部分填这里。</summary>
    public string CncPosNodeX { get; set; } = "/Channel/GeometricAxis/actToolBasePos[u1, X]";
    public string CncPosNodeY { get; set; } = "/Channel/GeometricAxis/actToolBasePos[u1, Y]";
    public string CncPosNodeZ { get; set; } = "/Channel/GeometricAxis/actToolBasePos[u1, Z1]";

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
    public double ExpectedOuterDiameterMm { get; set; } = 34.2;

    /// <summary>视觉像素尺寸（mm/px）。
    /// ✅ 2026-11-05 真机实测回填：以工件白色外环 Ø34.2mm 为标尺，实测 R≈691px → 0.0247 mm/px，
    /// 对应视野约 47.5×29.7mm。⚠ 该值与镜头/物距/相机安装角度绑定，改动机械或镜头必须重测。
    /// 影响：尺寸校验、基准小圆直径过滤、坐标换算。</summary>
    public double VisionPixelSizeMm { get; set; } = 0.0247;

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
