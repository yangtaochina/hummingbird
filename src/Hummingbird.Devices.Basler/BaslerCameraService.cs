using Hummingbird.Core;

namespace Hummingbird.Devices.Basler;

/// <summary>
/// Basler pylon 相机实现。
/// ⚠️ 编译前提：安装 pylon SDK 7.5（客户已提供安装包 Basler_pylon_7.5.0.15658.exe），
///    csproj 会自动引用 "C:\Program Files\Basler\pylon 7\bin\Basler.Pylon.dll"。
/// ⚠️ 首次编译/联机时需对照 pylon 的 C# 例程核对 API 细节（OneShot/参数名各版本略有差异）。
/// </summary>
public sealed class BaslerCameraService : ICameraService
{
    private readonly string _serial;
    private Camera? _cam;

    public BaslerCameraService(string serialNumber = "")
    { _serial = serialNumber; }

    public string DeviceName => "Basler " + (string.IsNullOrEmpty(_serial) ? "(first)" : _serial);
    public bool IsOpen => _cam?.IsOpen == true;

    public Task OpenAsync(CancellationToken ct = default)
    {
        _cam = string.IsNullOrEmpty(_serial) ? new Camera() : new Camera(_serial);
        _cam.ConnectionLost += (_, e) => { };
        _cam.Open();
        // 常用默认：8bit 灰度 + 连续采集参数就绪（OneShot 需要）
        _cam.Parameters[PLCamera.PixelFormat].TrySetValue(PLPixelFormat.Mono8);
        return Task.CompletedTask;
    }

    public Task CloseAsync()
    {
        _cam?.Close();
        _cam?.Dispose();
        _cam = null;
        return Task.CompletedTask;
    }

    public Task<CameraFrame> GrabAsync(int timeoutMs = 3000)
    {
        if (_cam is not { IsOpen: true }) throw new InvalidOperationException("相机未打开");
        using IGrabResult grab = _cam.OneShot(timeoutMs);
        if (!grab.GrabSucceeded)
            throw new TimeoutException($"采集失败/超时（{timeoutMs}ms）");
        var pixels = grab.PixelData as byte[]
            ?? throw new InvalidOperationException("非 Mono8 数据，请在 OpenAsync 里强制 Mono8");
        var buf = new byte[pixels.Length];
        Array.Copy(pixels, buf, pixels.Length);
        return Task.FromResult(new CameraFrame(buf, grab.Width, grab.Height, DateTime.Now));
    }

    public Task SetExposureUsAsync(double exposureUs)
    {
        if (_cam is not { IsOpen: true }) return Task.CompletedTask;
        _cam.Parameters[PLCamera.ExposureMode].TrySetValue(PLCamera.ExposureMode.Timed);
        _cam.Parameters[PLCamera.ExposureTime].TrySetValue(Math.Max(20, exposureUs));
        return Task.CompletedTask;
    }

    public Task SetGainAsync(double gain)
    {
        if (_cam is not { IsOpen: true }) return Task.CompletedTask;
        _cam.Parameters[PLCamera.Gain].TrySetValue(Math.Max(0, gain));
        return Task.CompletedTask;
    }

    public void Dispose() => CloseAsync().GetAwaiter().GetResult();
}
