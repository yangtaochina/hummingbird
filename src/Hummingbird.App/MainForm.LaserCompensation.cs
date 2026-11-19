using System.Globalization;
using System.Text;
using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Vision;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private TabPage BuildLaserCompensationTab(Func<RingScanSession> readSession,
        Func<RingScanSettings> readSettings, Func<RingScanResult?> readFit,
        Func<IReadOnlyList<HoleTarget>> readTargets, Action<RingScanSettings> restoreSettings, out Action invalidate)
    {
        var tab = new TabPage("激光补偿（预览）");
        LaserSpotCalibration? calibration = null;
        LaserCompensationPreview? preview = null;
        var commands = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        var optics = new CheckBox { Text = "镜头、相机高度、工作距离与落点来源一致", AutoSize = true };
        var detail = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            Dock = DockStyle.Fill,
            ScrollBars = ScrollBars.Vertical,
            Text = "请导入独立的激光落点预览标定JSON。\r\n定位通过后自动计算18孔补偿；加工前再次核对来源及光学条件。"
        };
        var table = new DataGridView
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            AllowUserToAddRows = false,
            AllowUserToDeleteRows = false,
            RowHeadersVisible = false,
            AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None
        };
        foreach (string title in new[] { "孔号", "相机X", "相机Y", "激光指令X", "激光指令Y" })
            table.Columns.Add(new DataGridViewTextBoxColumn
            {
                HeaderText = title,
                Width = title == "孔号" ? 55 : 105,
                SortMode = DataGridViewColumnSortMode.NotSortable
            });
        var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        var views = new TabControl { Dock = DockStyle.Fill };
        var coordinates = new TabPage("补偿坐标表"); coordinates.Controls.Add(table);
        var source = new TabPage("落点来源原图"); source.Controls.Add(picture);
        views.TabPages.AddRange([coordinates, source]);
        var hint = new Label
        {
            Dock = DockStyle.Fill,
            AutoSize = true,
            Padding = new Padding(3),
            Text = "补偿候选未完成独立复核。工件取放后须重新采集圆心/方向；旧定位只可离线查看。复核MPF只导出文本，不启动机床或控制激光。"
        };

        void InvalidatePreview()
        {
            preview = null; table.Rows.Clear();
            detail.Text = calibration is null ? "尚未导入落点预览标定。"
                : Description(calibration) + "\r\n孔位或参数变化后，请重新计算补偿预览。";
        }
        invalidate = InvalidatePreview;
        optics.CheckedChanged += (_, _) => { InvalidatePreview(); _refreshLaserCompensation?.Invoke(); };

        void Run(Action action,bool userAction=true)
        {
            if(userAction)
            {
                if(_workspaceBusy || _laserExposureTask is {IsCompleted:false})return;
                _stopCaptureMonitor?.Invoke();
            }
            try { action(); }
            catch (Exception ex) { preview = null; table.Rows.Clear(); detail.Text = ex.Message; _log.Error("[激光补偿预览] " + ex.Message); }
        }
        var extra = new ContextMenuStrip();
        void Extra(string text, Action action) => extra.Items.Add(text, null, (_, _) => Run(action));
        LaserSpotCalibration Current(bool requireConfirmation=true)
        {
            var value = calibration ?? throw new InvalidOperationException("请先导入激光落点预览标定JSON。");
            if(_state.OpticalRecalculationRequired)throw new InvalidOperationException("光学条件已调整，旧落点已停用；请在光学与落点页重新计算并导入新的真实试打来源。");
            if (requireConfirmation && !optics.Checked && !_guidedOperationConfirmed) throw new InvalidOperationException("请核对镜头、相机高度和工作距离是否仍与落点来源一致。");
            value.VerifySources(); value.ValidateOptics(readSettings());
            return value;
        }
        _readProductionCalibration=()=>Current();
        _readCalibrationForStartup=()=>Current(false);
        void LoadCalibration(string path)
        {
            calibration = null; optics.Checked = false; InvalidatePreview();
            var previous = picture.Image; picture.Image = null; previous?.Dispose();
            var loaded = LaserSpotCalibration.Load(path);
            if(_state.OpticalRecalculationRequired && loaded.SourceTrialSha256==_state.InvalidatedLaserTrialSha256)
                throw new InvalidDataException("这是光学调整前的旧试打来源，不能重新启用；请选择调整后的新试打结果。");
            if(_state.OpticalChangedAt is {} changed)
            {
                using var trial=JsonDocument.Parse(File.ReadAllText(loaded.SourceTrialPath));
                if(!trial.RootElement.TryGetProperty("Started",out var started) || started.GetDateTimeOffset()<changed)
                    throw new InvalidDataException("落点试打早于本次光学调整，请使用调整后取得的新来源。");
            }
            var before = RingScanSolver.LoadFrame(loaded.BeforeImagePath);
            var after = RingScanSolver.LoadFrame(loaded.AfterImagePath);
            if (before.Width != loaded.OpticalSettings.ImageWidth || before.Height != loaded.OpticalSettings.ImageHeight ||
                after.Width != loaded.OpticalSettings.ImageWidth || after.Height != loaded.OpticalSettings.ImageHeight)
                throw new InvalidDataException("落点原图实际尺寸与标定来源不符。");
            ShowFrame(picture, after, null);
            using (var graphics = Graphics.FromImage(picture.Image!))
            using (var pen = new Pen(Color.Cyan, 2))
            {
                float x = (float)loaded.PixelX, y = (float)loaded.PixelY;
                graphics.DrawLine(pen, x - 18, y, x + 18, y); graphics.DrawLine(pen, x, y - 18, x, y + 18);
                float centerX = (after.Width - 1) / 2f, centerY = (after.Height - 1) / 2f;
                graphics.DrawLine(Pens.Orange, centerX - 14, centerY, centerX + 14, centerY);
                graphics.DrawLine(Pens.Orange, centerX, centerY - 14, centerX, centerY + 14);
            }
            calibration = loaded; _state.OpticalRecalculationRequired=false; InvalidatePreview(); views.SelectedTab = source;
            _state.LaserCalibrationPath = Path.GetFullPath(path); SaveOperatorState();
            detail.Text = Description(loaded) + "\r\n青色十字：落点候选；橙色十字：画面中心。请核对光学条件。";
            try { loaded.ValidateOptics(readSettings()); }
            catch (InvalidDataException) { detail.Text += "\r\n当前XY标定不同，可点击“新建记录并采用来源XY标定”。这不会恢复旧工件位姿。"; }
        }
        _markOpticsChanged=()=>
        {
            _state.OpticalRecalculationRequired=true;_state.OpticalChangedAt=DateTimeOffset.Now;
            _invalidateOpticalBaseline?.Invoke();
            if(calibration is not null)_state.InvalidatedLaserTrialSha256=calibration.SourceTrialSha256;
            optics.Checked=false;InvalidatePreview();_fixedCConfirmed.Checked=false;SaveOperatorState();
            detail.Text="光学条件已调整，旧落点停用。请重新计算XY响应、建立六步基准并取得新的试打落点来源。";
        };
        _confirmAutomaticOpticsResult=path=>
        {
            if(_workspaceBusy || _laserExposureTask is {IsCompleted:false})throw new InvalidOperationException("请等待当前校准结束。");
            var checkedValue=LaserSpotCalibration.Load(path);
            if(calibration is null || calibration.SourceTrialSha256!=checkedValue.SourceTrialSha256 || calibration.PixelX!=checkedValue.PixelX || calibration.PixelY!=checkedValue.PixelY || Path.GetFullPath(path)!=Path.GetFullPath(_state.LaserCalibrationPath))
                throw new InvalidOperationException("本次结果与当前载入的落点不同，请重新核对。");
            checkedValue.ValidateOptics(readSettings());
            if(_state.OpticalRecalculationRequired)throw new InvalidOperationException("光学条件又有改变，本次结果已经停用。");
            optics.Checked=true;
        };
        _importOpticalCalibration=path=>{LoadCalibration(path);return Task.CompletedTask;};
        _restoreLaserCalibration = () =>
        {
            if (File.Exists(_state.LaserCalibrationPath)) Run(() => LoadCalibration(_state.LaserCalibrationPath));
            return Task.CompletedTask;
        };
        commands.Controls.Add(Btn("导入落点JSON", (_, _) => Run(() =>
        {
            using var dialog = new OpenFileDialog { Filter = "激光落点预览标定|*.json" };
            if (dialog.ShowDialog(this) == DialogResult.OK) LoadCalibration(dialog.FileName);
        })));
        commands.Controls.Add(Btn("采用来源XY标定", (_, _) => Run(() =>
        {
            var value = calibration ?? throw new InvalidOperationException("请先导入激光落点预览标定JSON。");
            value.VerifySources();
            restoreSettings(JsonSerializer.Deserialize<RingScanSettings>(JsonSerializer.Serialize(value.OpticalSettings))!);
            optics.Checked = false; InvalidatePreview();
            detail.Text = Description(value) + "\r\n已新建空定位记录并采用来源XY参数；仍需核对光学条件，工件圆心/方向需重新采集。";
        })));
        Extra("清除落点候选", () =>
        {
            calibration = null; optics.Checked = false; InvalidatePreview();
            _state.LaserCalibrationPath = ""; SaveOperatorState();
            var previous = picture.Image; picture.Image = null; previous?.Dispose();
        });
        void CalculatePreview()
        {
            preview = null; table.Rows.Clear();
            var value = Current();
            var fit = readFit() ?? throw new InvalidOperationException("请先用当前装夹的照片计算圆心、方向和18孔预览。");
            var result = LaserSpotCompensation.Preview(value, readSettings(), fit.ReferenceZ, fit.ReferenceC, readTargets());
            foreach (var target in result.Targets)
                table.Rows.Add(target.Id, target.CameraX.ToString("F4", CultureInfo.InvariantCulture),
                    target.CameraY.ToString("F4", CultureInfo.InvariantCulture),
                    target.LaserCommandX.ToString("F4", CultureInfo.InvariantCulture),
                    target.LaserCommandY.ToString("F4", CultureInfo.InvariantCulture));
            preview = result; views.SelectedTab = coordinates;
            detail.Text = Description(value) + $"\r\n已计算{result.Targets.Count}孔候选：激光指令＝相机坐标＋补偿。\r\n"
                + "补偿不改变CAD孔距或定位结果；加工前会再次核对本次来源、Z/C及参数。";
        }
        _refreshLaserCompensation = () =>
        {
            if (calibration is not null && optics.Checked && readFit() is not null && readTargets().Count == 18) Run(CalculatePreview,userAction:false);
        };
        Extra("刷新18孔补偿预览", CalculatePreview);
        Extra("保存补偿预览JSON", () =>
        {
            Current();
            var result = preview ?? throw new InvalidOperationException("请先重新计算18孔补偿预览。");
            using var dialog = new SaveFileDialog { Filter = "激光补偿预览|*.json", FileName = "laser_compensation_preview.json" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(dialog.FileName, JsonSerializer.Serialize(new
            {
                Kind = "LaserCompensationPreview",
                GeneratedAt = DateTimeOffset.Now,
                PreviewOnly = true,
                OpticalConditionsOperatorConfirmed = true,
                PartClampingVerified = false,
                SourceSession = readSession(),
                Result = result,
                Warning = "候选预览，不代表工件当前装夹或实际加工精度已验收。"
            }, new JsonSerializerOptions { WriteIndented = true }));
            detail.Text = "已保存补偿前后坐标、光学参数和落点来源；该报告不下发到加工流程。";
        });
        Extra("导出落点无出光复核MPF", () =>
        {
            var value = Current();
            string program = LaserSpotCompensation.RecenterTrial(value, readSettings());
            using var dialog = new SaveFileDialog { Filter = "落点往返复核程序|*.MPF", FileName = "HM_SPOT_CHECK.MPF" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            File.WriteAllText(dialog.FileName, program, Encoding.ASCII);
            RecordEvidenceNc(dialog.FileName);
            detail.Text = $"已导出 {dialog.FileName}\r\n必须先在设备上禁止出光，保持Z/C不变，核对起始MCS与程序注释的试打位置一致。\r\n"
                + "N40：现有痕迹到画面中心；N60：返回原落点像素。两处停稳拍照，禁止运行中补打。\r\n"
                + "程序包含小范围XY运动，仅由现场面板启动。";
        });
        var more = Btn("更多 ▾", (sender, _) => extra.Show((Control)sender!, new Point(0, ((Control)sender!).Height)));
        commands.Controls.Add(more);
        var tip = new ToolTip(); tip.SetToolTip(more, hint.Text);

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 105));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(commands, 0, 0); layout.Controls.Add(optics, 0, 1); layout.Controls.Add(detail, 0, 2);
        layout.Controls.Add(views, 0, 3); tab.Controls.Add(layout);
        picture.Disposed += (_, _) => picture.Image?.Dispose(); tab.Disposed += (_, _) => { extra.Dispose(); tip.Dispose(); };
        return tab;
    }

    private static string Description(LaserSpotCalibration calibration)
    {
        var offset = LaserSpotCompensation.Offset(calibration, calibration.OpticalSettings);
        return $"候选像素 ({calibration.PixelX:F2}, {calibration.PixelY:F2})；来源 {Path.GetFileName(Path.GetDirectoryName(calibration.SourceTrialPath))}\r\n"
            + $"X指令补偿 {offset.X:+0.0000;-0.0000;0.0000}mm，Y指令补偿 {offset.Y:+0.0000;-0.0000;0.0000}mm\r\n"
            + $"参考Z={calibration.ReferenceZ:F4}，C={calibration.ReferenceC:F4}°；仅预览，尚未验收。";
    }
}
