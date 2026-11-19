using System.Globalization;
using System.Text;
using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Devices.LaserRtu;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private CancellationTokenSource? _stationReturnStop;
    private Button? _stationReturnStopButton;
    private bool _stationReturnUiAction;
    private Action? _selectStationReturnPanel;
    private Action<string>? _loadStationReturnProfile;

    private TabPage BuildStationReturnTab(Func<IReadOnlyList<CaptureWaypoint>?> readRoute, Func<Func<Task>, Task> run)
    {
        var tab = new TabPage("工位恢复");
        var layout = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoScroll = true, Padding = new Padding(8) };
        var summary = new Label { Name = "StationReturnProfile", AutoSize = true, MaximumSize = new Size(580, 0), Text = "正在读取随程序交付的已核定工位参数。" };
        var status = new TextBox { Name = "StationReturnStatus", Multiline = true, ReadOnly = true, Dock = DockStyle.Bottom, Height = 115, ScrollBars = ScrollBars.Vertical };
        var confirmed = new CheckBox { Name = "StationReturnReferenceConfirmed", AutoSize = false, Height = 46, Text = "本次机床参考有效，回位区域已清空，MZ2保持条件已核对" };
        StationReturnProfile? profile = null;
        string? resultFolder = null;
        _loadStationReturnProfile = path =>
        {
            var loaded = JsonSerializer.Deserialize<StationReturnProfile>(File.ReadAllText(path)) ?? throw new InvalidDataException("工位恢复配置为空。");
            loaded.Validate(); StationProgramFiles.VerifyPairedProgram(path,StationProgramFiles.ReturnProfile,"HM_RETURN.MPF",StationReturnNcProgram.Generate(loaded));
            profile = loaded; confirmed.Checked = false;
            var b = loaded.WorkEnvelope!;
            summary.Text = "当前配置："+Path.GetFullPath(path)+"\r\n配置是上位机核对恢复顺序、范围及程序编号的参数文件，无需上传到机床。\r\n"+FormattableString.Invariant($"安全MZ1={loaded.ClearanceZ:F5} → C={b.C:F4}° → X={loaded.TargetX:F5} Y={loaded.TargetY:F5} → 工作MZ1={b.Z:F5}mm\r\n入口：X[{b.MinX},{b.MaxX}]，Y[{b.MinY},{b.MaxY}]，MZ1[{loaded.EntryMinZ},{loaded.EntryMaxZ}]\r\nMZ2：{loaded.SecondaryAxisCondition}");
            status.Text = "已自动使用已核定工位参数，无需重复载入JSON。机床已有对应HM_RETURN时，按上方步骤直接启动；软件尚未连接或运动。";
            _state.LastStationReturnProfilePath = Path.GetFullPath(path); SaveOperatorState();
        };
        async Task Act(Func<Task> action)
        {
            _stationReturnUiAction = true;
            try
            {
                await run(async () =>
                {
                    try { await action(); }
                    catch (Exception ex) { status.Text = "恢复未完成：" + ex.Message + "\r\n" + (resultFolder ?? "") + "\r\n未确认回位时不能继续加工。机床停止以面板为准。"; throw; }
                });
            }
            finally { _stationReturnUiAction = false; }
        }
        var load = Btn("更换回位配置（维护）", async (_, _) => await Act(() =>
        {
            using var dialog = new OpenFileDialog { Filter = "工位恢复配置|*.json" };
            if (dialog.ShowDialog(this) == DialogResult.OK) _loadStationReturnProfile(dialog.FileName);
            return Task.CompletedTask;
        }));
        var export = Btn("导出回位NC", async (_, _) => await Act(() =>
        {
            var p = profile ?? throw new InvalidOperationException("回位参数未就绪，请在配置说明中查看缺失或错误原因。");
            string nc = StationReturnNcProgram.Generate(p);
            using var dialog = new SaveFileDialog { Filter = "回位NC|*.MPF", FileName = "HM_RETURN.MPF" };
            if (dialog.ShowDialog(this) == DialogResult.OK)
            {
                File.WriteAllText(dialog.FileName, nc, Encoding.ASCII); RecordEvidenceNc(dialog.FileName);
                status.Text = "已导出：" + dialog.FileName + "\r\n先结束其他NC，在面板选择HM_RETURN并按Cycle Start。它只进入等待；软件核对关光后才提交回位。每次切回工位使用同一份NC；配置变化才需重新导出。";
            }
            return Task.CompletedTask;
        }));
        var start = Btn("开始恢复表盘工位（不出光）", async (_, _) => await Act(async () =>
        {
            // 冻结本次配置；任何操作失败均不重发运动。
            var p = JsonSerializer.Deserialize<StationReturnProfile>(JsonSerializer.Serialize(profile ?? throw new InvalidOperationException("回位参数未就绪，请在配置说明中查看缺失或错误原因。")))!;
            p.Validate();
            if (_cfg.DeviceMode != "Real" || _cnc is not ISinumerikVarAccess access || !_cnc.IsConnected)
                throw new InvalidOperationException("自动回位需要已连接的真机数控；模拟界面不提交运动。");
            if (!confirmed.Checked) throw new InvalidOperationException("请确认本次机床参考有效、回位区域及MZ2保持条件。");
            var route = readRoute() ?? throw new InvalidOperationException("请先载入该工位的六图基准路线。");
            if (route.Count != 6 || Math.Abs(route[0].X - p.TargetX) > .005 || Math.Abs(route[0].Y - p.TargetY) > .005 ||
                route.Any(w => Math.Abs(w.Z - p.WorkEnvelope!.Z) > .010 || Math.Abs(Math.IEEERemainder(w.C - p.WorkEnvelope!.C, 360)) > .005))
                throw new InvalidOperationException("当前六图路线与回位工位不一致。");
            if (_laser is not ModbusRtuLaserService device || !device.IsOpen)
                throw new InvalidOperationException("请先连接激光串口以读回确认关光；回位不应用功率或出光。");
            string root = EnsureCaptureRoot() ?? throw new OperationCanceledException("未选择回位记录保存目录。");
            resultFolder = Path.Combine(root, "工位恢复_" + DateTime.Now.ToString("yyyyMMdd_HHmmssfff") + "_" + Guid.NewGuid().ToString("N")[..6]);
            Directory.CreateDirectory(resultFolder);
            File.WriteAllText(Path.Combine(resultFolder, "回位程序_核对副本.MPF"), StationReturnNcProgram.Generate(p), Encoding.ASCII);
            var record = new StationReturnRecord();
            Task Persist(StationReturnRecord value)
            {
                string file = Path.Combine(resultFolder, "工位恢复记录.json");
                string temporary = file + ".tmp";
                File.WriteAllText(temporary, JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }));
                File.Move(temporary, file, true);
                return Task.CompletedTask;
            }
            _stopCaptureMonitor?.Invoke();
            _fixedCConfirmed.Checked = false;
            using var stop = new CancellationTokenSource(); _stationReturnStop = stop;
            try
            {
                await device.ReadRecordedProtocolAsync();
                var session = new StationReturnSession(_cnc, access, p, device.VerifyEmissionOffAsync, text => status.Text = text + "\r\n" + resultFolder);
                await session.RunAsync(record, Persist, stop.Token);
                // C来自本次NC完成快照，不能把它当作持续实际反馈，也不自动勾选本次装夹确认。
                _spotTrialC.Text = p.WorkEnvelope!.C.ToString("0.0000", CultureInfo.InvariantCulture);
                status.Text = "工位恢复完成：XYZ实际读回、NC完成编号及C完成快照均符合。\r\nC完成快照=" + record.After!.C.ToString("F4", CultureInfo.InvariantCulture)
                    + "°；尚无持续C反馈，后续仍需核对面板并勾选本次装夹确认。\r\n新建工件，面板从头启动HM_MASTER，再按原流程自动六处采图及18孔加工。\r\n记录：" + resultFolder;
            }
            finally { confirmed.Checked = false; _stationReturnStop = null; }
        }));
        start.Name = "StationReturnStart"; start.Enabled = _cfg.DeviceMode == "Real";
        _stationReturnStopButton = Btn("停止工位恢复", (_, _) =>
        {
            _stationReturnStop?.Cancel();
            status.Text = "已请求停止回位；NC在当前运动段结束后检查退出。若需立即停止，请使用机床面板进给保持或急停。不要继续加工。";
        });
        var files = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true };
        files.Controls.AddRange([load, export]); files.Visible=false;
        summary.Visible=false;
        var instructions=new Label {Name="StationReturnInstructions",AutoSize=true,MaximumSize=new Size(960,0),Text=
            "机床已有对应HM_RETURN：结束其他NC → 面板选择该程序（或此前上传后的实际名称），从头Cycle Start一次，进入等待 → 勾选本次参考/区域/MZ2确认 → 点击“开始恢复表盘工位”。\r\n"
            +"机床没有该程序：使用随程序附带的HM_RETURN.MPF上传，核对上传后的名称并在面板选择，再按上述顺序运行。已有对应程序无需重新上传或导出。\r\n"
            +"工位恢复JSON已随程序提供并自动读取；它是上位机参数，不是需要在机床启动的文件。"};
        var help=new FlowLayoutPanel {AutoSize=true,WrapContents=true};
        help.Controls.AddRange([Btn("没有回位程序：前往上传",(_,_)=>ShowNcUpload("HM_RETURN.MPF")),
            Btn("打开配套程序文件夹",async(_,_)=>await UiAction(()=>{OpenLocalFolder(StationProgramFiles.Folder);return Task.CompletedTask;})),
            Btn("配置说明 / 维护",(_,_)=>{files.Visible=!files.Visible;summary.Visible=files.Visible;})]);
        actions.Controls.AddRange([start, _stationReturnStopButton]);
        layout.Controls.AddRange([instructions,help,confirmed, actions,files,summary,
            new Label { AutoSize = true, MaximumSize = new Size(580, 0), Text = "回位程序只移动MZ1、C、XY，不移动MZ2。回位不等于回参考点；断电后需先由机床人员确认参考有效。入口范围外由机床人员先安全送入，不能从任意位置直接回位。" }]);
        layout.ClientSizeChanged += (_, _) =>
        {
            int width = Math.Max(230, layout.ClientSize.Width - 40);
            confirmed.Width = width; files.MaximumSize = actions.MaximumSize = new Size(width, 0);
            foreach (Label label in layout.Controls.OfType<Label>()) label.MaximumSize = new Size(width, 0);
        };
        tab.Controls.Add(layout); tab.Controls.Add(status);
        try { _loadStationReturnProfile(StationProgramFiles.SelectProfile(_state.LastStationReturnProfilePath,StationProgramFiles.ReturnProfile)); }
        catch (Exception ex) { profile=null;status.Text="工位参数未就绪："+ex.Message;summary.Text=status.Text; }
        return tab;
    }
}
