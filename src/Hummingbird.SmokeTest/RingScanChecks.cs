using System.Globalization;
using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

internal static class RingScanChecks
{
    public static int Run(string? siteRecord)
    {
        int failed = 0;
        void Check(bool condition, string text)
        {
            Console.WriteLine((condition ? "[PASS] " : "[FAIL] ") + text);
            if (!condition) failed++;
        }
        void Reject(Action operation, string text)
        {
            try { operation(); Check(false, text); }
            catch (InvalidDataException) { Check(true, text); }
        }
        var solver = new RingScanSolver();
        string folder = Path.Combine(Path.GetTempPath(), "hummingbird-ring-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var session = SyntheticSession(folder);
            var fit = solver.Analyze(session);
            double error = double.Hypot(fit.CenterX + 12.3, fit.CenterY - 45.6);
            Check(error < 0.002, $"合成圆环经X/Y位移及旋转像素矩阵还原，圆心误差 {error * 1000:F2}µm < 2µm");
            Check(Math.Abs(fit.RadiusMm - 14.45) < 0.002, "保持真实半径，拟合使用四处独立采图");
            Check(fit.Views.Count == 4 && fit.Views.All(v => v.Count >= 20), "四个方位均参与拟合");

            RingScanSession Copy() => JsonSerializer.Deserialize<RingScanSession>(JsonSerializer.Serialize(session))!;
            var blankHint=Copy();blankHint.Settings.HintX=0;blankHint.Settings.HintY=0;
            var recovered=solver.Analyze(blankHint);
            Check(recovered.Initialization is {EstimatedFromCapturePositions:true} &&
                double.Hypot(recovered.CenterX-fit.CenterX,recovered.CenterY-fit.CenterY)<.002,
                "粗中心默认0时由本次四方位初始化，仍由原图恢复圆心");
            Check(blankHint.Settings.HintX==0 && blankHint.Settings.HintY==0,
                "自动初始化不改写输入记录和采图元数据");
            Check(fit.Initialization is {EstimatedFromCapturePositions:false},"有效的人工粗中心保留原行为");
            var degenerate=Copy();degenerate.Settings.HintX=0;degenerate.Settings.HintY=0;
            degenerate.Frames=degenerate.Frames.Select((f,i)=>f with {X=-150+i,Y=-190}).ToList();
            Reject(()=>solver.Analyze(degenerate),"粗中心自动初始化不接受共线采图位置");
            var clustered=Copy();clustered.Settings.HintX=0;clustered.Settings.HintY=0;
            clustered.Frames=clustered.Frames.Select((f,i)=>f with {X=-150+.2*Math.Cos(i),Y=-190+.2*Math.Sin(i)}).ToList();
            Reject(()=>solver.Analyze(clustered),"四个很近的位置不能借自动粗中心冒充整圈采图");
            var partial=Copy();partial.Settings.HintX=0;partial.Settings.HintY=0;
            partial.Frames=partial.Frames.Select((f,i)=>f with {
                X=-12.3+14.45*Math.Cos(i*.25),Y=45.6+14.45*Math.Sin(i*.25)}).ToList();
            Reject(()=>solver.Analyze(partial),"同一段圆弧的多张照片仍拒绝覆盖不足，不放宽150度间隔");
            var offCenter=SyntheticSession(folder,.95,.20,"off_center_");
            var offCenterFit=solver.Analyze(offCenter);
            Check(double.Hypot(offCenterFit.CenterX+12.3,offCenterFit.CenterY-45.6)<.004 &&
                offCenterFit.Views.All(v=>v.Count>=20),"圆环偏离画面中心约560像素时仍从有效画幅提取四方位圆边");
            var changed = Copy(); changed.Frames[1] = changed.Frames[1] with { C = 1 };
            Reject(() => solver.Analyze(changed), "拒绝C轴转动后混合照片");
            changed = Copy(); changed.Frames[2] = changed.Frames[2] with { Z = 0.1 };
            Reject(() => solver.Analyze(changed), "拒绝不同Z高度照片");
            changed = Copy(); changed.Settings.Jxy = changed.Settings.Jxx; changed.Settings.Jyy = changed.Settings.Jyx;
            Reject(() => solver.Analyze(changed), "拒绝奇异像素响应矩阵");
            changed = Copy(); changed.Settings.ImageWidth = 1919;
            Reject(() => solver.Analyze(changed), "拒绝图像分辨率与标定不符");
            changed = Copy(); changed.Frames = Enumerable.Repeat(changed.Frames[0], 4).ToList();
            Reject(() => solver.Analyze(changed), "拒绝四张同方位图伪装成全周采样");
            changed = Copy(); changed.Frames[1] = changed.Frames[1] with { Y = changed.Frames[1].Y + 0.6 };
            Reject(() => solver.Analyze(changed), "拒绝径向坐标配错造成的圆边不一致");
            changed = Copy(); changed.Frames[0] = changed.Frames[0] with { C = 359.999 };
            changed.Frames[1] = changed.Frames[1] with { C = 0.001 };
            Check(solver.Analyze(changed).Views.Count == 4, "C角度按360°环绕比较");

            string blank = Path.Combine(folder, "blank.bmp");
            RingScanSolver.SaveFrame(blank, new CameraFrame(new byte[1920 * 1200], 1920, 1200, DateTime.Now));
            changed = Copy(); changed.Frames[0] = changed.Frames[0] with { ImagePath = blank };
            Reject(() => solver.Analyze(changed), "拒绝单个方位无圆边，避免静默丢帧");

            string record = Path.Combine(folder, "record.json");
            session.Save(record);
            var reloaded = RingScanSession.Load(record);
            Check(reloaded.Frames.All(f => File.Exists(f.ImagePath)), "相对图像路径可从JSON所在目录重新加载");
            Check(double.Hypot(solver.Analyze(reloaded).CenterX - fit.CenterX,
                solver.Analyze(reloaded).CenterY - fit.CenterY) < 1e-9, "采图记录保存重载保留结果");

            var observedAt = new DateTime(2026, 11, 12, 12, 0, 0, DateTimeKind.Utc);
            var bound = RingCaptureArchive.BindImage(session.Frames[0], session.Settings, observedAt,
                1920, 1200, AxisCoordinateSource.OpcUaReadback, AxisCoordinateSource.ManualPanelEntry, null, "RotaryScan");
            string sidecar = RingCaptureArchive.SaveSidecar(bound, session.Settings);
            var single = RingScanSession.Load(sidecar).Frames.Single();
            Check(single.Capture?.XyzSource == AxisCoordinateSource.OpcUaReadback &&
                single.Capture.CSource == AxisCoordinateSource.ManualPanelEntry && single.Capture.CNode == null &&
                single.Capture.HostCaptureTime == observedAt && File.Exists(single.ImagePath),
                "单图JSON保留真实数据来源和时间，不将手填C当自动读回");
            Check(reloaded.Frames.All(f => f.Capture == null), "旧记录兼容读取且不伪造采图来源");
            var imported = RingCaptureArchive.BindImage(session.Frames[1], session.Settings, null, 1920, 1200,
                AxisCoordinateSource.Unknown, AxisCoordinateSource.Unknown, null, "ImportedOrManualRegistration");
            Check(imported.Capture!.HostCaptureTime == null && imported.Capture.CSource == AxisCoordinateSource.Unknown,
                "历史照片补档不推测拍照时间或坐标来源");
            Reject(() => RingCaptureArchive.BindImage(session.Frames[0], session.Settings, observedAt, 1920, 1200,
                AxisCoordinateSource.OpcUaReadback, AxisCoordinateSource.OpcUaReadback, null, "FixedCScan"),
                "未提供实际C节点时不允许标记为C自动读回");
            var actualFrame = RingCaptureArchive.BindImage(session.Frames[0], session.Settings, observedAt, 1920, 1200,
                AxisCoordinateSource.OpcUaReadback, AxisCoordinateSource.OpcUaReadback, "/verified/actualC", "FixedCScan");
            Check(actualFrame.Capture!.CNode == "/verified/actualC", "自动C记录保留反馈节点");
            double oldJ = session.Settings.Jxx;
            session.Settings.Jxx += 10;
            Check(bound.Capture!.SettingsAtRegistration.Jxx == oldJ, "标定参数快照不随之后界面修改变化");
            session.Settings.Jxx = oldJ;
            byte[] original = File.ReadAllBytes(bound.ImagePath);
            File.WriteAllBytes(bound.ImagePath, [1,2,3]);
            Reject(() => RingCaptureArchive.VerifyImage(bound), "原图被覆盖后拒绝静默使用错配记录");
            changed = Copy(); changed.Frames[0] = bound;
            Reject(() => solver.Analyze(changed), "圆心拟合前核对原图与记录一致性");
            File.WriteAllBytes(bound.ImagePath, original);
            var edited = bound with { C = 5, Capture = bound.Capture! with { EditedAfterCapture = true } };
            RingCaptureArchive.SaveSidecar(edited, session.Settings);
            Check(RingScanSession.Load(sidecar).Frames[0].Capture!.EditedAfterCapture,
                "记录修改标志在保存重载后仍保留");

            var template = HolePatternTemplate.LoadJson("templates/dial_18holes.json");
            var targets = RingScanPreview.Targets(fit, template, 30);
            double expected = double.Hypot(template.Holes[0].X - template.Holes[1].X, template.Holes[0].Y - template.Holes[1].Y);
            double actual = double.Hypot(targets[0].MachineX - targets[1].MachineX, targets[0].MachineY - targets[1].MachineY);
            Check(targets.Count == 18 && Math.Abs(actual - expected) < 1e-10, "18孔刚性变换保持CAD孔距");
            var axisTemplate = new HolePatternTemplate { Holes = [new HoleTemplate(1, 0, 10, 0.3)] };
            var north = RingScanPreview.Targets(fit, axisTemplate, 90)[0];
            var east = RingScanPreview.Targets(fit, axisTemplate, 0)[0];
            Check(Math.Abs(north.MachineY - fit.CenterY - 10) < 1e-9 &&
                Math.Abs(east.MachineX - fit.CenterX - 10) < 1e-9, "12点角度定义与机床轴向一致");
            Reject(() => RingScanPreview.Targets(fit, template, double.NaN), "缺少方向角时拒绝孔位预览");
            var culture = CultureInfo.CurrentCulture;
            try
            {
                CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");
                string nc = RingScanPreview.CameraTrial(fit, targets);
                Check(nc.Contains("SUPA G1") && nc.Contains("F60") && nc.Split(" M0").Length - 1 == 19 &&
                    !nc.Contains("G0 ") && nc.Contains("0.0000"), "相机试走采用MCS、逐孔暂停与固定小数点");
            }
            finally { CultureInfo.CurrentCulture = culture; }

            if (siteRecord is not null)
            {
                var site = RingScanSession.Load(Path.GetFullPath(siteRecord));
                var result = solver.Analyze(site);
                Console.WriteLine(FormattableString.Invariant($"SITE X={result.CenterX:F6} Y={result.CenterY:F6} R={result.RadiusMm:F6} RMS={result.RmsMm:F6} SENSITIVITY={result.SensitivityMm:F6}"));
                Check(result.Views.Count == 4 && result.RmsMm < 0.030 && result.SensitivityMm < 0.020 &&
                    double.Hypot(result.CenterX + 147.396, result.CenterY + 196.609) < 0.015,
                    "2026-11-11现场四帧与离线试算一致");
                foreach (var view in result.Views) Console.WriteLine($"  {view.Name}: N={view.Count}, RMS={view.RmsMm * 1000:F1}µm");
            }
        }
        catch (Exception ex) { Console.WriteLine(ex); failed++; }
        finally
        {
            // 仅删除本测试在独立临时目录创建的文件。
            foreach (string file in Directory.EnumerateFiles(folder)) File.Delete(file);
            Directory.Delete(folder);
        }
        Console.WriteLine(failed == 0 ? "===== RING SCAN ALL PASS =====" : $"===== {failed} FAILED =====");
        return failed == 0 ? 0 : 1;
    }

