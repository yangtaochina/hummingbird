using System.Globalization;
using System.Reflection;
using Hummingbird.Core;
using Hummingbird.Vision;
using Hummingbird.Devices.LaserRtu;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private FieldEvidenceArchive? _fieldEvidence;
    private readonly List<string> _evidenceIssues=new();
    private CancellationTokenSource? _evidenceStop;
    private Func<RingScanSession>? _evidenceReadSession;
    private void RecordLaserParameterCheck(LaserPresetCheck check)
    {
        string name="激光参数核对_"+check.Started.ToString("yyyyMMdd_HHmmssfff")+"_"+Guid.NewGuid().ToString("N")[..6]+".json";
        var report=new {Kind="LaserPresetReadbackCheck",DeviceMode=_cfg.DeviceMode,EmissionCommandSent=false,Check=check};
        if(Directory.Exists(_state.CaptureRoot))
        {
            try
            {
                string path=Path.Combine(_state.CaptureRoot,name);
                File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(report,new System.Text.Json.JsonSerializerOptions {WriteIndented=true}));
                _log.Info("[激光参数] 请求/应答/读回记录已保存："+path);
            }
            catch(Exception ex){_log.Warn("激光参数核对记录保存失败："+ex.Message);}
        }
        EvidenceAction(()=>_fieldEvidence!.AddJson("激光参数/"+name,report,"参数应用请求、应答标志与实际读回，不出光"));
        RefreshEvidencePackage();
    }
    private void RefreshEvidencePackage()
    {
        if(_fieldEvidence is null || _evidenceReadSession is null)return;
        try{ExportFieldEvidence(_evidenceReadSession());}catch(Exception ex){AddEvidenceIssue("PackageRefresh",ex);_log.Warn("资料包自动更新未完成，原目录仍保留。");}
    }
    private void AddEvidenceIssue(string phase,Exception error)
    {
        string message=error.Message;
        if(!string.IsNullOrEmpty(_cfg.CncPassword))message=message.Replace(_cfg.CncPassword,"[已隐藏]",StringComparison.Ordinal);
        _evidenceIssues.Add(phase+":"+error.GetType().Name+":"+message);
    }
    private void EvidenceAction(Action action)
    {
        if(_fieldEvidence is null)return;
        try{action();}catch(Exception ex){AddEvidenceIssue("EvidenceAppend",ex);_log.Warn("资料增补未完成："+ex.GetType().Name);}
    }
    private void RecordEvidenceTrial(string folder,SpotTrialRecord record)
    {
        EvidenceAction(()=>
        {
            string prefix="试打/"+Path.GetFileName(folder)+"/";
            _fieldEvidence!.AddJson(prefix+"trial.json",TrialEvidence(record),"试打结构化结果（不含原始日志）");
            foreach(string name in new[]{"before.bmp","after.bmp","before.capture.json","after.capture.json"})
                if(File.Exists(Path.Combine(folder,name)))_fieldEvidence.AddFile(Path.Combine(folder,name),prefix+name,"试打前后原图及坐标");
        });
        if(record.Completed || record.ShutdownConfirmed==true)RefreshEvidencePackage();
    }
    private static object TrialEvidence(SpotTrialRecord record)=>new {Kind="CuratedSpotTrialEvidence",record.DeviceMode,record.DryRun,record.Completed,record.Phase,
        record.ProductionRunId,record.HoleId,record.Started,record.Finished,record.RequestedExposureMs,record.RequestedPreset,record.CommandWindowMs,record.OpticalExposureMs,record.EmissionRequested,
        record.ShutdownConfirmed,record.LaserStateBefore,record.StartPosition,record.EndPosition,record.BeforeImage,record.AfterImage,
        record.SoftwareOpticalSettings,record.RequestedCameraExposureUs,record.RequestedCameraGain,record.Error};

    private string ExportProductionResult(string folder,RingScanSession session,ProductionRunRecord record,LaserSpotCalibration calibration)
    {
        var archive=new FieldEvidenceArchive(Path.Combine(folder,"本件资料"),_cfg.DeviceMode,[_cfg.CncPassword]);
        archive.CaptureSession(session);
        foreach(string name in new[]{"run.json","本件结果.md","加工事件.json","自动启动检查.json","定位稳定性检查.json","运行来源.json","定位与补偿.json","采图诊断.json"})
            if(File.Exists(Path.Combine(folder,name)))archive.AddFile(Path.Combine(folder,name),"整件/"+name,"本件进度及计算来源");
        foreach(string path in Directory.GetFiles(folder,"参考11点框预测_*.json"))
            archive.AddFile(path,"定位/参考框复核/"+Path.GetFileName(path),"本件圆环与1点推导的11点搜索初值，不是加工坐标");
        string baselineCheck=Path.Combine(Directory.GetParent(folder)!.FullName,"采图基准检查.json");
        if(File.Exists(baselineCheck))archive.AddFile(baselineCheck,"定位/采图基准检查.json","本次复用基准来源及完整六图复核，不是本件加工坐标");
        if(record.Rework is { } rework)
        {
            string plan=Path.Combine(Directory.GetParent(folder)!.FullName,"补加工计划.json");
            if(File.Exists(plan))archive.AddFile(plan,"整件/补加工计划.json","本轮额外加工量及关联前轮");
            archive.AddFile(rework.ParentRecordPath,"原加工来源/run.json","前轮已完成记录，不作为本轮完成证据");
            archive.AddFile(rework.ParentManifestPath,"原加工来源/manifest.json","前轮资料清单指纹");
            string reservation=Path.Combine(Path.GetDirectoryName(rework.ParentManifestPath)!,"本轮补加工登记.json");
            if(File.Exists(reservation))archive.AddFile(reservation,"原加工来源/补加工登记.json","同一来源轮次只登记一轮补加工");
            if(rework.RetryOfRunId is { } failed)
            {
                var retryPlan=System.Text.Json.JsonSerializer.Deserialize<ProductionReworkPlan>(File.ReadAllText(plan))??throw new InvalidDataException("重试计划为空。");
                var proof=retryPlan.RetryEvidence??throw new InvalidDataException("缺少未出光证据。");
                _=proof.Verify();
                archive.AddFile(proof.RunPath,"未出光重试依据/run.json","被替代的采图失败轮次，不计为完成加工");
                archive.AddFile(proof.GuardPath,"未出光重试依据/本件真实加工记录.json","原失败轮次的独立持久占用记录");
                archive.AddFile(proof.PlanPath,"未出光重试依据/补加工计划.json","原失败轮次的计划及追加参数");
                string history=Path.Combine(Path.GetDirectoryName(reservation)!,"补加工登记历史_"+failed.ToString("N")+".json");
                archive.AddFile(history,"未出光重试依据/原补加工登记.json","登记替代前的原始历史，保留追溯关系");
            }
        }
        foreach(var frame in session.Frames)
        {
            string path=Path.ChangeExtension(frame.ImagePath,"region.json");
            if(File.Exists(path))archive.AddFile(path,"定位/参考框复核/"+GuidedCapture.Index(frame)+".region.json","原参考框、局部候选及原圆边筛选结果");
        }
        foreach(string directory in Directory.GetDirectories(folder,"hole_*"))
        {
            string path=Path.Combine(directory,"trial.json");if(!File.Exists(path))continue;
            var trial=System.Text.Json.JsonSerializer.Deserialize<SpotTrialRecord>(File.ReadAllText(path))??throw new InvalidDataException("孔位记录为空。");
            if(trial.ProductionRunId is { } runId && runId!=record.RunId)throw new InvalidDataException("孔位记录属于另一件运行，未混入资料包。");
            foreach(var image in new[]{trial.BeforeImage,trial.AfterImage}.Where(i=>i is not null))
                if(LaserSpotCalibration.Hash(Path.Combine(directory,image!.FileName))!=image.Sha256)throw new InvalidDataException("孔位原图与记录指纹不符，未生成资料包。");
            string prefix="孔位/"+Path.GetFileName(directory)+"/";
            archive.AddJson(prefix+"trial.json",TrialEvidence(trial),"孔位参数、状态及图像绑定");
            foreach(string name in new[]{"before.bmp","after.bmp","before.capture.json","after.capture.json"})
                if(File.Exists(Path.Combine(directory,name)))archive.AddFile(Path.Combine(directory,name),prefix+name,"孔位前后原图及坐标");
        }
        string trialPath=archive.AddFile(calibration.SourceTrialPath,"落点来源/trial.json","原始落点来源");
        string before=archive.AddFile(calibration.BeforeImagePath,"落点来源/before.bmp","原始落点前图");
        string after=archive.AddFile(calibration.AfterImagePath,"落点来源/after.bmp","原始落点后图");
        string portable=Path.Combine(archive.Folder,"落点来源","激光落点.json");
        (calibration with {SourceTrialPath=trialPath,BeforeImagePath=before,AfterImagePath=after}).Save(portable);
        archive.AddFile(portable,"落点来源/激光落点.json","可移植落点标定");
        return archive.Export(ProductionJobArchive.Summary(record));
    }
    private void RecordEvidenceArtifacts(string folder)
    {
        if(_fieldEvidence is null)return;
        foreach(string directory in Directory.EnumerateDirectories(folder).Where(p=>Path.GetFileName(p).StartsWith("曝光选优_")||Path.GetFileName(p).StartsWith("XY微调_")))
            foreach(string file in Directory.EnumerateFiles(directory).Where(p=>Path.GetExtension(p) is ".bmp" or ".json"))
                EvidenceAction(()=>_fieldEvidence.AddFile(file,"定位/过程/"+Path.GetFileName(directory)+"/"+Path.GetFileName(file),"曝光或微调过程证据"));
    }
    private void RecordEvidenceNc(string path)
    {
        EvidenceAction(()=>_fieldEvidence!.AddFile(path,"NC/"+Path.GetFileName(path),"本次导出的NC文本，不代表已执行"));
        RefreshEvidencePackage();
    }
    private void RecordEvidenceCalibration()
    {
        if(_fieldEvidence is null || !File.Exists(_state.LaserCalibrationPath))return;
        var source=LaserSpotCalibration.Load(_state.LaserCalibrationPath);string prefix="落点来源/";
        string trial=_fieldEvidence.AddFile(source.SourceTrialPath,prefix+"trial.json","落点原始来源，保持指纹");
        string before=_fieldEvidence.AddFile(source.BeforeImagePath,prefix+"before.bmp","落点来源原图");
        string after=_fieldEvidence.AddFile(source.AfterImagePath,prefix+"after.bmp","落点来源原图");
        string destination=Path.Combine(_fieldEvidence.Folder,"落点来源","激光落点.json");
        (source with {SourceTrialPath=trial,BeforeImagePath=before,AfterImagePath=after}).Save(destination);
        _fieldEvidence.AddFile(destination,prefix+"激光落点.json","可移植落点来源引用");
    }
    private string ExportFieldEvidence(RingScanSession session)
    {
        var archive=_fieldEvidence??throw new InvalidOperationException("请先开始现场取证。");
        EvidenceAction(()=>archive.CaptureSession(session));EvidenceAction(RecordEvidenceCalibration);
        archive.AddJson("采集问题.json",_evidenceIssues.ToArray(),"未成功采集的项目");
        return archive.Export($"# 现场资料汇总\n\n设备模式：{_cfg.DeviceMode}。模拟模式资料不代表真机。\n\n"
            +$"收录定位记录引用：{session.Frames.Count}/6；历史照片按原时间保留，不能当作本次新采照片。\n\n"
            +$"取证问题数：{_evidenceIssues.Count}。详见采集问题.json；缺项不伪装为检查通过。\n\n"
            +"本功能只采图和读参数，不发送运动、出光或工艺参数修改指令。\n\n"
            +"当前点/自动曝光/重复图在自动检查中；后续定位、微调及试打资料会自动增补并更新ZIP。\n\n"
            +"C来源以每张记录为准：人工面板输入不等于实际自动读回。相机参数为请求下发值。\n\n"
            +"时长和功率由客户在单点试打页选择，执行记录保存请求设置和设备读回；如需质量验收，另附实物孔径/深度/崩边测量。夹具/机床面板照片按需要提供。\n\n"
            +"直接传本ZIP即可；manifest.json可核对文件长度与SHA256。配置、操作偏好和原始日志未收集。\n");
    }
    private TabPage BuildFieldEvidenceTab(Func<RingScanSession> readSession,Func<int> readStep,Func<Func<Task>,Task> run)
    {
        _evidenceReadSession=readSession;
        var tab=new TabPage("现场取证");
        var flow=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
        var status=new TextBox {Multiline=true,ReadOnly=true,Width=310,Height=260,ScrollBars=ScrollBars.Vertical};
        var includeLaser=new CheckBox {AutoSize=true,Checked=true,Text="收集激光只读参数（不出光）"};
        async Task RunEvidence(Func<Task> action)
        {
            if(_workspaceBusy || _laserReadBusy || _laserExposureTask is {IsCompleted:false}) {status.Text="其他操作正在进行，请等待完成或先停止。";return;}
            status.Text="正在准备现场取证……";
            await run(async()=>
            {
                try{await action();}
                catch(Exception ex){status.Text="现场取证未完成："+ex.Message+"\r\n资料根目录："+_state.CaptureRoot;throw;}
            });
        }
        var hint=Lbl("开始后自动拍当前位置，多档曝光和重复图一并保存；后续照片自动增补。结束时生成ZIP，直接传给我。");hint.MaximumSize=new Size(310,0);
        var begin=Btn("开始现场自动取证",async (_,_)=>await RunEvidence(async()=>
        {
            string? root=EnsureCaptureRoot();if(root is null)throw new OperationCanceledException("未选择资料根目录，尚未开始取证。");
            if(!_fixedCConfirmed.Checked)throw new InvalidOperationException("请先核对并确认顶部固定C和装夹；随后自动连接相机、数控并采图。");
            if(_fieldEvidence is not null)ExportFieldEvidence(readSession());
            _fieldEvidence=new FieldEvidenceArchive(Path.Combine(root,"现场取证_"+DateTime.Now.ToString("yyyyMMdd_HHmmssfff")+"_"+Guid.NewGuid().ToString("N")[..6]),_cfg.DeviceMode,[_cfg.CncPassword]);
            _evidenceIssues.Clear();
            using var cancellation=new CancellationTokenSource();_evidenceStop=cancellation;
            var ct=cancellation.Token;
            try
            {
                if(_camera is not {IsOpen:true} || !_cnc.IsConnected)await ConnectAcquisitionDevices();
                if(_camera is not {IsOpen:true} || !_cnc.IsConnected)throw new InvalidOperationException("相机或数控未连接，已建立资料目录保存缺项。");
                var settings=readSession().Settings;int step=readStep();var selectedStep=GuidedCapture.Steps[step];
                var start=await ReadSpotPosition();
                _fieldEvidence.AddJson("自动检查/环境.json",new {DeviceMode=_cfg.DeviceMode,AppVersion=Assembly.GetExecutingAssembly().GetName().Version?.ToString(),AppSha256=LaserSpotCalibration.Hash(Assembly.GetExecutingAssembly().Location),
                    Camera=_camera.DeviceName,Settings=settings,StartPosition=start,Step=selectedStep.Name,RequestedExposureUs=(double)_numExp.Value,RequestedGain=(double)_numGain.Value},"软件版本、光学标定和坐标来源");
                try{_fieldEvidence.AddJson("自动检查/相机诊断.json",new {Parameters=await _cameraCapture!.DiagnosticsAsync()},"相机只读范围");}
                catch(Exception ex){AddEvidenceIssue("CameraDiagnostics",ex);}
                double gain=(double)_numGain.Value;int number=0;
                async Task<RingScanFrame> SaveImage(CameraFrame image,double exposure,string label)
                {
                    var position=await ReadSpotPosition();bool stable=true;
                    try{StationarySpotTrial.CheckStable(start,position);}catch{stable=false;}
                    string file=$"自动检查/{number++:D2}_{label}.bmp",path=Path.Combine(_fieldEvidence.Folder,file.Replace('/',Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);RingScanSolver.SaveFrame(path,image);
                    _fieldEvidence.AddFile(path,file,"当前位置原始灰度图");
                    var frame=RingCaptureArchive.BindImage(new(selectedStep.Name,path,position.X,position.Y,position.Z,position.C){Role=selectedStep.Role},settings,
                        image.Timestamp,image.Width,image.Height,_cfg.DeviceMode=="Sim"?AxisCoordinateSource.Simulator:AxisCoordinateSource.OpcUaReadback,position.CSource,position.CNode,"FieldEvidenceOnly");
                    frame=frame with {ImagePath=Path.GetFileName(path),Capture=frame.Capture! with {RequestedExposureUs=exposure,RequestedGain=gain,DeviceMode=_cfg.DeviceMode,CameraParameterSource="SentBeforeEvidenceCapture"}};
                    _fieldEvidence.AddJson(Path.ChangeExtension(file,".capture.json"),new {Frame=frame,StableSinceStart=stable,ReferencePosition=start,ReadAfterCapture=position},"采图时间、坐标、参数、判稳");
                    if(!stable)throw new InvalidOperationException("取证期间位置发生变化；已保留照片和前后坐标。");
                    return frame with {ImagePath=path};
                }
                status.Text="自动试拍曝光并收集坐标……";
                var selection=await _cameraCapture!.SelectExposureAsync((double)_numExp.Value,gain,async(image,exposure)=>
                {
                    var frame=await SaveImage(image,exposure,"曝光_"+exposure.ToString("F0",CultureInfo.InvariantCulture));
                    var previous=readSession().Frames.FirstOrDefault(f=>GuidedCapture.Index(f)==step);
                    var seed=previous is null?null:GuidedCapture.TranslateRegion(previous,settings,frame.X,frame.Y,frame.Capture!.ImageSha256);
                    var quality=ExposureSelectionPolicy.Measure(image,image.Width==settings.ImageWidth&&image.Height==settings.ImageHeight?seed:null);
                    if(seed is not null && selectedStep.Role!=RingFrameRole.RingArc && image.Width==settings.ImageWidth && image.Height==settings.ImageHeight)
                    {
                        var inspected=await Task.Run(()=>new DatumDirectionSolver().Inspect(frame with {FaceSeed=seed},settings,readSession().DirectionOptions));
                        quality=quality with {FeatureDetected=inspected.Status==DatumHoleInspectionStatus.Detected,Detail=inspected.Message};
                        _fieldEvidence.AddJson($"自动检查/识别_{number:D2}.json",inspected,"实体边界检查结果");
                    }
                    return quality;
                },ct,restoreExposureUs:(double)_numExp.Value);
                _fieldEvidence.AddJson("自动检查/曝光选优.json",new {selection.ExposureUs,Gain=gain,selection.Trials},"曝光选优结果");
                for(int i=0;i<3;i++)
                {
                    ct.ThrowIfCancellationRequested();await SaveImage(await _cameraCapture.CaptureAsync(selection.ExposureUs,gain),selection.ExposureUs,"重复_"+i);
                    await Task.Delay(200,ct);
                }
                await _cameraCapture.ApplyAsync((double)_numExp.Value,gain,ct);
                if(_cnc is ISinumerikVarAccess variables)
                {
                    var registers=new Dictionary<int,object?>();
                    for(int r=220;r<=229;r++){ct.ThrowIfCancellationRequested();try{registers[r]=await variables.ReadVarAsync($"/Channel/Parameter/R[u1, {r}]",ct);}catch(Exception ex){registers[r]="Unavailable:"+ex.GetType().Name;}}
                    _fieldEvidence.AddJson("自动检查/主控R参数只读.json",registers,"只读快照，不代表R区已确认可专用");
                }
                if(includeLaser.Checked && _cfg.DeviceMode=="Real")
                {
                    try{if(!_laser.IsOpen)await OpenLaserPort();if(_laser is ModbusRtuLaserService device)_fieldEvidence.AddJson("自动检查/激光只读.json",await device.ReadRecordedProtocolAsync(),"当前读回设置，不出光、不改参数");}
                    catch(Exception ex){AddEvidenceIssue("LaserReadback",ex);}
                }
                else _fieldEvidence.AddJson("自动检查/激光未读取.json",new {Reason=_cfg.DeviceMode=="Sim"?"Simulator":"UserExcluded"},"未读取原因");
            }
            catch(Exception ex){AddEvidenceIssue("AutomaticCheck",ex);}
            finally
            {
                _evidenceStop=null;
                if(_cameraCapture is not null && _camera is {IsOpen:true})try{await _cameraCapture.ApplyAsync((double)_numExp.Value,(double)_numGain.Value);}catch(Exception ex){AddEvidenceIssue("CameraRestore",ex);}
                string zip=ExportFieldEvidence(readSession());
                status.Text="资料已自动生成：\r\n"+zip+"\r\n后续定位/试打自动增补并更新ZIP；也可点击“更新并生成资料包”手动刷新。\r\n缺项数："+_evidenceIssues.Count;
            }
        }));
        var export=Btn("更新并生成资料包",async(_,_)=>await RunEvidence(()=>
        {status.Text="直接传这个文件：\r\n"+ExportFieldEvidence(readSession());return Task.CompletedTask;}));
        FormClosing+=(_,_)=>_evidenceStop?.Cancel();
        flow.Controls.Add(hint);flow.Controls.Add(includeLaser);flow.Controls.Add(begin);flow.Controls.Add(export);flow.Controls.Add(status);tab.Controls.Add(flow);return tab;
    }
}
