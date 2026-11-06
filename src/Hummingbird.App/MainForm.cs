using System.IO.Ports;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    private readonly IRotaryTableService _rotary;
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
    private readonly Label _lblRotaryStatus = new() { AutoSize = true, MaximumSize = new Size(760, 0), Text = "转台：未初始化" };
    private readonly Label _lblRotaryAngle = new() { AutoSize = true, Text = "当前角度：-", Padding = new Padding(0, 6, 0, 0) };
    private readonly NumericUpDown _numRotaryTarget = Num(-3600, 3600, 30, 0.1m);
    private readonly TextBox _txtRotaryManual = new() { Width = 100, Text = "0" };
    /// <summary>只读探测防重复点击（一次探测会读几十个节点，连点会建多个 OPC UA 会话）。</summary>
    private bool _probeBusy;
    private readonly NumericUpDown _numFreq = Num(50, 1000, 200, 10);
    private readonly NumericUpDown _numPulse = Num(1, 10, 5, 1);
    private readonly NumericUpDown _numR = Num(1, 999, 1, 1);
    private readonly NumericUpDown _numRval = Num(-100000, 100000, 0, 1);
    private readonly TextBox _txtCom = new() { Width = 80, Text = "COM3" };
    private readonly ComboBox _cmbBaud = new() { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
    /// <summary>人工装夹粗对齐角（度）：工件按 12H 摆正的大致角度，用于消解基准孔组的 90° 旋转对称歧义。</summary>
    private readonly TextBox _txtCoarse = new() { Width = 70, Text = "0" };

    // —— 定位策略（策略模式）：界面判断/切换用哪套定位算法 ——
    private readonly PositioningStrategySelector _selector;
    private readonly ComboBox _cmbStrategy = new() { Width = 300, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _lblStrategyDesc = new() { AutoSize = true, MaximumSize = new Size(700, 0), Padding = new Padding(4, 8, 0, 0) };
    private readonly TextBox _txtStrategyCheck = new()
    {
        Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical,
        Width = 700, Height = 200, Font = new Font("Consolas", 9F), BackColor = Color.White
    };
    // ⚠ 2026-11-05 现场照片复核：托板只是三根金属条拼的，随手摆在回转台上靠一根挡块限位，
    //   既没有定位销锁中心、也没有角向定位 → 「夹具锁心」必须默认勾掉。
    private readonly CheckBox _chkFixtureCenters = new() { Text = "夹具定位销/止口能锁死工件中心（现场照片：没有，默认否）", AutoSize = true, Checked = false };
    private readonly CheckBox _chkFiducialAvailable = new() { Text = "有相机看得清的标记孔（红光下不可见，默认否）", AutoSize = true, Checked = false };
    private readonly CheckBox _chkFiducialRotates = new() { Text = "标记孔随工件一起转动（用工件自带的双环孔，默认是）", AutoSize = true, Checked = true };
    private readonly CheckBox _chkFullInView = new() { Text = "整盘能入画（视野短边 ≥ 34.2mm）", AutoSize = true, Checked = false };
    private readonly CheckBox _chkDatumVisible = new() { Text = "8 个双环基准孔在画面里清晰可见", AutoSize = true, Checked = false };
    private readonly CheckBox _chkTwoHolesInView = new() { Text = "视野能同时看清 3 个相邻基准孔（约需 6×20mm，现有视野够）", AutoSize = true, Checked = false };
    private readonly TextBox _txtFixCx = new() { Width = 90, Text = "" };
    private readonly TextBox _txtFixCy = new() { Width = 90, Text = "" };
    private readonly TextBox _txtFidR = new() { Width = 70, Text = "14.45" };
    private readonly TextBox _txtFidClock = new() { Width = 70, Text = "0" };
    private readonly TextBox _txtFidDia = new() { Width = 70, Text = "0.8" };
    private readonly TextBox _txtFidTol = new() { Width = 70, Text = "1.5" };
    private readonly TextBox _txtManualAngle = new() { Width = 70, Text = "0" };

    /// <summary>现场诊断结果输出区（相机参数范围 / 曝光扫描 / 视野诊断）。</summary>
    private readonly TextBox _txtDiag = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Width = 700,
        Height = 180,
        Font = new Font("Consolas", 9F),
        BackColor = Color.White
    };
    /// <summary>当前加载的孔位模板（诊断时需要基准孔半径）。</summary>
    private HolePatternTemplate _template = new();

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

        // 界面默认曝光/增益与 config.json 保持一致（2026-11-06 修正）：
        // 原来界面写死 8000us / 增益 1，配置是 5000us / 增益 0 —— 点一次「应用参数」
        // 就会把相机又改回 8000us，现场会以为配置没生效。现在只保留一套权威值。
        if (_cfg.CameraExposureUs > 0)
            _numExp.Value = (decimal)Math.Clamp(_cfg.CameraExposureUs,
                (double)_numExp.Minimum, (double)_numExp.Maximum);
        if (_cfg.CameraGain >= 0)
            _numGain.Value = (decimal)Math.Clamp(_cfg.CameraGain,
                (double)_numGain.Minimum, (double)_numGain.Maximum);

        // 孔位模板：以 DWG 权威图纸为准（P26009G打孔图纸 2026.11.2，18 个待加工单环孔）
        var template = HolePatternTemplate.LoadJson("templates/dial_18holes.json");
        _template = template;
        _log.Info($"加载孔位模板：{template.PartName}（{template.Holes.Count}孔，基准模式={template.DatumMode}）");
        if (template.Note.Length > 0) _log.Info($"模板说明：{template.Note}");

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
            // pylon SDK 已装（csproj 探测到 Basler.Pylon.dll）→ 启用真机相机
            _camera = new BaslerCameraService(_cfg.CameraSerialNumber, _log);
#else
            // 本机没探测到 pylon SDK：相机不可用，但数控/激光仍可调试
            _camera = null;
#endif
        }

        // 转台服务（2026-11-06 客户新方案：转台分度 + 相机多帧定位）。
        // 关键：与 OPC UA 网络解耦 —— 默认「人工抄角」，网络不通也能采多帧，不被机床通讯卡死。
        var rotaryOpt = _cfg.BuildRotaryOptions();
        bool autoRotary = _cfg.RotaryMode == nameof(RotaryTableMode.OpcUaRPlc) && _cnc is ISinumerikVarAccess;
        if (autoRotary)
        {
            _rotary = new OpcUaRotaryTableService((ISinumerikVarAccess)_cnc, rotaryOpt, _log);
        }
        else
        {
            var mode = _cfg.RotaryMode == nameof(RotaryTableMode.Simulated)
                ? RotaryTableMode.Simulated : RotaryTableMode.ManualEntry;
            _rotary = new SimRotaryTableService(mode, rotaryOpt, _log);
            if (_cfg.RotaryMode == nameof(RotaryTableMode.OpcUaRPlc))
                _log.Warn("[转台] 配置要求自动分度，但当前不是 Real/OPC UA 设备模式 → 已降级为人工抄角");
        }
        _log.Info($"[转台] 模式={_rotary.Mode} 轴={rotaryOpt.AxisName}：{_rotary.ReadinessDetail}");

        // 策略模式：注册全部定位策略，界面可随时切换 _wf.Vision 指向哪一个。
        // 加工流程（AutoRunWorkflow）只认 IVisionService 抽象，切换策略零改动。
        _selector = new PositioningStrategySelector(new IPositioningStrategy[]
        {
            new FullFieldDatumHolesStrategy(_log),
            new TwoFiducialStrategy(_log),          // ④ 多孔自主定位（现场照片复核后新增，不依赖夹具）
            new SingleFiducialStrategy(_log),
            new ManualFixtureStrategy(_log)
        });

        _wf = new AutoRunWorkflow(_log, _camera, _selector.All[0], _cnc, _laser)
        {
            Template = template,
            VisionOptions = new VisionOptions
            {
                // 外径优先取模板（图纸 Ø34.2 DIAL），模板缺省才用配置文件
                ExpectedOuterDiameterMm = template.PartOuterDiameterMm > 0
                    ? template.PartOuterDiameterMm : _cfg.ExpectedOuterDiameterMm,
                PixelSizeMm = _cfg.VisionPixelSizeMm,
                NominalDatumAngleDeg = template.NominalDatumAngleDeg,
                DatumMode = template.DatumMode,
                DatumCircleDiameterMm = template.DatumCircleDiameterMm,
                DatumHoleAngles = template.DatumHoleAnglesDeg.ToArray(),
                DatumHoleRadiusMm = template.DatumHoleRadiusMm,
                // 单标记孔策略参数（config.json 可覆盖，界面「定位策略」页可实时改）
                StrategyId = _cfg.PositioningStrategyId,
                FixtureCenterPxX = _cfg.FixtureCenterPxX,
                FixtureCenterPxY = _cfg.FixtureCenterPxY,
                FiducialRadiusMm = _cfg.FiducialRadiusMm,
                FiducialDiameterMm = _cfg.FiducialDiameterMm,
                FiducialRadiusToleranceMm = _cfg.FiducialRadiusToleranceMm,
                FiducialNominalClockDeg = _cfg.FiducialNominalClockDeg,
                FixtureAssumedAngleDeg = _cfg.FixtureAssumedAngleDeg,
                // 多孔自主定位参数（StrategyIds.TwoFiducials）
                TwoHoleScaleTolerancePct = _cfg.TwoHoleScaleTolerancePct
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
        // 粗对齐角：工件自带定位孔组 90° 对称 → 单图 4 个等效转角，用它消歧
        topAuto.Controls.Add(Lbl("粗对齐角°"));
        topAuto.Controls.Add(_txtCoarse);
        topAuto.Controls.Add(Lbl("(工件按12H摆正,±10°内)"));
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

        // 现场诊断组（2026-11-05：曝光拉满仍全黑 / 视野疑似只有几毫米）
        var gbDiag = GroupBoxOf("现场诊断",
            Btn("①相机参数范围", async (_, _) => await DiagParamRanges()),
            Btn("②自动曝光扫描", async (_, _) => await DiagExposureScan()),
            Btn("③视野诊断(mm/px)", async (_, _) => await DiagFieldOfView()),
            Btn("保存诊断结果", (_, _) => SaveDiag()),
            _txtDiag);

        var gbLaser = GroupBoxOf("激光器",
            Lbl("串口"), _txtCom, Lbl("波特率"), _cmbBaud,
            Btn("打开串口", async (_, _) => await OpenLaserPort()),
            Btn("关闭串口", async (_, _) => { await _laser.CloseAsync(); _log.Info("激光串口已关闭"); }),
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
            Btn("断开连接", async (_, _) => { await _cnc.DisconnectAsync(); _log.Info("数控已断开（会话已释放，可重新连接）"); }),
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
        dbgRoot.Controls.Add(gbDiag);
        dbgRoot.Controls.Add(gbLaser);
        dbgRoot.Controls.Add(gbCnc);
        tabDbg.Controls.Add(dbgRoot);

        // ---- 页签4：定位策略（策略模式：界面判断/切换调用哪套定位算法）----
        var tabStrategy = new TabPage("定位策略");
        var stRoot = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, Padding = new Padding(8) };

        var gbPick = new GroupBox { Text = "选择定位策略（切换后立即生效，加工流程无需重启）", AutoSize = true, Padding = new Padding(8) };
        var pickFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
        var pickRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true };
        foreach (var s in _selector.All) _cmbStrategy.Items.Add(s.DisplayName);
        pickRow.Controls.Add(Lbl("策略"));
        pickRow.Controls.Add(_cmbStrategy);
        pickRow.Controls.Add(Btn("自动推荐（按当前现场条件）", (_, _) => AutoRecommendStrategy()));
        pickRow.Controls.Add(Btn("策略自检 / 看缺什么", (_, _) => RunStrategyCheck()));
        pickFlow.Controls.Add(pickRow);
        pickFlow.Controls.Add(_lblStrategyDesc);
        gbPick.Controls.Add(pickFlow);
        stRoot.Controls.Add(gbPick);

        var gbCond = new GroupBox { Text = "现场条件（自动推荐与自检的依据，按实际情况勾选）", AutoSize = true, Padding = new Padding(8) };
        var condFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true };
        foreach (var c in new[] { _chkFixtureCenters, _chkFiducialAvailable, _chkFiducialRotates,
                                  _chkFullInView, _chkDatumVisible, _chkTwoHolesInView })
            condFlow.Controls.Add(c);
        condFlow.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(700, 0), ForeColor = Color.DimGray,
            Text = "2026-11-06 现场复核（推翻 11-05 的换白光判断）：①白光/手机手电拍照**全黑**，" +
                   "必须开红光才有图像（疑似镜头前红色窄带滤光片）→ 换白光这条路作废；" +
                   "②托板没有定位销 → 「夹具锁心」勾掉；" +
                   "③真机连 2~3 个双环小孔都拍不到 → 策略④「同帧多孔」现场不可行，最后一项默认勾掉。\r\n" +
                   "现行路线：上料定位到 1 孔 + 转台分度拍多帧，每帧只需看清 1 个孔，按已知角度逆旋转合并。" +
                   "第 3 孔一致性仍是消镜像二义性的数学前提，保留作换镜头后的备选。",
            Padding = new Padding(4, 6, 0, 0)
        });
        gbCond.Controls.Add(condFlow);
        stRoot.Controls.Add(gbCond);

        var gbFid = new GroupBox { Text = "单标记孔策略参数（客户新方案）", AutoSize = true, Padding = new Padding(8) };
        var fidFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true, MaximumSize = new Size(760, 0) };
        fidFlow.Controls.Add(Lbl("夹具中心像素 X")); fidFlow.Controls.Add(_txtFixCx);
        fidFlow.Controls.Add(Lbl("Y")); fidFlow.Controls.Add(_txtFixCy);
        fidFlow.Controls.Add(Btn("用当前定位结果填入中心", (_, _) => FillFixtureCenterFromPose()));
        fidFlow.Controls.Add(Btn("拍当前孔并示教名义角", async (_, _) => await TeachFiducialAngle()));
        fidFlow.Controls.Add(Lbl("名义半径mm")); fidFlow.Controls.Add(_txtFidR);
        fidFlow.Controls.Add(Lbl("名义时钟角°")); fidFlow.Controls.Add(_txtFidClock);
        fidFlow.Controls.Add(Lbl("孔径mm")); fidFlow.Controls.Add(_txtFidDia);
        fidFlow.Controls.Add(Lbl("半径容差mm")); fidFlow.Controls.Add(_txtFidTol);
        fidFlow.Controls.Add(Lbl("机械对刀转角°")); fidFlow.Controls.Add(_txtManualAngle);
        fidFlow.Controls.Add(Btn("应用参数", (_, _) => ApplyStrategyParams()));
        fidFlow.Controls.Add(Btn("保存到 config.json", (_, _) => SaveStrategyConfig()));
        fidFlow.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(720, 0), ForeColor = Color.DimGray,
            Text = "夹具中心像素：装标准件→拍照整盘定位→点『用当前定位结果填入中心』；" +
                   "也可先把工件按 12H 摆正、相机对准标记孔，再点『拍当前孔并示教名义角』自动反算名义角。",
            Padding = new Padding(4, 6, 0, 0)
        });
        gbFid.Controls.Add(fidFlow);
        stRoot.Controls.Add(gbFid);

        stRoot.Controls.Add(_txtStrategyCheck);
        tabStrategy.Controls.Add(stRoot);

        // ---- 页签5：转台（2026-11-06 客户新方案：转台分度，让相机拍到更多信息）----
        var tabRotary = new TabPage("转台");
        var rtRoot = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, AutoScroll = true, Padding = new Padding(8) };

        // ---- OPC UA 只读探测（2026-11-06 客户确认网络已联通 → 网络通之后的第一步）----
        // 独立建会话、只读扫描、扫完断开，与主程序 DeviceMode(Sim/Real) 完全解耦：
        // 即使主流程还在 Sim，也能一键核实真机的节点路径与写权限，且绝不写机床。
        var gbRtProbe = new GroupBox { Text = "① OPC UA 只读探测（网络已通，先跑这个）", AutoSize = true, Padding = new Padding(8), MaximumSize = new Size(780, 0) };
        var rtProbeFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, MaximumSize = new Size(760, 0) };
        rtProbeFlow.Controls.Add(Btn("连接并探测节点/写权限（只读，不动机床）", async (_, _) => await ProbeOpcUaAsync()));
        rtProbeFlow.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(740, 0), ForeColor = Color.DimGray,
            Text = "自动保存 OPCUA探测-<时间>.txt 到程序目录，把文件发回开发侧即可定死节点路径。\r\n" +
                   "探测内容：命名空间表、R90 是否存在及当前账号能否写、轴实际角度候选节点、PLC 目录浏览。全程只读。"
        });
        gbRtProbe.Controls.Add(rtProbeFlow);
        rtRoot.Controls.Add(gbRtProbe);

        var gbRtState = new GroupBox { Text = "状态", AutoSize = true, Padding = new Padding(8), MaximumSize = new Size(780, 0) };
        var rtStateFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, AutoSize = true, MaximumSize = new Size(760, 0) };
        rtStateFlow.Controls.Add(_lblRotaryStatus);
        rtStateFlow.Controls.Add(_lblRotaryAngle);
        rtStateFlow.Controls.Add(Btn("刷新状态", (_, _) => RefreshRotaryUi()));
        gbRtState.Controls.Add(rtStateFlow);
        rtRoot.Controls.Add(gbRtState);

        var gbRtMove = new GroupBox { Text = "分度到角度（每转一次拍一帧）", AutoSize = true, Padding = new Padding(8) };
        var rtMoveFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true, MaximumSize = new Size(760, 0) };
        rtMoveFlow.Controls.Add(Lbl("目标角度°"));
        rtMoveFlow.Controls.Add(_numRotaryTarget);
        rtMoveFlow.Controls.Add(Btn("转到目标角", async (_, _) => await MoveRotaryAsync()));
        rtMoveFlow.Controls.Add(Btn("读回角度", async (_, _) => await ReadRotaryAngleAsync()));
        gbRtMove.Controls.Add(rtMoveFlow);
        rtRoot.Controls.Add(gbRtMove);

        var gbRtManual = new GroupBox { Text = "人工抄角（OPC UA 没通时用它，不依赖机床通讯）", AutoSize = true, Padding = new Padding(8) };
        var rtManualFlow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, WrapContents = true, MaximumSize = new Size(760, 0) };
        rtManualFlow.Controls.Add(Lbl("HMI 上读到的实际角度°"));
        rtManualFlow.Controls.Add(_txtRotaryManual);
        rtManualFlow.Controls.Add(Btn("已转到该角度", async (_, _) => await ConfirmManualAngleAsync()));
        rtManualFlow.Controls.Add(Btn("设为 0°（回零位）", async (_, _) => { _txtRotaryManual.Text = "0"; await ConfirmManualAngleAsync(); }));
        gbRtManual.Controls.Add(rtManualFlow);
        rtRoot.Controls.Add(gbRtManual);

        rtRoot.Controls.Add(new Label
        {
            AutoSize = true, MaximumSize = new Size(760, 0), ForeColor = Color.DimGray,
            Text = "【2026-11-06 状态更新】客户已确认 OPC UA 联通 ✔。但**网络通 ≠ 能自动分度**：\r\n" +
                   "还差 R 参数写权限、转台实际角度节点、PLC 触发/到位位、机床侧 NC 子程序 + 握手。\r\n" +
                   "所以主流程仍默认「人工抄角」：分度定位只需要每帧对应的角度，操作工手动转盘、从 HMI 抄角度填进来，\r\n" +
                   "程序照样能把多帧孔心按已知角度逆旋转合并，不耽误视觉方案验证。\r\n" +
                   "先用上面【只读探测】把能自动查的查清楚，再决定是否切 OpcUaRPlc 自动模式。\r\n\r\n" +
                   "【自动分度需要机床侧给的东西】①转台是数控轴（轴名、角度分辨率、有无回程间隙）；" +
                   "②一段预置 NC 子程序：读 R90 的目标角并执行 G0 C=R90 / POS[C]=R90；" +
                   "③PLC 握手：触发位 + 到位位（节点路径用 UaExpert 核实后填 config.json）；" +
                   "④OPC UA 用户要有变量写权限（StateWrite/GudWrite/PlcWrite）；" +
                   "⑤安全联锁：门关、夹紧松开、急停、单次最大转角。\r\n" +
                   "⚠ 828D 的轴位置变量是只读的，不能靠写轴位置驱动转台；本程序也绝不在未配置节点时臆造 NodeId。",
            Padding = new Padding(4, 10, 0, 0)
        });
        tabRotary.Controls.Add(rtRoot);

        _tabs.TabPages.Add(tabAuto);
        _tabs.TabPages.Add(tabCal);
        _tabs.TabPages.Add(tabDbg);
        _tabs.TabPages.Add(tabStrategy);
        _tabs.TabPages.Add(tabRotary);

        // 初始化策略下拉框 + 参数输入框
        InitStrategyUi();
        RefreshRotaryUi();

        var logHost = new Panel { Dock = DockStyle.Bottom, Height = 180 };
        logHost.Controls.Add(_rtbLog);
        Controls.Add(_tabs);
        Controls.Add(logHost);
    }

    // ================= 定位策略（策略模式）=================

    /// <summary>初始化策略下拉框和参数输入框（从 config.json 读入）。</summary>
    private void InitStrategyUi()
    {
        var o = _wf.VisionOptions;
        _txtFixCx.Text = double.IsNaN(o.FixtureCenterPxX) ? "" : o.FixtureCenterPxX.ToString("F1");
        _txtFixCy.Text = double.IsNaN(o.FixtureCenterPxY) ? "" : o.FixtureCenterPxY.ToString("F1");
        _txtFidR.Text = o.FiducialRadiusMm.ToString("F2");
        _txtFidClock.Text = o.FiducialNominalClockDeg.ToString("F1");
        _txtFidDia.Text = o.FiducialDiameterMm.ToString("F2");
        _txtFidTol.Text = o.FiducialRadiusToleranceMm.ToString("F2");
        _txtManualAngle.Text = o.FixtureAssumedAngleDeg.ToString("F1");

        int idx = _selector.All
            .Select((s, i) => (s, i))
            .FirstOrDefault(t => string.Equals(t.s.Id, o.StrategyId, StringComparison.OrdinalIgnoreCase)).i;
        // FirstOrDefault 找不到时返回 (null,0)，正好落到第 ① 项
        _cmbStrategy.SelectedIndex = Math.Max(0, Math.Min(_selector.All.Count - 1, idx));
        _cmbStrategy.SelectedIndexChanged += (_, _) => ApplySelectedStrategy();
        ApplySelectedStrategy();
        // 启动时先跑一次自检：把"当前策略缺什么前提"直接摆出来，避免现场拿未标定的配置去加工
        RunStrategyCheck();
    }

    /// <summary>下拉框切换：把 _wf.Vision 指向新策略，并显示其原理与前置条件。</summary>
    private void ApplySelectedStrategy()
    {
        int i = _cmbStrategy.SelectedIndex;
        if (i < 0 || i >= _selector.All.Count) return;
        var s = _selector.All[i];
        _wf.Vision = s;
        _wf.VisionOptions = _wf.VisionOptions with { StrategyId = s.Id };

        _lblStrategyDesc.Text = s.Description + Environment.NewLine +
            "前置条件：" + string.Join("；", s.Prerequisites);
        _log.Info($"定位策略已切换：{s.DisplayName}（{s.Id}）");
        _log.Info("  " + s.Description);
    }

    /// <summary>组装现场条件快照。九点标定状态直接从 workflow 读，不用人工勾。</summary>
    private StrategyContext BuildStrategyContext()
    {
        var o = _wf.VisionOptions;
        return new StrategyContext(
            FixtureCentersPart: _chkFixtureCenters.Checked,
            FiducialAvailable: _chkFiducialAvailable.Checked,
            FiducialRotatesWithPart: _chkFiducialRotates.Checked,
            FullPartInView: _chkFullInView.Checked,
            DatumHolesVisible: _chkDatumVisible.Checked,
            CalibrationValid: _wf.Calibration is { Valid: true },
            FiducialTaught: !double.IsNaN(o.FixtureCenterPxX) && !double.IsNaN(o.FixtureCenterPxY),
            CoarseAlignProvided: !double.IsNaN(o.CoarseAlignDeg),
            TwoHolesInView: _chkTwoHolesInView.Checked);
    }

    /// <summary>「策略自检」：列出每个策略能不能用、缺什么，并给出推荐。</summary>
    private void RunStrategyCheck()
    {
        var ctx = BuildStrategyContext();
        _txtStrategyCheck.Text = _selector.DescribeAll(ctx);
        _log.Info("已执行定位策略自检（结果见『定位策略』页）");
    }

    /// <summary>
    /// 加工/定位前的软拦截：当前策略硬前提不满足时明确告警。
    /// 不强制禁止（现场可能就是想拍张图看看），但日志和策略页会把缺口写清楚。
    /// </summary>
    private bool WarnIfStrategyNotReady()
    {
        var ctx = BuildStrategyContext();
        if (_wf.Vision is not IPositioningStrategy strategy) return true;
        var r = strategy.Check(ctx);
        if (r.CanRun) return true;

        _txtStrategyCheck.Text = _selector.DescribeAll(ctx);
        _log.Warn($"当前策略【{strategy.DisplayName}】前提不满足：{r.Reason} " +
                  "—— 请到『定位策略』页看自检结果，或点『自动推荐』切换策略。");
        return false;
    }

    /// <summary>「自动推荐」：按工程优先级选一个策略并直接切换过去。</summary>
    private void AutoRecommendStrategy()
    {
        var ctx = BuildStrategyContext();
        var (rec, why) = _selector.Recommend(ctx);
        _txtStrategyCheck.Text = _selector.DescribeAll(ctx);
        if (rec is null)
        {
            _log.Warn("自动推荐失败：" + why);
            MessageBox.Show(this, why, "无法推荐定位策略", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        int idx = _selector.All.Select((s, i) => (s, i)).First(t => t.s.Id == rec.Id).i;
        if (_cmbStrategy.SelectedIndex != idx) _cmbStrategy.SelectedIndex = idx;
        else ApplySelectedStrategy();
        _log.Info("自动推荐 → " + rec.DisplayName + "。" + why);
        MessageBox.Show(this, why + "\n\n已切换到：" + rec.DisplayName,
            "定位策略自动推荐", MessageBoxButtons.OK, MessageBoxIcon.Information);
    }

    /// <summary>把界面上的单标记孔参数写进 VisionOptions（不落盘）。</summary>
    private void ApplyStrategyParams()
    {
        double P(TextBox t, double fallback) => double.TryParse(t.Text.Trim(), out double v) ? v : fallback;
        double cx = string.IsNullOrWhiteSpace(_txtFixCx.Text) ? double.NaN : P(_txtFixCx, double.NaN);
        double cy = string.IsNullOrWhiteSpace(_txtFixCy.Text) ? double.NaN : P(_txtFixCy, double.NaN);

        var o = _wf.VisionOptions;
        _wf.VisionOptions = o with
        {
            FixtureCenterPxX = cx,
            FixtureCenterPxY = cy,
            FiducialRadiusMm = P(_txtFidR, o.FiducialRadiusMm),
            FiducialNominalClockDeg = P(_txtFidClock, o.FiducialNominalClockDeg),
            FiducialDiameterMm = P(_txtFidDia, o.FiducialDiameterMm),
            FiducialRadiusToleranceMm = P(_txtFidTol, o.FiducialRadiusToleranceMm),
            FixtureAssumedAngleDeg = P(_txtManualAngle, o.FixtureAssumedAngleDeg)
        };
        _log.Info($"策略参数已应用：夹具中心=({cx:F1},{cy:F1})px 名义半径={_wf.VisionOptions.FiducialRadiusMm:F2}mm " +
                  $"名义角={_wf.VisionOptions.FiducialNominalClockDeg:F1}° 孔径={_wf.VisionOptions.FiducialDiameterMm:F2}mm");
        RunStrategyCheck();
    }

    /// <summary>用最近一次成功的整盘定位结果，把工件中心像素填成夹具中心。</summary>
    private void FillFixtureCenterFromPose()
    {
        if (_lastPose is not { Success: true })
        {
            MessageBox.Show(this, "还没有成功的定位结果。\n先把标准件装夹到位、用『① 整盘视觉定位』拍一张成功的图，再点这个按钮。",
                "无法标定夹具中心", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        _txtFixCx.Text = _lastPose.CenterPxX.ToString("F1");
        _txtFixCy.Text = _lastPose.CenterPxY.ToString("F1");
        ApplyStrategyParams();
        _log.Info($"夹具中心已按上次定位结果标定为 ({_lastPose.CenterPxX:F1},{_lastPose.CenterPxY:F1})px。" +
                  "⚠ 相机/镜头/夹具一动就要重标。");
    }

    /// <summary>
    /// 示教名义角：把工件按 12H 摆正、相机对准标记孔，拍一张 → 检测孔心 →
    /// 反算「该孔在表盘系的名义时钟角」，省得人工从图纸换算。
    /// </summary>
    private async Task TeachFiducialAngle()
    {
        if (_camera is null) { _log.Warn("相机不可用"); return; }
        if (!_camera.IsOpen) { _log.Warn("请先点『打开相机』"); return; }
        var o = _wf.VisionOptions;
        if (double.IsNaN(o.FixtureCenterPxX) || double.IsNaN(o.FixtureCenterPxY))
        {
            MessageBox.Show(this, "请先标定夹具中心像素（X/Y），否则算不出孔相对中心的方向。",
                "缺少夹具中心", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        try
        {
            var frame = await _camera.GrabAsync(3000);
            ShowFrame(_pbMain, frame, null);
            var hole = SingleFiducialStrategy.DetectFiducialHole(frame, o, _log);
            if (hole is null)
            {
                _log.Warn("示教失败：没找到标记孔。把相机对准该孔、调好曝光再试。");
                return;
            }
            double dx = hole.Cx - o.FixtureCenterPxX, dy = hole.Cy - o.FixtureCenterPxY;
            double clock = ((90 - Math.Atan2(-dy, dx) * 180 / Math.PI) % 360 + 360) % 360;
            _txtFidClock.Text = clock.ToString("F2");
            ApplyStrategyParams();
            _log.Info($"示教完成：零位标记孔时钟角 = {clock:F2}°（孔心 {hole.Cx:F1},{hole.Cy:F1}px，圆度 {hole.Circularity:F2}）。" +
                      "此后每次装夹的转角 = 实测角 − 该名义角。");
        }
        catch (Exception ex) { _log.Error("示教异常：" + ex.Message); }
    }

    /// <summary>把当前策略 ID 与单标记孔参数写回 config.json（保留其它配置字段）。</summary>
    private void SaveStrategyConfig()
    {
        try
        {
            ApplyStrategyParams();
            var o = _wf.VisionOptions;
            string path = Path.Combine(AppContext.BaseDirectory, "config.json");
            if (!File.Exists(path)) path = "config.json";
            var node = JsonNode.Parse(File.ReadAllText(path))?.AsObject()
                       ?? throw new InvalidDataException("config.json 不是有效 JSON 对象");

            node["PositioningStrategyId"] = o.StrategyId;
            // NaN 不能写进 JSON（JsonNode 会生成非法数值）→ 未标定时直接删掉这两个键
            if (double.IsNaN(o.FixtureCenterPxX)) node.Remove("FixtureCenterPxX");
            else node["FixtureCenterPxX"] = o.FixtureCenterPxX;
            if (double.IsNaN(o.FixtureCenterPxY)) node.Remove("FixtureCenterPxY");
            else node["FixtureCenterPxY"] = o.FixtureCenterPxY;

            node["FiducialRadiusMm"] = o.FiducialRadiusMm;
            node["FiducialDiameterMm"] = o.FiducialDiameterMm;
            node["FiducialRadiusToleranceMm"] = o.FiducialRadiusToleranceMm;
            node["FiducialNominalClockDeg"] = o.FiducialNominalClockDeg;
            node["FixtureAssumedAngleDeg"] = o.FixtureAssumedAngleDeg;

            File.WriteAllText(path, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Encoding.UTF8);
            _log.Info("定位策略配置已保存：" + path);
            MessageBox.Show(this, "已保存到：\n" + path, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex) { _log.Error("保存策略配置失败：" + ex.Message); }
    }

    // ==================================================================
    // 转台页签的动作（2026-11-06）
    // ==================================================================

    /// <summary>
    /// OPC UA 只读探测：单独建会话（与 DeviceMode=Sim/Real 无关）→ 扫节点与写权限 → 存报告 → 断开。
    /// 网络通之后的第一步就该跑这个；全程不写任何机床变量、不触发任何轴运动。
    /// </summary>
    private async Task ProbeOpcUaAsync()
    {
        if (_probeBusy) return;
        _probeBusy = true;
        _log.Info($"[探测] 开始只读探测 → {_cfg.CncEndpoint}（独立会话；不写机床、不触发运动）");

        var probe = new OpcUaCncService(_log)
        {
            VarNamespaceKeyword = _cfg.CncVarNamespace,
            UserName = _cfg.CncUserName,
            Password = _cfg.CncPassword,
            UseSecureEndpoint = _cfg.CncUseSecurity,
            RunConnectDiagnostics = false   // 专项探测自带更完整的扫描，跳过连接时的通用诊断
        };
        try
        {
            bool ok = await probe.ConnectAsync(_cfg.CncEndpoint);
            if (!ok)
            {
                _log.Warn("[探测] 会话建立失败 → 检查 IP / 4840 端口 / 客户端证书是否被 828D 信任 / 账号权限");
                MessageBox.Show("OPC UA 会话建立失败，请检查网络、端口 4840、客户端证书信任与账号权限。\r\n详细原因见下方日志。",
                    "探测失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }

            var report = await probe.ProbeRotaryAsync(_cfg.RotaryTargetRNumber);
            string file = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                                       $"OPCUA探测-{DateTime.Now:yyyyMMdd-HHmmss}.txt");
            File.WriteAllText(file, report.Text, new UTF8Encoding(true));   // BOM：记事本直接打开不乱码
            foreach (var line in report.Text.Split('\n'))
                if (line.Trim().Length > 0) _log.Info("[探测] " + line.TrimEnd('\r'));
            _log.Info($"[探测] 报告已保存：{file}");

            MessageBox.Show(
                $"只读探测完成（未写机床、未触发运动）。\r\n\r\n" +
                $"轴角度候选 {report.CandidateAngleNodes.Count} 条，报告已保存：\r\n{file}\r\n\r\n" +
                "请把该文件发回开发侧，用来定死 config.json 里的节点路径。",
                "OPC UA 探测完成", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _log.Error("[探测] 异常：" + ex.Message);
            MessageBox.Show("探测异常：" + ex.Message, "探测失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            try { await probe.DisconnectAsync(); } catch { /* 断开失败不阻塞界面 */ }
            _probeBusy = false;
        }
    }

    private void RefreshRotaryUi()
    {
        _lblRotaryStatus.Text = "转台模式：" + _rotary.Mode + " ｜ " + _rotary.ReadinessDetail;
        _lblRotaryAngle.Text = $"当前角度：{_rotary.CurrentAngleDeg:F3}°";
    }

    private async Task MoveRotaryAsync()
    {
        double target = (double)_numRotaryTarget.Value;
        _log.Info($"【转台】请求转到 {target:F3}°（模式 {_rotary.Mode}）");
        var r = await _rotary.MoveToAsync(target);
        if (r.Success) _log.Info("【转台】" + r.Detail);
        else _log.Warn("【转台】未到位：" + r.Detail);
        RefreshRotaryUi();
        MessageBox.Show(r.Detail, r.Success ? "转台到位" : "转台未动作",
            MessageBoxButtons.OK, r.Success ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }

    private async Task ReadRotaryAngleAsync()
    {
        var a = await _rotary.ReadAngleAsync();
        _log.Info(a is null
            ? "【转台】读不到实际角度（未配置 RotaryActualAngleNode / 未连接 / 人工模式）→ 人工模式请用左侧抄角"
            : $"【转台】实际角度 = {a:F3}°");
        RefreshRotaryUi();
    }

    private async Task ConfirmManualAngleAsync()
    {
        if (!double.TryParse(_txtRotaryManual.Text.Trim(), out double deg))
        {
            MessageBox.Show("角度填的不是数字。请照抄 HMI 上显示的转台实际角度。", "输入错误",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        bool ok = await _rotary.SetAngleAsync(deg);
        if (ok) _log.Info($"【转台】人工抄角确认：{_rotary.CurrentAngleDeg:F3}° —— 可以拍下一帧了");
        else _log.Warn("【转台】人工抄角被拒绝（角度超范围或单次转角过大）");
        RefreshRotaryUi();
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

    /// <summary>窗体关闭时释放设备：断开 OPC UA 会话 + 关闭激光串口。
    /// 不释放则 828D 侧会话残留到超时、串口被占用，导致下次连接不上（现场调试反复启停必踩）。</summary>
    protected override async void OnFormClosed(FormClosedEventArgs e)
    {
        try { await _cnc.DisconnectAsync(); } catch { /* 进程即将退出，尽力而为 */ }
        try { await _laser.CloseAsync(); } catch { }
        try { _camera?.Dispose(); } catch { }
        base.OnFormClosed(e);
    }

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
                await ApplyConfiguredCameraParams();
                _log.Info("相机已打开：" + _camera.DeviceName);
            }
            catch (Exception ex) { _log.Error("相机打开失败：" + ex.Message + "（检查USB3线、相机供电）"); }
        }
    }

    /// <summary>config.json 配置了曝光/增益时，打开相机后强制下发——
    /// 防止 pylon Viewer 或其他人改乱的相机参数被带进加工流程（2026-11-02 现场教训）。
    /// 2026-11-06 修正：增益 0 是现场要的合法值，不能当成「不设置」跳过，
    /// 否则 pylon 里被调高的增益会一直留在相机里。曝光仍保留 0=不下发的语义。</summary>
    private async Task ApplyConfiguredCameraParams()
    {
        if (_camera == null) return;
        if (_cfg.CameraExposureUs > 0)
            await _camera.SetExposureUsAsync(_cfg.CameraExposureUs);
        if (_cfg.CameraGain >= 0)
            await _camera.SetGainAsync(_cfg.CameraGain);
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
            await ApplyConfiguredCameraParams();
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

    /// <summary>
    /// 把界面上的"粗对齐角"同步到视觉选项。
    /// 工件自带的 8 个定位孔按 90° 对称 → 单张图只能得到 4 个等效转角，
    /// 必须靠操作工"把工件按 12H 大致摆正"的角度来消歧（容差 ±10°）。
    /// </summary>
    private void SyncCoarseAlign()
    {
        if (double.TryParse(_txtCoarse.Text.Trim(), out double deg))
        {
            // 归一化到 ±180
            deg = ((deg % 360) + 540) % 360 - 180;
            _wf.VisionOptions = _wf.VisionOptions.WithCoarseAlign(deg);
        }
        else
        {
            _wf.VisionOptions = _wf.VisionOptions.WithCoarseAlign(double.NaN);
        }
    }

    private async Task GrabLocateShow()
    {
        try
        {
            if (_camera is null) { _log.Warn("相机不可用（未探测到 pylon SDK，见 Hummingbird.Devices.Basler/README.md）"); return; }
            if (_camera is SimCameraService sc) sc.FreezePose = _chkFreeze.Checked;
            SyncCoarseAlign();
            WarnIfStrategyNotReady();
            var (pose, frame) = await _wf.GrabAndLocateAsync();
            _lastPose = pose; _lastFrame = frame;
            ShowFrame(_pbMain, frame, pose);
            ShowFrame(_pbCal, frame, null);
            _lblPose.Text = pose.Success
                ? $"定位成功 | 中心({pose.CenterPxX:F0},{pose.CenterPxY:F0})px | 转角 {pose.AngleOffsetDeg:F2}° | 置信度 {pose.Confidence:F2} | {pose.Detail}"
                : "定位失败：" + pose.Detail;
            // 多解时把候选角直接摆出来，操作工照着"粗对齐角"填即可
            if (pose.Success && pose.AngleCandidatesDeg.Count > 1)
            {
                _txtCoarse.Text = pose.AngleOffsetDeg.ToString("F1");
                _log.Warn($"角向有 {pose.AngleCandidatesDeg.Count} 个等效解（{string.Join(", ", pose.AngleCandidatesDeg.Select(c => c.ToString("F1")))}°），" +
                          "已按当前粗对齐角选定。请确认工件是否按 12H 摆正（必要时改'粗对齐角'后重拍）");
            }
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
            SyncCoarseAlign();
            if (!WarnIfStrategyNotReady())
            {
                var ans = MessageBox.Show(this,
                    "当前定位策略有硬前提未满足（详见『定位策略』页自检结果）。\n" +
                    "继续加工可能出错误孔位甚至打废工件。\n\n仍要继续吗？",
                    "定位策略前提不满足", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans != DialogResult.Yes) { _log.Warn("已取消：定位策略前提不满足"); return; }
            }
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

    // ---------- 现场诊断（2026-11-05） ----------
    //
    // 现场现象：关闭红光后画面全黑；红光长亮时能看到中心孔周围金属；
    //           手机照明 + 曝光拉满也只看到"一点点亮的东西"。
    // 要分清三件事，不能靠猜：
    //   ① 相机允许的最大曝光/增益是多少？（是不是界面/驱动把值卡住了）
    //   ② 当前照明到底需要多长曝光才够亮？
    //   ③ 视野有多大 → 角向基准孔（R14.45mm）在不在画面内？
    //      不在 → datumHoles 方案直接失效，必须改硬件方案或改人工对刀。

    /// <summary>① 查询相机真实参数范围（曝光/增益/黑电平/gamma/Binning/ROI/传感器）。</summary>
    private async Task DiagParamRanges()
    {
        if (_camera is not ICameraDiagnostics diag)
        {
            _log.Warn("当前相机不支持诊断（模拟相机或未装 pylon SDK）");
            _txtDiag.Text = "当前相机不支持参数范围查询。";
            return;
        }
        if (!_camera.IsOpen) { _log.Warn("请先点『打开相机』"); _txtDiag.Text = "相机未打开。"; return; }

        try
        {
            string s = await Task.Run(() => diag.GetParameterRanges());
            _txtDiag.Text = s;
            _log.Info("【诊断①】相机参数范围：\n" + s);
        }
        catch (Exception ex)
        {
            _log.Error("参数范围查询失败：" + ex.Message);
            _txtDiag.Text = "查询失败：" + ex.Message;
        }
    }

    /// <summary>② 自动曝光扫描：一次性找出当前照明下够亮的曝光档。</summary>
    private async Task DiagExposureScan()
    {
        if (_camera is not ICameraDiagnostics diag)
        {
            _log.Warn("当前相机不支持诊断");
            _txtDiag.Text = "当前相机不支持曝光扫描。";
            return;
        }
        if (!_camera.IsOpen) { _log.Warn("请先点『打开相机』"); _txtDiag.Text = "相机未打开。"; return; }

        // 覆盖从短到很长的曝光跨度：现场报"曝光加到最大还是暗"，这里要看到最上限的表现
        double[] list = { 100, 500, 1000, 5000, 20000, 50000, 100000, 300000, 1000000 };
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("曝光扫描（曝光us / 平均灰度 / 峰值灰度）");
        sb.AppendLine("------------------------------------------------");
        try
        {
            var probes = await diag.ScanExposureAsync(list);
            foreach (var p in probes)
            {
                string line = p.Ok
                    ? $"{p.ExposureUs,10:F0}  均值 {p.MeanGray,6:F1}  峰值 {p.MaxGray,3:F0}"
                    : $"{p.ExposureUs,10:F0}  采集失败";
                sb.AppendLine(line);
                _log.Info("【诊断②】" + line);
            }

            // ---- 判据（用真机实测数据校准过，2026-11-05）----
            // 真机扫描结果：5000us 均值63/峰值255；20000us 均值202；50000us 起全白(255/255)。
            //   → "够亮"的标准不能只看峰值（峰值一到255就饱和了），要看**均值**落在 40~200 之间。
            //   → 均值 < 40 = 太暗（噪点多，定位不稳）；均值 > 200 = 过曝（结构糊了）
            var tooDark = probes.Where(p => p.Ok && p.MeanGray < 40).ToList();
            var usable = probes.Where(p => p.Ok && p.MeanGray >= 40 && p.MeanGray <= 200).ToList();
            var overExposed = probes.Where(p => p.Ok && p.MeanGray > 200).ToList();

            sb.AppendLine("------------------------------------------------");
            if (usable.Count > 0)
            {
                var best = usable.OrderBy(p => p.ExposureUs).First();
                // 「更稳的一档」= 均值最接近可用区间中点 120 的档，而不是均值最小的档。
                // 早期写成 OrderBy(MeanGray).First() 会恒等于上面最短曝光那档，报告里重复输出同一档。
                var safe = usable.OrderBy(p => Math.Abs(p.MeanGray - 120)).First();
                sb.AppendLine($"结论：✅ 可用曝光区间 {usable.Min(p => p.ExposureUs):F0} ~ {usable.Max(p => p.ExposureUs):F0}us");
                sb.AppendLine($"      推荐起点：{best.ExposureUs:F0}us（均值 {best.MeanGray:F1}）；" +
                              $"更稳的一档：{safe.ExposureUs:F0}us（均值 {safe.MeanGray:F1}）");
                sb.AppendLine("      判据：均值 40~200 为可用（<40 太暗噪点多，>200 过曝结构糊掉）；峰值 255 表示有饱和区域。");
                if (overExposed.Count > 0)
                    sb.AppendLine($"      注意：{overExposed.Min(p => p.ExposureUs):F0}us 起已过曝，不要用。");
            }
            else if (tooDark.Count > 0 && overExposed.Count > 0)
            {
                sb.AppendLine("结论：⚠ 曝光区间**跳空**——低档太暗、高档过曝，中间没有可用档。");
                sb.AppendLine($"      太暗：≤ {tooDark.Max(p => p.ExposureUs):F0}us；过曝：≥ {overExposed.Min(p => p.ExposureUs):F0}us");
                sb.AppendLine("      → 这是**动态范围不够**的典型表现：光太弱的同时相机一开大就饱和。");
                sb.AppendLine("      → 处理：①降低光源强度 + 缩短曝光（把光打散、拉长曝光）②换低噪声/高动态范围的照明方案");
                sb.AppendLine("            ③如果红光是激光引光，考虑用更弱的光或加漫射罩降低峰值");
                sb.AppendLine("      → 同时可试：增益保持 0，靠曝光调节（增益会同时放大噪点和饱和区）");
            }
            else if (overExposed.Count > 0 && overExposed.Count == probes.Count)
            {
                sb.AppendLine("结论：❌ 所有档位都过曝 → 曝光要往小调（当前设置比画面需要的亮太多）。");
            }
            else
            {
                sb.AppendLine("结论：❌ 所有曝光档位画面都明显偏暗 → 不是曝光设置问题，是**照明光强不足**。");
                sb.AppendLine("      处理：①改红光照明方式（同轴 / 低角度斜照明 / 偏振片 / 入射角），不是换光源颜色；");
                sb.AppendLine("            ②白光全黑已确认，别换白光LED；向客户/机床厂确认镜头前是否装了红色窄带滤光片、截止波长多少；");
                sb.AppendLine("            ③检查镜头光圈是否被拧到最小、是否装了减光片；");
                sb.AppendLine("            ④确认红光光源功率与工作距离 —— 照明光强不足才是根因。");
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine("扫描失败：" + ex.Message);
            _log.Error("曝光扫描失败：" + ex.Message);
        }
        finally
        {
            // ⚠ 2026-11-06 现场事故：扫描最后一档 1000000us 会把相机留在全白过曝档。
            // 设备层已恢复「扫描前值」，这里再强制下发 config.json 的配置值双保险：
            // 即使扫描前相机本来就被 pylon 改乱，扫完也必须落到 5000us / 增益 0。
            // 异常路径同样执行，保证相机永远不会因为诊断动作被改坏。
            try
            {
                await ApplyConfiguredCameraParams();
                if (_cfg.CameraExposureUs > 0)
                    _numExp.Value = (decimal)Math.Clamp(_cfg.CameraExposureUs,
                        (double)_numExp.Minimum, (double)_numExp.Maximum);
                if (_cfg.CameraGain >= 0)
                    _numGain.Value = (decimal)Math.Clamp(_cfg.CameraGain,
                        (double)_numGain.Minimum, (double)_numGain.Maximum);
                sb.AppendLine($"扫描结束：曝光已恢复为配置值 {_cfg.CameraExposureUs:F0}us、增益 {_cfg.CameraGain:F1}（相机不会停在过曝档）");
                _log.Info($"[诊断②] 曝光已恢复为配置值 {_cfg.CameraExposureUs:F0}us / 增益 {_cfg.CameraGain:F1}");
            }
            catch (Exception ex)
            {
                sb.AppendLine("⚠ 恢复曝光失败：" + ex.Message + " —— 请手动点『应用参数』把曝光改回 5000us");
                _log.Error("[诊断②] 恢复曝光失败：" + ex.Message);
            }
            _txtDiag.Text = sb.ToString();
        }
    }

    /// <summary>③ 视野诊断：用**白色外环（Ø34.2mm，图纸已知）**反算 mm/px，判断基准孔在不在画面内。</summary>
    private async Task DiagFieldOfView()
    {
        if (_camera is null) { _log.Warn("相机不可用"); return; }
        if (!_camera.IsOpen) { _log.Warn("请先点『打开相机』"); _txtDiag.Text = "相机未打开。"; return; }

        try
        {
            var frame = await _camera.GrabAsync(3000);
            ShowFrame(_pbMain, frame, null);
            double datumR = _template.DatumHoleRadiusMm > 0 ? _template.DatumHoleRadiusMm : 14.45;
            double outerD = _template.PartOuterDiameterMm > 0 ? _template.PartOuterDiameterMm : 34.2;
            var diag = new FieldDiagnosis(_log);
            // 传入已标定的 mm/px 做交叉校验（2026-11-06）：现场曾把局部弧/辉光边缘
            // 拟合成假外环（0.056066 vs 标定 0.0234，差 2.4 倍）却仍报「✅ 方案可行」。
            // 有标定值时必须拦住这种假阳性，宁可报"作废"也不给错误结论。
            var r = diag.Analyze(frame, datumR, outerD, _cfg.VisionPixelSizeMm);
            _txtDiag.Text = r.Detail;

            // 结论分级提示：现场最关心的不是数字，而是"能不能干"
            if (r.Success && !r.DatumVisible)
            {
                MessageBox.Show(this,
                    "诊断结论：**不能认定基准孔可见，datumHoles 整盘方案不可用**\n\n" +
                    "8 个双环孔在红光下不反光；画面里的亮斑是灯光反射辉光，不是孔。\n" +
                    "白光拍照全黑（现场已确认，疑似镜头前装了红色窄带滤光片），不要再试换白光。\n\n" +
                    "现场已定的路线：上料定位到 1 孔 + 转台分度拍多帧，每帧只需看清 1 个孔。\n" +
                    "详细判据见下方诊断文本。",
                    "诊断③：基准孔不可见", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            else if (r.Success)
            {
                MessageBox.Show(this,
                    $"诊断结论：基准孔**可见**，角向定位方案可用。\n\n" +
                    $"mm/px = {r.MmPerPx:F6}\n" +
                    $"视野 = {r.FovWidthMm:F1} × {r.FovHeightMm:F1} mm\n" +
                    $"基准孔余量 = {r.MarginMm:F2} mm" +
                    (r.MarginMm < 1.5 ? "（⚠ 余量很小，装夹精度要严格控制）" : ""),
                    "诊断③", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else
            {
                // 标尺不可信 / 全黑 / 全白 / 未量到：明确告诉现场"这帧不能用来做判断"，
                // 避免只把文字塞进文本框、现场却拿旧结论继续干。
                string brief = r.Detail.Length > 600 ? r.Detail[..600] + "…" : r.Detail;
                MessageBox.Show(this,
                    "诊断未通过，本帧结论作废（不要据此判断能不能定位/打孔）。\n\n" + brief,
                    "诊断③：标尺不可信 / 画面不可用", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }

            if (r.Success)
                _log.Info("【诊断③】" + r.Detail.Replace("\n", " | "));
            else
                _log.Warn("【诊断③】" + r.Detail);
        }
        catch (Exception ex)
        {
            _log.Error("视野诊断失败：" + ex.Message);
            _txtDiag.Text = "视野诊断失败：" + ex.Message;
        }
    }

    /// <summary>保存诊断结果到文件（带回现场分析/发给客户）。</summary>
    private void SaveDiag()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "output", $"诊断_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _txtDiag.Text, Encoding.UTF8);
            _log.Info("诊断结果已保存：" + path);
            MessageBox.Show(this, "已保存：\n" + path, "提示", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
        catch (Exception ex)
        {
            _log.Error("保存诊断结果失败：" + ex.Message);
        }
    }

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
