namespace Hummingbird.Core;

/// <summary>相机服务：采集灰度图（8bit）。实现可换：Basler pylon / 模拟 / 其他品牌。</summary>
public interface ICameraService : IDisposable
{
    string DeviceName { get; }
    bool IsOpen { get; }

    Task OpenAsync(CancellationToken ct = default);
    Task CloseAsync();

    /// <summary>单帧采集（触发一次，返回一帧灰度图）。</summary>
    Task<CameraFrame> GrabAsync(int timeoutMs = 3000);

    Task SetExposureUsAsync(double exposureUs);
    Task SetGainAsync(double gain);
}

/// <summary>提供实际可设置的曝光范围，供有限档搜索使用。</summary>
public interface ICameraExposureRange
{
    (double MinimumUs, double MaximumUs) GetExposureRange();
}
