using System.Text.Json;
using Hummingbird.Core;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    internal async Task CheckAutomaticOpticalUi(string folder)
    {
        var results=new List<object>();
        void Check(bool value,string name){results.Add(new{Passed=value,Name=name});if(!value)throw new InvalidOperationException(name);}
        IEnumerable<Control> Children(Control root)=>root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Children(c)));
        var tabs=(TabControl)_opticsPage.Controls[0];
        Check(tabs.TabPages[0].Text=="自动校准" && tabs.TabPages.Cast<TabPage>().Any(p=>p.Text=="维护：已有照片分析"),"默认展示自动校准，原照片与JSON分析移到维护页");
        Check(!Children(_opticsPage).OfType<Button>().Single(b=>b.Name=="AutomaticOpticsStart").Enabled &&
            !Children(_opticsPage).OfType<Button>().Single(b=>b.Name=="AutomaticOpticsXyOnly").Enabled,"Sim模式禁止自动校准运动及出光");
        Check(!Children(tabs.TabPages[0]).OfType<CheckBox>().Any(),"校准页不要求预先记住并勾选长串检查项，现场许可在开始时集中确认");
        Check(!_guidedOperationConfirmed && _guidedOperationC is null,"初始不恢复校准或加工的现场许可");
        var profile=JsonSerializer.Deserialize<OpticalCalibrationProfile>(File.ReadAllText(Path.Combine(StationProgramFiles.Folder,"自动光学校准配置.json")))!;
        profile.Validate();StationProgramFiles.VerifyPairedProgram(Path.Combine(StationProgramFiles.Folder,"自动光学校准配置.json"),Path.Combine(StationProgramFiles.Folder,"自动光学校准配置.json"),
            "HM_OPTICAL.MPF",ContinuousNcProgram.Generate(profile.Envelope!,profile.SessionToken));
        string program=File.ReadAllText(Path.Combine(StationProgramFiles.Folder,"HM_OPTICAL.MPF"));
        Check(program.Contains("R225=171501") && !System.Text.RegularExpressions.Regex.IsMatch(program,@"\b[ZC]\s*=|\bM[34]\b"),"配套校准NC与参数一致、使用独立会话且不含Z/C移动或激光指令");
        bool outside=false;try{await ReadFixedCAsync();}catch(InvalidOperationException){outside=true;}
        Check(outside,"未确认且没有校准会话时不能读取人工C值");
        using(var scope=new CancellationTokenSource())
        {
            _automaticOpticsStop=scope;_automaticOpticsFixedC=(254.8245,AxisCoordinateSource.ManualPanelEntry,null);
            var current=await ReadFixedCAsync();
            Check(current.Value==254.8245 && !_fixedCConfirmed.Checked,"校准仅在本次任务作用域使用已经冻结的面板C，不恢复全局确认");
            RequestProductionStop();Check(scope.IsCancellationRequested,"公共停止入口取消自动校准");
            _automaticOpticsStop=null;_automaticOpticsFixedC=null;
        }
        outside=false;try{await ReadFixedCAsync();}catch(InvalidOperationException){outside=true;}
        Check(outside && !_cnc.IsConnected && !_laser.IsOpen,"任务结束后人工C作用域关闭，测试不连接真实设备");
        File.WriteAllText(Path.Combine(folder,"automatic-optics-ui-checks.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
    }
}
