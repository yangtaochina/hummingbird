using System.Globalization;

namespace Hummingbird.Core;

/// <summary>连续会话适配器；不启动NC。完成序号和真实XYZ判稳同时满足后返回。</summary>
public sealed class ContinuousNcSession(ICncService cnc,ISinumerikVarAccess vars,NcTravelEnvelope bounds,int token,
    Func<CancellationToken,Task<double>> readC)
{
    private int sequence;
    private bool uncertain;
    private readonly SemaphoreSlim gate=new(1,1);
    private async Task<double> Read(int r,CancellationToken ct)
    {
        var value=await vars.ReadVarAsync($"/Channel/Parameter/R[u1, {r}]",ct);
        if(value is null)throw new IOException("主控NC参数读回为空。");
        double number=Convert.ToDouble(value,CultureInfo.InvariantCulture);
        if(!double.IsFinite(number))throw new IOException("主控NC参数读回无效。");
        return number;
    }
    private async Task Write(int r,double value,CancellationToken ct)
    {if(!await cnc.WriteRAsync(r,value,ct))throw new IOException($"主控NC R{r}写入失败。");}
    private async Task<MachineStatus> Position(CancellationToken ct)
    {
        var p=await cnc.GetStatusAsync(ct);double c=await readC(ct);
        if(!p.Connected || p.Message!="OK" || !bounds.Contains(p.PosX,p.PosY) || !double.IsFinite(p.PosZ) || Math.Abs(p.PosZ-bounds.Z)>.010 ||
            !double.IsFinite(c) || Math.Abs(Math.IEEERemainder(c-bounds.C,360))>.005)
            throw new IOException("主控NC位置、固定Z/C或通信状态不符。");
        return p;
    }
    public async Task<MachineStatus> VerifyReadyAsync(CancellationToken ct=default)
    {
        bounds.Validate();
        var actual=new Dictionary<int,double>();
        foreach(int r in new[]{220,221,222,225,226})actual[r]=await Read(r,ct);
        if(uncertain || actual[225]!=token || actual[220] is not (1 or 3) || actual[221]!=sequence || actual[222]!=sequence || actual[226]!=0)
            throw new InvalidOperationException($"主控NC未就绪：R220={actual[220]}，请求/完成序号={actual[221]}/{actual[222]}（期望{sequence}），会话={actual[225]}（期望{token}），退出请求={actual[226]}。请在面板从头启动本次主控，先处理面板报警；不要手动改R值跳过检查。");
        return await Position(ct);
    }
    public async Task MoveAndSettleAsync(double x,double y,CancellationToken ct)
    {
        bounds.Validate();if(!bounds.Contains(x,y))throw new InvalidOperationException("目标超出核定XY运动边界。");
        if(!await gate.WaitAsync(0,ct))throw new InvalidOperationException("主控NC已有指令进行中。");
        try
        {
            if(uncertain)throw new InvalidOperationException("上条运动执行情况不明，禁止重发。");
            await VerifyReadyAsync(ct);
            await Write(223,x,ct);await Write(224,y,ct);
            if(Math.Abs(await Read(223,ct)-x)>1e-7 || Math.Abs(await Read(224,ct)-y)>1e-7 || await Read(225,ct)!=token)
                throw new IOException("目标读回不一致，未提交运动。");
            ct.ThrowIfCancellationRequested();uncertain=true;int next=sequence+1;await Write(221,next,ct);
            var deadline=DateTimeOffset.UtcNow.AddSeconds(120);var stable=new CapturePositionGate();
            var waypoint=new CaptureWaypoint("Continuous",RingFrameRole.RingArc,x,y,bounds.Z,bounds.C);
            while(DateTimeOffset.UtcNow<deadline)
            {
                ct.ThrowIfCancellationRequested();
                if(await Read(225,ct)!=token || await Read(220,ct)==9)throw new IOException("主控NC错误或会话变化。");
                var p=await Position(ct);
                if(await Read(222,ct)==next && await Read(220,ct)==3 && stable.Observe(waypoint,p.PosX,p.PosY,p.PosZ,await readC(ct),DateTimeOffset.UtcNow))
                {sequence=next;uncertain=false;return;}
                await Task.Delay(100,ct);
            }
            throw new TimeoutException("主控NC到位确认超时。");
        }
        finally{gate.Release();}
    }
    public async Task EndAsync()
    {
        if(await Read(225,CancellationToken.None)!=token)throw new IOException("NC会话已改变，未向另一会话写退出。");
        if(await Read(220,CancellationToken.None)==0)return;
        await Write(226,9,CancellationToken.None);
        var deadline=DateTimeOffset.UtcNow.AddSeconds(5);
        while(DateTimeOffset.UtcNow<deadline)
        {
            if(await Read(225,CancellationToken.None)!=token)throw new IOException("NC退出时会话改变。");
            double state=await Read(220,CancellationToken.None);
            if(state==0)return;
            if(state==9)throw new IOException("主控NC故障暂停，请在面板停止或复位；未确认NC正常退出。");
            await Task.Delay(100);
        }
        throw new TimeoutException("已请求主控退出，但未读回R220=0；请核对面板状态。");
    }
}
