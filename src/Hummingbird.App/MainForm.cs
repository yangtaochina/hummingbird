using System.Globalization;
using System.Runtime.InteropServices;
using Hummingbird.Core;
using Hummingbird.Devices.LaserRtu;
using Hummingbird.Devices.OpcUa;
using Hummingbird.Devices.Sim;
#if HAS_PYLON
using Hummingbird.Devices.Basler;
#endif

namespace Hummingbird.App;

/// <summary>固定C工作站：定位采集、单点试打、NC文件、设备维护。启动不连接或驱动设备。</summary>
public sealed partial class MainForm : Form
{
    private readonly IAppLogger _log;
    private readonly AppConfig _cfg;
    private readonly ICameraService? _camera;
    private readonly CameraCaptureCoordinator? _cameraCapture;
    private readonly ICncService _cnc;
    private readonly ILaserService _laser;
    private readonly HolePatternTemplate _template;
    private readonly OperatorWorkspaceStore _stateStore;
    private OperatorWorkspaceState _state;
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly RichTextBox _rtbLog = new() { Dock = DockStyle.Fill, ReadOnly = true, Font = new Font("Consolas", 9F) };
    private readonly Label _latestLog = new() { Name="LatestLogLine", Dock = DockStyle.Fill, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Text = "日志：暂无" };
    private readonly PictureBox _livePicture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label _liveStatus = new() { Dock = DockStyle.Bottom, Height = 38, Text = "相机未连接", AutoEllipsis=true };
    private DateTimeOffset? _lastPreviewAt;
    private DateTimeOffset _previewRetryAt;
    private readonly PictureBox _trialPicture = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly NumericUpDown _numExp = Num(20, 1000000, 5000, 100);
    private readonly NumericUpDown _numGain = Num(0, 20, 0, .5m);
    private readonly Label _cameraStatus = new() { AutoSize = true, Text = "相机未连接" };
    private readonly Label _lblPos = new() { AutoSize = true, Text = "数控未连接" };
    private string? _lastCncConnectionError;
    private readonly CheckBox _liveEnabled = new() { AutoSize = true, Text = "实时画面" };
    private readonly CheckBox _fixedCConfirmed = new() { AutoSize = true, Text = "本次装夹、参考状态及固定C已核对" };
    private readonly TextBox _txtCom = new() { Width = 80 };
    private readonly ComboBox _cmbBaud = new() { Width = 95, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly System.Windows.Forms.Timer _previewTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer _positionTimer = new() { Interval = 500 };
    private CancellationTokenSource? _cameraChange;
    private bool _previewBusy, _positionBusy, _workspaceBusy, _closing;
    private bool _settingSelectedExposure;
    private Task? _workspaceTask;
    private Action? _stopCaptureMonitor;
    private Func<string, Task>? _loadWorkspace;
    private Action<int>? _selectCaptureStep;
    private Func<int,CancellationToken,Task<RingScanFrame>>? _captureGuidedStep;
    private Func<Task>? _restoreLaserCalibration;
    private Action? _refreshLaserCompensation;
    private Action<string, string>? _logSink;

    public MainForm(IAppLogger? logger = null) : this(logger, null, null) { }
    internal MainForm(IAppLogger? logger, AppConfig? config, OperatorWorkspaceStore? store, bool offlineUiCheck = false)
    {
        _log = logger ?? new MemoryLogger(); _cfg = config ?? AppConfig.LoadOrDefault("config.json");
        if (_cfg.DeviceMode is not ("Real" or "Sim")) throw new InvalidDataException("DeviceMode只能为Real或Sim。");
        _stateStore = store ?? new OperatorWorkspaceStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hummingbird", "operator-state.json"));
        try { _state = _stateStore.Load(); } catch (Exception ex) { _state = new(); _log.Warn("操作记录未恢复：" + ex.Message); }
        Text = "飞秒创芯 · 固定C表盘工作站 v1.7.0" + (_cfg.DeviceMode == "Sim" ? " · 离线演示" : "");
        Font = new Font("Microsoft YaHei UI", 9F); ClientSize = new Size(1360, 900); MinimumSize = new Size(1100, 740); StartPosition = FormStartPosition.CenterScreen;
        _numExp.Value = (decimal)(_state.CameraExposureUs ?? Math.Clamp(double.IsFinite(_cfg.CameraExposureUs) ? _cfg.CameraExposureUs : 5000, 20, 1000000));
        _numGain.Value = (decimal)(_state.CameraGain ?? Math.Clamp(_cfg.CameraGain, 0, 20));
        _spotTrialC.Text = _state.LastManualC.Length > 0 ? _state.LastManualC : (_cfg.DeviceMode == "Sim" ? "0" : "");
        _txtCom.Text = _cfg.LaserComPort; _cmbBaud.Items.AddRange(new object[] { 9600, 19200, 38400, 57600, 115200 });
        _cmbBaud.SelectedItem = _cfg.LaserBaud; if (_cmbBaud.SelectedIndex < 0) _cmbBaud.SelectedItem = 115200;
        _template = HolePatternTemplate.LoadJson("templates/dial_18holes.json");
        if (_cfg.DeviceMode == "Sim")
        {
            _camera = new SimCameraService { Template = _template }; _cnc = new SimCncService(_log.Info); _laser = new SimLaserService(_log.Info);
        }
        else
        {
            _cnc = new OpcUaCncService(_log)
            {
                ProgramSharePath = _cfg.CncProgramShare,
                VarNamespaceKeyword = _cfg.CncVarNamespace,
                UserName = _cfg.CncUserName,
                Password = _cfg.CncPassword,
                UseSecureEndpoint = _cfg.CncUseSecurity,
                PosNodeX = _cfg.CncPosNodeX,
                PosNodeY = _cfg.CncPosNodeY,
                PosNodeZ = _cfg.CncPosNodeZ
            };
            _laser = new ModbusRtuLaserService(_log);
#if HAS_PYLON
            _camera = new BaslerCameraService(_cfg.CameraSerialNumber, _log);
#endif
        }
        if (_camera is not null) _cameraCapture = new(_camera);
        BuildUi(); HookLog(); UpdateLaserCalibrationButtons();
        _fixedCConfirmed.CheckedChanged += (_, _) => UpdateLaserCalibrationButtons();
        _numExp.ValueChanged += (_, _) => CameraParameterChanged(); _numGain.ValueChanged += (_, _) => CameraParameterChanged();
        _spotTrialC.TextChanged += (_, _) => { _fixedCConfirmed.Checked = false; _state.LastManualC = _spotTrialC.Text; SaveOperatorState(); };
        _liveEnabled.CheckedChanged += (_, _) => { if (_liveEnabled.Checked) { _previewTimer.Start();_previewRetryAt=DateTimeOffset.MinValue; } else { _previewTimer.Stop();_liveStatus.Text="实时预览已关闭；当前显示的是最后一帧。"; } };
        _previewTimer.Tick += async (_, _) => await PreviewTick(); _positionTimer.Tick += async (_, _) => await PositionTick();
        Shown += async (_, _) =>
        {
            if (offlineUiCheck) return;
            _positionTimer.Start();
            if (File.Exists(_state.LastSessionPath) && _loadWorkspace is not null) await _loadWorkspace(_state.LastSessionPath);
            if (_restoreLaserCalibration is not null) await _restoreLaserCalibration();
        };
        FormClosing += async (_, e) =>
        {
            _stopCaptureMonitor?.Invoke(); _previewTimer.Stop(); _cameraChange?.Cancel(); SaveOperatorState();
            if (_workspaceTask is { IsCompleted: false } task) { e.Cancel = true; await task; if (!IsDisposed) BeginInvoke(new Action(Close)); }
        };
        _log.Info("固定C工作站就绪；启动不连接设备、不移动轴、不恢复装夹有效确认。");
    }

    private void BuildUi()
    {
        BuildWorkflowUi();
    }
    private TabPage BuildSpotTrialTab()
    {
        var tab = new TabPage("单点试打");
        var layout = new TableLayoutPanel { Dock=DockStyle.Fill, ColumnCount=2, RowCount=1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,55));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,45));
        var flow = WorkflowFlow();
        flow.Controls.Add(Lbl("单点仅用于现场选定位置的试打和落点采样。加工参数在“维护 → 设备与参数”统一设置。"));
        flow.Controls.Add(BuildSingleSpotTrialControls());
        flow.Controls.Add(Btn("前往参数设置", (_,_)=>SelectPage(_environmentPage)));
        layout.Controls.Add(flow,0,0); layout.Controls.Add(_trialPicture,1,0);
        tab.Controls.Add(layout); return tab;
    }
    private TabPage BuildMaintenanceTab()
    {
        var tab = new TabPage("设备维护"); var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        var diagnostics = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, Width = 1000, Height = 280 };
        flow.Controls.Add(GroupBoxOf("相机维护", Btn("连接相机", async (_, _) => await UiAction(OpenCamera)),
            Btn("断开相机", async (_, _) => await UiAction(async () => { _liveEnabled.Checked = false; if (_cameraCapture is not null) await _cameraCapture.CloseAsync(); _cameraStatus.Text = "相机已断开"; })),
            Btn("读取相机参数范围", async (_, _) => await UiAction(async () => diagnostics.Text = _cameraCapture is null ? "相机不可用" : await _cameraCapture.DiagnosticsAsync()))));
        flow.Controls.Add(GroupBoxOf("数控连接", Lbl("端点：" + _cfg.CncEndpoint), Btn("连接数控", async (_, _) => await UiAction(ConnectCnc,_lblPos)),
            Btn("断开数控", async (_, _) => await UiAction(async () => await _cnc.DisconnectAsync(),_lblPos))));
        flow.Controls.Add(Lbl("日常定位使用固定C的六步采集；相机XY参数与激光落点可在定位采集页导入。"));
        flow.Controls.Add(diagnostics); tab.Controls.Add(flow); return tab;
    }
    private async Task UiAction(Func<Task> action,Label? failureTarget=null)
    {
        try { await action(); } catch (Exception ex) { _log.Error(ex.Message); (failureTarget??_cameraStatus).Text = ex.Message; }
    }
    private async Task ConnectAcquisitionDevices() { await UiAction(OpenCamera); await UiAction(ConnectCnc,_lblPos); UpdateLaserCalibrationButtons(); }
    private async Task ConnectCnc()
    {
        if(_cnc.IsConnected){await PositionTick();return;}
        _lastCncConnectionError=null;_lblPos.ForeColor=Color.DarkOrange;_lblPos.Text="正在连接数控……";
        try
        {
            bool ok=await _cnc.ConnectAsync(_cfg.CncEndpoint);if(!ok)throw new InvalidOperationException("数控未能建立会话。");
            _log.Info("数控已连接，采图时自动读取XYZ。");await PositionTick();UpdateLaserCalibrationButtons();
        }
        catch(Exception ex)
        {
            string message=ex.Message;if(!string.IsNullOrEmpty(_cfg.CncPassword))message=message.Replace(_cfg.CncPassword,"[已隐藏]",StringComparison.Ordinal);
            _lastCncConnectionError=message;_lblPos.ForeColor=Color.DarkRed;_lblPos.Text="数控连接失败："+message;
            throw new InvalidOperationException("数控连接失败："+message);
        }
    }
    private async Task OpenCamera()
    {
        if (_cameraCapture is null) throw new InvalidOperationException("相机不可用，请检查pylon安装和配置。");
        await _cameraCapture.OpenAsync((double)_numExp.Value, (double)_numGain.Value);
        _cameraStatus.Text = $"相机已连接，参数已下发：{_numExp.Value}µs / 增益{_numGain.Value}"; _liveEnabled.Checked = true;
        _previewRetryAt=DateTimeOffset.MinValue;await PreviewTick();
        UpdateLaserCalibrationButtons();
    }
    private void CameraParameterChanged()
    {
        _state.CameraExposureUs = (double)_numExp.Value; _state.CameraGain = (double)_numGain.Value; SaveOperatorState();
        if (_settingSelectedExposure) return;
        _cameraChange?.Cancel(); _cameraChange = new CancellationTokenSource(); _ = ApplyChangedCameraParameters(_cameraChange.Token);
    }
    private async Task ApplyChangedCameraParameters(CancellationToken token)
    {
        try
        {
            await Task.Delay(300, token);
            if (_camera is not { IsOpen: true }) { _cameraStatus.Text = "相机参数已保存，连接/拍照时自动下发。"; return; }
            await _cameraCapture!.ApplyAsync((double)_numExp.Value, (double)_numGain.Value, token);
            if (!token.IsCancellationRequested) _cameraStatus.Text = $"相机参数已下发：{_numExp.Value}µs / 增益{_numGain.Value}";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { _cameraStatus.Text = "相机参数未完成下发：" + ex.Message; _log.Error(_cameraStatus.Text); }
    }
    private Task<CameraFrame> CaptureFrameAsync() => _cameraCapture?.CaptureAsync((double)_numExp.Value, (double)_numGain.Value) ?? throw new InvalidOperationException("相机不可用。");
    private async Task PreviewTick()
    {
        if(_closing || !_liveEnabled.Checked)return;
        bool productionPreview=_productionStop is not null || _spotTrialStop is not null || _stationReturnStop is not null || _automaticOpticsStop is not null;
        if((_workspaceBusy || _laserExposureTask is {IsCompleted:false}) && !productionPreview)
        { _liveStatus.ForeColor=Color.DarkOrange;_liveStatus.Text="正在采图／分析，实时预览暂停；显示最后一帧。";return; }
        if (_previewBusy || DateTimeOffset.Now<_previewRetryAt) return; _previewBusy = true;
        try
        {
            var frame = _cameraCapture is null ? null : await _cameraCapture.TryPreviewAsync();
            if(frame is not null && !IsDisposed)
            {
                ShowFrame(_livePicture,frame,null);
                if(_trialPicture.Visible)ShowFrame(_trialPicture,frame,null);
                _lastPreviewAt=DateTimeOffset.Now;_liveStatus.ForeColor=Color.DarkGreen;
                _liveStatus.Text=$"实时画面 · 最近刷新 {_lastPreviewAt:HH:mm:ss.fff} · "+(productionPreview?"加工监看；正式拍照占用时短暂停顿":"移动XY看这里，停稳后拍照");
            }
            else { _liveStatus.ForeColor=Color.DarkOrange;_liveStatus.Text=_camera is {IsOpen:true}?"相机正忙；显示最后一帧，等待刷新。":"相机未连接；当前画面不能用于找位。"; }
        }
        catch (Exception ex)
        {
            _previewRetryAt=DateTimeOffset.Now.AddSeconds(2);_liveStatus.ForeColor=Color.DarkRed;
            _liveStatus.Text=$"预览异常，2秒后重试；最后刷新 {(_lastPreviewAt?.ToString("HH:mm:ss.fff")??"尚无画面")}。";
            _log.Warn("实时预览异常，将重试：" + ex.Message);
        }
        finally { _previewBusy = false; }
    }

    private void ShowLiveMagnifier(bool offlineCheck=false)
    {
        using var window=new Form {Text="实时找位放大 · 仅显示，不移动机床",ClientSize=new Size(1100,760),MinimumSize=new Size(700,500),StartPosition=FormStartPosition.CenterParent,Opacity=offlineCheck?0:1,ShowInTaskbar=!offlineCheck};
        var picture=new PictureBox {Dock=DockStyle.Fill,BackColor=Color.Black,SizeMode=PictureBoxSizeMode.Zoom};
        var status=new Label {Dock=DockStyle.Bottom,Height=40};window.Controls.Add(picture);window.Controls.Add(status);
        DateTimeOffset? displayed=null;
        using var timer=new System.Windows.Forms.Timer {Interval=200};
        void RefreshImage()
        {
            status.Text=_liveStatus.Text;status.ForeColor=_liveStatus.ForeColor;
            if(_livePicture.Image is not null && displayed!=_lastPreviewAt)
            {var old=picture.Image;picture.Image=(Image)_livePicture.Image.Clone();old?.Dispose();displayed=_lastPreviewAt;}
        }
        timer.Tick+=(_,_)=>RefreshImage();window.Shown+=(_,_)=>{_liveEnabled.Checked=true;RefreshImage();timer.Start();};
        window.FormClosed+=(_,_)=>{timer.Stop();picture.Image?.Dispose();};window.ShowDialog(this);
    }
    private async Task PositionTick()
    {
        if (_positionBusy || _closing) return; _positionBusy = true;
        try
        {
            if (!_cnc.IsConnected) { _lblPos.Text = "数控未连接"+(_lastCncConnectionError is null?"":"："+_lastCncConnectionError); return; }
            var s = await _cnc.GetStatusAsync();
            _lblPos.Text = HasValidCoordinates(s) ? $"{(_cfg.DeviceMode == "Sim" ? "模拟 " : "")}MCS X={s.PosX:F4}  Y={s.PosY:F4}  Z={s.PosZ:F4}" : "未取得有效MCS：" + s.Message;
            _lblPos.ForeColor=HasValidCoordinates(s)?Color.DarkGreen:Color.DarkRed;
        }
        catch (Exception ex) { _lblPos.Text = "数控读数失败：" + ex.Message; }
        finally { _positionBusy = false; }
    }
    private bool HasValidCoordinates(MachineStatus status) => status.Connected &&
        (status.Message == "OK" || _cfg.DeviceMode == "Sim" && status.Message == "运行中") &&
        new[] { status.PosX, status.PosY, status.PosZ }.All(double.IsFinite);
    private async Task<(double Value, AxisCoordinateSource Source, string? Node)> ReadFixedCAsync()
    {
        if (_cnc is ISinumerikVarAccess access && !string.IsNullOrWhiteSpace(_cfg.RotaryActualAngleNode))
            return (RingNumber(Convert.ToString(await access.ReadVarAsync(_cfg.RotaryActualAngleNode), CultureInfo.InvariantCulture)), AxisCoordinateSource.OpcUaReadback, _cfg.RotaryActualAngleNode);
        if(_automaticOpticsStop is not null && _automaticOpticsFixedC is {} scoped)return scoped;
        if(_guidedOperationC is {} guided)return guided;
        if (!_fixedCConfirmed.Checked) throw new InvalidOperationException("请核对面板C实际角度，填写上方固定C并确认保持固定；本次只需确认一次。");
        return (RingNumber(_spotTrialC.Text), AxisCoordinateSource.ManualPanelEntry, null);
    }
    private void SaveOperatorState() { try { _stateStore.Save(_state); } catch (Exception ex) { _log.Warn("操作进度未保存：" + ex.Message); } }
    private string? EnsureCaptureRoot(bool chooseAgain = false)
    {
        if (!chooseAgain && Directory.Exists(_state.CaptureRoot)) return _state.CaptureRoot;
        using var dialog = new FolderBrowserDialog { Description = "选择采集资料根目录（以后自动建工件文件夹，无需逐张另存）", SelectedPath = Directory.Exists(_state.CaptureRoot) ? _state.CaptureRoot : "" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return null;
        _state.CaptureRoot = dialog.SelectedPath; SaveOperatorState(); return dialog.SelectedPath;
    }
    private async Task OpenLaserPort()
    {
        _laserReadBusy = true; UpdateLaserCalibrationButtons();
        try
        {
            await _laser.OpenAsync(_txtCom.Text.Trim(), (int)(_cmbBaud.SelectedItem ?? 115200));
            if (_laser is ModbusRtuLaserService rtu)
            {
                var state = await rtu.ReadRecordedProtocolAsync();
                _laserCalibrationStatus.Text = (_laserPreset.Matches(state) ? "当前界面设置匹配：" : "当前读回与界面设置不同，可应用当前参数：") + state.Description;
            }
        }
        finally { _laserReadBusy = false; UpdateLaserCalibrationButtons(); }
    }
    private async Task LaserProbe()
    {
        if (_laser is not ModbusRtuLaserService rtu) throw new InvalidOperationException("离线模式不连接真实激光串口。");
        if (!rtu.IsOpen) throw new InvalidOperationException("请先打开激光串口。"); _log.Info("激光只读探测：" + await rtu.ProbeAsync());
    }
    private void HookLog()
    {
        _logSink = (level, message) =>
        {
            if (!IsHandleCreated || IsDisposed) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed) return; _rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] [{level}] {message}\n");
                    _latestLog.Text=$"[{DateTime.Now:HH:mm:ss}] {message.ReplaceLineEndings(" ")}";_latestLog.ForeColor=level.Contains("ERR",StringComparison.OrdinalIgnoreCase)?Color.DarkRed:SystemColors.ControlText;
                    if (_rtbLog.TextLength > 150000) _rtbLog.Text = _rtbLog.Text[^100000..]; _rtbLog.SelectionStart = _rtbLog.TextLength; _rtbLog.ScrollToCaret();
                });
            }
            catch (InvalidOperationException) { }
        }; _log.LineAdded += _logSink;
    }
    protected override async void OnFormClosed(FormClosedEventArgs e)
    {
        _closing = true; _previewTimer.Stop(); _positionTimer.Stop(); _cameraChange?.Cancel(); if (_logSink is not null) _log.LineAdded -= _logSink;
        try { await _cnc.DisconnectAsync(); } catch { }
        try { await _laser.CloseAsync(); } catch { }
        try { if (_cameraCapture is not null) await _cameraCapture.CloseAsync(); _camera?.Dispose(); } catch { }
        _livePicture.Image?.Dispose(); base.OnFormClosed(e);
    }
    private static void ShowFrame(PictureBox picture, CameraFrame frame, PartPose? unused)
    {
        if (frame.Gray.Length != (long)frame.Width * frame.Height) throw new InvalidDataException("图像数据长度错误。");
        var bitmap = new Bitmap(frame.Width, frame.Height, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var data = bitmap.LockBits(new Rectangle(0, 0, frame.Width, frame.Height), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[frame.Width * 3]; for (int y = 0; y < frame.Height; y++)
            {
                for (int x = 0; x < frame.Width; x++) { byte value = frame.Gray[y * frame.Width + x]; row[x * 3] = value; row[x * 3 + 1] = value; row[x * 3 + 2] = value; }
                Marshal.Copy(row, 0, data.Scan0 + y * data.Stride, row.Length);
            }
        }
        finally { bitmap.UnlockBits(data); }
        var old = picture.Image; picture.Image = bitmap; old?.Dispose();
    }
    private static Button Btn(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 3, 6, 3), Margin = new Padding(0, 4, 7, 4) }; button.Click += onClick; return button;
    }
    private static NumericUpDown Num(decimal min, decimal max, decimal value, decimal step = 1m) => new() { Minimum = min, Maximum = max, Value = value, Increment = step, DecimalPlaces = step < 1 ? 1 : 0, Width = 85 };
    private static Label Lbl(string text) => new() { Text = text, AutoSize = true, Padding = new Padding(0, 7, 3, 0) };
    private static GroupBox GroupBoxOf(string title, params Control[] controls)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, MaximumSize = new Size(1100, 0) }; flow.Controls.AddRange(controls);
        var group = new GroupBox { Text = title, AutoSize = true, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) }; group.Controls.Add(flow); return group;
    }
}
