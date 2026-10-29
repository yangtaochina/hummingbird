using Hummingbird.Core;

#if !HAS_PYLON
// 未安装 pylon SDK 时本文件整体不参与编译（csproj 探测不到 Basler.Pylon.dll）。
// 安装 pylon SDK 后重新编译即可自动启用，无需改代码。详见 README.md。
#else
using Basler.Pylon;

namespace Hummingbird.Devices.Basler;

/// <summary>
/// Basler pylon 相机真机实现（ICameraService）。
///
/// API 依据：本机 pylon 7.5.0.15658（E:\Program Files\Basler\pylon 7），
/// 已对照 SDK 的 Basler.Pylon.xml 和官方 C# 例程（Development\Samples\C#\Basler.Pylon\Grab）核实：
///   - 单帧采集用 StreamGrabber.GrabOne(timeoutMs)（注意：pylon 7.5 没有 Camera.OneShot）
///   - 像素格式直接写字符串 "Mono8"（pylon 参数体系是字符串键值，跨版本最稳）
///   - 采集模式经 CameraOpened 事件挂 Configuration.AcquireSingleFrame（单帧模式）
///
/// 首次联机建议：先用 pylon Viewer 确认相机可见、能出 Mono8 图，再跑本软件。
/// </summary>
public sealed class BaslerCameraService : ICameraService
{
    private readonly string _serial;        // 相机序列号；空 = 取第一台发现的相机
    private readonly IAppLogger _log;       // 运行日志（可为 null，只走界面时不影响功能）
    private Camera? _cam;

    public BaslerCameraService(string serialNumber = "", IAppLogger? log = null)
    {
        _serial = serialNumber;
        _log = log ?? new NullLogger();
    }

    /// <summary>设备显示名：序列号空时显示 "(第一台)"。</summary>
    public string DeviceName => "Basler " + (string.IsNullOrEmpty(_serial) ? "(第一台)" : _serial);

    public bool IsOpen => _cam?.IsOpen == true;

    // ------------------------------------------------------------------
    // 打开/关闭
    // ------------------------------------------------------------------

    /// <summary>
    /// 打开相机并配置成"单帧采集 + Mono8 灰度"。
    /// 步骤：枚举/选定相机 → Open → 单帧模式 → 强制 Mono8（视觉算法只吃 8bit 灰度）。
    /// </summary>
    public Task OpenAsync(CancellationToken ct = default)
    {
        _log.Info($"[相机] 正在打开 {DeviceName} ...");

        // 按(可选)序列号创建 Camera；找不到相机/被占用时构造函数或 Open 会抛异常
        _cam = string.IsNullOrEmpty(_serial) ? new Camera() : new Camera(_serial);
        _log.Info($"[相机] 型号 {_cam.CameraInfo[CameraInfoKey.ModelName]}  SN {_cam.CameraInfo[CameraInfoKey.SerialNumber]}");

        // 相机断连回调：现场网线松了/交换机断电时第一时间知道，而不是等到拍照超时
        _cam.ConnectionLost += (_, _) =>
            _log.Error("[相机] 连接丢失！（检查网线/供电后重新连接设备）");

        // CameraOpened 事件里配置采集参数（pylon 推荐做法：参数在打开瞬间写入）
        _cam.CameraOpened += Configuration.AcquireSingleFrame;   // 单帧采集模式（配合 GrabOne）

        _cam.Open();
        _log.Info("[相机] 已打开");

        // 强制 Mono8：万一相机上次被 pylon Viewer 改成彩色格式，这里拉回来。
        // pylon 参数体系是字符串键值，枚举值直接写字符串 "Mono8" 最稳（跨版本兼容）。
        if (!_cam.Parameters[PLCamera.PixelFormat].TrySetValue("Mono8"))
            _log.Warn("[相机] 设置 Mono8 失败（相机可能不支持），将按当前格式采集");
        else
            _log.Info("[相机] 像素格式 = Mono8");

        return Task.CompletedTask;
    }

