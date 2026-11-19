using Hummingbird.Devices.Sim;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private Action? _cancelProductionDemo;
    private TabPage BuildProductionDemoTab(Func<Func<Task>,Task> run)
    {
        var tab=new TabPage("整件流程演练");
        var panel=new FlowLayoutPanel {Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};
        var description=Lbl("合成数据演练：六步采图→每点纠偏复拍→18孔逐孔调度。不会连接任何真实设备，也不会出光。");
        description.MaximumSize=new Size(320,0);
        var duration=new NumericUpDown {Minimum=0,Maximum=600000,Value=0,Increment=1000,Width=140};
        var status=new TextBox {Multiline=true,ReadOnly=true,Width=310,Height=260,ScrollBars=ScrollBars.Vertical};
        CancellationTokenSource? cancellation=null;
        var start=Btn("运行整件离线演练",async (_,_)=>await run(async()=>
        {
            string? root=EnsureCaptureRoot();if(root is null)return;
            string folder=Path.Combine(root,"整件模拟_"+DateTime.Now.ToString("yyyyMMdd_HHmmssfff"));
            using var stop=new CancellationTokenSource();cancellation=stop;_cancelProductionDemo=stop.Cancel;
            try
            {
                status.Text="演练进行中……";
                var record=await OfflineProductionDemo.RunAsync(folder,duration.Value==0?null:(int)duration.Value,stop.Token);
                status.Text=$"完成：{record.CapturedSteps}步采图、{record.Holes.Count}孔演练。\r\n未调用实际出光接口。\r\n记录：{folder}\r\n"
                    +"此演练验证流程状态，图像识别和目标坐标为合成数据。现场补图后仍需通过真实定位检查。\r\n"
                    +"演练计划时长仅归档；真实加工使用单点试打页客户选择的时长和功率。";
            }
            catch(Exception ex){status.Text="演练停止："+ex.Message+"\r\n记录："+folder;throw;}
            finally{cancellation=null;_cancelProductionDemo=null;}
        }));
        FormClosing+=(_,_)=>cancellation?.Cancel();
        panel.Controls.Add(description);panel.Controls.Add(Lbl("计划出光时长 ms（0=待现场补充，仅演练记录）"));panel.Controls.Add(duration);
        panel.Controls.Add(start);panel.Controls.Add(status);tab.Controls.Add(panel);return tab;
    }
}
