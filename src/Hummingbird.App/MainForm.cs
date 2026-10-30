using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Devices.LaserRtu;
using Hummingbird.Devices.OpcUa;
using Hummingbird.Devices.Sim;
using Hummingbird.Vision;
#if HAS_PYLON
using Hummingbird.Devices.Basler;
#endif

namespace Hummingbird.App;

/// <summary>
/// 主窗体：自动加工 / 标定（九点） / 设备调试 三个页签 + 底部日志区。
/// 日志器由 Program.cs 传入（全局同一份，界面显示 + 文件落盘）。
/// </summary>
public sealed class MainForm : Form
{
    private readonly IAppLogger _log;
    private readonly AppConfig _cfg;
    private readonly ICameraService? _camera;
    private readonly ICncService _cnc;
    private readonly ILaserService _laser;
    private readonly AutoRunWorkflow _wf;

    private PartPose? _lastPose;
    private CameraFrame? _lastFrame;
    private Point _clickPx;
    private bool _clickValid;

    // —— 控件
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill };
    private readonly RichTextBox _rtbLog = new() { Dock = DockStyle.Fill, ReadOnly = true, BackColor = Color.White, Font = new Font("Consolas", 9F) };
    private readonly PictureBox _pbMain = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly PictureBox _pbCal = new() { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
    private readonly Label _lblPose = new() { Dock = DockStyle.Top, Height = 30, Text = "待拍照定位", Padding = new Padding(8, 6, 0, 0) };
    private readonly Label _lblCal = new() { Dock = DockStyle.Top, Height = 30, Text = "标定：未标定", Padding = new Padding(8, 6, 0, 0) };
    private readonly Label _lblPos = new() { AutoSize = true, Text = "机床  X=-  Y=-  Z=-", Padding = new Padding(0, 8, 0, 0) };
    private readonly DataGridView _gv = new() { Dock = DockStyle.Fill, AllowUserToAddRows = true, RowHeadersWidth = 40 };
    private readonly CheckBox _chkFreeze = new() { Text = "冻结位姿(Sim标定用)", AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
    private readonly TextBox _txtMx = new() { Width = 90, Text = "0" };
    private readonly TextBox _txtMy = new() { Width = 90, Text = "0" };
    private readonly TextBox _txtEndpoint = new() { Width = 240 };
    private readonly NumericUpDown _numExp = Num(20, 1000000, 8000, 100);
    private readonly NumericUpDown _numGain = Num(0, 20, 1, 0.5m);
    private readonly NumericUpDown _numPower = Num(0, 10000, 6000, 100);
    private readonly NumericUpDown _numFreq = Num(50, 1000, 200, 10);
    private readonly NumericUpDown _numPulse = Num(1, 10, 5, 1);
    private readonly NumericUpDown _numR = Num(1, 999, 1, 1);
    private readonly NumericUpDown _numRval = Num(-100000, 100000, 0, 1);
    private readonly TextBox _txtCom = new() { Width = 80, Text = "COM3" };
    private readonly ComboBox _cmbBaud = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };

    /// <param name="logger">全局日志器；不传则自建（设计器/单元测试场景）。</param>
    public MainForm(IAppLogger? logger = null)
    {
        _log = logger ?? new MemoryLogger();

        Text = "蜂鸟 · 表盘激光打孔工作站";
        Font = new Font("Microsoft YaHei UI", 9F);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1280, 820);

        _cfg = AppConfig.LoadOrDefault("config.json");
        _log.Info($"读取配置 config.json：DeviceMode={_cfg.DeviceMode} 数控端点={_cfg.CncEndpoint} 串口={_cfg.LaserComPort}@{_cfg.LaserBaud}");

        var template = HolePatternTemplate.LoadJson("templates/dial_25holes.json");
        _log.Info($"加载孔位模板：{template.PartName}（{template.Holes.Count}孔）");

        if (_cfg.DeviceMode == "Sim")
        {
            // ---- Sim 模式：三件套全模拟，无硬件可跑通全流程 ----
            var simCam = new SimCameraService { Template = template };
            var simCnc = new SimCncService(m => _log.Info(m));
            var simLaser = new SimLaserService(m => _log.Info(m));
            _camera = simCam; _cnc = simCnc; _laser = simLaser;
        }
        else
        {
            // ---- Real 模式：真机三件套 ----
            _cnc = new OpcUaCncService(_log) { ProgramSharePath = _cfg.CncProgramShare };
            _laser = new ModbusRtuLaserService(_log);
#if HAS_PYLON
            // pylon SDK 已装（csproj 探测到 Basler.Pylon.dll）→ 启用真机相机
            _camera = new BaslerCameraService(_cfg.CameraSerialNumber, _log);
#else
            // 本机没探测到 pylon SDK：相机不可用，但数控/激光仍可调试
            _camera = null;
#endif
        }

        _wf = new AutoRunWorkflow(_log, _camera, new DialPositioning(_log), _cnc, _laser)
        {
            Template = template,
            VisionOptions = new VisionOptions
            {
                ExpectedOuterDiameterMm = _cfg.ExpectedOuterDiameterMm,
                NominalDatumAngleDeg = template.NominalDatumAngleDeg
            },
            Convention = new TransformConvention
            {
                SwapXY = _cfg.ConvSwapXY, NegateX = _cfg.ConvNegateX,
                NegateY = _cfg.ConvNegateY, ExtraRotationDeg = _cfg.ConvExtraRotationDeg
            }
        };

        BuildUi();
        HookLog();
        LoadCalibration();
        _cnc.SubscribePosition(p =>
        {
            if (IsHandleCreated) BeginInvoke(() => _lblPos.Text = $"机床  X={p.X:F3}  Y={p.Y:F3}  Z={p.Z:F3}");
        }, 200);

        _log.Info($"模式：{_cfg.DeviceMode} | 模板：{template.PartName}（{template.Holes.Count}孔）");
#if HAS_PYLON
        if (_cfg.DeviceMode == "Real")
            _log.Info($"相机真机已启用：Basler pylon（序列号配置：\"{_cfg.CameraSerialNumber}\" 空=第一台）");
#else
        if (_cfg.DeviceMode == "Real" && _camera is null)
            _log.Warn("相机不可用：本机未探测到 pylon SDK（重装 pylon 或见 Hummingbird.Devices.Basler/README.md），当前可调试数控/激光");
#endif
    }

    // ================= UI 构建 =================

    private void BuildUi()
    {
        // ---- 页签1：自动加工 ----
        var tabAuto = new TabPage("自动加工");
        var topAuto = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8), WrapContents = true };
        topAuto.Controls.Add(Btn("连接设备", async (_, _) => await ConnectDevices()));
        topAuto.Controls.Add(Btn("拍照定位", async (_, _) => await GrabLocateShow()));
        topAuto.Controls.Add(Btn("一键加工（定位+下发+监控）", async (_, _) => await AutoRun()));
        topAuto.Controls.Add(Btn("模拟NC启动(Sim)", (_, _) => SimStart()));
        topAuto.Controls.Add(Btn("取消监控", (_, _) => _wf.CancelRun()));
        tabAuto.Controls.Add(_pbMain);
        tabAuto.Controls.Add(_lblPose);
        tabAuto.Controls.Add(topAuto);

        // ---- 页签2：标定 ----
        var tabCal = new TabPage("标定（九点）");
        var topCal = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
        topCal.Controls.Add(Lbl("机床X(mm)"));
        topCal.Controls.Add(_txtMx);
        topCal.Controls.Add(Lbl("机床Y(mm)"));
        topCal.Controls.Add(_txtMy);
        topCal.Controls.Add(Btn("拍照", async (_, _) => await GrabLocateShow()));
        topCal.Controls.Add(Btn("添加标定点（图上点选像素）", (_, _) => AddCalPoint()));
        topCal.Controls.Add(_chkFreeze);

        var calOps = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 44, Padding = new Padding(8) };
        calOps.Controls.Add(Btn("执行标定", (_, _) => FitCalibration()));
        calOps.Controls.Add(Btn("保存标定", (_, _) => SaveCalibration()));
        calOps.Controls.Add(Btn("重新载入", (_, _) => { _gv.Rows.Clear(); LoadCalibration(); }));

        _gv.Columns.Add("c0", "机床X (mm)");
        _gv.Columns.Add("c1", "机床Y (mm)");
        _gv.Columns.Add("c2", "像素X");
        _gv.Columns.Add("c3", "像素Y");
        foreach (DataGridViewColumn c in _gv.Columns) { c.Width = 110; c.SortMode = DataGridViewColumnSortMode.NotSortable; }

        var right = new Panel { Dock = DockStyle.Fill };
        right.Controls.Add(_gv);
        right.Controls.Add(calOps);
        right.Controls.Add(_lblCal);

        var hint = new Label { Dock = DockStyle.Top, Height = 30, Text = "点击图像上的标定点（现场=激光在废料上打的标记点），填入该点机床坐标后“添加标定点”", Padding = new Padding(8, 6, 0, 0), ForeColor = Color.DimGray };
        var left = new Panel { Dock = DockStyle.Fill };
        left.Controls.Add(_pbCal);
        left.Controls.Add(hint);
        _pbCal.MouseClick += OnCalImageClick;

        var split = new SplitContainer { Dock = DockStyle.Fill, SplitterDistance = 720 };
        split.Panel1.Controls.Add(left);
        split.Panel2.Controls.Add(right);

        tabCal.Controls.Add(split);
        tabCal.Controls.Add(topCal);

        // ---- 页签3：设备调试 ----
        var tabDbg = new TabPage("设备调试");
        var gbCam = GroupBoxOf("相机",
            Btn("打开相机", async (_, _) => await OpenCamera()),
            Btn("关闭相机", async (_, _) => { if (_camera != null) await _camera.CloseAsync(); _log.Info("相机关闭"); }),
            Lbl("曝光us"), _numExp, Lbl("增益"), _numGain,
            Btn("应用参数", async (_, _) => await ApplyCameraParams()),
            Btn("拍照(显示在自动页)", async (_, _) => await GrabLocateShow()));
        var gbLaser = GroupBoxOf("激光器",
            Lbl("串口"), _txtCom, Lbl("波特率"), _cmbBaud,
            Btn("打开串口", async (_, _) => await OpenLaserPort()),
            Btn("读寄存器测试(不出光)", async (_, _) => await LaserProbe()),
            Btn("扫描全部串口(不出光)", async (_, _) => await LaserScan()),
            Lbl("功率0-10000"), _numPower, Lbl("重频kHz"), _numFreq, Lbl("脉冲数"), _numPulse,
            Btn("设置参数", async (_, _) => await ApplyLaserParams()),
            Btn("出光一次", async (_, _) => { await _laser.FireBurstAsync(); _log.Info("已出光一次"); }),
            Btn("开光", async (_, _) => { await _laser.LaserOnAsync(); _log.Warn("激光已开！"); }),
            Btn("关光", async (_, _) => { await _laser.LaserOffAsync(); _log.Info("激光已关"); }));
        var gbCnc = GroupBoxOf("数控(828D OPC UA)",
            Lbl("端点"), _txtEndpoint,
            Btn("连接", async (_, _) => { var ok = await _cnc.ConnectAsync(_txtEndpoint.Text); _log.Info(ok ? "数控已连接" : "数控连接失败"); }),
            Btn("读状态", async (_, _) => { var st = await _cnc.GetStatusAsync(); _log.Info($"模式={st.Mode} 程序={st.ProgramName} X={st.PosX} Y={st.PosY} {st.Message}"); }),
            Lbl("R序号"), _numR, Lbl("值"), _numRval,
            Btn("写入R参数", async (_, _) => { var ok = await _cnc.WriteRAsync((int)_numR.Value, (double)_numRval.Value); _log.Info(ok ? $"R{(int)_numR.Value}={_numRval.Value} 已写入" : "写入失败"); }),
            _lblPos);
        _txtEndpoint.Text = _cfg.CncEndpoint;
        _txtCom.Text = string.IsNullOrEmpty(_cfg.LaserComPort) ? "COM3" : _cfg.LaserComPort;
        _cmbBaud.Items.AddRange(new object[] { 9600, 19200, 38400, 57600, 115200, 230400 });
        int ib = _cmbBaud.Items.IndexOf(_cfg.LaserBaud > 0 ? _cfg.LaserBaud : 115200);
        _cmbBaud.SelectedIndex = ib < 0 ? _cmbBaud.Items.IndexOf(115200) : ib;


        var dbgRoot = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, Padding = new Padding(8) };
        dbgRoot.Controls.Add(gbCam);
        dbgRoot.Controls.Add(gbLaser);
        dbgRoot.Controls.Add(gbCnc);
        tabDbg.Controls.Add(dbgRoot);

        _tabs.TabPages.Add(tabAuto);
        _tabs.TabPages.Add(tabCal);
        _tabs.TabPages.Add(tabDbg);

        var logHost = new Panel { Dock = DockStyle.Bottom, Height = 180 };
        logHost.Controls.Add(_rtbLog);
        Controls.Add(_tabs);
        Controls.Add(logHost);
    }

    private void HookLog()
    {
        _log.LineAdded += (lvl, msg) =>
        {
            if (!IsHandleCreated) return;
            BeginInvoke(() =>
            {
                _rtbLog.AppendText($"[{DateTime.Now:HH:mm:ss}] [{lvl}] {msg}\n");
                _rtbLog.SelectionStart = _rtbLog.TextLength;
                _rtbLog.ScrollToCaret();
            });
        };
    }

    // ================= 动作 =================

    private async Task ConnectDevices()
    {
        // 三台设备各自独立连接：机床网络不通时，相机/激光照常可测（现场调试常用）
        try
        {
            bool ok = await _cnc.ConnectAsync(_cfg.CncEndpoint);
            _log.Info(ok ? $"数控已连接：{_cfg.CncEndpoint}" : "数控连接失败");
        }
        catch (Exception ex) { _log.Error("数控连接失败：" + ex.Message + "（不影响相机/激光调试）"); }

        try
        {
            await _laser.OpenAsync(_txtCom.Text.Trim(), (int)(_cmbBaud.SelectedItem ?? 115200));
            await ApplyLaserParams();
            _log.Info($"激光器串口已打开：{_txtCom.Text.Trim()}@{_cmbBaud.SelectedItem}");
        }
        catch (Exception ex) { _log.Error("激光器串口打开失败：" + ex.Message + "（检查USB转232是否插好、口是否被占用）"); }

        if (_camera != null)
        {
            try
            {
                await _camera.OpenAsync();
                _log.Info("相机已打开：" + _camera.DeviceName);
            }
            catch (Exception ex) { _log.Error("相机打开失败：" + ex.Message + "（检查USB3线、相机供电）"); }
        }
    }

    private async Task ApplyCameraParams()
    {
        if (_camera == null) { _log.Warn("相机不可用"); return; }
        await _camera.SetExposureUsAsync((double)_numExp.Value);
        await _camera.SetGainAsync((double)_numGain.Value);
        _log.Info($"相机参数：曝光{_numExp.Value}us 增益{_numGain.Value}");
    }

    private async Task ApplyLaserParams()
    {
        await _laser.SetPowerAsync((int)_numPower.Value);
        await _laser.SetFrequencyKhzAsync((int)_numFreq.Value);
        await _laser.SetPulseCountAsync((int)_numPulse.Value);
        _log.Info($"激光参数：功率{_numPower.Value}/10000 重频{_numFreq.Value}kHz 脉冲{_numPulse.Value}");
    }

    // ---------- 相机/激光 单机连接测试（现场调试用） ----------

    /// <summary>单独打开相机（不依赖机床/激光）。USB 相机：检查 USB3 线、不能被 pylon Viewer 占用。</summary>
    private async Task OpenCamera()
    {
        if (_camera == null) { _log.Warn("相机不可用（本机未探测到 pylon SDK，见 Hummingbird.Devices.Basler/README.md）"); return; }
        try
        {
            await _camera.OpenAsync();
            _log.Info("相机已打开：" + _camera.DeviceName);
        }
        catch (Exception ex)
        {
            _log.Error("相机打开失败：" + ex.Message + "（检查：USB3线是否插好、相机电源、是否被 pylon Viewer 占用）");
        }
    }

    /// <summary>按界面上的 串口+波特率 打开激光器串口。</summary>
    private async Task OpenLaserPort()
    {
        try
        {
            await _laser.OpenAsync(_txtCom.Text.Trim(), (int)(_cmbBaud.SelectedItem ?? 115200));
            _log.Info($"激光器串口已打开：{_txtCom.Text.Trim()}@{_cmbBaud.SelectedItem} 8N1");
        }
        catch (Exception ex)
        {
            _log.Error("串口打开失败：" + ex.Message + "（检查 USB转232 是否插好、口是否被其他软件占用）");
        }
    }

    /// <summary>在当前串口上做只读探测（绝不出光），分辨两套协议哪套是真的。</summary>
    private async Task LaserProbe()
    {
        if (_laser is not ModbusRtuLaserService rtu)
        {
            _log.Warn("仅 Real 模式（串口 Modbus RTU）可用");
            return;
        }
        if (!rtu.IsOpen) { _log.Warn("串口未打开，先点\"打开串口\"或\"扫描全部串口\""); return; }
        _log.Info($"开始只读探测 {_txtCom.Text.Trim()}@{_cmbBaud.SelectedItem}（读寄存器，不出光）...");
        var r = await rtu.ProbeAsync();
        _log.Info(r.Length > 0 ? $"探测结果：{r}" : "无应答 → 换个波特率再试，或直接点\"扫描全部串口\"");
    }

    /// <summary>
    /// 全自动扫描：发现的所有串口 × 常用波特率，逐组只读探测（不出光）。
    /// 用于现场一次性搞清楚：哪个口、哪个波特率、哪套寄存器表。
    /// </summary>
    private async Task LaserScan()
    {
        if (_laser is not ModbusRtuLaserService rtu)
        {
            _log.Warn("仅 Real 模式（串口 Modbus RTU）可用");
            return;
        }
        string[] ports = SerialPort.GetPortNames();
        if (ports.Length == 0)
        {
            _log.Error("未发现任何串口：检查 USB 转 232（或485）是否插到电脑、驱动是否装好（设备管理器→端口 COM和LPT）");
            return;
        }
        int[] bauds = { 9600, 19200, 38400, 57600, 115200 };
        _log.Info($"发现串口：{string.Join("  ", ports)}；开始扫描 ×{bauds.Length} 档波特率（每组约3秒，只读不出光）...");
        bool any = false;
        foreach (var port in ports)
        {
            foreach (var baud in bauds)
            {
                try
                {
                    await rtu.OpenAsync(port, baud);
                    var r = await rtu.ProbeAsync();
                    if (r.Length > 0)
                    {
                        _log.Info($"✓✓✓ {port}@{baud}：{r}");
                        _txtCom.Text = port;
                        _cmbBaud.SelectedIndex = Math.Max(0, _cmbBaud.Items.IndexOf(baud));
                        any = true;
                    }
                    else _log.Info($"  {port}@{baud}：无应答");
                }
                catch (Exception ex)
                {
                    _log.Info($"  {port}@{baud}：{ex.GetType().Name}（{ex.Message}）");
                }
            }
        }
        if (any) _log.Info("扫描完成：命中项见上（串口/波特率已自动回填到界面）");
        else _log.Warn("扫描完成：全部无应答。若线确实已接好且激光器已上电 → 大概率电平不匹配：说明书V3.0是RS485，客户说RS232；若拿到的是USB转232而激光器是485口，需要换 USB转485");
    }

    private async Task GrabLocateShow()
    {
        try
        {
            if (_camera is null) { _log.Warn("相机不可用（未探测到 pylon SDK，见 Hummingbird.Devices.Basler/README.md）"); return; }
            if (_camera is SimCameraService sc) sc.FreezePose = _chkFreeze.Checked;
            var (pose, frame) = await _wf.GrabAndLocateAsync();
            _lastPose = pose; _lastFrame = frame;
            ShowFrame(_pbMain, frame, pose);
            ShowFrame(_pbCal, frame, null);
            _lblPose.Text = pose.Success
                ? $"定位成功 | 中心({pose.CenterPxX:F0},{pose.CenterPxY:F0})px | 转角 {pose.AngleOffsetDeg:F2}° | 置信度 {pose.Confidence:F2} | {pose.Detail}"
                : "定位失败：" + pose.Detail;
        }
        catch (Exception ex)
        {
            _log.Error("拍照定位异常：" + ex.Message);
        }
    }

    private async Task AutoRun()
    {
        try
        {
            if (_camera is SimCameraService sc) sc.FreezePose = false;
            _log.Info("==== 一键加工开始 ====");
            var (pose, frame) = await _wf.GrabAndLocateAsync();
            if (!pose.Success) { _log.Error("终止：定位失败"); return; }
            _lastPose = pose; _lastFrame = frame;
            ShowFrame(_pbMain, frame, pose);

            var targets = _wf.ComputeTargets(pose);
            foreach (var t in targets.Take(3))
                _log.Info($"  孔#{t.Id} {t.Clock}: ({t.MachineX:F3}, {t.MachineY:F3})");
            _log.Info($"  …… 共 {targets.Count} 孔");

            if (!await _wf.DeployProgramAsync(pose)) return;
            if (_cnc is SimCncService sim) sim.StartSimulatedRun();
            await _wf.RunLaserSyncAsync(targets);
        }
        catch (Exception ex)
        {
            _log.Error("自动加工异常：" + ex.Message);
        }
    }

    private void SimStart()
    {
        try
        {
            if (_cnc is SimCncService sim) sim.StartSimulatedRun();
            else _log.Warn("仅 Sim 模式可用（真机由操作工在 NC 面板按启动）");
        }
        catch (Exception ex) { _log.Warn(ex.Message); }
    }

    // ================= 标定 =================

    private void OnCalImageClick(object? sender, MouseEventArgs e)
    {
        if (_pbCal.Image is null) { _log.Warn("请先拍照"); return; }
        var img = _pbCal.Image;
        double scale = Math.Min(1.0 * _pbCal.ClientSize.Width / img.Width, 1.0 * _pbCal.ClientSize.Height / img.Height);
        double offX = (_pbCal.ClientSize.Width - img.Width * scale) / 2;
        double offY = (_pbCal.ClientSize.Height - img.Height * scale) / 2;
        int ix = (int)((e.X - offX) / scale), iy = (int)((e.Y - offY) / scale);
        if (ix < 0 || iy < 0 || ix >= img.Width || iy >= img.Height) { _clickValid = false; return; }
        _clickPx = new Point(ix, iy);
        _clickValid = true;
        _lblCal.Text = $"标定：已点选像素 ({ix}, {iy})，填机床坐标后点“添加标定点”";
    }

    private void AddCalPoint()
    {
        if (!_clickValid) { _log.Warn("请先在左侧图像上点击标定点"); return; }
        if (!double.TryParse(_txtMx.Text, out var mx) || !double.TryParse(_txtMy.Text, out var my))
        { _log.Warn("请输入该点的机床坐标 X/Y（mm）"); return; }
        _gv.Rows.Add(mx, my, (double)_clickPx.X, (double)_clickPx.Y);
        _clickValid = false;
        _lblCal.Text = $"标定：已录入 {GridPointCount()} 点";
    }

    private int GridPointCount()
    {
        int n = 0;
        foreach (DataGridViewRow r in _gv.Rows) if (!r.IsNewRow && CellOk(r, 0) && CellOk(r, 1) && CellOk(r, 2) && CellOk(r, 3)) n++;
        return n;
    }

    private void FitCalibration()
    {
        var pairs = new List<CalibPointPair>();
        foreach (DataGridViewRow r in _gv.Rows)
        {
            if (r.IsNewRow) continue;
            if (TryD(r, 0, out var mx) && TryD(r, 1, out var my) && TryD(r, 2, out var px) && TryD(r, 3, out var py))
                pairs.Add(new CalibPointPair(px, py, mx, my));
        }
        if (pairs.Count < 3) { _log.Warn("有效标定点不足（至少3点，推荐9点）"); return; }

        var res = new AffineCalibrationSolver().Fit(pairs);
        _wf.Calibration = res.Valid ? res : null;
        _lblCal.Text = $"标定（{pairs.Count}点）：{res.Note}";
        if (res.Valid) _log.Info("标定完成：" + res.Note);
        else _log.Warn("标定不合格：" + res.Note);
    }

    private void SaveCalibration()
    {
        var pairs = new List<CalibPointPair>();
        foreach (DataGridViewRow r in _gv.Rows)
        {
            if (r.IsNewRow) continue;
            if (TryD(r, 0, out var mx) && TryD(r, 1, out var my) && TryD(r, 2, out var px) && TryD(r, 3, out var py))
                pairs.Add(new CalibPointPair(px, py, mx, my));
        }
        File.WriteAllText("calibration.json", JsonSerializer.Serialize(pairs, new JsonSerializerOptions { WriteIndented = true }));
        _log.Info($"标定已保存（{pairs.Count}点）→ calibration.json");
    }

    private void LoadCalibration()
    {
        try
        {
            if (!File.Exists("calibration.json")) return;
            var pairs = JsonSerializer.Deserialize<List<CalibPointPair>>(File.ReadAllText("calibration.json"));
            if (pairs is not { Count: > 0 }) return;
            foreach (var p in pairs) _gv.Rows.Add(p.Mx, p.My, p.Px, p.Py);
            var res = new AffineCalibrationSolver().Fit(pairs);
            _wf.Calibration = res.Valid ? res : null;
            _lblCal.Text = $"标定（{pairs.Count}点，载入）：{res.Note}";
            _log.Info("载入标定：" + res.Note);
        }
        catch (Exception ex)
        {
            _log.Warn("载入标定失败：" + ex.Message);
        }
    }

    private static bool CellOk(DataGridViewRow r, int i) => TryD(r, i, out _);

    private static bool TryD(DataGridViewRow r, int i, out double v)
    {
        v = 0;
        try { return r.Cells[i].Value is { } val && double.TryParse(Convert.ToString(val), out v); }
        catch { return false; }
    }

    // ================= 显示 =================

    private static void ShowFrame(PictureBox pb, CameraFrame f, PartPose? pose)
    {
        int w = f.Width, h = f.Height;
        var rgb = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++) { byte g = f.Gray[i]; rgb[3 * i] = g; rgb[3 * i + 1] = g; rgb[3 * i + 2] = g; }
        var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        var bd = bmp.LockBits(new Rectangle(0, 0, w, h), System.Drawing.Imaging.ImageLockMode.WriteOnly, System.Drawing.Imaging.PixelFormat.Format24bppRgb);
        Marshal.Copy(rgb, 0, bd.Scan0, rgb.Length);
        bmp.UnlockBits(bd);

        if (pose is { Success: true })
        {
            using var g = Graphics.FromImage(bmp);
            using var pen = new Pen(Color.FromArgb(0, 220, 0), 2);
            float cx = (float)pose.CenterPxX, cy = (float)pose.CenterPxY, r = (float)pose.OuterRadiusPx;
            g.DrawEllipse(pen, cx - r, cy - r, 2 * r, 2 * r);
            g.DrawLine(pen, cx - 40, cy, cx + 40, cy);
            g.DrawLine(pen, cx, cy - 40, cx, cy + 40);
            double a = Math.PI / 180 * pose.DatumClockDeg;   // 零位方向（橙线）
            using var pen2 = new Pen(Color.FromArgb(255, 90, 0), 3);
            g.DrawLine(pen2, cx, cy, cx + (float)Math.Sin(a) * r, cy - (float)Math.Cos(a) * r);
        }
        var old = pb.Image;
        pb.Image = bmp;
        old?.Dispose();
    }

    // ================= 小工具 =================

    private static Button Btn(string text, EventHandler onClick)
    {
        var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 3, 6, 3), Margin = new Padding(0, 6, 8, 0) };
        b.Click += onClick;
        return b;
    }

    private static NumericUpDown Num(decimal min, decimal max, decimal value, decimal step = 1m)
        => new() { Minimum = min, Maximum = max, Value = value, Increment = step, Width = 90, Margin = new Padding(0, 6, 8, 0) };

    private static Label Lbl(string t)
        => new() { Text = t, AutoSize = true, Padding = new Padding(0, 9, 4, 0) };

    private static GroupBox GroupBoxOf(string title, params Control[] controls)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true };
        foreach (var c in controls) flow.Controls.Add(c);
        var g = new GroupBox { Text = title, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(10), Margin = new Padding(0, 4, 0, 4) };
        g.Controls.Add(flow);
        return g;
    }
}
