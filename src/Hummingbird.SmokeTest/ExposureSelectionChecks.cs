using Hummingbird.Core;

internal static class ExposureSelectionChecks
{
    public static async Task<int> Run()
    {
        int passed = 0;
        void Check(bool condition, string name)
        {
            if (!condition) throw new Exception(name);
            passed++; Console.WriteLine("[PASS] " + name);
        }
        async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch { return true; } }
        CameraFrame Image(byte a, byte b) => new(Enumerable.Range(0, 64 * 64).Select(i => i % 64 < 32 ? a : b).ToArray(), 64, 64, DateTime.Now);
        var dark = ExposureSelectionPolicy.Measure(Image(0, 0));
        var saturated = ExposureSelectionPolicy.Measure(Image(255, 255));
        var clear = ExposureSelectionPolicy.Measure(Image(40, 190));
        Check(clear.Score > dark.Score && clear.Score > saturated.Score, "清楚边界优于全黑和全白");
        Check(ExposureSelectionPolicy.Better(dark with { FeatureDetected = true }, clear with { FeatureDetected = false }), "实体边界识别通过优先于亮度评分");
        Check(ExposureSelectionPolicy.Candidates(20).Distinct().Count() == 3 && ExposureSelectionPolicy.Candidates(1000000).Max() == 1000000, "极限曝光去重并限制搜索范围");
        var roiImage = Image(10, 10);
        for (int y = 24; y < 40; y++)
        for (int x = 24; x < 40; x++) roiImage.Gray[y * 64 + x] = x < 32 ? (byte)40 : (byte)200;
        Check(ExposureSelectionPolicy.Measure(roiImage, new(32, 32, 6, "", "test")).Contrast == 160, "按参考圆面局部评分");
        using var camera = new Camera(); var coordinator = new CameraCaptureCoordinator(camera);
        await coordinator.OpenAsync(1000, 0);
        int evaluations = 0;
        var result = await coordinator.SelectExposureAsync(1000, 0, async (frame, exposure) =>
        {
            evaluations++;
            Check(await coordinator.TryPreviewAsync() is null, "搜索期间预览不能插入");
            return new ExposureQuality(exposure == 2000 ? 100 : 1, 0, 0, 100);
        });
        Check(evaluations == 4 && result.Trials.Count == 4, "候选按相机实际范围钳位去重");
        Check(camera.Grabs == evaluations * 2 && result.Frame.Gray[0] == 20 && result.ExposureUs == 2000 && camera.Exposure == 2000,
            "每档丢弃首帧，返回评分过的获选原帧并恢复选中曝光");
        Check(await Fails(() => coordinator.SelectExposureAsync(1000, 0, (_, _) => throw new IOException("analysis failed"))) && camera.Exposure == 1000,
            "分析失败恢复搜索前曝光");
        Check(await Fails(() => coordinator.SelectExposureAsync(1000, 0, (_, _) => throw new IOException("analysis failed"), restoreExposureUs: 1500)) && camera.Exposure == 1500,
            "分步记忆作为搜索中心时，失败仍恢复界面原曝光");
        var cancellation = new CancellationTokenSource(); int reviewed = 0;
        Check(await Fails(() => coordinator.SelectExposureAsync(1000, 0, (_, _) =>
        {
            reviewed++; cancellation.Cancel(); return Task.FromResult(clear);
        }, cancellation.Token)) && reviewed == 1 && camera.Exposure == 1000, "取消后停止搜索并恢复原曝光");
        camera.FailGrab = true;
        Check(await Fails(() => coordinator.SelectExposureAsync(1000, 0, (_, _) => Task.FromResult(clear))) && camera.Exposure == 1000,
            "采图失败不返回伪成功"); camera.FailGrab = false;
        Check(await coordinator.TryPreviewAsync() is not null, "失败后释放相机协调锁");
        camera.FailExposure = true; int grabs = camera.Grabs;
        Check(await Fails(() => coordinator.SelectExposureAsync(1000, 0, (_, _) => Task.FromResult(clear))) && camera.Grabs == grabs,
            "参数设置失败不继续试拍");
        Console.WriteLine($"Exposure selection checks: {passed} passed."); return 0;
    }

    private sealed class Camera : ICameraService, ICameraExposureRange
    {
        public string DeviceName => "Exposure test";
        public bool IsOpen { get; private set; }
        public double Exposure; public int Grabs; public bool FailGrab, FailExposure;
        public (double MinimumUs, double MaximumUs) GetExposureRange() => (500, 4000);
        public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; return Task.CompletedTask; }
        public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }
        public Task SetExposureUsAsync(double value) { if (FailExposure) throw new IOException("exposure failed"); Exposure = value; return Task.CompletedTask; }
        public Task SetGainAsync(double value) => Task.CompletedTask;
        public Task<CameraFrame> GrabAsync(int timeoutMs = 3000)
        {
            if (FailGrab) throw new IOException("grab failed"); Grabs++;
            return Task.FromResult(new CameraFrame(Enumerable.Repeat((byte)(Exposure / 100), 4096).ToArray(), 64, 64, DateTime.Now));
        }
        public void Dispose() { }
    }
}
