namespace Hummingbird.Core;

/// <summary>串行化预览、参数设置和正式采图；每次正式采图明确下发当前参数。</summary>
public sealed class CameraCaptureCoordinator(ICameraService camera)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task OpenAsync(double exposureUs, double gain)
    {
        Validate(exposureUs, gain);
        await gate.WaitAsync();
        try { await Task.Run(async () => { if (!camera.IsOpen) await camera.OpenAsync(); await Apply(exposureUs, gain); }); }
        finally { gate.Release(); }
    }
    public async Task ApplyAsync(double exposureUs, double gain, CancellationToken ct = default)
    {
        Validate(exposureUs, gain); await gate.WaitAsync(ct);
        try { ct.ThrowIfCancellationRequested(); await Task.Run(() => Apply(exposureUs, gain)); }
        finally { gate.Release(); }
    }
    public async Task<CameraFrame> CaptureAsync(double exposureUs, double gain)
    {
        Validate(exposureUs, gain); await gate.WaitAsync();
        try { return await Task.Run(async () => { await Apply(exposureUs, gain); return await camera.GrabAsync(5000); }); }
        finally { gate.Release(); }
    }
    public async Task<CameraFrame?> TryPreviewAsync()
    {
        if (!camera.IsOpen || !await gate.WaitAsync(0)) return null;
        try { return await Task.Run(() => camera.GrabAsync(1500)); }
        finally { gate.Release(); }
    }
    public async Task<ExposureSelection> SelectExposureAsync(double basis, double gain,
        Func<CameraFrame, double, Task<ExposureQuality>> evaluate, CancellationToken ct = default, double? restoreExposureUs = null)
    {
        Validate(basis, gain);
        double restoreExposure = restoreExposureUs ?? basis; Validate(restoreExposure, gain);
        var candidates = ExposureSelectionPolicy.Candidates(basis);
        double minimum = 20, maximum = 1000000;
        await gate.WaitAsync(ct);
        try
        {
                if (camera is ICameraExposureRange range)
                {
                    var limits = await Task.Run(range.GetExposureRange);
                    minimum = Math.Max(20, limits.MinimumUs); maximum = Math.Min(1000000, limits.MaximumUs);
                    if (!double.IsFinite(minimum) || !double.IsFinite(maximum) || minimum > maximum)
                        throw new InvalidDataException("相机曝光范围无效。");
                    candidates = candidates.Select(v => Math.Clamp(v, minimum, maximum)).Distinct().ToArray();
                }
                CameraFrame? best = null; ExposureQuality? quality = null; double selected = basis;
                var trials = new List<ExposureTrial>();
                try
                {
                    async Task<bool> Try(double exposure)
                    {
                        ct.ThrowIfCancellationRequested();
                        var frame = await Task.Run(async () =>
                        {
                            await Apply(exposure, gain);
                            _ = await camera.GrabAsync(5000); // 参数切换后丢弃首帧。
                            return await camera.GrabAsync(5000);
                        }, ct);
                        var review = await evaluate(frame, exposure);
                        ct.ThrowIfCancellationRequested();
                        if (!double.IsFinite(review.Score)) throw new InvalidDataException("曝光评分无效。");
                        trials.Add(new(exposure, review));
                        if (quality is not null && !ExposureSelectionPolicy.Better(review, quality)) return false;
                        best = frame; quality = review; selected = exposure; return true;
                    }
                    foreach (double exposure in candidates) await Try(exposure);
                    // 特征检查未通过、且最佳档落在已试范围的边缘时，说明合适的曝光可能在范围之外：
                    // 朝该方向每次加倍或减半再试，最多两档，新一档不更好就停。
                    for (int extension = 0; extension < ExposureSelectionPolicy.MaximumExtensions; extension++)
                    {
                        if (ExposureSelectionPolicy.Extend(selected, quality!, trials.Select(t => t.ExposureUs), minimum, maximum) is not { } next ||
                            !await Try(next)) break;
                    }
                    await Task.Run(() => Apply(selected, gain));
                    return new ExposureSelection(best!, selected, trials.AsReadOnly());
                }
                catch (Exception searchError)
                {
                    try { await Task.Run(() => Apply(restoreExposure, gain)); }
                    catch (Exception restoreError) { throw new AggregateException("曝光搜索失败，原参数恢复也失败。", searchError, restoreError); }
                    throw;
                }
        }
        finally { gate.Release(); }
    }
    public async Task CloseAsync()
    {
        await gate.WaitAsync(); try { await camera.CloseAsync(); } finally { gate.Release(); }
    }
    public async Task<string> DiagnosticsAsync()
    {
        await gate.WaitAsync();
        try { return await Task.Run(() => camera is ICameraDiagnostics diagnostics ? diagnostics.GetParameterRanges() : "当前相机没有参数范围诊断接口。"); }
        finally { gate.Release(); }
    }
    private async Task Apply(double exposure, double gain)
    {
        if (!camera.IsOpen) throw new InvalidOperationException("相机未连接。");
        await camera.SetExposureUsAsync(exposure); await camera.SetGainAsync(gain);
    }
    private static void Validate(double exposure, double gain)
    {
        if (!double.IsFinite(exposure) || exposure < 20 || exposure > 1000000 || !double.IsFinite(gain) || gain < 0 || gain > 20)
            throw new ArgumentOutOfRangeException(nameof(exposure), "相机曝光或增益超出界面支持范围。");
    }
}
