using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Devices.LaserRtu;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private CancellationTokenSource? _productionStop;
    private Func<LaserSpotCalibration>? _readProductionCalibration;
    private Action<ProductionRunRecord,string>? _reportProductionProgress;
    private Action<string>? _reportProductionDetail;
    private Func<string,Task>? _loadProductionProfile;
    private Button? _productionStopButton;
    private Action<string?>? _restoreProductionJobView;
    private bool _productionUiAction;
    private Action? _selectProductionPanel;
    private ProductionReworkPlan? _activeReworkPlan;
    private Action<ProductionReworkPlan?>? _showProductionRework;
    private Func<string,Task>? _prepareProductionRework;

    private sealed record ProductionPreparation(ContinuousNcSession Nc,ModbusRtuLaserService Laser,
        LaserSpotCalibration Calibration,IReadOnlyList<CaptureWaypoint> Route,LaserProcessPreset Preset,
        ProductionReworkPlan? ReworkPlan,ProductionReworkSource? ReworkSource);

    private void RequestProductionStop()
    {
        _guidedOperationStop?.Cancel();
        _productionStop?.Cancel();_spotTrialStop?.Cancel();
        if(_laser is ModbusRtuLaserService device)device.Calibration.RequestStop();
        _stopCaptureMonitor?.Invoke();
    }

    private TabPage BuildConnectedProductionTab(Func<ProductionStationProfile,bool,Task> start,
        Func<ProductionStationProfile,bool,Task<string>> check,Func<Func<Task>,Task> run)
    {
        var tab=new TabPage("一键加工");
        _selectProductionPanel=()=>{if(tab.Parent is TabControl parent)parent.SelectedTab=tab;};
        async Task Ui(Func<Task> action)
        {
            _productionUiAction=true;
            try{await run(action);}finally{_productionUiAction=false;}
        }
        var setup=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};
        var toggleSetup=Btn("维护与详细检查",(_,_)=>{});
        void SetSetup(bool visible){setup.Visible=visible;toggleSetup.Text=visible?"收起维护与检查":"维护与详细检查";}
        toggleSetup.Click+=(_,_)=>SetSetup(!setup.Visible);
        ProductionStationProfile? profile=null;
        ProductionRunRecord? current=null;
        Button? reworkButton=null;
        bool IsRetryCandidate()=>current is {Rework:not null,Completed:false,DryRun:false,Phase:"Failed" or "Canceled",LaserOffConfirmed:true,NcExitConfirmed:true}
            && current.Holes.Count==0 && current.HoleTimings.Count==0 && current.CurrentHoleId is null;
        void RefreshReworkButton(){if(reworkButton is not null)reworkButton.Text=IsRetryCandidate()?"未出光重试":"准备补加工";}
        string currentFolder="",detail="";
        var profileReady=new Label {Name="ProductionDefaultConfiguration",AutoSize=true,MaximumSize=new Size(950,0)};
        var profileSummary=new Label {Name="ProductionProfileSummary",AutoSize=true,MaximumSize=new Size(950,0)};
        var instructions=new Label {Name="MasterProgramInstructions",AutoSize=true,MaximumSize=new Size(950,0),Text=
            "机床已有对应HM_MASTER：每轮加工前在面板选择该程序（或原HM_MASTER_FIX / 上传后的实际名称），从头Cycle Start一次进入等待；点击一键加工，软件自动核对等待状态、工作Z/C与配置。已在本轮起始等待时不重复启动。\r\n"
            +"机床没有：上传配套HM_MASTER.MPF，核对上传后的名称，在面板选择并启动。主控JSON是上位机使用的范围、固定Z/C、R区及会话参数，已随程序提供并自动读取。"};
        var settings=new Label {AutoSize=true,MaximumSize=new Size(850,0)};
        void RefreshSettings()
        {
            settings.Text=_activeReworkPlan?.Info is { } rw
                ?$"补加工：追加{_laserDurationSeconds.Value:0.###}秒/孔 · 功率原始{_laserPowerRaw.Value}\r\n累计请求{rw.CumulativeRequestedMs/1000.0:0.###}秒/孔 · 18孔约{_laserDurationSeconds.Value*18/60:0.##}分钟。"
                :$"当前每孔：{_laserDurationSeconds.Value:0.###}秒 · 功率原始{_laserPowerRaw.Value} · 18孔纯出光约{_laserDurationSeconds.Value*18/60:0.##}分钟。";
            if(_activeReworkPlan?.Info is { } expected && (expected.AdditionalExposureMs!=(int)(_laserDurationSeconds.Value*1000)||expected.PowerRaw!=(ushort)_laserPowerRaw.Value))
                settings.Text+="\r\n参数与计划不同，请重新准备补加工记录。";
            _reworkSummary.Text=_activeReworkPlan?.Info is {} info ? $"来源轮次：{info.ParentRunId}\r\n补加工第{info.Round}轮 · 追加{info.AdditionalExposureMs/1000.0:0.###}秒/孔 · 累计请求{info.CumulativeRequestedMs/1000.0:0.###}秒/孔 · 功率原始{info.PowerRaw}\r\n"+settings.Text : "尚未准备补加工。先在“维护 → 设备与参数”设置本轮追加参数，再选择前轮记录。";
        }
        RefreshSettings();_laserDurationSeconds.ValueChanged+=(_,_)=>RefreshSettings();_laserPowerRaw.ValueChanged+=(_,_)=>RefreshSettings();
        var state=new TextBox {Name="ProductionStatus",Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill,
            Text="放好新工件 → 面板从头启动HM_MASTER进入等待 → 一键加工。程序自动检查、建档、六处定位及18孔加工；缺项会提示处理方法。"};
        var progress=new Label {Name="ProductionProgress",AutoSize=true,Text="尚未开始 · 采图0/6 · 完成0/18"};
        var table=new DataGridView {Name="ProductionHoles",Dock=DockStyle.Fill,ReadOnly=true,AllowUserToAddRows=false,
            AllowUserToDeleteRows=false,RowHeadersVisible=false,AutoSizeColumnsMode=DataGridViewAutoSizeColumnsMode.Fill};
        table.Columns.Add("Id","孔号");table.Columns.Add("Status","工序状态");table.Columns.Add("Time","完成时间");
        table.Columns[0].FillWeight=45;table.Columns[1].FillWeight=150;table.Columns[2].FillWeight=85;
        foreach(int id in Enumerable.Range(1,18))table.Rows.Add(id,"未开始","");
        void RefreshProgress()
        {
            if(current is null)return;
            RefreshReworkButton();
            int count=current.Holes.Count(p=>p.Value==ProductionHolePhase.Completed);
            string elapsed=current.CurrentHoleId is { } id && current.HoleTimings.TryGetValue(id,out var timing) && timing.FinishedAt is null
                ?$" · 本孔工序已用{Math.Max(0,(DateTimeOffset.Now-timing.StartedAt).TotalSeconds):0}秒":"";
            progress.Text=$"{(current.DryRun?"无出光试走":current.Rework is null?"真实加工":"同件补加工")} · 采图{current.CapturedSteps}/6 · 完成{count}/18"+elapsed;
            progress.ForeColor=current.Error is not null?Color.DarkRed:current.ArchiveError is not null?Color.DarkOrange:current.Completed?Color.DarkGreen:Color.DarkBlue;
            state.Text=ProductionJobArchive.PhaseText(current)+(detail.Length==0?"":"\r\n"+detail)
                +(current.Error is null?"":"\r\n"+current.Error)+"\r\n资料："+currentFolder;
            foreach(int hole in Enumerable.Range(1,18))
            {
                var row=table.Rows[hole-1];row.Cells[1].Value=ProductionJobArchive.HoleText(current,hole);
                row.Cells[2].Value=current.HoleTimings.GetValueOrDefault(hole)?.FinishedAt?.ToString("HH:mm:ss")??"";
                row.DefaultCellStyle.ForeColor=current.Holes.GetValueOrDefault(hole)==ProductionHolePhase.Uncertain?Color.DarkRed:
                    current.Holes.GetValueOrDefault(hole)==ProductionHolePhase.Completed?Color.DarkGreen:Color.Black;
            }
        }
        _reportProductionProgress=(record,folder)=>{if(current?.RunId!=record.RunId){detail="";SetSetup(false);}current=record;currentFolder=folder;RefreshProgress();};
        _reportProductionDetail=text=>{detail=text;RefreshProgress();};
        _restoreProductionJobView=job=>
        {
            _activeReworkPlan=null;
            string planFile=Path.Combine(job??"","补加工计划.json");
            if(job is not null && File.Exists(planFile))
            {
                _activeReworkPlan=JsonSerializer.Deserialize<ProductionReworkPlan>(File.ReadAllText(planFile))??throw new InvalidDataException("补加工计划为空。");
                if(_activeReworkPlan.Version!=1 || _activeReworkPlan.Info is null)throw new InvalidDataException("补加工计划无效。");
            }
            _showProductionRework?.Invoke(_activeReworkPlan);RefreshSettings();
            current=null;detail="";currentFolder=job??"";progress.ForeColor=Color.Black;
            RefreshReworkButton();
            SetSetup(false);
            progress.Text="尚未开始 · 采图0/6 · 完成0/18";
            foreach(DataGridViewRow row in table.Rows){row.Cells[1].Value="未开始";row.Cells[2].Value="";row.DefaultCellStyle.ForeColor=Color.Black;}
            state.Text=_activeReworkPlan?.Info is { } pending?$"已建立同件18孔补加工记录。前轮{pending.ParentRunId}；本轮追加{pending.AdditionalExposureMs/1000.0:0.###}秒/孔、原始功率{pending.PowerRaw}。回装后自动重新定位，不用旧绝对坐标。":
                "放好全新待加工件，面板从头启动HM_MASTER进入等待，再点击一键加工。";
            string path=Path.Combine(job??"",ProductionJobArchive.MachiningRecordName);
            if(job is null || !File.Exists(path))return;
            try
            {
                var saved=JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(path))??throw new InvalidDataException("记录为空。");
                _reportProductionProgress?.Invoke(ProductionJobArchive.ReviewAfterRestart(saved),Path.Combine(job,"整件_"+saved.RunId.ToString("N")));
                detail=IsRetryCandidate()?"已恢复采图失败记录；可点‘未出光重试’，核对独立记录后建立新一轮，不在本目录重跑。":
                    "已恢复本件加工状态；本件禁止全18孔重复执行。";RefreshProgress();
            }
            catch(Exception ex){progress.ForeColor=Color.DarkRed;state.Text="本件加工记录无法读取，禁止重打："+ex.Message;}
        };
        var timer=new System.Windows.Forms.Timer {Interval=1000};timer.Tick+=(_,_)=>{if(_productionStop is not null)RefreshProgress();};timer.Start();
        tab.Disposed+=(_,_)=>timer.Dispose();

        Task LoadProfile(string path)
        {
            var loaded=JsonSerializer.Deserialize<ProductionStationProfile>(File.ReadAllText(path))??throw new InvalidDataException("主控配置为空。");
            loaded.Validate();StationProgramFiles.VerifyPairedProgram(path,StationProgramFiles.ProductionProfile,"HM_MASTER.MPF",ContinuousNcProgram.Generate(loaded.Envelope!,loaded.SessionToken));
            profile=loaded;
            SetSetup(false);
            _state.LastProductionProfilePath=Path.GetFullPath(path);SaveOperatorState();
            profileReady.Text="主控配置已自动读取；机床已有对应HM_MASTER时，面板从头Cycle Start进入等待后检查就绪。没有程序时使用下方上传入口。";
            profileSummary.Text=$"已使用{(Path.GetFullPath(path)==Path.GetFullPath(StationProgramFiles.ProductionProfile)?"交付":"维护指定")}主控配置：{Path.GetFileName(path)} · 会话{loaded.SessionToken}；无需每次手动载入JSON。\r\n路径：{Path.GetFullPath(path)}";
            state.Text="配置已自动读取。放好工件，面板启动HM_MASTER进入等待，点击一键加工；工作位置和程序状态由软件自动检查。";
            return Task.CompletedTask;
        }
        _loadProductionProfile=LoadProfile;
        var load=Btn("更换主控配置（维护）",async(_,_)=>await Ui(async()=>
        {
            using var dialog=new OpenFileDialog {Filter="主控配置|*.json"};if(dialog.ShowDialog(this)!=DialogResult.OK)return;
            await LoadProfile(dialog.FileName);
        }));
        var export=Btn("导出整件主控NC",async(_,_)=>await Ui(()=>
        {
            var p=profile??throw new InvalidOperationException("主控参数未就绪，请查看配置说明及交付文件。");p.Validate();
            using var dialog=new SaveFileDialog {Filter="主控程序|*.MPF",FileName="HM_MASTER.MPF"};if(dialog.ShowDialog(this)!=DialogResult.OK)return Task.CompletedTask;
            File.WriteAllText(dialog.FileName,ContinuousNcProgram.Generate(p.Envelope!,p.SessionToken),System.Text.Encoding.ASCII);RecordEvidenceNc(dialog.FileName);
            state.Text="主控已导出。面板选择本次程序，从头Cycle Start一次；正常等待软件目标时暂时不动。";return Task.CompletedTask;
        }));
        var inspect=Btn("检查整件就绪（不运动）",async(_,_)=>await Ui(async()=>
        {
            var p=profile??throw new InvalidOperationException("主控参数未就绪，请查看配置说明及交付文件。");
            try{state.Text=await check(p,!p.AllowMachining);}
            catch(Exception ex){state.Text="尚未就绪："+ex.Message;throw;}
        }));
        async Task Execute(bool dryRun)
        {
            if(_cfg.DeviceMode!="Real")throw new InvalidOperationException("模拟模式使用整件流程演练或历史联接回放。");
            var p=profile??throw new InvalidOperationException("主控参数未就绪，请查看配置说明及交付文件。");p.ValidateFor(dryRun);
            SetSetup(false);
            state.Text=dryRun?"正在检查并开始试走……":"正在检查并开始本件18孔加工……";
            try
            {
                var workpiece=(_inspectOneClickWorkpiece??throw new InvalidOperationException("工件建档入口未接入。"))();
                string summary=workpiece.Description+"\r\n"+(dryRun?"本轮仅试走，不出光。":
                    $"本轮每孔{_laserDurationSeconds.Value:0.###}秒 · 功率原始{_laserPowerRaw.Value}，共18孔。点击确认后将自动定位并加工。");
                await RunGuidedOperation(p.Envelope!,p.SessionToken,"HM_MASTER",summary,!dryRun,false,async ct=>
                {
                    if(workpiece.Create)await (_createOneClickWorkpiece??throw new InvalidOperationException("自动建档入口未接入。"))();
                    ct.ThrowIfCancellationRequested();await start(p,dryRun);RefreshProgress();
                },text=>state.Text=text,workpiece.Create?"已放入全新未加工工件，装夹及参考有效、报警已处理、区域清空，镜头／高度／工作距离未改变。":null,
                    _=>(_inspectProductionInputs??throw new InvalidOperationException("自动资料检查未接入。"))(p));
            }
            catch(Exception ex){SetSetup(true);state.Text="已停止："+ex.Message+"\r\n"+(current is null?"":$"本件完成{current.Holes.Count(p=>p.Value==ProductionHolePhase.Completed)}/18；资料：{currentFolder}");throw;}
        }
        var dry=Btn("整件无出光试走",async(_,_)=>await Ui(()=>Execute(true)));
        var real=Btn("一键加工",async(_,_)=>await Ui(()=>Execute(false)));
        real.Name="OneClickProductionStart";real.BackColor=Color.FromArgb(28,115,165);real.ForeColor=Color.White;
        real.Font=new Font(Font.FontFamily,12,FontStyle.Bold);real.Padding=new Padding(14,9,14,9);
        _showProductionRework=plan=>{_activeReworkPlan=plan;real.Text=plan is null?"一键加工":"一键补加工";RefreshSettings();};
        inspect.Enabled=dry.Enabled=real.Enabled=_cfg.DeviceMode=="Real";
        _productionStopButton=Btn("停止整件加工",(_,_)=>{RequestProductionStop();detail="已请求停止，正在清理及核对关光；机床实际停止以面板为准。";RefreshProgress();});
        var open=Btn("打开本件结果",(_,_)=>{if(Directory.Exists(currentFolder))System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(currentFolder){UseShellExecute=true});});
        var rework=Btn("准备补加工",async(_,_)=>await Ui(async()=>
        {
            bool retry=IsRetryCandidate();
            string? source=(current is {Completed:true,DryRun:false} || retry) && File.Exists(Path.Combine(currentFolder,"run.json"))?Path.Combine(currentFolder,"run.json"):null;
            if(source is null)
            {
                using var dialog=new OpenFileDialog {Filter="加工记录|run.json;本件真实加工记录.json|JSON|*.json",Title="选择已完成加工记录，或采图失败且未出光的补加工记录"};
                if(dialog.ShowDialog(this)!=DialogResult.OK)return;source=dialog.FileName;
            }
            await (_prepareProductionRework??throw new InvalidOperationException("补加工入口未接入。"))(source);
            SetSetup(true);state.Text="补加工记录已建立；参数为本轮额外加工量。确认回装、工作Z/C及原主控从头启动，再检查就绪。"+
                (_activeReworkPlan?.Info?.RetryOfRunId is { } failed?$"\r\n关联未出光失败轮次{failed}，该失败轮不计入累计出光请求。":"");
        }));
        reworkButton=rework;RefreshReworkButton();
        rework.Name="PrepareProductionRework";
        var controls=new FlowLayoutPanel {AutoSize=true,Dock=DockStyle.Fill,WrapContents=true};
        var hint=Lbl("每个全新待加工件先新建工件并确认装夹。完成或异常后不自动重打；已加工件不能当新件全18孔重跑。");hint.MaximumSize=new Size(850,0);
        controls.FlowDirection=FlowDirection.TopDown;
        var mainActions=new FlowLayoutPanel{AutoSize=true,WrapContents=true};mainActions.Controls.AddRange([real,_productionStopButton,open]);
        controls.Controls.AddRange([settings,mainActions,Btn("处理缺项：设备与参数",(_,_)=>SelectPage(_environmentPage)),toggleSetup]);
        _reworkActions.Controls.Add(rework);
        var maintenance=new FlowLayoutPanel {AutoSize=true,WrapContents=true,Visible=false};
        maintenance.Controls.AddRange([profileSummary,load,export]);
        var help=new FlowLayoutPanel {AutoSize=true,WrapContents=true};
        help.Controls.AddRange([Btn("主控程序 / 配置说明",(_,_)=>{instructions.Visible=!instructions.Visible;maintenance.Visible=instructions.Visible;}),
            Btn("没有主控：前往上传",(_,_)=>ShowNcUpload("HM_MASTER.MPF")),
            Btn("打开配套程序文件夹",async(_,_)=>await UiAction(()=>{OpenLocalFolder(StationProgramFiles.Folder);return Task.CompletedTask;}))]);
        instructions.Visible=false;
        setup.Controls.AddRange([profileReady,help,instructions,maintenance,inspect,dry,Btn("同件补加工 / 未出光重试",(_,_)=>SelectPage(_reworkPage))]);
        controls.SizeChanged+=(_,_)=>{int width=Math.Max(250,controls.ClientSize.Width-12);hint.MaximumSize=settings.MaximumSize=new Size(width,0);};
        var layout=new TableLayoutPanel {Dock=DockStyle.Fill,ColumnCount=1,RowCount=5};
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent,100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Absolute,70));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));layout.RowStyles.Add(new RowStyle(SizeType.Percent,100));
        layout.Controls.Add(controls,0,0);layout.Controls.Add(setup,0,1);layout.Controls.Add(state,0,2);layout.Controls.Add(progress,0,3);layout.Controls.Add(table,0,4);tab.Controls.Add(layout);
        try{LoadProfile(StationProgramFiles.SelectProfile(_state.LastProductionProfilePath,StationProgramFiles.ProductionProfile)).GetAwaiter().GetResult();}
        catch(Exception ex){profile=null;state.Text="主控参数未就绪："+ex.Message;profileSummary.Text=profileReady.Text=state.Text;}
        return tab;
    }
}