    internal static RingScanSession SyntheticSession(string folder,double stageOffsetX=.12,double stageOffsetY=-.08,string prefix="")
    {
        const double cx = -12.3, cy = 45.6, radius = 14.45;
        var data = new RingScanSession { Settings = new RingScanSettings { HintX = cx + 0.1, HintY = cy - 0.2 } };
        for (int view = 0; view < 4; view++)
        {
            double angle = view * Math.PI / 2;
            var pose = new RingScanFrame(view.ToString(), Path.Combine(folder, $"{prefix}view{view}.bmp"),
                cx + radius * Math.Cos(angle) + stageOffsetX, cy + radius * Math.Sin(angle) + stageOffsetY, 0, 0);
            var gray = new byte[1920 * 1200];
            for (int y = 0; y < 1200; y++)
                for (int x = 0; x < 1920; x++)
                {
                    var p = data.Settings.ToMachine(pose, x, y);
                    double radial = double.Hypot(p.X - cx, p.Y - cy) - radius;
                    gray[y * 1920 + x] = (byte)(20 + 200 * Math.Exp(-radial * radial / (2 * 0.04 * 0.04)));
                }
            RingScanSolver.SaveFrame(pose.ImagePath, new CameraFrame(gray, 1920, 1200, DateTime.Now));
            data.Frames.Add(pose);
        }
        return data;
    }
}
