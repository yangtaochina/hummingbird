using Hummingbird.Devices.LaserRtu;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private LaserProcessPreset _laserPreset => LaserProcessPreset.CustomerConfigured(
        LaserCalibrationController.ParseRequestMilliseconds(_laserExposureMs.Text),(ushort)_laserPowerRaw.Value);
    private readonly TextBox _laserExposureMs = new() { Width = 85, PlaceholderText = "待确定" };
    private readonly NumericUpDown _laserDurationSeconds = new() {Name="LaserDurationSeconds",Width=110,Minimum=.05m,Maximum=600,DecimalPlaces=3,Increment=.5m,Value=2};
    private readonly NumericUpDown _laserPowerRaw = new() {Name="LaserPowerRaw",Width=110,Minimum=1,Maximum=10000,Value=3950};
    private readonly Label _laserCalibrationStatus = new() { AutoSize = true, MaximumSize = new Size(850, 0), Text = "先只读核对参数，再试片打点。保留设备当前参数；请求时长不等于实际光学出光时长。" };
    private readonly Label _laserReadyReason = new() { AutoSize = true, MaximumSize = new Size(900, 0), ForeColor = Color.DarkOrange };
    private Button? _spotTrialExposureButton;
    private Button? _spotTrialDryRunButton;
    private Button? _laserStopButton;
    private Task? _laserExposureTask;
    private bool _laserReadBusy;

    private async Task RunLaserDebugAction(Func<Task> action, bool allowWhileBusy = false)
    {
        if (!allowWhileBusy && (_workspaceBusy || _laserReadBusy || _laserExposureTask is { IsCompleted: false }))
        { _log.Warn("试打/演练正在进行，请先停止或等待完成。"); return; }
        try { await action(); }
        catch (Exception ex) { _laserCalibrationStatus.Text="激光操作未完成："+ex.Message;_log.Error("激光操作未完成：" + ex.Message); }
        finally { UpdateLaserCalibrationButtons(); }
    }

    private Control BuildLaserCalibrationControls()
    {
        var read = Btn("读回标定参数（不出光）", async (_, _) =>
        {
            if (_laserReadBusy || _laserExposureTask is { IsCompleted: false }) return;
            _laserReadBusy = true;
            UpdateLaserCalibrationButtons();
            try
            {
                await LaserProbe();
                if (_laser is ModbusRtuLaserService rtu && rtu.Calibration.LastState is { } state)
                {
                    _laserCalibrationStatus.Text = state.Description;
                    if (!state.CanExpose) _laserCalibrationStatus.Text += "；当前条件不满足，真实试打保持禁用。";
                }
                else _laserCalibrationStatus.Text = "协议尚未核实，请提供只读测试日志；定时试打未启用。";
            }
            catch (Exception ex) { _log.Error("激光标定读回失败：" + ex.Message); }
            finally { _laserReadBusy = false; UpdateLaserCalibrationButtons(); }
        });
        _laserStopButton = Btn("停止试打／关光", async (_, _) =>
        {
            _productionStop?.Cancel();
            _spotTrialStop?.Cancel();_automaticOpticsStop?.Cancel();
            if (_spotTrialDryRunActive) return;
            if (_laser is not ModbusRtuLaserService rtu) return;
            rtu.Calibration.RequestStop();
            try { await rtu.LaserOffAsync(); }
            catch (Exception ex) { _log.Error("关光未确认：" + ex.Message); }
        });
        FormClosing += async (_, e) =>
        {
            if (_laserExposureTask is not { IsCompleted: false })
            {
                if (_laser is ModbusRtuLaserService pending && !pending.Calibration.ShutdownConfirmed)
                {
                    e.Cancel = true;
                    _laserCalibrationStatus.Text = "设备关光尚未确认，请先关光并只读核实，再退出。";
                }
                return;
            }
            e.Cancel = true;
            _spotTrialStop?.Cancel();
            if (_laser is ModbusRtuLaserService rtu) rtu.Calibration.RequestStop();
            await _laserExposureTask;
            if (_laser is ModbusRtuLaserService device && !device.Calibration.ShutdownConfirmed)
            {
                _laserCalibrationStatus.Text = "关光尚未确认，保留连接；请用设备自身关光/急停，并重新只读核实。";
                return;
            }
            BeginInvoke(new Action(Close));
        };
        _laserDurationSeconds.Value=(_state.LaserRequestMs??LaserProcessPreset.Recorded20261113.RequestedExposureMs)/1000m;
        _laserPowerRaw.Value=_state.LaserPowerRaw??LaserProcessPreset.Recorded20261113.Settings.Power;
        _laserExposureMs.Text=decimal.ToInt32(_laserDurationSeconds.Value*1000m).ToString(System.Globalization.CultureInfo.InvariantCulture);
        void SaveProcessSettings()
        {
            _laserExposureMs.Text=decimal.ToInt32(_laserDurationSeconds.Value*1000m).ToString(System.Globalization.CultureInfo.InvariantCulture);
            _state.LaserRequestMs=LaserCalibrationController.ParseRequestMilliseconds(_laserExposureMs.Text);
            _state.LaserPowerRaw=(ushort)_laserPowerRaw.Value;SaveOperatorState();
            UpdateLaserCalibrationButtons();
        }
        _laserDurationSeconds.ValueChanged+=(_,_)=>SaveProcessSettings();
        _laserPowerRaw.ValueChanged+=(_,_)=>SaveProcessSettings();
        var historical=Btn("载入历史设置",(_,_)=>
        {
            _laserDurationSeconds.Value=LaserProcessPreset.Recorded20261113.RequestedExposureMs/1000m;
            _laserPowerRaw.Value=LaserProcessPreset.Recorded20261113.Settings.Power;
            _laserCalibrationStatus.Text="已载入历史2秒/3950设置；尚未向设备写入。";
        });
        var customer=Btn("载入客户参数（60秒／4420）",(_,_)=>
        {
            _laserDurationSeconds.Value=LaserProcessPreset.CustomerConfirmed20261116.RequestedExposureMs/1000m;
            _laserPowerRaw.Value=LaserProcessPreset.CustomerConfirmed20261116.Settings.Power;
            _laserCalibrationStatus.Text="已载入客户连续60秒／AOM2 12%(4420)设置，尚未写入设备。请点击应用当前参数并核对读回。";
        });
        var restorePreset = Btn("应用当前参数（不出光）", async (_, _) => await RunLaserDebugAction(async () =>
        {
            if (_laser is not ModbusRtuLaserService rtu || !rtu.IsOpen) throw new InvalidOperationException("请打开激光串口。");
            var requested=_laserPreset;
            var previousCheck=rtu.Calibration.LastPresetCheck;
            _laserReadBusy = true; UpdateLaserCalibrationButtons();
            try
            {
                await rtu.ReadRecordedProtocolAsync();
                var restored = await rtu.RestoreRecordedPresetAsync(requested);
                _laserCalibrationStatus.Text = $"当前设置已应用：每孔{requested.RequestedExposureMs/1000.0:0.###}秒；" + restored.Description;
            }
            finally
            {
                if(rtu.Calibration.LastPresetCheck is { } check && !ReferenceEquals(check,previousCheck))RecordLaserParameterCheck(check);
                _laserReadBusy = false; UpdateLaserCalibrationButtons();
            }
        }));
        var requestRow=new FlowLayoutPanel {AutoSize=true,WrapContents=false};
        requestRow.Controls.AddRange([Lbl("每孔出光时长（秒）"),_laserDurationSeconds,Lbl("功率设定值（设备单位，1～10000）"),_laserPowerRaw]);
        var group = GroupBoxOf("加工参数（单点与整件共用，自动保存）",
            Lbl("先在厂家软件完成“开机/启动”并排除告警，再释放串口供本程序连接；本程序打开串口只建立通信。"),
            requestRow,
            customer,restorePreset,historical,read,_laserStopButton,_laserReadyReason,
            Lbl("时长可选0.050～600秒；重频100kHz、Burst 1、内部控制沿用历史设置。修改输入不会立即出光。"),
            Lbl("客户已确认本机AOM2 12%(4420)、每孔连续60秒；这只是此设定的对应关系，不推算其他百分比或瓦数。历史设置为2秒/3950。"),_laserCalibrationStatus);
        var parameterFlow=(FlowLayoutPanel)group.Controls[0];
        parameterFlow.SetFlowBreak(parameterFlow.Controls[0],true);
        parameterFlow.SetFlowBreak(requestRow,true);
        parameterFlow.SetFlowBreak(_laserStopButton,true);
        parameterFlow.SetFlowBreak(_laserReadyReason,true);
        group.MaximumSize = new Size(1050, 0);
        return group;
    }

    private void UpdateLaserCalibrationButtons()
    {
        bool busy = _workspaceBusy || _laserReadBusy || _laserExposureTask is { IsCompleted: false };
        bool acquisitionReady = _camera is { IsOpen: true } && _cnc.IsConnected && _fixedCConfirmed.Checked;
        bool durationValid = false;
        try { LaserCalibrationController.ParseRequestMilliseconds(_laserExposureMs.Text); durationValid = true; }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
        bool ready = !busy && acquisitionReady && durationValid && _laser is ModbusRtuLaserService rtu &&
            rtu.Calibration.LastState is { CanExpose: true } state && _laserPreset.Matches(state);
        if (_spotTrialExposureButton is not null) _spotTrialExposureButton.Enabled = ready;
        if (_spotTrialDryRunButton is not null) _spotTrialDryRunButton.Enabled = !busy && acquisitionReady;
        _laserExposureMs.Enabled = !busy;
        _laserDurationSeconds.Enabled=_laserPowerRaw.Enabled=!busy;
        _laserReadyReason.Text = busy ? "当前操作进行中。" : !acquisitionReady ? "先连接相机和数控，并核对上方本次装夹与固定C。" : !durationValid ? "试打未启用：请填写已经确定的请求时长。" :
            _laser is not ModbusRtuLaserService ? "离线演示不启用真实出光。" :
            ((ModbusRtuLaserService)_laser).Calibration.LastState is null ? "试打未启用：请重新读取激光参数。" :
            ((ModbusRtuLaserService)_laser).Calibration.LastState!.Power==0 ? "功率读回0：请先确认厂家软件已完成开机/启动并排除告警，再核对当前参数；打开串口不代表设备已开机就绪。" :
            !ready ? "当前读回与界面设置不符，或设备未关光；请应用当前参数并核对读回。" : $"当前设置匹配：每孔{_laserPreset.RequestedExposureMs/1000.0:0.###}秒，功率{_laserPreset.Settings.Power}；执行时再次核对。";
    }

    private async Task StartLaserTrialOperation(Func<Task> action, bool refreshAfter = false)
    {
        if (_workspaceBusy || _laserReadBusy || _laserExposureTask is { IsCompleted: false }) return;
        _stopCaptureMonitor?.Invoke(); _cameraChange?.Cancel();
        var restore = new List<(Control Control, bool Enabled)>();
        void DisableActions(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if(child.Name=="GlobalProductionStop")continue;
                if (child is Button or TextBox or NumericUpDown or ComboBox or CheckBox)
                {
                    if (child == _laserStopButton || child == _automaticOpticsStopButton || child is Button { Text: "关光" }) continue;
                    restore.Add((child, child.Enabled)); child.Enabled = false;
                }
                else DisableActions(child);
            }
        }
        DisableActions(this);
        try { _laserExposureTask = action(); await _laserExposureTask; }
        finally
        {
            _laserExposureTask = null;
            if (refreshAfter && _laser is ModbusRtuLaserService rtu && rtu.IsOpen)
            {
                _laserReadBusy = true;
                try { await rtu.ReadRecordedProtocolAsync(); }
                catch (Exception ex) { _log.Error("出光后自动读回失败：" + ex.Message); }
                finally { _laserReadBusy = false; }
            }
            foreach (var item in restore) if (!item.Control.IsDisposed) item.Control.Enabled = item.Enabled;
            UpdateLaserCalibrationButtons();
        }
    }

}
