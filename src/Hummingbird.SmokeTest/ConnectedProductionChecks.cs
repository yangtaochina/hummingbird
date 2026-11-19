using Hummingbird.Core;
using Hummingbird.Vision;
using Hummingbird.Devices.LaserRtu;
using System.Text.Json;

internal static class ConnectedProductionChecks
{
    public static async Task<int> Run(string? output)
    {
        int passed=0;void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("[PASS] "+name);}
        async Task<bool> Fails(Func<Task> action){try{await action();return false;}catch{return true;}}
        string root=Path.GetFullPath(output??"src/Hummingbird.App/obj/connected-production-verified");Directory.CreateDirectory(root);
        var session=RingScanSession.Load(Path.GetFullPath("现场图片4/新环境_当前六帧定位记录.json"));
        var route=GuidedCapture.Route(session);
        Check(route.Count==6,"历史新定位前缀明确对应六步，原始记录不改写");
        var calibration=LaserSpotCalibration.Load(Path.GetFullPath("现场图片5/单点试打_20261113_121007417/激光落点_预览标定.json"));
        var template=HolePatternTemplate.LoadJson("src/templates/dial_18holes.json");
        double x=route[0].X,y=route[0].Y;int moves=0;bool ended=false;
        var writes=new List<(ushort Register,ushort Value)>();var trials=new List<SpotTrialRecord>();
        var regs=new Dictionary<ushort,ushort>{{1,0},{2,0},{7,0},{28,100},{29,1}};
        var controller=new LaserCalibrationController(r=>Task.FromResult(regs[r]),(r,v)=>{writes.Add((r,v));regs[r]=v;return Task.CompletedTask;},_=>{},()=>Task.FromResult((ushort)3950));
        controller.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy);
        var run=new ProductionRunRecord {DryRun=false,Synthetic=true,RequestedExposureMs=50};
        var cell=new ConnectedProductionCell(
            ct=>{ct.ThrowIfCancellationRequested();if(regs[7]!=0)throw new IOException("模拟激光未关。");return Task.CompletedTask;},
            (tx,ty,ct)=>{ct.ThrowIfCancellationRequested();x=tx;y=ty;moves++;return Task.CompletedTask;},
            (index,ct)=>
            {
                ct.ThrowIfCancellationRequested();var frame=session.Frames.Single(f=>GuidedCapture.Index(f)==index);RingCaptureArchive.VerifyImage(frame);
                if(frame.Role!=RingFrameRole.RingArc && new DatumDirectionSolver().Inspect(frame,session.Settings,session.DirectionOptions).Status!=DatumHoleInspectionStatus.Detected)
                    throw new InvalidDataException("历史参考特征未通过。");
                return Task.FromResult(new CaptureCorrection(0,0,true,frame.ImagePath));
            },
            ct=>
            {
                var result=new DialAlignmentSolver().Analyze(session,template);
                var compensated=LaserSpotCompensation.Preview(calibration,session.Settings,result.Ring.ReferenceZ,result.Ring.ReferenceC,result.Targets);
                File.WriteAllText(Path.Combine(root,"historical-positioning.json"),JsonSerializer.Serialize(new {HistoricalReplay=true,Result=compensated}));
                return Task.FromResult<IReadOnlyList<ProductionPoint>>(compensated.Targets.Select(p=>new ProductionPoint(p.Id,p.LaserCommandX,p.LaserCommandY)).ToArray());
            },
            async(ms,ct)=>
            {
                await controller.ReadStateAsync();int id=run.Holes.Single(p=>p.Value==ProductionHolePhase.Started).Key;
                var record=new SpotTrialRecord {DeviceMode="Sim",DryRun=false,RequestedExposureMs=ms,OperatorNotes="历史图片回放＋模拟寄存器，非实际加工"};
                var trial=new StationarySpotTrial(()=>Task.FromResult(new SpotTrialPosition(x,y,route[0].Z,route[0].C,AxisCoordinateSource.Simulator,null,DateTimeOffset.Now){XyzSource="Simulator"}),
                    (phase,pos)=>Task.FromResult(new SpotTrialImage("replayed_"+phase+".bmp","SIMULATED",1920,1200,DateTime.Now,pos)),
                    state=>{File.WriteAllText(Path.Combine(root,$"hole_{id}.json"),JsonSerializer.Serialize(state));return Task.CompletedTask;},controller,_=>{});
                await trial.RunAsync(record,ct);trials.Add(record);
            },
            record=>{File.WriteAllText(Path.Combine(root,"run.json"),JsonSerializer.Serialize(record));return Task.CompletedTask;},
            ()=>{ended=true;return Task.CompletedTask;});
        await new ContinuousProduction(cell).RunAsync(route,run);
        Check(run.Completed && run.CapturedSteps==6 && run.Holes.Count==18,"真实历史六帧经原视觉算法及落点补偿接入完整调度");
        Check(moves==24 && trials.Count==18 && ended,"同一联接器连续完成六处采图和18孔单孔工序");
        Check(writes.Count==36 && writes.Where((p,i)=>i%2==0).All(p=>p==(7,1)) && writes.Where((p,i)=>i%2==1).All(p=>p==(7,0)),"18孔复用原定时出光控制器，只写正确开关寄存器");
        Check(trials.All(t=>t.Completed && t.ShutdownConfirmed==true && t.BeforeImage is not null && t.AfterImage is not null),"每孔归档前后图证据并确认关光");
        Check(regs[28]==100 && regs[29]==1 && regs[1]==0,"流程不自行修改重频、Burst和控制模式");
        var invalid=new ProductionStationProfile();Check(await Fails(()=>Task.Run(invalid.Validate)),"未核定主控配置禁止运行");
        var latest=RingScanSession.Load("现场图片5/当前装夹_定位/ring_scan_实体圆边模型.json");int emissions=0;
        var rejectCell=new ConnectedProductionCell(ct=>Task.CompletedTask,(a,b,ct)=>Task.CompletedTask,
            (index,ct)=>
            {
                var frame=latest.Frames.Single(f=>GuidedCapture.Index(f)==index);var inspection=new DatumDirectionSolver().Inspect(frame,latest.Settings,latest.DirectionOptions);
                if(inspection.Status!=DatumHoleInspectionStatus.Detected)throw new InvalidDataException("当前实体圆边未通过。");
                return Task.FromResult(new CaptureCorrection(0,0,true,frame.ImagePath));
            },ct=>throw new Exception("不应进入定位"),(ms,ct)=>{emissions++;return Task.CompletedTask;},record=>Task.CompletedTask,()=>Task.CompletedTask);
        var rejected=new ProductionRunRecord {Synthetic=true};
        Check(await Fails(()=>new ContinuousProduction(rejectCell).RunAsync(GuidedCapture.Route(latest),rejected)) && rejected.Holes.Count==0 && emissions==0,"最新失败1点仍阻断后续工序，不因联接或历史成功而放行");
        Console.WriteLine($"Connected production: {passed} passed; historical replay, simulated axes/registers only.");return 0;
    }
}
