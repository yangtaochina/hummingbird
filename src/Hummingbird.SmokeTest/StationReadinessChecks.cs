using Hummingbird.Core;

internal static class StationReadinessChecks
{
    public static async Task<int> Run()
    {
        int passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("PASS "+name);}
        var machine=new FakeMachine();var bounds=new NcTravelEnvelope(-160.4,-134,-209.6,-181.7,-201.87324,254.8245);
        Task<StationReadinessReport> Inspect(double? c=254.8245)=>StationReadiness.InspectAsync(machine,machine,bounds,161339,"HM_MASTER",_=>Task.FromResult(c));
        var ready=await Inspect();Check(ready.CanContinue && !ready.NeedsPanelC && machine.Writes==0,"实际Z/C、边界及NC起始等待自动通过，检查不写机床");
        var manual=await Inspect(null);Check(manual.CanContinue && manual.NeedsPanelC && manual.ActualC is null,"无C反馈时仅要求人工补充，不把配置C冒充实测");
        foreach(double c in new[]{0.0,double.NaN,double.PositiveInfinity})Check(!(await Inspect(c)).CanContinue,"错误或非有限C反馈拒绝启动");
        Check((await Inspect(614.8245)).CanContinue,"C轴跨整周按同一实际角度核对");
        var broken=await StationReadiness.InspectAsync(machine,machine,bounds,161339,"HM_MASTER",_=>Task.FromException<double?>(new IOException("bad feedback")));
        Check(!broken.CanContinue,"已配置反馈读取失败不能静默退为人工C");
        foreach(var (r,value) in new[]{(225,171501.0),(220,0.0),(220,9.0),(220,2.0),(221,1.0),(222,1.0),(226,9.0),(225,double.NaN)})
        {
            double original=machine.R[r];machine.R[r]=value;
            Check(!(await Inspect()).CanContinue && machine.Writes==0,"错误会话／故障／未启动／非零序号／退出状态不自动修复或启动："+r+"="+value);
            machine.R[r]=original;
        }
        var good=machine.Position;
        foreach(var invalid in new[]{good with{PosZ=-192},good with{PosX=-170},good with{PosY=double.NaN},good with{Message="读取失败"},good with{Connected=false}})
        {machine.Position=invalid;Check(!(await Inspect()).CanContinue,"未恢复工位或无效读数不能开始");}
        machine.Position=good;
        foreach(string text in new[]{"","0","NaN","254,8245"})
        {bool rejected=false;try{StationReadiness.ValidatePanelC(text,bounds);}catch(InvalidOperationException){rejected=true;}Check(rejected,"人工面板C缺失或错误不接受："+text);}
        Check(StationReadiness.ValidatePanelC("254.8245",bounds)==254.8245,"人工输入须明确符合本工位角度");
        using var stop=new CancellationTokenSource();stop.Cancel();bool canceled=false;
        try{await StationReadiness.InspectAsync(machine,machine,bounds,161339,"HM_MASTER",_=>Task.FromResult<double?>(null),stop.Token);}
        catch(OperationCanceledException){canceled=true;}
        Check(canceled && machine.Writes==0,"取消自动检查不提交任何机床请求");
        Console.WriteLine($"Station readiness: {passed} passed; simulated read-only interfaces.");return 0;
    }

    private sealed class FakeMachine : ICncService,ISinumerikVarAccess
    {
        public string Endpoint=>"sim";public bool IsConnected=>true;public int Writes;
        public MachineStatus Position=new(true,"AUTO","-",-147,-196,-201.87324,"OK");
        public Dictionary<int,double> R=new(){[220]=1,[221]=0,[222]=0,[225]=161339,[226]=0};
        public Task<MachineStatus> GetStatusAsync(CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult(Position);}
        public Task<object?> ReadVarAsync(string path,CancellationToken ct=default){ct.ThrowIfCancellationRequested();return Task.FromResult<object?>(R[int.Parse(path.Split(',')[1].Trim().TrimEnd(']'))]);}
        public Task<bool> WriteRAsync(int r,double value,CancellationToken ct=default){Writes++;throw new InvalidOperationException("No writes allowed");}
        public Task<bool> WriteVarAsync(string path,object value,CancellationToken ct=default){Writes++;throw new InvalidOperationException("No writes allowed");}
        public Task<bool> ConnectAsync(string endpoint,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task DisconnectAsync()=>Task.CompletedTask;
        public Task<bool> UploadProgramAsync(string name,string content,CancellationToken ct=default)=>throw new NotSupportedException();
        public IDisposable SubscribePosition(Action<AxisPosition> change,int intervalMs=200)=>throw new NotSupportedException();
        public void Dispose(){}
    }
}
