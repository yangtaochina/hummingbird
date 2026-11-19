using Hummingbird.Core;
using Hummingbird.Devices.Sim;
using System.Text.Json;

internal static class ContinuousProductionChecks
{
    public static async Task<int> Run(string? output)
    {
        int passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("[PASS] "+name);}
        async Task<bool> Fails(Func<Task> action){try{await action();return false;}catch{return true;}}
        string folder=Path.GetFullPath(output??Path.Combine("obj","continuous-production-checks"));Directory.CreateDirectory(folder);
        var record=await OfflineProductionDemo.RunAsync(Path.Combine(folder,"dry-run"),8000);
        Check(record.Completed && record.CapturedSteps==6 && record.Holes.Count==18 && record.Holes.Values.All(p=>p==ProductionHolePhase.Completed),"六步纠偏复拍和18孔完整演练");
        Check(record.DryRun && record.RequestedExposureMs==8000,"较长计划时长可以归档，不执行真实出光");
        var cell=new OfflineProductionDemo(Path.Combine(folder,"live-simulator"));
        var work=new ContinuousProduction(cell);var live=new ProductionRunRecord {DryRun=false,RequestedExposureMs=8000};
        await work.RunAsync(OfflineProductionDemo.Route(),live);
        Check(cell.Moves==30 && cell.EmissionCalls==18 && cell.NcEnded,"同一会话12次采图位置调度后完成18孔，演练出光适配器被调用18次");
        Check(live.Events.IndexOf("HoleStarted:1")<live.Events.IndexOf("HoleCompleted:1") && live.Events.IndexOf("HoleCompleted:1")<live.Events.IndexOf("HoleMove:2"),"前孔完成后才调度下一孔");
        Check(await Fails(()=>work.RunAsync(OfflineProductionDemo.Route(),live)) && live.Completed,"已有记录拒绝自动重跑，保留原完成记录");
        var badCapture=new OfflineProductionDemo(Path.Combine(folder,"capture-failed")){FailCaptureStep=2};var badRecord=new ProductionRunRecord();
        Check(await Fails(()=>new ContinuousProduction(badCapture).RunAsync(OfflineProductionDemo.Route(),badRecord)) && badRecord.CapturedSteps==2 && badRecord.Holes.Count==0 && badCapture.EmissionCalls==0 && badCapture.NcEnded,"采图失败在定位加工前停止并保存进度");
        var badEmission=new OfflineProductionDemo(Path.Combine(folder,"emission-uncertain")){FailEmissionNumber=2};var failed=new ProductionRunRecord {DryRun=false,RequestedExposureMs=8000};
        Check(await Fails(()=>new ContinuousProduction(badEmission).RunAsync(OfflineProductionDemo.Route(),failed)) && failed.Holes[1]==ProductionHolePhase.Completed && failed.Holes[2]==ProductionHolePhase.Uncertain && failed.Holes.Count==2 && badEmission.EmissionCalls==2,"出光应答不明记录为不确定孔，不重打、不移动到下一孔");
        var noOff=new OfflineProductionDemo(Path.Combine(folder,"off-failed")){OffConfirmed=false};
        Check(await Fails(()=>new ContinuousProduction(noOff).RunAsync(OfflineProductionDemo.Route(),new())) && noOff.Moves==0,"关光未确认时不允许任何移动");
        var noArchive=new OfflineProductionDemo(Path.Combine(folder,"archive-failed")){FailPersist=true};
        Check(await Fails(()=>new ContinuousProduction(noArchive).RunAsync(OfflineProductionDemo.Route(),new())) && noArchive.Moves==0 && noArchive.EmissionCalls==0,"归档失败不会启动运动或出光");
        var unknownDuration=new OfflineProductionDemo(Path.Combine(folder,"unknown-duration"));
        Check(await Fails(()=>new ContinuousProduction(unknownDuration).RunAsync(OfflineProductionDemo.Route(),new(){DryRun=false})) && unknownDuration.Moves==0,"真实请求时长未确定不进入流程");
        using var stop=new CancellationTokenSource();stop.Cancel();var cancelled=new OfflineProductionDemo(Path.Combine(folder,"cancelled"));
        Check(await Fails(()=>new ContinuousProduction(cancelled).RunAsync(OfflineProductionDemo.Route(),new(),stop.Token)) && cancelled.Moves==0,"预取消不执行动作");
        using var midStop=new CancellationTokenSource();
        var mid=new OfflineProductionDemo(Path.Combine(folder,"mid-stop")){OnPersist=r=>{if(r.Phase=="HoleStarted:2")midStop.Cancel();}};
        var midRecord=new ProductionRunRecord {DryRun=false,RequestedExposureMs=8000};
        Check(await Fails(()=>new ContinuousProduction(mid).RunAsync(OfflineProductionDemo.Route(),midRecord,midStop.Token)) && mid.EmissionCalls==1 && midRecord.Holes[2]==ProductionHolePhase.Uncertain && midRecord.Phase=="Canceled",
            "运行中停止不会继续出光，保留待核对孔记录");
        var concurrentCell=new OfflineProductionDemo(Path.Combine(folder,"concurrent"));var concurrentFlow=new ContinuousProduction(concurrentCell);
        var active=concurrentFlow.RunAsync(OfflineProductionDemo.Route(),new());
        Check(await Fails(()=>concurrentFlow.RunAsync(OfflineProductionDemo.Route(),new())),"同一调度器拒绝并发整件启动");await active;
        var bounds=new NcTravelEnvelope(-20,20,-20,20,-1,0);string nc=ContinuousNcProgram.Generate(bounds,789);
        Check(nc.Contains($"HM_WAIT>{ContinuousNcProgram.IdleWaitTicks}") && ContinuousNcProgram.IdleWaitTicks*100>LaserRequestLimits.MaximumExposureMs,
            "主控等待窗口覆盖客户最长时长及归档余量");
        Check(nc.Contains("SUPA G1 X=R227 Y=R228") && nc.Split('\n').Count(l=>l.Trim()=="M0")==1 && nc.Contains("HM_ERROR:\r\nR220=9\r\nM0") && !nc.Contains("M62"),"主控NC正常循环不含逐点M0，仅故障暂停，无出光指令");
        NcSignedOperandChecks.Run(Check);
        var machine=new Machine();var session=new ContinuousNcSession(machine,machine,bounds,789,ct=>Task.FromResult(0.0));
        await session.MoveAndSettleAsync(1,2,CancellationToken.None);await session.MoveAndSettleAsync(2,3,CancellationToken.None);
        Check(machine.R[222]==2 && machine.X==2 && machine.Y==3,"连续NC会话两次序号握手并等待坐标判稳");
        Check(await Fails(()=>session.MoveAndSettleAsync(21,0,CancellationToken.None)) && machine.R[221]==2,"超出已核定运动边界不提交指令");
        machine.FailWrite=224;
        Check(await Fails(()=>session.MoveAndSettleAsync(3,4,CancellationToken.None)) && machine.R[221]==2,"部分目标写入失败不提交新序号");
        await session.EndAsync();Check(machine.R[226]==9,"结束时发送当前会话退出请求");
        Check(machine.R[220]==0,"主控退出必须读回结束状态，不能只把请求写入当完成");
        var readyMachine=new Machine();var readySession=new ContinuousNcSession(readyMachine,readyMachine,bounds,789,ct=>Task.FromResult(0.0));
        await readySession.VerifyReadyAsync();Check(readyMachine.Writes==0,"整件就绪检查只读，不提交XY或退出请求");
        readyMachine.R[225]=100;string? notReady=null;
        try{await readySession.VerifyReadyAsync();}catch(InvalidOperationException ex){notReady=ex.Message;}
        Check(notReady?.Contains("会话=100（期望789）")==true && readyMachine.Writes==0,"就绪失败显示实际会话及期望值，保留硬件状态");
        readyMachine.R[225]=789;readyMachine.R[220]=0;
        Check(await Fails(()=>readySession.VerifyReadyAsync()) && readyMachine.Writes==0,"面板尚未启动主控时预检拒绝，未修改R值");
        var guardFolder=Path.Combine(folder,"job-guard");Directory.CreateDirectory(guardFolder);
        var guarded=new ProductionRunRecord {DryRun=false,RequestedExposureMs=60000};
        ProductionJobArchive.Begin(guardFolder,guarded);
        ProductionJobArchive.Save(guardFolder,Path.Combine(guardFolder,"run"),guarded);
        Check(File.Exists(Path.Combine(guardFolder,ProductionJobArchive.MachiningRecordName)) &&
            await Fails(()=>Task.Run(()=>ProductionJobArchive.Begin(guardFolder,new(){DryRun=false}))),"真实加工开始前持久占用本工件，拦住重复启动");
        Check(await Fails(()=>Task.Run(()=>ProductionJobArchive.EnsureUnused(guardFolder))),"模拟程序重启后，同一工件目录仍不能重打");
        Check(await Fails(()=>Task.Run(()=>ProductionJobArchive.Save(guardFolder,Path.Combine(guardFolder,"wrong-run"),new(){DryRun=false}))),
            "另一次运行不能覆盖本件真实加工状态");
        Check(live.HoleTimings.Count==18 && live.HoleTimings.Values.All(h=>h.FinishedAt>=h.StartedAt) && live.FinishedAt>=live.StartedAt &&
            live.LaserOffConfirmed==true && live.NcExitConfirmed,"成功记录含18孔时间、整件起止及清理确认");
        Check(failed.StoppedDuring=="HoleStarted:2" && failed.CurrentHoleId==2 && failed.Holes[2]==ProductionHolePhase.Uncertain &&
            ProductionJobArchive.Summary(failed).Contains("待核对，禁止自动重打"),"异常结果明确停止孔、已完成孔与待核对孔");
        var customerRun=new ProductionRunRecord {DryRun=false,RequestedExposureMs=60000};
        var customerCell=new OfflineProductionDemo(Path.Combine(folder,"customer-60s-adapter"));
        await new ContinuousProduction(customerCell).RunAsync(OfflineProductionDemo.Route(),customerRun);
        Check(customerRun.Completed && customerCell.EmissionCalls==18 && customerRun.RequestedExposureMs==60000,
            "客户60秒请求依次传入18孔调度，模拟不等待或触发真实激光");
        var legacyProfile=JsonSerializer.Deserialize<ProductionStationProfile>("""{"Envelope":{"MinX":-20,"MaxX":20,"MinY":-20,"MaxY":20,"Z":-1,"C":0},"R220To229Reserved":true}""")!;
        legacyProfile.ValidateFor(true);
        Check(await Fails(()=>Task.Run(()=>legacyProfile.ValidateFor(false))),"旧无出光配置不会隐式转为整件加工许可");
        legacyProfile.AllowMachining=true;legacyProfile.ValidateFor(false);
        Check(legacyProfile.AllowMachining,"明确本次加工配置允许复用已核定边界执行18孔");
        string lockFolder=Path.Combine(folder,"temporary-file-lock");
        var lockRecord=new ProductionRunRecord {Phase="Created"};ProductionJobArchive.SaveRunRecord(lockFolder,lockRecord);
        var fileLock=new FileStream(Path.Combine(lockFolder,"run.json"),FileMode.Open,FileAccess.Read,FileShare.Read);
        lockRecord.Phase="Starting";
        var saveAfterLock=Task.Run(()=>ProductionJobArchive.SaveRunRecord(lockFolder,lockRecord));
        await Task.Delay(90);fileLock.Dispose();await saveAfterLock;
        Check(JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(Path.Combine(lockFolder,"run.json")))!.Phase=="Starting",
            "Windows记录短暂被占用时重试原子写盘，不重试出光或丢失进度");
        var interrupted=new ProductionRunRecord {DryRun=false,Phase="HoleStarted:2",CurrentHoleId=2,LaserOffConfirmed=true,
            Holes=new() {[1]=ProductionHolePhase.Completed,[2]=ProductionHolePhase.Started}};
        var recovered=ProductionJobArchive.ReviewAfterRestart(interrupted);
        Check(recovered.Phase=="Interrupted" && recovered.Holes[1]==ProductionHolePhase.Completed && recovered.Holes[2]==ProductionHolePhase.Uncertain &&
            recovered.LaserOffConfirmed is null && interrupted.Phase=="HoleStarted:2","重启恢复中断件为待核对，不沿用旧关光确认或改写原始记录");
        var replay=JsonSerializer.Deserialize<ProductionRunRecord>(File.ReadAllText(Path.Combine(folder,"emission-uncertain","run.json")))!;
        Check(replay.Holes[2]==ProductionHolePhase.Uncertain && !replay.Completed,"失败孔状态可从磁盘恢复用于核对");
        Console.WriteLine($"Continuous production: {passed} passed; simulations only; output={folder}");return 0;
    }
    private sealed class Machine:ICncService,ISinumerikVarAccess
    {
        public string Endpoint=>"simulation";public bool IsConnected=>true;public double X,Y;public int FailWrite=-1;public int Writes;
        public Dictionary<int,double> R=new(){[220]=1,[221]=0,[222]=0,[223]=0,[224]=0,[225]=789,[226]=0};
        public Task<object?> ReadVarAsync(string path,CancellationToken ct=default)=>Task.FromResult<object?>(R[int.Parse(path.Split(',')[1].Trim().TrimEnd(']'))]);
        public Task<bool> WriteRAsync(int r,double value,CancellationToken ct=default)
        {Writes++;if(r==FailWrite)return Task.FromResult(false);R[r]=value;if(r==221){X=R[223];Y=R[224];R[222]=value;R[220]=3;}if(r==226 && value==9)R[220]=0;return Task.FromResult(true);}
        public Task<MachineStatus> GetStatusAsync(CancellationToken ct=default)=>Task.FromResult(new MachineStatus(true,"AUTO","HM_MASTER",X,Y,-1,"OK"));
        public Task<bool> ConnectAsync(string endpoint,CancellationToken ct=default)=>Task.FromResult(true);
        public Task DisconnectAsync()=>Task.CompletedTask;
        public Task<bool> WriteVarAsync(string nodePath,object value,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<bool> UploadProgramAsync(string name,string content,CancellationToken ct=default)=>throw new NotSupportedException();
        public IDisposable SubscribePosition(Action<AxisPosition> onChange,int intervalMs=200)=>throw new NotSupportedException();
        public void Dispose(){}
    }
}
