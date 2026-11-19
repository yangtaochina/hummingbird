using System.Text.Json;
using Hummingbird.Core;

namespace Hummingbird.App;

/// <summary>隐藏窗体布局检查。固定使用模拟设备及指定输出目录，不读取真实设备配置。</summary>
internal static class OfflineUiChecks
{
    public static int Run(string[] args)
    {
        if (args.Length < 1) throw new ArgumentException("--ui-check 需要输出目录，可选采集记录路径。");
        string folder = Path.GetFullPath(args[0]); Directory.CreateDirectory(folder);
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles(); Application.SetCompatibleTextRenderingDefault(false);
        int exitCode = 1; var context = new ApplicationContext();
        var start = new System.Windows.Forms.Timer { Interval = 50 };
        start.Tick += async (_, _) =>
        {
            start.Stop();
            try
            {
                using var form = new MainForm(new MemoryLogger(), new AppConfig { DeviceMode = "Sim" },
                    new OperatorWorkspaceStore(Path.Combine(folder, "operator-state.json")), offlineUiCheck: true);
                form.ShowInTaskbar = false; form.Opacity = 0; form.Show();
                await form.CheckGuidedProductionUi(folder);
                if(args.ElementAtOrDefault(1)=="--layout-only"){await form.CheckAutomaticOpticalUi(folder);form.WriteWorkflowLayoutArtifacts(folder);}
                else await form.WriteOfflineUiCheckArtifacts(folder, args.ElementAtOrDefault(1), args.ElementAtOrDefault(2),args.ElementAtOrDefault(3),args.ElementAtOrDefault(4),args.ElementAtOrDefault(5));
                form.Close(); exitCode = 0;
            }
            catch (Exception ex) { File.WriteAllText(Path.Combine(folder, "error.txt"), ex.ToString()); }
            finally { start.Dispose(); context.ExitThread(); }
        };
        start.Start(); Application.Run(context); return exitCode;
    }
}

