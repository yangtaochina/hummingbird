using System.Globalization;
using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Devices.LaserRtu;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private CancellationTokenSource? _guidedOperationStop;
    private (double Value,AxisCoordinateSource Source,string? Node)? _guidedOperationC;
    private bool _guidedOperationConfirmed;
    private object? _guidedStartupEvidence;
    private Func<LaserSpotCalibration>? _readCalibrationForStartup;
    private Func<ProductionStationProfile,Task<string>>? _inspectProductionInputs;
    private Func<(bool Create,string Description)>? _inspectOneClickWorkpiece;
    private Func<Task>? _createOneClickWorkpiece;

    private async Task ConnectGuidedDevices(CancellationToken ct)
    {
        if(_cfg.DeviceMode!="Real")throw new InvalidOperationException("离线模式不能启动真实加工或自动校准。");
        if(_camera is not {IsOpen:true})await OpenCamera();ct.ThrowIfCancellationRequested();
        if(!_cnc.IsConnected)await ConnectCnc();ct.ThrowIfCancellationRequested();
        if(!_laser.IsOpen)await OpenLaserPort();ct.ThrowIfCancellationRequested();
    }

    private async Task RunGuidedOperation(NcTravelEnvelope bounds,int sessionToken,string program,string summary,
        bool requireExposure,bool optical,Func<CancellationToken,Task> action,Action<string> progress,string? consentText=null,
        Func<CancellationToken,Task<string>>? inspectInputs=null)
    {
        if(_guidedOperationStop is not null)throw new InvalidOperationException("已有启动检查进行中。");
        using var stop=new CancellationTokenSource();_guidedOperationStop=stop;var ct=stop.Token;
        try
        {
            _fixedCConfirmed.Checked=false;
            if(inspectInputs is not null)
            {
                progress("正在自动核对六步基准、光学参数及落点来源……");
                summary+="\r\n"+await inspectInputs(ct);ct.ThrowIfCancellationRequested();
            }
            progress("正在自动连接设备并核对工位、机床程序与激光状态……");
            await ConnectGuidedDevices(ct);
            if(_cnc is not ISinumerikVarAccess access || _laser is not ModbusRtuLaserService laser)
                throw new InvalidOperationException("设备接口不支持当前自动流程，请检查环境与参数。");
            var report=await StationReadiness.InspectAsync(_cnc,access,bounds,sessionToken,program,async token=>
            {
                if(string.IsNullOrWhiteSpace(_cfg.RotaryActualAngleNode))return null;
                return RingNumber(Convert.ToString(await access.ReadVarAsync(_cfg.RotaryActualAngleNode,token),CultureInfo.InvariantCulture));
            },ct);
            var laserState=await laser.ReadRecordedProtocolAsync();ct.ThrowIfCancellationRequested();
            var problems=report.Problems.ToList();
            if(laserState.Emission!=0)problems.Add("激光未确认关光：请先关光并检查设备状态。");
            if(requireExposure && (!laserState.CanExpose || !_laserPreset.Matches(laserState)))
                problems.Add("激光状态或参数不符：请在“环境与参数”应用当前时长／功率并核对读回，再点击开始。");
            if(problems.Count>0)throw new InvalidOperationException("自动检查发现以下待处理项：\r\n• "+string.Join("\r\n• ",problems));
            ct.ThrowIfCancellationRequested();
            using var dialog=new OperatorStartDialog(bounds,report,summary,optical,requireExposure,_spotTrialC.Text);
            if(consentText is not null)dialog.Consent.Text=consentText;
            if(report.NeedsPanelC)dialog.Consent.Text+="\r\n所填C为本次刚读取的面板实际角度。";
            if(dialog.ShowDialog(this)!=DialogResult.OK){progress("已取消启动，尚未提交运动或出光。");return;}
            ct.ThrowIfCancellationRequested();
            _guidedOperationC=report.ActualC is {} actual
                ?(actual,AxisCoordinateSource.OpcUaReadback,_cfg.RotaryActualAngleNode)
                :(dialog.PanelC,AxisCoordinateSource.ManualPanelEntry,null);
            _guidedOperationConfirmed=true;
            _guidedStartupEvidence=new {At=DateTimeOffset.Now,Program=program,Readback=report,ManualConfirmation=dialog.Consent.Text,
                FixedC=_guidedOperationC.Value.Value,CSource=_guidedOperationC.Value.Source,CNode=_guidedOperationC.Value.Node,Summary=summary};
            progress("启动检查通过，正在复核本次资料并开始……");
            await action(ct);
        }
        finally{_guidedOperationConfirmed=false;_guidedOperationC=null;_guidedStartupEvidence=null;_guidedOperationStop=null;UpdateLaserCalibrationButtons();}
    }

    private void SaveGuidedStartupEvidence(string folder)
    {
        if(_guidedStartupEvidence is not null)File.WriteAllText(Path.Combine(folder,"自动启动检查.json"),
            JsonSerializer.Serialize(_guidedStartupEvidence,new JsonSerializerOptions{WriteIndented=true}));
    }

    /// <summary>只询问接口无法判定的现场条件；输入只在本次任务有效，不持久保存为已确认。</summary>
    private sealed class OperatorStartDialog : Form
    {
        public double PanelC {get;private set;}
        internal TextBox PanelInput {get;}=new(){Width=150,Name="OperatorPanelC"};
        internal CheckBox Consent {get;}=new(){AutoSize=true,Name="OperatorStartConsent"};
        internal Button StartButton {get;}=new(){AutoSize=true,Text="确认并开始",Name="OperatorConfirmStart"};
        internal Label Error {get;}=new(){AutoSize=true,ForeColor=Color.DarkRed,MaximumSize=new Size(570,0)};
        public OperatorStartDialog(NcTravelEnvelope bounds,StationReadinessReport report,string summary,bool optical,bool withExposure,string panelC)
        {
            Text=optical?"开始自动校准":"开始本轮加工";ClientSize=new Size(640,440);MinimumSize=new Size(600,440);
            StartPosition=FormStartPosition.CenterParent;ShowInTaskbar=false;MaximizeBox=false;MinimizeBox=false;
            var flow=WorkflowFlow();
            flow.Controls.Add(new Label{AutoSize=true,MaximumSize=new Size(570,0),Text=summary+"\r\n\r\n自动检查通过：设备连接、程序等待、工作范围、Z高度、激光关光"+(withExposure?"及当前工艺参数。":"。")
                +(report.NeedsPanelC?"\r\n此机未接入C轴实际反馈，仅需补充本次面板C。":$"\r\nC轴实际反馈：{report.ActualC:F4}°。")});
            PanelInput.Text=panelC;PanelInput.Visible=report.NeedsPanelC;
            if(report.NeedsPanelC)flow.Controls.Add(GroupBoxOf("面板当前C角度（°）",PanelInput,new Label{AutoSize=true,Text=$"要求{bounds.C:F4}°；请填写面板实测值。"}));
            Consent.Text=optical?"机床参考有效、面板报警已处理，当前区域允许本次XY采样"+(withExposure?"及按显示参数留痕一次。":"。")
                :"本次工件已正确装夹、参考有效、报警已处理、区域清空，镜头／高度／工作距离未改变。";
            Consent.MaximumSize=new Size(570,0);flow.Controls.Add(Consent);flow.Controls.Add(Error);
            var buttons=new FlowLayoutPanel{AutoSize=true};var cancel=new Button{Text="取消",AutoSize=true,DialogResult=DialogResult.Cancel};
            StartButton.Click+=(_,_)=>
            {
                try
                {
                    if(!Consent.Checked)throw new InvalidOperationException("请确认本次现场条件后开始。");
                    PanelC=report.ActualC??StationReadiness.ValidatePanelC(PanelInput.Text,bounds);
                    DialogResult=DialogResult.OK;Close();
                }
                catch(Exception ex){Error.Text=ex.Message;}
            };
            buttons.Controls.AddRange([StartButton,cancel]);flow.Controls.Add(buttons);Controls.Add(flow);CancelButton=cancel;
        }
    }
}