    /// <summary>关闭并释放相机（先 Close 再 Dispose，pylon 标准顺序）。</summary>
    public Task CloseAsync()
    {
        if (_cam != null)
        {
            try
            {
                if (_cam.IsOpen) _cam.Close();
                _log.Info("[相机] 已关闭");
            }
            finally
            {
                _cam.Dispose();
                _cam = null;
            }
        }
        return Task.CompletedTask;
    }

    // ------------------------------------------------------------------
    // 采集
    // ------------------------------------------------------------------

    /// <summary>
    /// 单帧采集：StreamGrabber.GrabOne 内部完成"启动采集→等图→停采集"，
    /// 返回的 IGrabResult 用完必须 Dispose（缓冲区归还给 pylon，否则内存泄漏）。
    /// 失败/超时会抛 TimeoutException / pylon 异常，由上层（Workflow/UI）统一捕获记日志。
    /// </summary>
    public Task<CameraFrame> GrabAsync(int timeoutMs = 3000)
    {
        if (_cam is not { IsOpen: true })
            throw new InvalidOperationException("[相机] 未打开（请先点\"连接设备\"）");

        var t0 = DateTime.UtcNow;
        using IGrabResult grab = _cam.StreamGrabber.GrabOne(timeoutMs);

        if (!grab.GrabSucceeded)
            throw new TimeoutException($"[相机] 采集失败：ErrorCode={grab.ErrorCode} {grab.ErrorDescription}（超时 {timeoutMs}ms）");

        var pixels = grab.PixelData as byte[]
            ?? throw new InvalidOperationException("[相机] 非 Mono8 数据（PixelData 不是 byte[]），请在打开时强制 Mono8");

        // 必须复制：PixelData 指向 pylon 内部缓冲区，Dispose 后失效
        var buf = new byte[pixels.Length];
        Array.Copy(pixels, buf, pixels.Length);

        _log.Info($"[相机] 采图成功 {grab.Width}x{grab.Height}，耗时 {(DateTime.UtcNow - t0).TotalMilliseconds:F0}ms");
        return Task.FromResult(new CameraFrame(buf, grab.Width, grab.Height, DateTime.Now));
    }

    // ------------------------------------------------------------------
    // 曝光/增益（视觉调光用）
    // ------------------------------------------------------------------

    /// <summary>设定曝光时间（微秒）。先切 Timed 曝光模式再写时间值；相机不支持时记警告不中断。</summary>
    public Task SetExposureUsAsync(double exposureUs)
    {
        if (_cam is not { IsOpen: true }) return Task.CompletedTask;
        bool okMode = _cam.Parameters[PLCamera.ExposureMode].TrySetValue(PLCamera.ExposureMode.Timed);
        bool okTime = _cam.Parameters[PLCamera.ExposureTime].TrySetValue(Math.Max(20, exposureUs));
        if (okMode && okTime) _log.Info($"[相机] 曝光 = {Math.Max(20, exposureUs):F0} us");
        else _log.Warn($"[相机] 设置曝光失败（模式:{okMode} 时间:{okTime}），可能超出相机范围");
        return Task.CompletedTask;
    }

    /// <summary>设定增益。范围钳到 0 起，避免负值异常。</summary>
    public Task SetGainAsync(double gain)
    {
        if (_cam is not { IsOpen: true }) return Task.CompletedTask;
        if (_cam.Parameters[PLCamera.Gain].TrySetValue(Math.Max(0, gain)))
            _log.Info($"[相机] 增益 = {Math.Max(0, gain):F1}");
        else
            _log.Warn("[相机] 设置增益失败");
        return Task.CompletedTask;
    }

    public void Dispose() => CloseAsync().GetAwaiter().GetResult();

    /// <summary>空日志器：调用方不传 logger 时的占位（避免到处判空）。</summary>
    private sealed class NullLogger : IAppLogger
    {
        public void Info(string m) { }
        public void Warn(string m) { }
        public void Error(string m) { }
#pragma warning disable CS0067
        public event Action<string, string>? LineAdded;
#pragma warning restore CS0067
    }
}

#endif
