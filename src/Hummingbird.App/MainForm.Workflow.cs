using System.Text.Json;
using Hummingbird.Core;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private TabPage _environmentPage = null!, _machiningPage = null!, _returnPage = null!, _opticsPage = null!, _toolsPage = null!, _reworkPage = null!;
    private TabControl _captureDetails = null!;
    private FlowLayoutPanel _captureProjectControls = null!;
    private readonly FlowLayoutPanel _reworkActions = new() { AutoSize=true, Dock=DockStyle.Top, Padding=new Padding(10) };
    private readonly Label _reworkSummary = new() { AutoSize=true, MaximumSize=new Size(950,0), Padding=new Padding(10) };
    private Func<Func<Task>,Task>? _runWorkspaceAction;
    private Action<RingScanSettings>? _adoptOpticalSettings;
    private Action? _markOpticsChanged;
    private Action? _invalidateOpticalBaseline;
    private Func<string,Task>? _importOpticalCalibration;
    private static FlowLayoutPanel WorkflowFlow()
    {
        var flow=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true,Padding=new Padding(10)};
        var widths=new Dictionary<Control,int>();
        void Fit()
        {
            if(flow.ClientSize.Width<400)return;
            int width=flow.ClientSize.Width-40;
            void FitItem(Control item,int available)
            {
                if(item is TextBox)
                {
                    if(!widths.ContainsKey(item))widths[item]=item.Width;
                    item.Width=Math.Min(widths[item],available);
                }
                else if(item.AutoSize)item.MaximumSize=new Size(available,0);
            }
            foreach(Control child in flow.Controls)
            {
                FitItem(child,width);
                if(child is GroupBox group)
                    foreach(var content in group.Controls.OfType<FlowLayoutPanel>())
                    {
                        content.MaximumSize=new Size(width-25,0);
                        foreach(Control item in content.Controls)FitItem(item,width-45);
                    }
            }
        }
        flow.SizeChanged+=(_,_)=>Fit();flow.ControlAdded+=(_,_)=>Fit();return flow;
    }

    private void BuildWorkflowUi()
    {
        var machining=BuildRingScanTab();
        _environmentPage=BuildEnvironmentPage();
        _reworkPage=new TabPage("4 同件补加工");
        var rework=WorkflowFlow();
        rework.Controls.Add(GroupBoxOf("已加工工件追加加工", Lbl("选择最新完成轮次，建立关联的空采集记录；程序重新定位并加工全部18孔。"),
            Lbl("本轮时长是追加量。采图失败且未出光时使用“未出光重试”；原失败轮不计入追加次数。")));
        rework.Controls.Add(_reworkActions); rework.Controls.Add(_reworkSummary);
        rework.Controls.Add(Btn("查看定位画面并继续加工",(_,_)=>_selectProductionPanel?.Invoke()));
        rework.Controls.Add(Lbl("准备后：面板从头启动原HM_MASTER → 第3页一键补加工。软件自动检查，缺项按提示处理。\r\n部分已加工或出光状态不明的轮次不能自动重试。原始轮次、失败依据和登记历史均保留。"));
        _reworkPage.Controls.Add(rework);
        _tabs.Multiline=true;
        _tabs.TabPages.AddRange([_environmentPage,_returnPage,machining,_reworkPage,_opticsPage,BuildArchivePage(),_toolsPage]);
        var emergency=Btn("停止加工／关光",async(_,_)=>{RequestProductionStop();if(_laser is Hummingbird.Devices.LaserRtu.ModbusRtuLaserService rtu)await UiAction(()=>rtu.LaserOffAsync());});
        emergency.Name="GlobalProductionStop";
        var header=new FlowLayoutPanel {Dock=DockStyle.Top,AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,MinimumSize=new Size(0,48),Padding=new Padding(8),WrapContents=true};
        header.Controls.AddRange([Lbl("飞秒创芯 · 自动定位加工"),_liveEnabled,
            emergency,
            _cameraStatus,Lbl("  "),_lblPos]);
        var logHost=new Panel {Dock=DockStyle.Bottom,Height=58};logHost.Controls.Add(_rtbLog);
        Controls.Add(_tabs);Controls.Add(header);Controls.Add(logHost);
        _captureDetails.SelectedIndex=0;
    }

    private TabPage BuildEnvironmentPage()
    {
        var tab=new TabPage("1 环境与参数"); var flow=WorkflowFlow();
        var report=new TextBox {Name="EnvironmentReport",Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Width=990,Height=125};
        void Inspect()
        {
            report.Text=$"运行模式：{_cfg.DeviceMode}；相机：{(_camera?.IsOpen==true?"已连接":"未连接")}；数控：{(_cnc.IsConnected?"已连接":"未连接")}；激光：{(_laser.IsOpen?"串口已打开":"未连接")}\r\n"
                +$"资料目录：{_state.CaptureRoot}\r\n六步基准：{_state.LastRouteSessionPath}\r\n激光落点：{_state.LaserCalibrationPath}\r\n主控配置：{_state.LastProductionProfilePath}\r\n"
                +"第3步一键加工会自动连接并检查，缺项显示处理方法；接口无法读取的参考、报警和装夹在启动时集中确认。";
        }
        flow.Controls.Add(GroupBoxOf("开工顺序", Lbl("厂家软件开机并消除告警、释放串口 → 连接设备 → 核对参数及读回 → 必要时恢复工位 → 新件或补加工。"),
            Btn("检查当前环境（不连接）",(_,_)=>Inspect()),Btn("连接相机和数控",async(_,_)=>{await UiAction(ConnectAcquisitionDevices);Inspect();}),
            Btn("选择资料根目录",(_,_)=>{EnsureCaptureRoot(true);Inspect();}),report));
        flow.Controls.Add(GroupBoxOf("激光连接",Lbl("串口"),_txtCom,Lbl("波特率"),_cmbBaud,
            Btn("打开串口",async(_,_)=>{await RunLaserDebugAction(OpenLaserPort);Inspect();}),
            Btn("关闭串口",async(_,_)=>{await RunLaserDebugAction(()=>_laser.CloseAsync());Inspect();})));
        flow.Controls.Add(BuildLaserCalibrationControls());
        flow.Controls.Add(GroupBoxOf("手动采图／维护时的现场确认",Lbl("一键加工会自动检查工位并集中提示缺项，无需先在此勾选。以下仅供手动采图和维护操作使用。"),
            Lbl("固定C°（面板实测）"),_spotTrialC,_fixedCConfirmed));
        flow.Controls.Add(GroupBoxOf("相机参数",Lbl("曝光 µs"),_numExp,Lbl("增益"),_numGain,
            Lbl("自动曝光在定位加工页启用；正式采图前自动下发并记录实际选用值。")));
        var configGuide=new TextBox {Name="ConfigurationGuide",Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Width=990,Height=155,Text=
            "config.json：程序目录中的设备连接配置。更新程序时保留现场文件；修改后重启生效。\r\n"
            +"机床程序/整件加工配置.json：上位机使用的XY范围、固定Z/C、R220～229专用及会话参数，自动读取。对应HM_MASTER.MPF由机床面板从头启动。\r\n"
            +"机床程序/工位恢复配置.json：上位机自动读取的恢复顺序、目标及范围；HM_RETURN.MPF由面板启动。已有对应MPF无需再次上传或导出；MZ2保持。\r\n"
            +"采集autosave / 六步基准：恢复照片和采图路线；新件仍重新定位。激光落点JSON须与原trial及前后图一起保留。\r\n"
            +"operator-state.json：当前Windows用户的操作偏好和路径；不会恢复本次装夹、参考或光学确认。\r\n"
            +"镜头、相机高度或工作距离调整后，进入第5页重新计算XY和落点。普通回位或工件取放无需默认重标。"};
        flow.Controls.Add(GroupBoxOf("配置文件说明",configGuide));
        flow.Controls.Add(Btn("下一步：工位恢复",(_,_)=>_tabs.SelectedTab=_returnPage));
        flow.Controls.Add(Btn("工作位置已恢复：前往定位加工",(_,_)=>_selectProductionPanel?.Invoke()));
        tab.Controls.Add(flow);tab.Enter+=(_,_)=>Inspect();Inspect();return tab;
    }

    private TabPage BuildArchivePage()
    {
        var tab=new TabPage("加工资料与日志");
        var type=new ComboBox {DropDownStyle=ComboBoxStyle.DropDownList,Width=160};
        type.Items.AddRange(["全部","新件加工","同件补加工","工位恢复","光学标定","现场取证","单点与演练"]);type.SelectedIndex=0;
        var table=new DataGridView {Name="ArchiveRecords",Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,AllowUserToDeleteRows=false,RowHeadersVisible=false,SelectionMode=DataGridViewSelectionMode.FullRowSelect,MultiSelect=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
        foreach(string title in new[]{"分类","记录目录","状态 / 关联轮次"})table.Columns.Add(title,title);
        var paths=new List<string>();
        void RefreshRecords()
        {
            table.Rows.Clear();paths.Clear();
            if(!Directory.Exists(_state.CaptureRoot))return;
            foreach(string path in Directory.EnumerateDirectories(_state.CaptureRoot).OrderByDescending(Path.GetFileName))
            {
                string name=Path.GetFileName(path);
                string category=name.StartsWith("补加工_")?"同件补加工":name.StartsWith("工件_")?"新件加工":name.StartsWith("工位恢复_")?"工位恢复":name.StartsWith("光学标定_")?"光学标定":name.StartsWith("现场取证_")?"现场取证":name.StartsWith("单点")||name.StartsWith("整件模拟_")?"单点与演练":"";
                if(category.Length==0 || type.SelectedIndex>0 && category!=(string)type.SelectedItem!)continue;
                string status="资料保留";
                string record=Path.Combine(path,ProductionJobArchive.MachiningRecordName);
                try
                {
                    if(File.Exists(record))
                    {
                        var run=JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(record))!;
                        status=$"{ProductionJobArchive.PhaseText(ProductionJobArchive.ReviewAfterRestart(run))} · {run.RunId.ToString()[..8]}";
                        if(run.Rework is {} rw)status+=$" · 第{rw.Round}轮 · 来源{rw.ParentRunId.ToString()[..8]}";
                    }
                }
                catch(Exception ex){status="记录需核对："+ex.Message;}
                table.Rows.Add(category,name,status);paths.Add(path);
            }
        }
        string Selected()=>table.CurrentRow is {} row && row.Index<paths.Count?paths[row.Index]:throw new InvalidOperationException("先选择一份记录。");
        var actions=new FlowLayoutPanel {Dock=DockStyle.Top,AutoSize=true};
        actions.Controls.AddRange([type,Btn("刷新列表",(_,_)=>UiArchive(RefreshRecords)),
            Btn("打开原始资料",(_,_)=>UiArchive(()=>OpenLocalFolder(Selected()))),
            Btn("恢复采集与加工状态",async(_,_)=>await UiAction(async()=>{await _loadWorkspace!(Path.Combine(Selected(),RingCaptureArchive.AutosaveName));_selectProductionPanel?.Invoke();})),
            Btn("作为补加工来源",async(_,_)=>await UiAction(async()=>{if(_workspaceBusy || _laserExposureTask is {IsCompleted:false})throw new InvalidOperationException("请等待当前操作结束。");string selected=Selected();await _runWorkspaceAction!(async()=>{await _prepareProductionRework!(Path.Combine(selected,ProductionJobArchive.MachiningRecordName));_tabs.SelectedTab=_reworkPage;});})),
            Btn("打开分类运行日志",(_,_)=>UiArchive(()=>OpenLocalFolder(Path.Combine(AppContext.BaseDirectory,"logs"))))]);
        var help=new Label {Dock=DockStyle.Bottom,Height=55,Text="本件目录保存六图、定位诊断、逐孔trial/前后图、run及补加工关联；完整本件资料包继续自动生成。\r\n运行日志另按日期与加工、视觉、激光、设备、异常分类保存。恢复记录仅供查看；同件追加从补加工入口建立新轮次。"};
        void UiArchive(Action action){try{action();}catch(Exception ex){_log.Warn("资料查看："+ex.Message);}}
        type.SelectedIndexChanged+=(_,_)=>UiArchive(RefreshRecords);tab.Enter+=(_,_)=>UiArchive(RefreshRecords);
        tab.Controls.Add(table);tab.Controls.Add(actions);tab.Controls.Add(help);return tab;
    }

    private static void OpenLocalFolder(string path)
    {
        if(!Directory.Exists(path))throw new DirectoryNotFoundException("目录尚未建立："+path);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path){UseShellExecute=true});
    }
}
