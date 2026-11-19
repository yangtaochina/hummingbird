using System.Collections.Concurrent;
using System.Text.Json;
using Hummingbird.Core;

internal static class GuidedCaptureChecks
{
    public static async Task<int> Run()
    {
        int passed = 0, failed = 0;
        void Check(bool ok, string name) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
        async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch { return true; } }
        bool Reject(Action action) { try { action(); return false; } catch { return true; } }
        using var camera = new Camera(); var coordinator = new CameraCaptureCoordinator(camera);
        Check(await Fails(() => coordinator.CaptureAsync(5000, 0)) && camera.Grabs == 0, "未连接时不采图");
        await coordinator.OpenAsync(5000, 2);
        Check(camera.IsOpen && camera.Events.SequenceEqual(new[] { "open", "exp:5000", "gain:2" }), "连接后统一下发相机参数");
        camera.Events.Clear(); await coordinator.CaptureAsync(2000, 1);
        Check(camera.Events.SequenceEqual(new[] { "exp:2000", "gain:1", "grab" }), "正式采图前下发当前曝光和增益");
        camera.FailExposure = true; int before = camera.Grabs;
        Check(await Fails(() => coordinator.CaptureAsync(3000, 0)) && camera.Grabs == before, "曝光下发失败不得归档为成功采图");
        camera.FailExposure = false; camera.FailGain = true;
        Check(await Fails(() => coordinator.CaptureAsync(3000, 0)) && camera.Grabs == before, "增益下发失败不得采图"); camera.FailGain = false;
        Check(await Fails(() => coordinator.CaptureAsync(double.NaN, 0)) && await Fails(() => coordinator.CaptureAsync(10, 0)) &&
            await Fails(() => coordinator.CaptureAsync(5000, 21)), "拒绝无效参数");
        camera.Hold = true; var capture = coordinator.CaptureAsync(5000, 0);
        await camera.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Check(await coordinator.TryPreviewAsync() is null, "正式采图占用相机时跳过预览");
        var apply = coordinator.ApplyAsync(6000, 1); await Task.Delay(30);
        Check(!apply.IsCompleted, "参数修改等待当前采图完成，不并发访问相机");
        camera.Release.SetResult(); await capture; await apply;
        Check(camera.Exposure == 6000 && camera.Gain == 1, "采图完成后处理排队的参数修改");
        camera.FailGrab = true; Check(await Fails(() => coordinator.CaptureAsync(5000, 0)), "采图故障向上报告"); camera.FailGrab = false;
        Check(await coordinator.TryPreviewAsync() is not null, "异常后相机锁正确释放");
        await coordinator.CloseAsync(); Check(await coordinator.TryPreviewAsync() is null, "断开后预览不调用相机");

