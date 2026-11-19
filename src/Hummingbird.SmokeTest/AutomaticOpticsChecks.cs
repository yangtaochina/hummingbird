using Hummingbird.Core;

internal static class AutomaticOpticsChecks
{
    public static async Task<int> Run()
    {
        int count=0;
        void Check(bool value,string name){if(!value)throw new Exception(name);count++;Console.WriteLine("PASS "+name);}
        async Task<bool> Fails(Func<Task> action){try{await action();return false;}catch{return true;}}
        OpticalCalibrationProfile Profile()=>new(){Envelope=new(-160.4,-134,-209.6,-181.7,-201.87324,254.8245),R220To229Reserved=true};
        var origin=new CaptureWaypoint("Origin",RingFrameRole.RingArc,-147,-196,-201.87324,254.8245);
        var profile=Profile();var points=profile.Targets(origin);
        Check(points.Count==5 && points[0]==points[1] && points[0]==points[4],"采样顺序包含原位、重复、独立XY及返回原位");
        Check(points[2].Y==origin.Y && points[3].X==origin.X && points.All(p=>double.Hypot(p.X-origin.X,p.Y-origin.Y)<=.100001),"局部位移不超过0.10mm，两个方向独立");
        var edge=profile.Targets(origin with{X=-134,Y=-181.7});
        Check(edge[2].X<edge[0].X && edge[3].Y<edge[0].Y && edge.All(p=>profile.Envelope!.Contains(p.X,p.Y)),"靠边界时使用范围内的负向位移，不扩展核定包络");
        foreach(var bad in new[]{origin with{X=-170},origin with{Z=-192},origin with{C=0},origin with{X=double.NaN}})
            Check(await Fails(()=>Task.Run(()=>profile.Targets(bad))),"越界或无效起点／Z/C拒绝："+bad.Name+bad.X+bad.Z+bad.C);
        profile=Profile();profile.StepMm=.2;Check(await Fails(()=>Task.Run(profile.Validate)),"拒绝超出0.10mm的步长");
        profile=Profile();profile.R220To229Reserved=false;Check(await Fails(()=>Task.Run(profile.Validate)),"未专用R区不生成校准动作");
        async Task<(OpticalSamplingRecord Record,int Moves,int Captures,int Calculations,int Ends,int Offs,bool Failed)> Execute(string fault="")
        {
            int moves=0,captures=0,calculations=0,ends=0,offs=0;var record=new OpticalSamplingRecord();
            using var stop=new CancellationTokenSource();if(fault=="preCancel")stop.Cancel();
            var plan=Profile();var intents=new List<string>();
            var runner=new AutomaticOpticalSampling(plan,
                _=>fault=="wrongNc"?Task.FromException<CaptureWaypoint>(new IOException("wrong NC")):Task.FromResult(origin),
                _=>{offs++;if(fault=="off")throw new IOException("not off");return Task.CompletedTask;},
                (_,_,ct)=>
                {
                    moves++;Check(intents.Last()=="MoveIntent:"+(moves+1),"每次运动前已保存本次意图");
                    if(fault=="move")throw new IOException("unknown motion");if(fault=="cancelMove"){stop.Cancel();ct.ThrowIfCancellationRequested();}
                    return Task.CompletedTask;
                },
                (i,_,_,_)=>{captures++;if(fault=="capture" || fault=="texture" && i==1)throw new IOException("camera or stationary texture failed");return Task.FromResult("capture"+i);},
                (paths,_)=>{calculations++;if(fault=="calculation")throw new InvalidDataException("ambiguous texture");Check(paths.Count==5,"计算包含返回原位实拍，不沿用旧照片");return Task.CompletedTask;},
                ()=>{ends++;if(fault=="exit")throw new IOException("exit unknown");return Task.CompletedTask;});
            bool failed=await Fails(()=>runner.RunAsync(record,r=>{if(fault=="save")throw new IOException("disk full");intents.Add(r.Phase);return Task.CompletedTask;},stop.Token));
            return(record,moves,captures,calculations,ends,offs,failed);
        }
        var ok=await Execute();
        Check(!ok.Failed && ok.Record.Completed && ok.Record.NcExitConfirmed && ok.Moves==3 && ok.Captures==5 && ok.Calculations==1 && ok.Ends==1,"一次完整采样计算并确认退出；正常路径仅三次XY请求");
        Check(ok.Offs>=10 && ok.Record.FinishedAt is not null,"采样和每次运动前持续核对关光，结束持久保存");
        foreach(string fault in new[]{"save","wrongNc","off","preCancel"})
        {
            var failed=await Execute(fault);
            Check(failed.Failed && failed.Moves==0 && failed.Captures==0 && !failed.Record.Completed,"前置失败不运动或采图："+fault);
        }
        var camera=await Execute("capture");Check(camera.Failed && camera.Moves==0 && camera.Ends==1 && !camera.Record.Completed,"首图失败退出自己的NC，不开始位移");
        var textureFailure=await Execute("texture");Check(textureFailure.Failed && textureFailure.Moves==0 && textureFailure.Captures==2 && textureFailure.Ends==1,
            "原位重复图纹理失败时尚无XY请求，清理后暂停而不继续采样");
        var movement=await Execute("move");Check(movement.Failed && movement.Moves==1 && movement.Captures==2 && movement.Ends==1,"运动应答不明不重发、不继续到下一点或盲目返回");
        var canceled=await Execute("cancelMove");Check(canceled.Failed && canceled.Moves==1 && canceled.Captures==2 && canceled.Ends==1,"中途取消停止后续采图并清理，不重走路径");
        var calculation=await Execute("calculation");Check(calculation.Failed && calculation.Ends==1 && !calculation.Record.Completed,"图像计算拒绝仍清理NC，不标记校准成功");
        var exit=await Execute("exit");Check(exit.Failed && exit.Ends==1 && !exit.Record.NcExitConfirmed && !exit.Record.Completed,"NC退出未确认时不完成，不自动重复退出请求");
        var previous=new OpticalSamplingRecord {Phase="Failed",FinishedAt=DateTimeOffset.Now};int invoked=0;
        var replay=new AutomaticOpticalSampling(Profile(),_=>{invoked++;return Task.FromResult(origin);},_=>Task.CompletedTask,(_,_,_)=>Task.CompletedTask,
            (_,_,_,_)=>Task.FromResult("unused"),(_,_)=>Task.CompletedTask,()=>Task.CompletedTask);
        Check(await Fails(()=>replay.RunAsync(previous,_=>Task.CompletedTask)) && invoked==0,"失败记录不能恢复为新一轮校准或重打依据");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var concurrent=new AutomaticOpticalSampling(Profile(),_=>Task.FromResult(origin),_=>Task.CompletedTask,(_,_,_)=>Task.CompletedTask,
            async(i,_,_,_)=>{if(i==0){entered.SetResult();await release.Task;}return "capture"+i;},(_,_)=>Task.CompletedTask,()=>Task.CompletedTask);
        var first=concurrent.RunAsync(new(),_=>Task.CompletedTask);await entered.Task;
        bool duplicate=await Fails(()=>concurrent.RunAsync(new(),_=>Task.CompletedTask));release.SetResult();await first;
        Check(duplicate,"同一调度器拒绝并行校准");
        Console.WriteLine($"Automatic optics: {count} passed; simulated callbacks only; no hardware or laser access.");return 0;
    }
}
