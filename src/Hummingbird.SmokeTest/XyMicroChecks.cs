using Hummingbird.Core;
using Hummingbird.Vision;

internal static class XyMicroChecks
{
    public static async Task<int> Run()
    {
        int passed=0;
        void Check(bool ok,string name){if(!ok)throw new Exception(name);passed++;Console.WriteLine("[PASS] "+name);}
        bool Reject(Action action){try{action();return false;}catch{return true;}}
        async Task<bool> Fails(Func<Task> action){try{await action();return false;}catch{return true;}}
        var settings=new RingScanSettings {ImageWidth=800,ImageHeight=600,Jxx=600,Jxy=-50,Jyx=50,Jyy=600};
        var random=new Random(72);var pixels=new byte[800*600];random.NextBytes(pixels);
        for(int i=0;i<pixels.Length;i++)pixels[i]=(byte)(20+pixels[i]/2);
        var source=new CameraFrame(pixels,800,600,DateTime.Now);
        CameraFrame Shift(int dx,int dy)
        {
            var result=new byte[pixels.Length];
            for(int y=0;y<600;y++)for(int x=0;x<800;x++)if(x-dx>=0 && x-dx<800 && y-dy>=0 && y-dy<600)
                result[y*800+x]=(byte)(pixels[(y-dy)*800+x-dx]*1.1+10);
            return new(result,800,600,DateTime.Now);
        }
        var seed=new DatumCircleSeed(400,300,60,"test","test");
        var shift=XyImageRegistration.Measure(source,Shift(24,-12),settings,seed);
        Check(shift.PixelDx==24 && shift.PixelDy==-12 && shift.Correlation>.99,"曝光亮度变化下恢复已知XY像素位移");
        Check(Math.Abs(settings.Jxx*shift.DeltaX+settings.Jxy*shift.DeltaY+24)<1e-8 &&
            Math.Abs(settings.Jyx*shift.DeltaX+settings.Jyy*shift.DeltaY-12)<1e-8,"按完整XY响应矩阵求反向修正，不交换或猜测轴方向");
        var aligned=XyImageRegistration.Measure(source,Shift(0,0),settings,seed);
        Check(aligned.PixelDx==0 && aligned.PixelDy==0,"复拍对齐后不再要求移动");
        Check(Reject(()=>XyImageRegistration.Measure(source,Shift(180,0),settings,seed)),"超出可搜索小偏移范围拒绝运动");
        var blank=new CameraFrame(new byte[pixels.Length],800,600,DateTime.Now);
        Check(Reject(()=>XyImageRegistration.Measure(blank,blank,settings,seed)),"无纹理照片不产生运动建议");
        Check(Reject(()=>XyImageRegistration.Measure(source,source,settings,seed with {Px=10})),"边缘模板不足拒绝匹配");
        var anchor=new CaptureWaypoint("参考1点",RingFrameRole.Reference1,0,0,-1,10);
        var cnc=new Fake();var session=new XyMicroMoveSession(cnc,cnc,anchor,123,ct=>Task.FromResult(10.0));
        await session.MoveAsync(.05,-.03,CancellationToken.None);
        Check(cnc.X==.05 && cnc.Y==-.03 && cnc.Writes.Select(x=>x.R).SequenceEqual(new[]{203,204,206,201}),"先写目标并读回，最后提交序号，实际到位后才返回");
        await session.MoveAsync(.04,-.02,CancellationToken.None);
        Check(await Fails(()=>session.MoveAsync(.03,-.01,CancellationToken.None)),"最多两次微调，不能无限搜索");
        await session.FinishAsync();Check(cnc.R[206]==9,"结束时通知NC退出等待");
        var bad=new Fake();var guard=new XyMicroMoveSession(bad,bad,anchor,123);
        Check(await Fails(()=>guard.MoveAsync(.201,0,CancellationToken.None)) && bad.Writes.Count==0,"累计修正超过0.20mm不写任何R参数");
        bad.R[205]=456;Check(await Fails(()=>guard.MoveAsync(.05,0,CancellationToken.None)) && bad.Writes.Count==0,"会话不匹配不提交运动");
        bad.R[205]=123;bad.FailR=204;
        Check(await Fails(()=>guard.MoveAsync(.05,0,CancellationToken.None)) && !bad.Writes.Any(w=>w.R==201),"目标部分写入失败不提交序号");
        var across=new Fake {X=-.15};var acrossGuard=new XyMicroMoveSession(across,across,anchor,123);
        Check(await Fails(()=>acrossGuard.MoveAsync(.15,0,CancellationToken.None)) && across.Writes.Count==0,"即使起终点都在起点范围内，单次跨越超过0.20mm也拒绝");
        var changedC=new Fake();var cGuard=new XyMicroMoveSession(changedC,changedC,anchor,123,ct=>Task.FromResult(11.0));
        Check(await Fails(()=>cGuard.MoveAsync(.05,0,CancellationToken.None)) && changedC.Writes.Count==0,"C确认值变化拒绝微调");
        var cancelled=new Fake();var cancelGuard=new XyMicroMoveSession(cancelled,cancelled,anchor,123);
        using var cancellation=new CancellationTokenSource();cancellation.Cancel();
        Check(await Fails(()=>cancelGuard.MoveAsync(.05,0,cancellation.Token)) && cancelled.Writes.Count==0,"预先取消不提交运动");
        string program=XyMicroMoveSession.Program(anchor,123);
        Check(program.Contains("SUPA G1 X=R207 Y=R208 F6") && program.Contains("STOPRE") && program.Contains("HM_WAIT>600") &&
            !program.Contains(" Z=") && !program.Contains(" C=") && !program.Contains("M3\r") && !program.Contains("M62"),"NC只执行有限XY修正，含会话与超时保护，无Z/C运动和激光指令");
        Console.WriteLine($"XY micro checks: {passed} passed; no hardware accessed.");return 0;
    }

    private sealed class Fake : ICncService,ISinumerikVarAccess
    {
        public string Endpoint=>"fake";public bool IsConnected=>true;
        public double X,Y;public int FailR=-1;
        public Dictionary<int,double> R=new(){[200]=1,[201]=0,[202]=0,[203]=0,[204]=0,[205]=123,[206]=0};
        public List<(int R,double Value)> Writes=[];
        public Task<object?> ReadVarAsync(string path,CancellationToken ct=default)
        {int index=int.Parse(path.Split(',')[1].Trim().TrimEnd(']'));return Task.FromResult<object?>(R[index]);}
        public Task<bool> WriteRAsync(int r,double value,CancellationToken ct=default)
        {
            if(r==FailR)return Task.FromResult(false);Writes.Add((r,value));R[r]=value;
            if(r==201){X=R[203];Y=R[204];R[202]=value;R[200]=3;}
            return Task.FromResult(true);
        }
        public Task<MachineStatus> GetStatusAsync(CancellationToken ct=default)=>Task.FromResult(new MachineStatus(true,"AUTO","HM_MICRO",X,Y,-1,"OK"));
        public Task<bool> ConnectAsync(string endpoint,CancellationToken ct=default)=>Task.FromResult(true);
        public Task DisconnectAsync()=>Task.CompletedTask;
        public Task<bool> UploadProgramAsync(string name,string content,CancellationToken ct=default)=>throw new NotSupportedException();
        public Task<bool> WriteVarAsync(string nodePath,object value,CancellationToken ct=default)=>throw new NotSupportedException();
        public IDisposable SubscribePosition(Action<AxisPosition> onChange,int intervalMs=200)=>throw new NotSupportedException();
        public void Dispose(){}
    }
}
