using System.Text.Json;
using Hummingbird.Core;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    internal async Task CheckGuidedProductionUi(string folder)
    {
        var checks=new List<object>();
        void Check(bool ok,string name){checks.Add(new{Passed=ok,Name=name});if(!ok)throw new InvalidOperationException(name);}
        IEnumerable<Control> Children(Control root)=>root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Children(c)));
        var previous=_tabs.SelectedTab;SelectPage(_machiningPage);_captureDetails.SelectedIndex=0;
        Check(_captureDetails.SelectedTab!.Text=="一键加工" && Children(_machiningPage).OfType<Button>().Single(b=>b.Name=="OneClickProductionStart").Visible,
            "定位与加工默认显示一键加工主按钮");
        Check(!Children(_machiningPage).OfType<Button>().Any(b=>b.Text=="工位恢复" || b.Name=="StationReturnShortcut"),"加工页不再出现工位恢复快捷按钮");
        Check(!_captureProjectControls.Visible && !Children(_machiningPage).OfType<Button>().Single(b=>b.Text=="整件无出光试走").Visible,
            "工件资料和调试试走默认收起");
        Check(!Children(_machiningPage).OfType<CheckBox>().Any(c=>c.Text.Contains("本次主控NC")) && !_spotTrialC.Visible,
            "日常加工不显示NC核对复选框或顶部维护C输入");
        Check(_inspectOneClickWorkpiece!().Create && !_guidedOperationConfirmed,"无记录时只提出自动建新件，不提前确认或创建加工占用");
        var bounds=new NcTravelEnvelope(-160.4,-134,-209.6,-181.7,-201.87324,254.8245);
        var manual=new StationReadinessReport(new(true,"AUTO","-",-147,-196,bounds.Z,"OK"),null,[]);
        using(var dialog=new OperatorStartDialog(bounds,manual,"全新工件；18孔，每孔60秒，原始功率4420。",false,true,"254.8245"))
        {
            dialog.Opacity=0;dialog.Show(this);
            Check(!dialog.Consent.Checked && dialog.PanelInput.Visible,"无C反馈时集中提示人工实测，默认不恢复许可");
            dialog.StartButton.PerformClick();Check(dialog.DialogResult!=DialogResult.OK,"未确认现场条件不能一键开始");
            dialog.Consent.Checked=true;dialog.PanelInput.Text="0";dialog.StartButton.PerformClick();
            Check(dialog.DialogResult!=DialogResult.OK && dialog.Error.Text.Contains("面板实际C"),"人工角度错误时明确提示并禁止开始");
            using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(0,0,dialog.Width,dialog.Height));bitmap.Save(Path.Combine(folder,"operator_manual_c.png"));}
            dialog.PanelInput.Text="254.8245";dialog.StartButton.PerformClick();
            Check(dialog.DialogResult==DialogResult.OK && dialog.PanelC==254.8245 && !_guidedOperationConfirmed && !_fixedCConfirmed.Checked,
                "仅本次对话返回明确许可，界面测试不会建立运行作用域或全局确认");
        }
        using(var dialog=new OperatorStartDialog(bounds,manual with{ActualC=254.8245},"仅XY采样，不出光。",true,false,"0"))
        {
            dialog.Opacity=0;dialog.Show(this);
            Check(!dialog.PanelInput.Visible && !dialog.Consent.Checked,"有实时C反馈时不要求人工填写，也不预勾选区域许可");
            using(var bitmap=new Bitmap(dialog.Width,dialog.Height)){dialog.DrawToBitmap(bitmap,new Rectangle(0,0,dialog.Width,dialog.Height));bitmap.Save(Path.Combine(folder,"operator_feedback_c.png"));}
            dialog.Close();
        }
        bool simBlocked=false;int actions=0;
        try{await RunGuidedOperation(bounds,161339,"HM_MASTER","Sim",true,false,_=>{actions++;return Task.CompletedTask;},_=>{});}
        catch(InvalidOperationException){simBlocked=true;}
        Check(simBlocked && actions==0 && _guidedOperationStop is null && _guidedOperationC is null && !_guidedOperationConfirmed && !_cnc.IsConnected && !_laser.IsOpen,
            "模拟环境不连接真机、不执行一键动作；失败关闭本次许可作用域");
        using(var stop=new CancellationTokenSource())
        {
            _guidedOperationStop=stop;RequestProductionStop();Check(stop.IsCancellationRequested,"公共停止覆盖自动连接和启动检查阶段");_guidedOperationStop=null;
        }
        SelectPage(previous!);
        File.WriteAllText(Path.Combine(folder,"guided-production-ui-checks.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
    }
}
