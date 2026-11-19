using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Devices.LaserRtu;
using Hummingbird.Vision;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private static XyEvidenceReview.Motion CheckOpticalStationaryTexture(RingScanFrame first,RingScanFrame repeated)
    {
        XyEvidenceReview.Motion result;
        try{result=XyEvidenceReview.Measure(new(first.ImagePath,first),new(repeated.ImagePath,repeated));}
        catch(InvalidDataException ex){throw new InvalidDataException("原位图像纹理不足或匹配不唯一，尚未请求XY移动；请调整清晰度或选择有清晰纹理的校准区域。"+ex.Message,ex);}
        if(result.MaxPatchSpreadPx>.25 || double.Hypot(result.Dx,result.Dy)>.25)
            throw new InvalidDataException("原位重复图不稳定，尚未请求XY移动；请停稳机床并检查成像。");
        return result;
    }
    private string? _lastAutomaticOpticsFolder;
    private string? _lastAutomaticOpticsCalibration;
    private Action<string>? _confirmAutomaticOpticsResult;
    private CancellationTokenSource? _automaticOpticsStop;
    private Button? _automaticOpticsStopButton;
    private (double Value,AxisCoordinateSource Source,string? Node)? _automaticOpticsFixedC;
    private TabPage BuildAutomaticOpticalTab(Func<Func<Task>,Task> run)
    {
        var page=new TabPage("自动校准");var flow=WorkflowFlow();
        var status=new TextBox {Name="AutomaticOpticsStatus",Dock=DockStyle.Bottom,Height=130,Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,
            Text="先把现场选定的校准区域放入画面并停稳。程序自动采图、计算与保存，不需要选择照片或JSON。"};
        var parameters=new Label {AutoSize=true,MaximumSize=new Size(950,0)};
        void RefreshParameters()
        {
            parameters.Text=$"本次校准试打：{_laserDurationSeconds.Value:0.###}秒 · 原始功率{_laserPowerRaw.Value}；沿用第1页参数，开始时再次核对读回。";
        }
        _laserDurationSeconds.ValueChanged+=(_,_)=>RefreshParameters();_laserPowerRaw.ValueChanged+=(_,_)=>RefreshParameters();RefreshParameters();
        flow.Controls.Add(GroupBoxOf("1 在机床面板启动校准程序",Lbl("使用配套HM_OPTICAL.MPF；机床已有对应程序时不需重复上传。结束其他NC，选择该程序（或上传后实际名称），从头Cycle Start一次进入等待。"),
            Btn("没有校准程序：前往上传",(_,_)=>ShowNcUpload("HM_OPTICAL.MPF")),
            Btn("前往参数与设备连接",(_,_)=>_tabs.SelectedTab=_environmentPage),
            Lbl("此程序只执行有界XY位移，不移动Z/C，不自行出光，与主控/回位程序共用R220～229，不能同时运行。")));
        flow.Controls.Add(GroupBoxOf("2 自动检查校准条件",Lbl("程序自动核对工作Z/C、校准程序等待、采样范围及原位图像纹理；缺项会提示处理。仅无法由接口判断的参考有效、区域允许采样及留痕，在开始时集中确认一次。正常换件或回位无需重新校准。"),parameters));
        string? result=null;
        async Task Start(bool withSpot)
        {
            _opticalUiAction=true;
            try
            {
                await run(async()=>
                {
                    try
                    {
                        string profilePath=Path.Combine(StationProgramFiles.Folder,"自动光学校准配置.json");
                        var profile=JsonSerializer.Deserialize<OpticalCalibrationProfile>(File.ReadAllText(profilePath))??throw new InvalidDataException("校准配置为空。");
                        profile.Validate();StationProgramFiles.VerifyPairedProgram(profilePath,profilePath,"HM_OPTICAL.MPF",ContinuousNcProgram.Generate(profile.Envelope!,profile.SessionToken));
                        string summary="自动选曝光并采图，先用原位重复图检查纹理和稳定性，通过后才进行两个方向各不超过0.10mm的采样，返回原位计算。\r\n"
                            +(withSpot?$"本轮还会按{_laserDurationSeconds.Value:0.###}秒／原始功率{_laserPowerRaw.Value}留痕一次，用于计算落点。":"本轮只计算XY，不出光。");
                        await RunGuidedOperation(profile.Envelope!,profile.SessionToken,"HM_OPTICAL",summary,withSpot,true,async ct=>
                        {ct.ThrowIfCancellationRequested();result=await RunAutomaticOpticalCalibration(withSpot,text=>status.Text=text);},text=>status.Text=text);
                    }
                    catch(Exception ex){status.Text="校准未完成："+ex.Message+"\r\n原资料保留；未完成项不自动采用，异常不自动重发或补打。";throw;}
                });
            }
            finally{_opticalUiAction=false;}
        }
        var start=Btn("开始自动校准（含一次试打）",async(_,_)=>await Start(true));start.Name="AutomaticOpticsStart";
        var xy=Btn("仅自动XY采图与计算（不出光）",async(_,_)=>await Start(false));xy.Name="AutomaticOpticsXyOnly";
        start.Enabled=xy.Enabled=_cfg.DeviceMode=="Real";
        _automaticOpticsStopButton=Btn("停止自动校准",(_,_)=>
        {
            RequestProductionStop();status.Text="已请求停止并执行关光清理；机床实际停止以面板为准，不自动返回或重打。";
        });_automaticOpticsStopButton.Name="AutomaticOpticsStop";
        flow.Controls.Add(GroupBoxOf("3 一次完成采图与计算",start,xy,_automaticOpticsStopButton,
            Btn("查看落点标注并确认",async(_,_)=>await UiAction(()=>{ShowAutomaticOpticsResult();return Task.CompletedTask;})),
            Btn("打开本次资料",async(_,_)=>await UiAction(()=>{if((result??_lastAutomaticOpticsFolder) is {} saved)OpenLocalFolder(saved);return Task.CompletedTask;})),
            Lbl("流程：自动选择一次曝光 → 原位/重复图 → X位移图 → Y位移图 → 返回原位复拍 → XY计算及一致性检查 → 退出NC → 一次试打前后图 → 落点计算及自动载入。"),
            Lbl("过程和结果自动按轮次保存。光学确实改变后的旧落点与旧六步基准停用；完成后仍按第3页流程建立新光学条件的六步基准，并核对本次落点。失败会显示具体原因，详细资料在维护页查看。")));
        page.Controls.Add(flow);page.Controls.Add(status);return page;
    }

    private void ShowAutomaticOpticsResult()
    {
        if(_lastAutomaticOpticsCalibration is not {} calibration || _lastAutomaticOpticsFolder is not {} folder)
            throw new InvalidOperationException("本次还没有完整通过的自动落点结果。");
        string path=Path.Combine(folder,"落点计算","落点标注_黄色落点_橙色画面中心.png");
        using var window=new Form {Text="本次自动落点结果",ClientSize=new Size(980,700),StartPosition=FormStartPosition.CenterParent};
        using var source=Image.FromFile(path);
        var picture=new PictureBox {Dock=DockStyle.Fill,SizeMode=PictureBoxSizeMode.Zoom,Image=source};
        var confirm=Btn("已核对本次落点和光学条件",(_,_)=>
        {
            try{_confirmAutomaticOpticsResult!(calibration);window.DialogResult=DialogResult.OK;window.Close();}
            catch(Exception ex){MessageBox.Show(window,ex.Message,"结果尚未确认");}
        });confirm.Dock=DockStyle.Bottom;
        var help=new Label {Dock=DockStyle.Top,Height=45,Text="黄色为本次求得落点，橙色为画面中心。此确认沿用原光学条件确认，不代替独立位置或实物质量验收。"};
        window.Controls.Add(picture);window.Controls.Add(confirm);window.Controls.Add(help);window.ShowDialog(this);
    }
    private async Task<string> RunAutomaticOpticalCalibration(bool withSpot,Action<string> progress)
    {
        if(_cfg.DeviceMode!="Real" || _cnc is not ISinumerikVarAccess vars || !_cnc.IsConnected || _camera is not {IsOpen:true} ||
            _laser is not ModbusRtuLaserService device || !device.IsOpen)
            throw new InvalidOperationException("先在第1页连接现场相机、数控和激光串口；离线模式不能执行自动校准。");
        if(!_fixedCConfirmed.Checked && !_guidedOperationConfirmed)throw new InvalidOperationException("本次校准尚未完成现场条件确认。");
        string profilePath=Path.Combine(StationProgramFiles.Folder,"自动光学校准配置.json");
        var profile=JsonSerializer.Deserialize<OpticalCalibrationProfile>(File.ReadAllText(profilePath))??throw new InvalidDataException("校准配置为空。");
        profile.Validate();StationProgramFiles.VerifyPairedProgram(profilePath,profilePath,"HM_OPTICAL.MPF",ContinuousNcProgram.Generate(profile.Envelope!,profile.SessionToken));
        var preset=_laserPreset;var initial=await device.ReadRecordedProtocolAsync();
        await device.VerifyEmissionOffAsync();
        if(withSpot && (!initial.CanExpose || !preset.Matches(initial)))throw new InvalidOperationException("请先核对关光及当前校准试打参数读回；本页不自动写工艺参数。");
        var nc=new ContinuousNcSession(_cnc,vars,profile.Envelope!,profile.SessionToken,async _=>(await ReadFixedCAsync()).Value);
        await nc.VerifyReadyAsync(); // 仅读状态，错误主控/回位会话不提交任何请求。
        string root=EnsureCaptureRoot()??throw new OperationCanceledException("未选择资料目录。");
        string folder=Path.Combine(root,"光学标定_自动_"+DateTime.Now.ToString("yyyyMMdd_HHmmssfff")+"_"+Guid.NewGuid().ToString("N")[..6]);Directory.CreateDirectory(folder);
        SaveGuidedStartupEvidence(folder);
        File.Copy(profilePath,Path.Combine(folder,"自动光学校准配置.json"));
        File.Copy(Path.Combine(StationProgramFiles.Folder,"HM_OPTICAL.MPF"),Path.Combine(folder,"校准程序_核对副本.MPF"));
        _lastAutomaticOpticsFolder=folder;_lastAutomaticOpticsCalibration=null;
        var sampling=new OpticalSamplingRecord();bool ncEndAttempted=false;
        async Task EndNc()
        {
            if(ncEndAttempted)throw new InvalidOperationException("NC退出已尝试，不自动重发退出请求。");
            ncEndAttempted=true;await nc.EndAsync();sampling.NcExitConfirmed=true;
        }
        string? xyResult=null;
        var oldSettings=_ringSettingsSnapshot!();double gain=(double)_numGain.Value,exposure=(double)_numExp.Value;
        var verifiedC=await ReadFixedCAsync();
        using var stop=CancellationTokenSource.CreateLinkedTokenSource(_guidedOperationStop?.Token??CancellationToken.None);_automaticOpticsStop=stop;_automaticOpticsFixedC=verifiedC;var ct=stop.Token;
        try
        {
            progress("正在核对原位、关光及校准会话；尚未出光。\r\n"+folder);
            _markOpticsChanged?.Invoke();
            var selected=await _cameraCapture!.SelectExposureAsync(exposure,gain,(frame,_)=>Task.FromResult(ExposureSelectionPolicy.Measure(frame)),ct);
            if(selected.Frame.Width!=1920 || selected.Frame.Height!=1200)throw new InvalidDataException("自动校准当前支持1920×1200原图，请核对相机设置。");
            exposure=selected.ExposureUs;_settingSelectedExposure=true;
            try{_numExp.Value=(decimal)exposure;}finally{_settingSelectedExposure=false;}
            await File.WriteAllTextAsync(Path.Combine(folder,"校准曝光选优.json"),JsonSerializer.Serialize(new{selected.ExposureUs,selected.Trials},new JsonSerializerOptions{WriteIndented=true}),ct);
            async Task Persist(OpticalSamplingRecord value)
            {
                string phase=value.Phase.Split(':')[0] switch
                {
                    "Checking"=>"核对起点与关光", "MoveIntent"=>"正在执行有界XY采样", "Capturing"=>"正在停稳采图",
                    "Calculating"=>"核对返回原位并计算XY", "ExitingNc"=>"等待校准NC退出", "Completed"=>"XY采样与计算完成",
                    "Failed"=>"采样失败", "Canceled"=>"已取消",_=>value.Phase
                };
                progress("自动光学校准："+phase+$" · 已保存{value.Captures.Count}/5张\r\n"+folder);
                string path=Path.Combine(folder,"自动XY采样记录.json");
                await File.WriteAllTextAsync(path+".tmp",JsonSerializer.Serialize(value,new JsonSerializerOptions{WriteIndented=true}));File.Move(path+".tmp",path,true);
            }
            string? firstSample=null;
            var runner=new AutomaticOpticalSampling(profile,
                async token=>{await nc.VerifyReadyAsync(token);var p=await ReadSpotPosition();return new("OpticalOrigin",RingFrameRole.RingArc,p.X,p.Y,p.Z,p.C);},
                async token=>{token.ThrowIfCancellationRequested();await device.VerifyEmissionOffAsync(token);},
                nc.MoveAndSettleAsync,
                async(index,x,y,token)=>
                {
                    token.ThrowIfCancellationRequested();var before=await ReadSpotPosition();
                    if(Math.Abs(before.X-x)>.005 || Math.Abs(before.Y-y)>.005)throw new IOException("采图实际位置与本次校准目标不一致。");
                    _=await _cameraCapture.CaptureAsync(exposure,gain);token.ThrowIfCancellationRequested();
                    var image=await _cameraCapture.CaptureAsync(exposure,gain);token.ThrowIfCancellationRequested();
                    var after=await ReadSpotPosition();StationarySpotTrial.CheckStable(before,after);
                    string path=Path.Combine(folder,$"自动采样_{index}.bmp");RingScanSolver.SaveFrame(path,image);
                    var frame=RingCaptureArchive.BindImage(new RingScanFrame($"OpticalSample{index}",path,before.X,before.Y,before.Z,before.C),oldSettings,
                        image.Timestamp,image.Width,image.Height,AxisCoordinateSource.OpcUaReadback,before.CSource,before.CNode,"AutomaticOpticalCalibration");
                    frame=frame with{Capture=frame.Capture! with{DeviceMode="Real",RequestedExposureUs=exposure,RequestedGain=gain}};
                    string sidecar=RingCaptureArchive.SaveSidecar(frame,oldSettings);
                    if(index==0)firstSample=sidecar;
                    if(index==1)
                    {
                        var first=RingScanSession.Load(firstSample!).Frames.Single();
                        var texture=await Task.Run(()=>CheckOpticalStationaryTexture(first,frame),token);
                        await File.WriteAllTextAsync(Path.Combine(folder,"采样前纹理与重复性检查.json"),JsonSerializer.Serialize(texture,new JsonSerializerOptions{WriteIndented=true}),token);
                    }
                    return sidecar;
                },
                async(paths,token)=>
                {
                    token.ThrowIfCancellationRequested();
                    var first=RingScanSession.Load(paths[1]).Frames.Single();var returned=RingScanSession.Load(paths[4]).Frames.Single();
                    var back=await Task.Run(()=>XyEvidenceReview.Measure(new(first.ImagePath,first),new(returned.ImagePath,returned)),token);
                    await File.WriteAllTextAsync(Path.Combine(folder,"返回原位图像复核.json"),JsonSerializer.Serialize(back,new JsonSerializerOptions{WriteIndented=true}),token);
                    if(back.MaxPatchSpreadPx>.25 || double.Hypot(back.Dx,back.Dy)>.25)throw new InvalidDataException("返回原位图像未通过0.25px重复性检查，未采用校准结果。");
                    var calculated=await Task.Run(()=>CalculateOpticalXy(paths.Take(4).ToArray(),Path.Combine(folder,"XY计算")),token);
                    xyResult=calculated.Path??throw new InvalidDataException(calculated.Description);
                },EndNc);
            await runner.RunAsync(sampling,Persist,ct);
            ct.ThrowIfCancellationRequested();
            _adoptOpticalSettings!(RingScanSession.Load(xyResult!).Settings); // 已返回原位且NC退出后才更新软件参数。
            if(!withSpot){progress("自动XY校准完成并已采用，NC已退出，未请求出光。\r\n"+folder);return folder;}
            progress($"XY已通过，正在原位按{preset.RequestedExposureMs/1000.0:0.###}秒／功率{preset.Settings.Power}保存一次试打前后图……");
            var actual=await ReadSpotPosition();var origin=sampling.Origin!;
            StationarySpotTrial.CheckStable(new(origin.X,origin.Y,origin.Z,origin.C,verifiedC.Source,verifiedC.Node,DateTimeOffset.Now),actual);
            string trialFolder=Path.Combine(folder,"单点落点来源");
            await RunSingleSpotTrial(false,propagateErrors:true,folderOverride:trialFolder,ct:ct,presetOverride:preset);
            ct.ThrowIfCancellationRequested();string analyzed=Path.Combine(folder,"落点计算");
            await Task.Run(()=>LaserSpotReview.Run(Path.Combine(trialFolder,"trial.json"),analyzed),ct);
            ct.ThrowIfCancellationRequested();
            using var report=JsonDocument.Parse(File.ReadAllText(Path.Combine(analyzed,"落点分析.json")));
            string calibrationPath=report.RootElement.GetProperty("CalibrationPath").GetString()!;
            await _importOpticalCalibration!(calibrationPath);_lastAutomaticOpticsCalibration=calibrationPath;
            progress("自动校准完成：XY与落点已计算、保存并载入，无需选择照片或JSON。\r\n点击“查看落点标注并确认”核对本次光学条件；新光学条件仍需新六步基准，本次未认定加工精度或孔质量合格。\r\n"+folder);
            return folder;
        }
        catch(Exception ex)
        {
            if(!ncEndAttempted)
                try{await EndNc();}catch(Exception cleanup){sampling.CleanupError=cleanup.Message;}
            await File.WriteAllTextAsync(Path.Combine(folder,"自动校准失败.json"),JsonSerializer.Serialize(new{At=DateTimeOffset.Now,Error=ex.ToString(),XyResult=xyResult,SamplingCompleted=sampling.Completed,sampling.NcExitConfirmed,sampling.CleanupError},new JsonSerializerOptions{WriteIndented=true}));
            throw;
        }
        finally{_automaticOpticsStop=null;_automaticOpticsFixedC=null;UpdateLaserCalibrationButtons();}
    }
}