public sealed partial class MainForm
{
    internal async Task WriteOfflineUiCheckArtifacts(string folder, string? source, string? laserCalibration,string? productionRun=null,string? reworkSource=null,string? retryRecord=null)
    {
        if (_cfg.DeviceMode != "Sim") throw new InvalidOperationException("界面检查只允许模拟设备。");
        await CheckAutomaticOpticalUi(folder);
        CheckBundledStationPrograms(folder);
        var checks = new List<object>();
        void Check(bool ok, string name)
        {
            checks.Add(new { Passed = ok, Name = name });
            if (!ok) throw new InvalidOperationException(name);
        }
        IEnumerable<Control> Children(Control parent) => parent.Controls.Cast<Control>().SelectMany(c => new[] { c }.Concat(Children(c)));
        Check(_tabs.TabPages.Cast<TabPage>().Select(t => t.Text).SequenceEqual(new[] { "1 环境与参数", "2 工位恢复", "3 定位与加工", "4 同件补加工", "5 光学与落点", "加工资料与日志", "维护与辅助" }), "按日常操作顺序组织七个入口");
        Check(!Children(this).OfType<Button>().Any(b => b.Text.Contains("九点") || b.Text.Contains("转台")) &&
            Children(this).OfType<Button>().Single(b=>b.Name=="OneClickProductionStart").Text=="一键加工", "旧标定入口移除，日常加工使用新一键入口");
        Check(!_fixedCConfirmed.Checked, "不恢复装夹有效确认");
        if(File.Exists(_state.LastRouteSessionPath))
        {
            var cached=_readReusableCaptureBaseline?.Invoke();
            Check(cached is not null && new Hummingbird.Vision.DialAlignmentSolver().Analyze(cached,_template).Targets.Count==18,
                "启动自动恢复缓存基准时完整复核六图，旧11点框可从原照片独立恢复");
            Check(!_cnc.IsConnected && !_laser.IsOpen && !_fixedCConfirmed.Checked,
                "基准恢复不连接设备、不恢复装夹确认或启动加工");
            if(File.Exists(_state.LastSessionPath) && Path.GetFullPath(_state.LastSessionPath).StartsWith(Path.GetFullPath(folder)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase) &&
                RingScanSession.Load(_state.LastSessionPath).Frames.Count==0)
            {
                await _loadWorkspace!(_state.LastSessionPath);
                var after=_readReusableCaptureBaseline!()!;
                Check(after.Frames.Count==6 && new Hummingbird.Vision.DatumDirectionSolver().Inspect(after.Frames.Single(f=>f.Role==RingFrameRole.Reference11),after.Settings,after.DirectionOptions).Status==DatumHoleInspectionStatus.Detected,
                    "重启恢复空工件后仍保留独立通过的缓存基准，复现直接新建工件路径");
            }
        }
        Check(_laserExposureMs.Text == "2000" && !_spotTrialExposureButton!.Enabled, "恢复已记录2000ms请求时长，模拟模式仍禁用真实出光");
        _laserDurationSeconds.Value=12.5m;_laserPowerRaw.Value=2800;
        var processState=_stateStore.Load();
        Check(processState.LaserRequestMs==12500 && processState.LaserPowerRaw==2800 && _laserPreset.RequestedExposureMs==12500 &&
            _laserPreset.Settings.Power==2800 && !_laser.IsOpen,"客户设置秒数和功率后持久保存，不自动连接或写激光");
        Check(!_spotTrialExposureButton!.Enabled && _laserDurationSeconds.Maximum==600 && _laserPowerRaw.Maximum==10000,
            "模拟模式禁止出光，界面显示有界的客户参数范围");
        _tabs.SelectedTab=_environmentPage;
        Children(this).OfType<Button>().Single(b=>b.Text=="载入历史设置").PerformVisibleClick();
        Check(_laserDurationSeconds.Value==2 && _laserPowerRaw.Value==3950 && !_laser.IsOpen,"历史参数只回填界面，不自动应用或出光");
        Children(this).OfType<Button>().Single(b=>b.Text=="载入客户参数（60秒／4420）").PerformVisibleClick();
        Check(_laserDurationSeconds.Value==60 && _laserPowerRaw.Value==4420 && _laserPreset==Hummingbird.Devices.LaserRtu.LaserProcessPreset.CustomerConfirmed20261116 &&
            !_laser.IsOpen,"客户60秒/4420一键载入并保留来源，按钮不连接、不写设备或出光");
        Check(Children(this).OfType<Label>().Any(l=>l.Text.Contains("当前每孔：60秒") && l.Text.Contains("18分钟")),"整件页同步显示当前参数及18孔纯请求时间");
        _laserDurationSeconds.Value=12.5m;_laserPowerRaw.Value=2800;
        using(var processImage=new Bitmap(Width,Height))
        {DrawToBitmap(processImage,new Rectangle(0,0,Width,Height));processImage.Save(Path.Combine(folder,"ui_process_parameters.png"));}
        Check(!_camera!.IsOpen && !_cnc.IsConnected, "启动不连接设备");
        _tabs.SelectedTab=_machiningPage;
        _captureDetails.SelectedTab=_captureDetails.TabPages.Cast<TabPage>().Single(p=>p.Text=="手动采图");
        Children(this).OfType<Button>().Single(b=>b.Text=="停稳拍照（自动存档）").PerformVisibleClick();
        while(_workspaceBusy)await Task.Delay(20);
        Check(Children(this).OfType<Label>().Any(l=>l.Text.StartsWith("操作未完成：") && l.Text.Contains("当前面板C")),
            "未确认装夹时拍照失败原因直接显示在按钮区域；实际提示="+Children(this).OfType<Label>().Single(l=>l.Name=="CaptureFeedback").Text+
            "；按钮Visible="+Children(this).OfType<Button>().Single(b=>b.Text=="停稳拍照（自动存档）").Visible);
        var captureConfirmation=Children(this).OfType<CheckBox>().Single(c=>c.Text=="本次装夹、参考状态和固定C已核对");
        captureConfirmation.Checked=true;Check(_fixedCConfirmed.Checked,"拍照旁确认框与顶部状态同步");
        _fixedCConfirmed.Checked=false;Check(!captureConfirmation.Checked,"顶部取消确认会同步取消拍照旁确认，不能绕过检查");
        var evidenceTabs=(TabControl)_toolsPage.Controls[0];
        var returnTab=_returnPage;
        Check(!Children(_machiningPage).OfType<Button>().Any(b=>b.Name=="StationReturnShortcut" || b.Text=="工位恢复"),
            "定位与加工页移除工位恢复按钮，保留独立第2页");
        _tabs.SelectedTab=returnTab;
        Check(!Children(returnTab).OfType<Button>().Single(b=>b.Name=="StationReturnStart").Enabled,
            "离线界面禁用自动回位，不连接或写机床");
        string returnProfile=Path.Combine(folder,"station-return-profile.json");
        File.Copy(StationProgramFiles.ReturnProfile,returnProfile);
        _loadStationReturnProfile!(returnProfile);
        Check(_stateStore.Load().LastStationReturnProfilePath==returnProfile && !_cnc.IsConnected && !_laser.IsOpen,
            "恢复配置路径持久保存，载入配置不连接机床或激光");
        Check(Children(returnTab).OfType<Label>().Single(l=>l.Name=="StationReturnProfile").Text.Contains("-192.17803") &&
            Children(returnTab).OfType<Label>().Single(l=>l.Name=="StationReturnProfile").Text.Contains("-201.87324"),
            "工位恢复清楚显示安全高度、工作高度及已核定顺序");
        var returnConfirmation=Children(returnTab).OfType<CheckBox>().Single();returnConfirmation.Checked=true;
        _loadStationReturnProfile(returnProfile);
        Check(!returnConfirmation.Checked && !_fixedCConfirmed.Checked,
            "重新载入回位配置不恢复本次参考确认，也不自动确认装夹固定C");
        _tabs.SelectedTab=_machiningPage;_captureDetails.SelectedTab=_captureDetails.TabPages.Cast<TabPage>().Single(p=>p.Text=="手动采图");
        var captureChoice=Children(this).OfType<ComboBox>().Single(c=>c.Items.Count==6);
        var captureGrid=Children(this).OfType<DataGridView>().Single(t=>t.Rows.Count==6);
        foreach(bool enabled in new[]{true,false})
        {
            captureGrid.Enabled=enabled;
            foreach(int step in Enumerable.Range(0,6))
            {
                _selectCaptureStep!(step);
                Check(captureChoice.SelectedIndex==step && captureGrid.CurrentRow?.Index==step,
                    $"程序选择采图步骤{step+1}后，下拉框和列表一致（列表Enabled={enabled}）");
            }
        }
        captureGrid.Enabled=true;
        foreach(int step in Enumerable.Range(0,6).Reverse())
        {
            captureChoice.SelectedIndex=step;
            Check(captureGrid.CurrentRow?.Index==step,$"手动选择步骤{step+1}时列表同步，不能反向跳回旧步骤");
        }
        // 走真实DataGridView鼠标处理路径；仅设置CurrentCell不能复现SetCurrentCellAddressCore重入。
        var mouseDown=typeof(DataGridView).GetMethod("OnMouseDown",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        var mouseUp=typeof(DataGridView).GetMethod("OnMouseUp",System.Reflection.BindingFlags.Instance|System.Reflection.BindingFlags.NonPublic)!;
        foreach(int step in Enumerable.Range(0,6))
        foreach(int column in new[]{1,2,3,0})
        {
            _tabs.SelectedTab=_machiningPage;_captureDetails.SelectedTab=_captureDetails.TabPages.Cast<TabPage>().Single(p=>p.Text=="手动采图");
            evidenceTabs.PerformLayout();captureGrid.PerformLayout();
            var cell=captureGrid.GetCellDisplayRectangle(column,step,false);
            var down=new MouseEventArgs(MouseButtons.Left,1,cell.Left+Math.Max(2,cell.Width/2),cell.Top+cell.Height/2,0);
            mouseDown.Invoke(captureGrid,[down]);mouseUp.Invoke(captureGrid,[down]);
            Check(captureChoice.SelectedIndex==step && captureGrid.CurrentCell?.RowIndex==step && captureGrid.CurrentCell.ColumnIndex==column,
                $"鼠标点击步骤{step+1}第{column+1}列不重入且保留实际点击单元格");
        }
        _selectCaptureStep!(0);
        _tabs.SelectedTab=_toolsPage;evidenceTabs.SelectedTab=evidenceTabs.TabPages.Cast<TabPage>().Single(p=>p.Text=="现场取证");
        _state.CaptureRoot=folder;
        Children(evidenceTabs.SelectedTab!).OfType<Button>().Single(b=>b.Text=="开始现场自动取证").PerformVisibleClick();
        while(_workspaceBusy)await Task.Delay(20);
        Check(Children(evidenceTabs.TabPages.Cast<TabPage>().Single(p=>p.Text=="现场取证")).OfType<TextBox>().Any(t=>t.Text.Contains("现场取证未完成：") && t.Text.Contains("固定C")),"取证初始化失败也显示具体原因，状态不再空白");
        Check(Children(this).OfType<ComboBox>().Any(c=>c.Items.Count==6 && c.Items[5]!.ToString()!.StartsWith("步骤6：")),"步骤编号不再伪装成已采6/6");
        Check(!Children(this).OfType<Button>().Single(b=>b.Name=="OneClickProductionStart").Enabled && !Children(this).OfType<Button>().Single(b=>b.Text=="整件无出光试走").Enabled,
            "模拟模式禁用真实整件运动及加工入口");
        Check(_readProductionCalibration is not null,"整件流程已接入现有光学确认和落点来源校验");
        string productionProfile=Path.Combine(folder,"production-profile.json");
        File.WriteAllText(productionProfile,JsonSerializer.Serialize(new ProductionStationProfile {Envelope=new(-20,20,-20,20,-1,0),SessionToken=789,R220To229Reserved=true,AllowMachining=true}));
        await _loadProductionProfile!(productionProfile);
        Check(_stateStore.Load().LastProductionProfilePath==productionProfile &&
            !_guidedOperationConfirmed && _guidedOperationC is null,
            "恢复主控配置路径，但不恢复本次面板/NC确认，不自动启动");
        var progressRecord=new ProductionRunRecord {DryRun=true,Synthetic=true,Phase="Failed",CapturedSteps=6,CurrentHoleId=2,
            StoppedDuring="HoleStarted:2",Error="模拟中断",Holes=new() {[1]=ProductionHolePhase.Completed,[2]=ProductionHolePhase.Uncertain}};
        _reportProductionProgress!(progressRecord,folder);
        var productionGrid=Children(this).OfType<DataGridView>().Single(t=>t.Name=="ProductionHoles");
        Check(productionGrid.Rows.Count==18 && productionGrid.Rows[0].Cells[1].Value!.ToString()=="试走完成" &&
            productionGrid.Rows[1].Cells[1].Value!.ToString()!.Contains("待核对") &&
            Children(this).OfType<Label>().Single(l=>l.Name=="ProductionProgress").Text.Contains("完成1/18"),
            "18孔进度区分完成、未开始和待核对孔，不把中断误显示整件完成");
        string savedJob=Path.Combine(folder,"saved-job");Directory.CreateDirectory(savedJob);
        File.WriteAllText(Path.Combine(savedJob,ProductionJobArchive.MachiningRecordName),JsonSerializer.Serialize(progressRecord));
        _restoreProductionJobView!(savedJob);
        Check(productionGrid.Rows[1].Cells[1].Value!.ToString()!.Contains("待核对"),"重新打开工件后恢复已完成及待核对状态");
        _restoreProductionJobView!(null);
        Check(productionGrid.Rows.Cast<DataGridViewRow>().All(r=>r.Cells[1].Value!.ToString()=="未开始") &&
            Children(this).OfType<Label>().Single(l=>l.Name=="ProductionProgress").Text.Contains("完成0/18"),"新件不会沿用上一件的18孔完成显示");
        _reportProductionProgress!(progressRecord,folder);
        Check(!Children(this).OfType<Button>().Single(b=>b.Text=="检查整件就绪（不运动）").Enabled,"模拟模式禁止读取真实主控进行就绪检查");
        _selectProductionPanel!();
        using(var productionCancellation=new CancellationTokenSource())
        {
            _productionStop=productionCancellation;
            Children(this).OfType<Button>().Single(b=>b.Text=="停止整件加工").PerformVisibleClick();
            Check(productionCancellation.IsCancellationRequested && !_laser.IsOpen,"整件停止入口取消流程，模拟界面不连接真实激光");
            _productionStop=null;
        }
        _selectProductionPanel!();
        using(var productionImage=new Bitmap(Width,Height))
        {DrawToBitmap(productionImage,new Rectangle(0,0,Width,Height));productionImage.Save(Path.Combine(folder,"ui_production_progress.png"));}
        if (source is not null)
        {
            string copy = Path.Combine(folder, "review-session.json"); RingScanSession.Load(Path.GetFullPath(source)).Save(copy);
            await _loadWorkspace!(copy);
            Check(!_fixedCConfirmed.Checked, "载入历史照片仍需本次装夹确认");
            using(var geometryReport=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"定位检查与预览.json"))))
            {
                var selection=geometryReport.RootElement.GetProperty("RingExposure");
                Check(selection.ValueKind==JsonValueKind.Object && selection.GetProperty("SelectedFrames").GetArrayLength()==4 &&
                    selection.GetProperty("Fit").ValueKind==JsonValueKind.Object,"界面载入完整记录自动执行四处曝光几何选优并保存依据");
                bool complete=geometryReport.RootElement.GetProperty("AlignmentCompleted").GetBoolean();
                var summary=Children(this).OfType<TextBox>().Single(t=>t.Name=="RingPreviewStatus");
                Check(complete ? summary.Text.StartsWith("18孔预览已生成（18/18）") && _captureDetails.SelectedTab!.Text=="圆心／18孔预览"
                    : summary.Text.StartsWith("18孔未生成：") && summary.ForeColor!=Color.DarkGreen && _captureDetails.SelectedTab!.Text=="检查与归档",
                    "完整定位自动展示18孔，参考未通过则明确提示未生成并展示原因");
                Check(complete ? geometryReport.RootElement.GetProperty("TargetCount").GetInt32()==18
                    : geometryReport.RootElement.GetProperty("TargetCount").GetInt32()==0 &&
                        !string.IsNullOrWhiteSpace(geometryReport.RootElement.GetProperty("Rejection").GetString()),
                    "归档明确保存18孔数量及未通过原因，圆环通过不代替整组定位通过");
                if(complete)
                {
                    var invalidDirection=RingScanSession.Load(copy);
                    invalidDirection.DirectionOptions=invalidDirection.DirectionOptions with {ExpectedGapDeg=120};
                    string failedPath=Path.Combine(folder,"invalid-direction.json");invalidDirection.Save(failedPath);
                    await _loadWorkspace!(failedPath);
                    Check(_state.LastRouteSessionPath==copy && _readReusableCaptureBaseline?.Invoke()?.DirectionOptions.ExpectedGapDeg==60,
                        "加载方向未通过的历史记录不能替换此前已核对基准及其参数");
                    using var failure=JsonDocument.Parse(File.ReadAllText(Path.Combine(folder,"定位检查与预览.json")));
                    Check(!failure.RootElement.GetProperty("AlignmentCompleted").GetBoolean() &&
                        failure.RootElement.GetProperty("TargetCount").GetInt32()==0 &&
                        failure.RootElement.GetProperty("Rejection").GetString()!.Contains("夹角") &&
                        summary.Text.StartsWith("18孔未生成：") && _captureDetails.SelectedTab!.Text=="检查与归档",
                        "圆环和单参考通过但方向拒绝时，清除旧18孔并归档具体失败");
                }
            }
            var rejected=RingScanSession.Load(copy);
            rejected.Frames=rejected.Frames.Select(f=>f.Role==RingFrameRole.RingArc?f:f with
                {FaceSeed=new(959.5,599.5,800,LaserSpotCalibration.Hash(f.ImagePath),"OfflineUiRejectedRegion")}).ToList();
            string rejectedPath=Path.Combine(folder,"rejected-session.json");rejected.Save(rejectedPath);
            await _loadWorkspace!(rejectedPath);
            Check(_state.LastRouteSessionPath==copy && _readReusableCaptureBaseline?.Invoke()?.Frames.Count==6,
                "恢复失败参考图时保留之前已核对路线，不清空或污染基准");
            Children(this).OfType<Button>().Single(b=>b.Text=="检查并预览18孔").PerformVisibleClick();
            while(_workspaceBusy)await Task.Delay(20);
            Check(_captureDetails.SelectedTab!.Text=="检查与归档" &&
                Children(_captureDetails.SelectedTab!).OfType<TextBox>().Single().Text.Contains("超出原图") &&
                Children(this).OfType<Label>().Any(l=>l.Text.Contains("18孔未生成：参考1点实体圆边未通过识别") && l.ForeColor!=Color.DarkGreen),
                "实际点击检查按钮时显示两参考失败及原图范围诊断，不被圆环选优绿色信息覆盖");
            await _loadWorkspace!(copy);
            var face = RingScanSession.Load(Path.GetFullPath(source)).Frames.First(f => f.Role == RingFrameRole.Reference11);
            string faceHash=LaserSpotCalibration.Hash(face.ImagePath);
            using var regionDialog = new FaceRegionDialog(Hummingbird.Vision.RingScanSolver.LoadFrame(face.ImagePath), face.Name,
                region=>Task.Run(()=>new Hummingbird.Vision.DatumDirectionSolver().Inspect(face with
                {FaceSeed=new(region.X+region.Width/2,region.Y+region.Height/2,(region.Width+region.Height)/4,faceHash,"OfflineUiRegion")},
                    rejected.Settings,rejected.DirectionOptions)))
            { Opacity = 0, ShowInTaskbar = false };
            regionDialog.Show(this); await Task.Delay(60);
            await regionDialog.CheckSelectionAsync(new RectangleF(159.5f,-200.5f,1600,1600));
            var confirm=Children(regionDialog).OfType<Button>().Single(b=>b.Text=="确认并保存框选");
            Check(regionDialog.Visible && regionDialog.DialogResult!=DialogResult.OK && !confirm.Enabled &&
                Children(regionDialog).OfType<TextBox>().Single().Text.Contains("超出原图"),
                "框选失败留在原照片显示原因，不能保存为通过，也不要求重新拍照");
            if(face.FaceSeed is { } seed)
            {
                await regionDialog.CheckSelectionAsync(new RectangleF((float)(seed.Px-seed.RadiusPx),(float)(seed.Py-seed.RadiusPx),
                    (float)(2*seed.RadiusPx),(float)(2*seed.RadiusPx)));
                Check(regionDialog.Visible && regionDialog.DialogResult!=DialogResult.OK && confirm.Enabled,
                    "实体圆边通过后仍须操作者确认，识别不会自动保存框选");
            }
            using (var bitmap = new Bitmap(regionDialog.Width, regionDialog.Height))
            { regionDialog.DrawToBitmap(bitmap, new Rectangle(0, 0, bitmap.Width, bitmap.Height)); bitmap.Save(Path.Combine(folder, "ui_face_region.png")); }
            if(confirm.Enabled){confirm.PerformVisibleClick();Check(regionDialog.DialogResult==DialogResult.OK,"点击确认后才接收通过的框选");}
            Check(LaserSpotCalibration.Hash(face.ImagePath)==faceHash,"框选检查不改写历史原图");
            regionDialog.Close();
        }
        if (laserCalibration is not null)
        {
            _state.LaserCalibrationPath = Path.GetFullPath(laserCalibration); await _restoreLaserCalibration!();
            Check(_state.LaserCalibrationPath == Path.GetFullPath(laserCalibration), "恢复落点标定路径及来源校验");
            Check(!Children(this).OfType<CheckBox>().Single(c => c.Text.StartsWith("镜头、相机高度")).Checked, "恢复落点不会代替本次光学条件确认");
            if(source is not null)
            {
                var startupProfile=JsonSerializer.Deserialize<ProductionStationProfile>(File.ReadAllText(StationProgramFiles.ProductionProfile))!;
                string readyText=await _inspectProductionInputs!(startupProfile);
                Check(readyText.Contains("自动资料检查通过") && !_cnc.IsConnected && !_laser.IsOpen && !_guidedOperationConfirmed,
                    "真实历史六图与落点自动完成资料预检，不要求提前勾选光学或主控确认，也不连接硬件");
            }
        }
        if(productionRun is not null && source is not null && laserCalibration is not null)
        {
            string replay=Path.Combine(folder,"historical-production-bundle");Directory.CreateDirectory(replay);
            foreach(string name in new[]{"run.json","运行来源.json","定位与补偿.json"})
                File.Copy(Path.Combine(productionRun,name),Path.Combine(replay,name));
            foreach(string original in Directory.GetDirectories(productionRun,"hole_*"))
            {
                string copy=Path.Combine(replay,Path.GetFileName(original));Directory.CreateDirectory(copy);
                foreach(string name in new[]{"trial.json","before.bmp","after.bmp","before.capture.json","after.capture.json"})
                    File.Copy(Path.Combine(original,name),Path.Combine(copy,name));
            }
            var historical=JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(Path.Combine(replay,"run.json")))!;
            File.WriteAllText(Path.Combine(replay,"本件结果.md"),ProductionJobArchive.Summary(historical));
            var replaySession=RingScanSession.Load(Path.GetFullPath(source));
            var replayCalibration=LaserSpotCalibration.Load(Path.GetFullPath(laserCalibration));
            string zip=await Task.Run(()=>ExportProductionResult(replay,replaySession,historical,replayCalibration));
            using var package=System.IO.Compression.ZipFile.OpenRead(zip);
            Check(package.Entries.Count(e=>e.FullName.StartsWith("孔位/") && e.FullName.EndsWith(".bmp"))==36 &&
                package.GetEntry("整件/本件结果.md") is not null && package.GetEntry("定位/整组记录.json") is not null,
                "无需先启动现场取证，独立本件ZIP也包含六图定位、18孔36图及结果");
            var entry=package.GetEntry("manifest.json")!;using var reader=new StreamReader(entry.Open());
            using var manifest=JsonDocument.Parse(reader.ReadToEnd());
            foreach(var file in manifest.RootElement.GetProperty("Files").EnumerateArray())
            {
                using var content=package.GetEntry(file.GetProperty("Path").GetString()!)!.Open();
                Check(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(content))==file.GetProperty("Sha256").GetString(),
                    "本件ZIP指纹："+file.GetProperty("Path").GetString());
            }
            Check(!package.Entries.Any(e=>e.FullName.EndsWith("config.json") || e.FullName.EndsWith(".log")),"独立本件ZIP不收集连接配置或原始日志");
        }
        foreach (var size in new[] { new Size(1360, 900), new Size(1100, 740) })
        {
            ClientSize = size;
            for (int i = 0; i < _tabs.TabPages.Count; i++)
            {
                _tabs.SelectedIndex = i; PerformLayout(); _tabs.SelectedTab!.PerformLayout(); await Task.Delay(60);
                using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                bitmap.Save(Path.Combine(folder, $"ui_{size.Width}_{i + 1}.png"));
            }
            _tabs.SelectedTab=_machiningPage;
            var detailTabs = _captureDetails;
            for (int i = 1; i < detailTabs.TabPages.Count; i++)
            {
                detailTabs.SelectedIndex = i; PerformLayout(); detailTabs.SelectedTab!.PerformLayout(); await Task.Delay(60);
                using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(0, 0, Width, Height));
                bitmap.Save(Path.Combine(folder, $"ui_{size.Width}_detail{i}.png"));
            }
            detailTabs.SelectedTab=detailTabs.TabPages.Cast<TabPage>().Single(t=>t.Text=="一键加工");
            _reportProductionProgress!(progressRecord,folder);PerformLayout();await Task.Delay(60);
            Check(productionGrid.Height>=80 && _productionStopButton!.Visible,$"{size.Width}宽度下运行进度表及停止按钮可见");
            using(var bitmap=new Bitmap(Width,Height))
            {DrawToBitmap(bitmap,new Rectangle(0,0,Width,Height));bitmap.Save(Path.Combine(folder,$"ui_{size.Width}_production.png"));}
            detailTabs.SelectedIndex = 0;
        }
        File.WriteAllText(Path.Combine(folder, "layout.json"), JsonSerializer.Serialize(Children(this).Select(c => new
        {
            Type = c.GetType().Name,
            Text = c is PictureBox ? "picture" : c.Text[..Math.Min(c.Text.Length, 35)],
            Bounds = new { c.Left, c.Top, c.Width, c.Height },
            Client = new { c.ClientSize.Width, c.ClientSize.Height },
            c.Visible,
            Parent = c.Parent?.GetType().Name
        }), new JsonSerializerOptions { WriteIndented = true }));
        _state.CaptureRoot = Path.Combine(folder, "sim-captures"); Directory.CreateDirectory(_state.CaptureRoot);
        _fixedCConfirmed.Checked = true; await ConnectAcquisitionDevices();
        bool propagated=false;
        try{await RunSingleSpotTrial(false,propagateErrors:true);}catch(InvalidOperationException){propagated=true;}
        Check(propagated,"单孔工序可将失败传播给整件调度，不吞掉错误继续下一孔");
        _tabs.SelectedTab=_machiningPage;
        var imageReviewTabs=Children(_machiningPage).OfType<TabControl>().Single(t=>t.TabPages.Cast<TabPage>().Any(p=>p.Text=="已采照片／框选"));
        imageReviewTabs.SelectedTab=imageReviewTabs.TabPages.Cast<TabPage>().Single(p=>p.Text=="已采照片／框选");
        PerformLayout();
        Check(_liveEnabled.Checked && _livePicture.Image is not null && _lastPreviewAt is not null,"连接相机后自动产生实时画面");
        var previewTime=_lastPreviewAt;
        for(int i=0;i<30 && _lastPreviewAt<=previewTime;i++)await Task.Delay(100);
        Check(_livePicture.Visible && _lastPreviewAt>previewTime && _livePicture.Height>=180,
            $"查看已采照片时左侧大画面仍持续刷新（Visible={_livePicture.Visible}, Height={_livePicture.Height}, Refreshed={_lastPreviewAt>previewTime}, Busy={_workspaceBusy}）");
        while(_previewBusy)await Task.Delay(50);
        var pausedAt=_lastPreviewAt;_workspaceBusy=true;
        try{await PreviewTick();Check(_lastPreviewAt==pausedAt && _liveStatus.Text.Contains("暂停"),"正式采图占用相机时明确显示预览暂停");}
        finally{_workspaceBusy=false;}
        await PreviewTick();Check(_lastPreviewAt>pausedAt && _liveStatus.Text.StartsWith("实时画面"),"采图结束后恢复实时画面及刷新时间");
        using(var previewProduction=new CancellationTokenSource())
        {
            while(_previewBusy)await Task.Delay(20);
            _workspaceBusy=true;_productionStop=previewProduction;
            var previousTime=_lastPreviewAt;
            try
            {
                await PreviewTick();
                Check(_lastPreviewAt>previousTime && _liveStatus.Text.Contains("加工监看"),"整件流程占用工作区时相机空闲仍更新实时画面");
                var entered=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var release=new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                var formal=_cameraCapture!.SelectExposureAsync((double)_numExp.Value,(double)_numGain.Value,async(frame,exposure)=>
                {
                    entered.TrySetResult(true);await release.Task;
                    return ExposureSelectionPolicy.Measure(frame);
                });
                try
                {
                    await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
                    while(_previewBusy)await Task.Delay(20);
                    var beforeBusy=_lastPreviewAt;await PreviewTick();
                    Check(_lastPreviewAt==beforeBusy && _liveStatus.Text.Contains("相机正忙"),"正式曝光搜索持锁时加工预览立即让位，并明确显示旧帧状态");
                }
                finally{release.TrySetResult(true);await formal;}
                await PreviewTick();Check(_liveStatus.Text.Contains("加工监看"),"正式采图释放相机后自动恢复加工监看");
            }
            finally{_productionStop=null;_workspaceBusy=false;}
        }
        using(var liveBitmap=new Bitmap(Width,Height)){DrawToBitmap(liveBitmap,new Rectangle(0,0,Width,Height));liveBitmap.Save(Path.Combine(folder,"ui_live_review.png"));}
        bool magnifierUpdated=false;var magnifierStart=_lastPreviewAt;int magnifierTicks=0;
        using(var closeMagnifier=new System.Windows.Forms.Timer {Interval=150})
        {
            closeMagnifier.Tick+=(_,_)=>
            {
                var window=OwnedForms.FirstOrDefault(f=>f.Text.StartsWith("实时找位放大"));if(window is null)return;
                magnifierTicks++;
                if(window.Controls.OfType<PictureBox>().Single().Image is not null && _lastPreviewAt>magnifierStart && magnifierTicks>=4)
                {
                    magnifierUpdated=true;using var bitmap=new Bitmap(window.Width,window.Height);window.DrawToBitmap(bitmap,new Rectangle(0,0,window.Width,window.Height));
                    bitmap.Save(Path.Combine(folder,"ui_live_magnifier.png"));closeMagnifier.Stop();window.Close();
                }
                else if(magnifierTicks>40){closeMagnifier.Stop();window.Close();}
            };
            closeMagnifier.Start();ShowLiveMagnifier(offlineCheck:true);closeMagnifier.Stop();
        }
        Check(magnifierUpdated,"放大找位窗口持续刷新且关闭后正常返回");
        await StartLaserTrialOperation(() => RunSingleSpotTrial(dryRun: true));
        string trial = Directory.GetDirectories(_state.CaptureRoot, "单点演练_*").Order().Last();
        var record = JsonSerializer.Deserialize<Hummingbird.Devices.LaserRtu.SpotTrialRecord>(File.ReadAllText(Path.Combine(trial, "trial.json")))!;
        Check(record.Completed && record.DryRun && record.DeviceMode == "Sim" && !record.EmissionRequested &&
            File.Exists(Path.Combine(trial, "before.bmp")) && File.Exists(Path.Combine(trial, "after.bmp")), "新界面无出光演练通过统一相机采图并自动归档前后图");
        if (source is null)
        {
            if (_camera is Hummingbird.Devices.Sim.SimCameraService simulated)
            {
                var settings = _ringSettingsSnapshot!();
                simulated.Width = settings.ImageWidth; simulated.Height = settings.ImageHeight; simulated.FreezePose = true;
            }
            _tabs.SelectedTab=_machiningPage;
            Check(Children(this).OfType<CheckBox>().Single(c => c.Text == "自动选择曝光").Checked, "默认启用采图自动曝光选优");
            Children(this).OfType<Button>().Single(b => b.Text == "停稳拍照（自动存档）").PerformVisibleClick();
            var deadline = DateTime.UtcNow.AddSeconds(45);
            while (_workspaceBusy && DateTime.UtcNow < deadline) await Task.Delay(100);
            Check(!_workspaceBusy, "模拟自动曝光采图正常结束");
            string sessionPath = _state.LastSessionPath;
            Check(File.Exists(sessionPath), "自动曝光采图保存整组记录");
            var session = RingScanSession.Load(sessionPath); var frame = session.Frames.Single();
            var reports = Directory.GetFiles(Path.GetDirectoryName(sessionPath)!, "选优结果.json", SearchOption.AllDirectories);
            Check(reports.Length == 1 && frame.Capture?.CameraParameterSource == "AutoExposureSelectedEvaluatedFrame" &&
                frame.Capture.RequestedExposureUs == _state.StepExposureUs[frame.Name], "选优报告、正式照片与分步曝光记忆一致");
            using var report = JsonDocument.Parse(File.ReadAllText(reports.Single()));
            double exposure = report.RootElement.GetProperty("SelectedExposureUs").GetDouble();
            string candidatePath = Path.Combine(Path.GetDirectoryName(reports.Single())!, exposure.ToString("G17", System.Globalization.CultureInfo.InvariantCulture) + "us.bmp");
            Check(LaserSpotCalibration.Hash(candidatePath) == frame.Capture!.ImageSha256,
                "正式归档照片就是实际评分获选照片");
        }
        _tabs.SelectedTab=_machiningPage;
        _state.CaptureRoot=Path.Combine(folder,"pinned-step-captures");Directory.CreateDirectory(_state.CaptureRoot);
        Children(this).OfType<Button>().Single(b=>b.Text=="新工件").PerformVisibleClick();
        while(_workspaceBusy)await Task.Delay(20);
        if(_camera is Hummingbird.Devices.Sim.SimCameraService pinnedCamera)
        {var optics=_ringSettingsSnapshot!();pinnedCamera.Width=optics.ImageWidth;pinnedCamera.Height=optics.ImageHeight;pinnedCamera.FreezePose=true;}
        var exposureChoice=Children(this).OfType<CheckBox>().Single(c=>c.Text=="自动选择曝光");
        bool oldExposureChoice=exposureChoice.Checked;exposureChoice.Checked=false;_fixedCConfirmed.Checked=true;
        using(var pinnedRun=new CancellationTokenSource())
        {
            _productionStop=pinnedRun;_workspaceBusy=true;
            try
            {
                _selectCaptureStep!(0);var first=await _captureGuidedStep!(0,pinnedRun.Token);
                // 故意让界面仍指向步骤1，验证自动采图以调用参数决定步骤2。
                _selectCaptureStep(0);var second=await _captureGuidedStep(1,pinnedRun.Token);
                var saved=RingScanSession.Load(_state.LastSessionPath);
                Check(GuidedCapture.Index(first)==0 && GuidedCapture.Index(second)==1 && second.Role==RingFrameRole.RingArc &&
                    second.Name=="圆环12点" && saved.Frames.Count==2 && saved.Frames.Any(f=>f.ImagePath==first.ImagePath),
                    "自动步骤2按指定编号保存圆环12点，界面停在步骤1也不覆盖第一张或丢归档");
                RingCaptureArchive.VerifyImage(second);
                Check(File.Exists(RingCaptureArchive.SidecarPath(second.ImagePath)),"步骤2原图、单图快照和整组自动记录实际落盘");
            }
            finally{_productionStop=null;_workspaceBusy=false;exposureChoice.Checked=oldExposureChoice;}
        }
        _tabs.SelectedTab=_toolsPage;
        var productionTabs=(TabControl)_toolsPage.Controls[0];
        productionTabs.SelectedTab=productionTabs.TabPages.Cast<TabPage>().Single(p=>p.Text=="整件流程演练");
        Children(productionTabs.SelectedTab!).OfType<NumericUpDown>().Single().Value=8000;
        Children(productionTabs.SelectedTab!).OfType<Button>().Single(b=>b.Text=="运行整件离线演练").PerformVisibleClick();
        var productionDeadline=DateTime.UtcNow.AddSeconds(30);
        while(_workspaceBusy && DateTime.UtcNow<productionDeadline)await Task.Delay(100);
        Check(!_workspaceBusy,"界面整件演练正常结束");
        string productionFolder=Directory.GetDirectories(_state.CaptureRoot,"整件模拟_*").Order().Last();
        var productionRecord=JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(Path.Combine(productionFolder,"run.json")))!;
        Check(productionRecord.Completed && productionRecord.Synthetic && productionRecord.DryRun && productionRecord.CapturedSteps==6 && productionRecord.Holes.Count==18 && productionRecord.RequestedExposureMs==8000,
            "界面演练保存六步及18孔合成记录，较长时长只作计划");
        productionTabs.SelectedTab=productionTabs.TabPages.Cast<TabPage>().Single(p=>p.Text=="现场取证");
        Children(productionTabs.SelectedTab!).OfType<Button>().Single(b=>b.Text=="开始现场自动取证").PerformVisibleClick();
        var evidenceDeadline=DateTime.UtcNow.AddSeconds(60);
        while(_workspaceBusy && DateTime.UtcNow<evidenceDeadline)await Task.Delay(100);
        Check(!_workspaceBusy && _fieldEvidence is not null,"模拟现场取证完成");
        string evidenceZip=_fieldEvidence!.Folder+".zip";
        Check(File.Exists(evidenceZip) && Directory.GetFiles(Path.Combine(_fieldEvidence.Folder,"自动检查"),"*.bmp").Length>=6,"自动曝光试拍和三张重复原图均进入资料目录");
        Check(File.Exists(Path.Combine(_fieldEvidence.Folder,"自动检查","激光未读取.json")),"模拟取证不连接真实激光串口");
        var failedRequest=Hummingbird.Devices.LaserRtu.LaserProcessPreset.CustomerConfigured(1000,3950);
        var failedReadback=failedRequest.Settings with {Power=0};
        RecordLaserParameterCheck(new(DateTimeOffset.Now,failedRequest,failedReadback,failedReadback,true,false,
            failedRequest.Differences(failedReadback),"功率设定值：请求3950，读回0"));
        using(var parameterPackage=System.IO.Compression.ZipFile.OpenRead(evidenceZip))
        {
            var parameterEntry=parameterPackage.Entries.Single(e=>e.FullName.StartsWith("激光参数/"));
            using var parameterJson=JsonDocument.Parse(parameterEntry.Open());
            Check(parameterJson.RootElement.GetProperty("DeviceMode").GetString()=="Sim" &&
                !parameterJson.RootElement.GetProperty("EmissionCommandSent").GetBoolean() &&
                parameterJson.RootElement.GetProperty("Check").GetProperty("Requested").GetProperty("Settings").GetProperty("Power").GetInt32()==3950 &&
                parameterJson.RootElement.GetProperty("Check").GetProperty("After").GetProperty("Power").GetInt32()==0,
                "参数不匹配请求及实际值自动保存并增补入ZIP，不发送出光指令");
        }
        await StartLaserTrialOperation(()=>RunSingleSpotTrial(dryRun:true));
        string dryFolder=Directory.GetDirectories(_state.CaptureRoot,"单点演练_*").Order().Last();
        using(var dryProcess=JsonDocument.Parse(File.ReadAllText(Path.Combine(dryFolder,"trial.json"))))
            Check(dryProcess.RootElement.GetProperty("RequestedPreset").GetProperty("Settings").GetProperty("Power").GetInt32()==2800 &&
                dryProcess.RootElement.GetProperty("RequestedPreset").GetProperty("RequestedExposureMs").GetInt32()==12500,
                "前后图归档记录客户请求功率和时长，演练不出光");
        using(var evidence=System.IO.Compression.ZipFile.OpenRead(evidenceZip))
        {
            Check(evidence.Entries.Any(e=>e.FullName.StartsWith("试打/") && e.FullName.EndsWith("before.bmp")) && evidence.Entries.Any(e=>e.FullName.EndsWith("trial.json")),"后续演练的前后图和结构化结果自动增补入包");
            Check(evidence.Entries.All(e=>!e.FullName.EndsWith("config.json") && !e.FullName.EndsWith(".log")),"资料包不含配置和原始日志");
        }
        if(reworkSource is not null)
        {
            _laserDurationSeconds.Value=60;_laserPowerRaw.Value=4420;
            var parent=ProductionReworkSource.Load(reworkSource);string originalHash=ProductionReworkSource.Hash(parent.RecordPath);
            await _prepareProductionRework!(reworkSource);
            string reworkSession=_state.LastSessionPath;
            var empty=RingScanSession.Load(reworkSession);
            var plan=JsonSerializer.Deserialize<ProductionReworkPlan>(File.ReadAllText(Path.Combine(Path.GetDirectoryName(reworkSession)!,"补加工计划.json")))!;
            Check(empty.Frames.Count==0 && plan.Info!.ParentRunId==parent.Record.RunId && plan.Info.AdditionalExposureMs==60000 && plan.Info.PowerRaw==4420,
                "同件补加工建立独立空记录与明确追加参数，不带入旧工件定位结果");
            Check(!_inspectOneClickWorkpiece!().Create,"一键补加工沿用关联空记录，不误建普通新工件");
            Check(_readReusableCaptureBaseline!()!.Frames.Count==6 && !_fixedCConfirmed.Checked && !File.Exists(parent.ReservationPath) &&
                ProductionReworkSource.Hash(parent.RecordPath)==originalHash,
                "准备补加工只复用已核对基准，不自动确认装夹或占用原轮次，不运动出光");
            var reworkStart=Children(this).OfType<Button>().Single(b=>b.Text=="一键补加工");
            Check(!reworkStart.Enabled && Children(this).OfType<Label>().Any(l=>l.Text.Contains("追加60秒/孔")&&l.Text.Contains("累计请求120秒/孔")),
                "界面明确本轮追加和累计请求，模拟模式继续禁止真实补加工");
            await _loadWorkspace!(reworkSession);
            Check(_activeReworkPlan?.Info?.PlanId==plan.Info!.PlanId && Children(this).OfType<Button>().Any(b=>b.Text=="一键补加工") && !_fixedCConfirmed.Checked,
                "恢复待补加工记录保留来源及专用模式，但不恢复装夹确认或自动开始");
            _tabs.SelectedTab=_machiningPage;Children(this).OfType<Button>().Single(b=>b.Text=="新工件").PerformVisibleClick();
            while(_workspaceBusy)await Task.Delay(20);
            Check(_activeReworkPlan is null && Children(this).OfType<Button>().Single(b=>b.Name=="OneClickProductionStart").Text=="一键加工" &&
                File.Exists(Path.Combine(Path.GetDirectoryName(reworkSession)!,"补加工计划.json")),
                "明确新建全新工件恢复普通模式，已准备的补加工记录仍保留");
        }
        if(retryRecord is not null)
        {
            _laserDurationSeconds.Value=60;_laserPowerRaw.Value=4420;
            string failedJob=Path.GetDirectoryName(retryRecord)!;
            var proof=ReworkZeroExposureEvidence.Load(retryRecord);var failed=proof.Verify();
            string guardHash=ProductionReworkSource.Hash(retryRecord);
            _restoreProductionJobView!(failedJob);
            var button=Children(this).OfType<Button>().Single(b=>b.Name=="PrepareProductionRework");
            Check(button.Text=="未出光重试" && Children(this).OfType<TextBox>().Single(t=>t.Name=="ProductionStatus").Text.Contains("未出光重试"),
                "加载本次采图失败补加工后明确显示未出光重试入口，不把0/18当作新工件");
            _selectProductionPanel!();button.PerformVisibleClick();
            while(_workspaceBusy)await Task.Delay(20);
            string retrySession=_state.LastSessionPath;
            var retryPlan=_activeReworkPlan??throw new Exception("未建立重试计划");
            Check(retryPlan.Info!.RetryOfRunId==failed.RunId && retryPlan.Info.CumulativeRequestedMs==120000 &&
                RingScanSession.Load(retrySession).Frames.Count==0 && !_fixedCConfirmed.Checked,
                "实际点击未出光重试后建立独立空六图记录，累计仍120秒且要求本次装夹确认");
            Check(ProductionReworkSource.Hash(retryRecord)==guardHash && !File.Exists(Path.Combine(Path.GetDirectoryName(retrySession)!,ProductionJobArchive.MachiningRecordName)),
                "GUI准备重试保留原失败占用，仅建计划，不运动或出光");
            await _loadWorkspace!(retrySession);
            Check(_activeReworkPlan?.Info?.RetryOfRunId==failed.RunId && _activeReworkPlan.RetryEvidence is not null &&
                Children(this).OfType<Button>().Single(b=>b.Text=="一键补加工").Enabled==false,
                "重启恢复重试计划保留独立未出光证据，Sim继续禁止真实出光");
            using(var retryImage=new Bitmap(Width,Height)){DrawToBitmap(retryImage,new Rectangle(0,0,Width,Height));retryImage.Save(Path.Combine(folder,"ui_rework_retry.png"));}
            string newJob=Path.GetDirectoryName(retrySession)!;
            var simulated=new ProductionRunRecord {DryRun=false,Synthetic=true,RequestedExposureMs=60000,Rework=retryPlan.Info,
                Phase="Failed",StoppedDuring="Starting",LaserOffConfirmed=true,NcExitConfirmed=true,Events=["Starting"]};
            string newRun=Path.Combine(newJob,"整件_"+simulated.RunId.ToString("N"));Directory.CreateDirectory(newRun);
            ProductionJobArchive.Begin(newJob,simulated);ProductionJobArchive.Save(newJob,newRun,simulated);
            string retryZip=ExportProductionResult(newRun,RingScanSession.Load(retrySession),simulated,LaserSpotCalibration.Load(laserCalibration!));
            using var package=System.IO.Compression.ZipFile.OpenRead(retryZip);
            Check(new[]{"run.json","本件真实加工记录.json","补加工计划.json","原补加工登记.json"}.All(n=>package.Entries.Any(e=>e.FullName=="未出光重试依据/"+n)),
                "独立本件包保留失败run、原占用、原计划及替代前登记历史，不丢失重试依据");
            using(var failedInZip=JsonDocument.Parse(package.Entries.Single(e=>e.FullName=="未出光重试依据/run.json").Open()))
                Check(failedInZip.RootElement.GetProperty("RunId").GetGuid()==failed.RunId &&
                    failedInZip.RootElement.GetProperty("Holes").EnumerateObject().Count()==0,"包内保留真实失败0孔记录，未伪造为完成或删除防重跑证据");
        }
        await CheckWorkflowAdditions(folder,Check,source,laserCalibration);
        File.WriteAllText(Path.Combine(folder, "checks.json"), JsonSerializer.Serialize(checks, new JsonSerializerOptions { WriteIndented = true }));
    }
}

internal static class WorkflowCheckExtensions
{
    internal static void PerformVisibleClick(this Button button)
    {
        var pages=new List<TabPage>();
        for(Control? p=button.Parent;p is not null;p=p.Parent)if(p is TabPage page)pages.Add(page);
        foreach(var page in pages.AsEnumerable().Reverse())if(page.Parent is TabControl tabs)tabs.SelectedTab=page;
        for(Control? parent=button.Parent;parent is not null && parent is not Form;parent=parent.Parent)
            if(parent is FlowLayoutPanel)parent.Visible=true;
        button.PerformClick();
    }
}