        string root = Path.Combine(Path.GetTempPath(), "hummingbird-guided-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        try
        {
            var store = new OperatorWorkspaceStore(Path.Combine(root, "prefs.json"));
            Check(store.Load().LastSessionPath == "", "首次启动使用空偏好");
            var prefs = new OperatorWorkspaceState
            {
                CameraExposureUs = 5000,
                CameraGain = 1,
                LaserRequestMs = 2000,
                LastManualC = "254.8241",
                CaptureRoot = root,
                LastSessionPath = "session.json",
                LastRouteSessionPath = "route.json",
                LaserCalibrationPath = "laser.json"
            };
            store.Save(prefs); var restored = store.Load();
            Check(restored.CameraExposureUs == 5000 && restored.LastRouteSessionPath == "route.json" && restored.LaserCalibrationPath == "laser.json", "保存并恢复参数和进度路径");
            string json = File.ReadAllText(store.Path);
            Check(!json.Contains("Password") && !json.Contains("Confirmed") && restored.LaserRequestMs==2000, "偏好保留已确定出光请求时长，不保存密码和装夹有效确认");
            File.WriteAllText(store.Path, "{\"Version\":2}"); Check(Reject(() => store.Load()), "拒绝未知偏好版本");
            File.WriteAllText(store.Path, "{\"CameraExposureUs\":-1}"); Check(Reject(() => store.Load()), "拒绝损坏的曝光偏好");
            store.Save(prefs); Check(Directory.GetFiles(root, "*.tmp").Length == 0 && store.Load().CameraGain == 1, "偏好原子替换后无残留临时文件");

            var settings = new RingScanSettings(); var session = new RingScanSession { Settings = settings };
            (double X, double Y)[] poses = [(10, 5), (0, 14.45), (14.45, 0), (0, -14.45), (-14.45, 0), (-10, 5)];
            for (int i = 0; i < 6; i++)
            {
                string path = Path.Combine(root, $"source{i}.bmp"); File.WriteAllBytes(path, new byte[] { 1, 2, (byte)i }); var step = GuidedCapture.Steps[i];
                var frame = new RingScanFrame(step.Name, path, poses[i].X, poses[i].Y, -201.316, 254.8241) { Role = step.Role };
                session.Frames.Add(RingCaptureArchive.BindImage(frame, settings, DateTime.Now, 1920, 1200, AxisCoordinateSource.OpcUaReadback, AxisCoordinateSource.ManualPanelEntry, null, "FixedCScan"));
            }
            var route = GuidedCapture.Route(session); Check(route.Count == 6 && route[0].Role == RingFrameRole.Reference1 && route[5].Role == RingFrameRole.Reference11, "完整记录按六步生成采图路线");
            string program = GuidedCapture.Program(route); var lines = program.Split('\n').Where(l => l.StartsWith('N')).ToArray();
            Check(lines.Count(l => l.Contains("SUPA G1")) == 6 && lines.Count(l => l.Contains(" M0")) == 7 &&
                lines.All(l => !l.Contains(" Z") && !l.Contains(" C") && !l.Contains("M3 ") && !l.Contains("M4 ")) && !program.Contains("M62"), "采图MPF仅XY逐点暂停，无Z/C运动或出光指令");
            Check(Reject(() => GuidedCapture.Program(route.Take(5).ToArray())), "禁止导出不完整的路线");
            var original = session.Frames[0];
            session.Frames[0] = original with { Z = original.Z + .02 }; Check(Reject(() => GuidedCapture.Route(session)), "拒绝Z不一致的路线");
            session.Frames[0] = original with { C = original.C + .1 }; Check(Reject(() => GuidedCapture.Route(session)), "拒绝C不一致的路线");
            session.Frames[0] = original with { Capture = original.Capture! with { XyzSource = AxisCoordinateSource.Simulator } };
            Check(Reject(() => GuidedCapture.Route(session)), "模拟坐标不能复用为真机路线");
            session.Frames[0] = original with { Capture = original.Capture! with { Purpose = "RotaryScan" } };
            Check(Reject(() => GuidedCapture.Route(session)), "拒绝混入旋转采集记录"); session.Frames[0] = original;
            session.Frames.Add(original); Check(Reject(() => GuidedCapture.Route(session)), "拒绝重复或额外步骤"); session.Frames.RemoveAt(6);
            File.AppendAllText(original.ImagePath, "changed"); Check(Reject(() => GuidedCapture.Route(session)), "路线恢复校验原图指纹");
            var seedFrame = original with { FaceSeed = new(800, 480, 80, "old", "OperatorImageRectangle") };
            var translated = GuidedCapture.TranslateRegion(seedFrame, settings, original.X + .1, original.Y - .1, "new");
            Check(translated is not null && Math.Abs(translated.Px - (800 + .1 * settings.Jxx - .1 * settings.Jxy)) < 1e-8 && translated.ImageSha256 == "new" &&
                translated.SelectionSource.Contains("MustBeRefitted"), "旧框选只平移为新照片初值，绑定新指纹并要求重新拟合");
            Check(GuidedCapture.TranslateRegion(seedFrame, settings, original.X + 100, original.Y, "new") is null, "落在画外的旧框选不复用");
            var gate = new CapturePositionGate(); var p = route[0]; var time = DateTimeOffset.UtcNow;
            bool Observe(double milliseconds, double dx = 0, double dc = 0) => gate.Observe(p, p.X + dx, p.Y, p.Z, p.C + dc, time.AddMilliseconds(milliseconds));
            Check(!Observe(0) && !Observe(499) && Observe(500), "到位后至少连续观察500ms才允许相机采图");
            gate.Reset(); Check(!Observe(0) && !Observe(300, .003) && !Observe(600, .003) && Observe(800, .003), "到位范围内再次移动也重新计时");
            Check(!Observe(1000, .006) && !Observe(1500) && Observe(2000), "离开目标位置后清空判稳状态");
            gate.Reset(); Check(!Observe(0, 0, 360) && Observe(500, 0, 360), "C角度按360度周期比较");
            Check(!gate.Observe(p, double.NaN, p.Y, p.Z, p.C, time.AddSeconds(3)) && !Observe(3500), "无效读数拒绝采图并重新计时");
            gate.Reset(); Check(!Observe(0) && !Observe(400) && !Observe(300) && !Observe(799) && Observe(800), "系统时钟回退后重新判稳");
        }
        finally { Directory.Delete(root, true); }
        Console.WriteLine($"Guided capture checks: {passed} passed, {failed} failed."); return failed == 0 ? 0 : 1;
    }

    private sealed class Camera : ICameraService
    {
        public string DeviceName => "Test camera"; public bool IsOpen { get; private set; }
        public ConcurrentQueue<string> Events { get; } = new();
        public int Grabs; public double Exposure, Gain; public bool FailExposure, FailGain, FailGrab, Hold;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task OpenAsync(CancellationToken ct = default) { IsOpen = true; Events.Enqueue("open"); return Task.CompletedTask; }
        public Task CloseAsync() { IsOpen = false; return Task.CompletedTask; }
        public Task SetExposureUsAsync(double value) { if (FailExposure) throw new IOException("exposure failed"); Exposure = value; Events.Enqueue($"exp:{value}"); return Task.CompletedTask; }
        public Task SetGainAsync(double value) { if (FailGain) throw new IOException("gain failed"); Gain = value; Events.Enqueue($"gain:{value}"); return Task.CompletedTask; }
        public async Task<CameraFrame> GrabAsync(int timeoutMs = 3000)
        {
            if (FailGrab) throw new IOException("grab failed"); Grabs++; Events.Enqueue("grab");
            if (Hold) { Hold = false; Entered.SetResult(); await Release.Task; }
            return new(new byte[32 * 32], 32, 32, DateTime.Now);
        }
        public void Dispose() { }
    }
}
