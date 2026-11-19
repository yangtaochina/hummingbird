using System.Globalization;

namespace Hummingbird.Core;

public sealed record XyMicroAdjustment(double PixelDx, double PixelDy, double DeltaX, double DeltaY, double Correlation);

/// <summary>面板启动的单点XY微调程序与R200～R209握手。不启动NC，不控制激光。</summary>
public sealed class XyMicroMoveSession(ICncService cnc, ISinumerikVarAccess variables,
    CaptureWaypoint anchor, int token, Func<CancellationToken,Task<double>>? readC = null)
{
    public const double MaximumOffsetMm = .20;
    private int sequence;
    private bool uncertain;
    private static string Node(int r) => $"/Channel/Parameter/R[u1, {r}]";
    private async Task<double> Read(int r, CancellationToken ct)
    {
        var raw = await variables.ReadVarAsync(Node(r), ct);
        if (raw is null) throw new IOException("微调握手读回为空。");
        double value = Convert.ToDouble(raw, CultureInfo.InvariantCulture);
        if (!double.IsFinite(value)) throw new IOException("微调握手读回无效。");
        return value;
    }
    private async Task Write(int r, double value, CancellationToken ct)
    {
        if (!await cnc.WriteRAsync(r, value, ct)) throw new IOException($"微调R{r}写入失败。");
    }
    public async Task MoveAsync(double targetX, double targetY, CancellationToken ct)
    {
        if (uncertain || sequence >= 2) throw new InvalidOperationException("微调次数已达上限或上次执行未确认，请停止并重新核对。");
        if (!double.IsFinite(targetX) || !double.IsFinite(targetY) || double.Hypot(targetX-anchor.X,targetY-anchor.Y)>MaximumOffsetMm)
            throw new InvalidOperationException("微调目标超过初始位置0.20mm范围。");
        ct.ThrowIfCancellationRequested();
        if (await Read(205, ct) != token || await Read(202, ct) != sequence || await Read(201, ct) != sequence ||
            await Read(200, ct) is not (1 or 3))
            throw new InvalidOperationException("微调NC未就绪、会话不符或上条指令尚未确认；请在面板启动本次导出的程序。");
        var before = await cnc.GetStatusAsync(ct);
        double currentC = readC is null ? anchor.C : await readC(ct);
        if (!before.Connected || before.Message != "OK" || Math.Abs(before.PosZ-anchor.Z)>.010 ||
            double.Hypot(targetX-before.PosX,targetY-before.PosY)>MaximumOffsetMm ||
            !double.IsFinite(currentC) || Math.Abs(Math.IEEERemainder(currentC-anchor.C,360))>.005 ||
            double.Hypot(before.PosX-anchor.X,before.PosY-anchor.Y)>MaximumOffsetMm + .005)
            throw new InvalidOperationException("当前位置与微调基准不符。");
        // 目标先写入并读回，序号最后提交；部分写入失败不能触发运动。
        await Write(203, targetX, ct); await Write(204, targetY, ct); await Write(206, 1, ct);
        if (Math.Abs(await Read(203,ct)-targetX)>1e-7 || Math.Abs(await Read(204,ct)-targetY)>1e-7 || await Read(205,ct)!=token)
            throw new IOException("微调目标或会话读回不一致，未提交运动。");
        uncertain = true; int next = sequence + 1;
        await Write(201, next, ct);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        var gate = new CapturePositionGate();
        var target = anchor with { X=targetX,Y=targetY };
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (await Read(205,ct)!=token || await Read(200,ct)==9) throw new IOException("微调NC会话改变或报告错误。");
            var status = await cnc.GetStatusAsync(ct);
            currentC = readC is null ? anchor.C : await readC(ct);
            if (!status.Connected || status.Message!="OK" || Math.Abs(status.PosZ-anchor.Z)>.010)
                throw new IOException("微调期间坐标读取失败或Z发生变化。");
            if (await Read(202,ct)==next && await Read(200,ct)==3 &&
                gate.Observe(target,status.PosX,status.PosY,status.PosZ,currentC,DateTimeOffset.UtcNow))
            { sequence = next; uncertain = false; return; }
            await Task.Delay(100,ct);
        }
        throw new TimeoutException("微调执行或到位确认超时，未继续拍照。");
    }
    public async Task FinishAsync()
    {
        if (await Read(205,CancellationToken.None)==token) await Write(206,9,CancellationToken.None);
    }
    public static string Program(CaptureWaypoint anchor, int token)
    {
        if (token<=0 || new[]{anchor.X,anchor.Y,anchor.Z,anchor.C}.Any(v=>!double.IsFinite(v)))
            throw new InvalidDataException("微调NC基准无效。");
        // 与整件主控一致，括起负坐标操作数，避免SINUMERIK链式运算报警12180。
        return FormattableString.Invariant($"""
            ; HUMMINGBIRD XY MICRO ADJUST - NO LASER, NO Z/C MOVE
            ; RESERVE R200-R209; REVIEW BEFORE PANEL START
            DEF INT HM_WAIT
            G90 G71 G94 G60
            STOPRE
            IF ABS($AA_IM[X]-({anchor.X:F6}))>0.005 GOTOF HM_ERR
            IF ABS($AA_IM[Y]-({anchor.Y:F6}))>0.005 GOTOF HM_ERR
            IF ABS($AA_IM[Z]-({anchor.Z:F6}))>0.010 GOTOF HM_ERR
            R201=0 R202=0 R206=0
            R205={token}
            R200=1
            HM_WAIT=0
            HM_LOOP:
            STOPRE
            G4 F0.1
            HM_WAIT=HM_WAIT+1
            IF HM_WAIT>600 GOTOF HM_ERR
            IF R206==9 GOTOF HM_END
            IF R201==R202 GOTOB HM_LOOP
            IF R201<>R202+1 GOTOF HM_ERR
            IF R201>2 GOTOF HM_ERR
            IF R206<>1 GOTOF HM_ERR
            R207=R203 R208=R204 R209=R201
            IF ABS(R207-({anchor.X:F6}))>0.20 GOTOF HM_ERR
            IF ABS(R208-({anchor.Y:F6}))>0.20 GOTOF HM_ERR
            IF SQRT((R207-({anchor.X:F6}))*(R207-({anchor.X:F6}))+(R208-({anchor.Y:F6}))*(R208-({anchor.Y:F6})))>0.20 GOTOF HM_ERR
            IF ABS($AA_IM[Z]-({anchor.Z:F6}))>0.010 GOTOF HM_ERR
            R200=2
            IF SQRT((R207-$AA_IM[X])*(R207-$AA_IM[X])+(R208-$AA_IM[Y])*(R208-$AA_IM[Y]))>0.20 GOTOF HM_ERR
            SUPA G1 X=R207 Y=R208 F6
            STOPRE
            R202=R209
            R200=3
            HM_WAIT=0
            GOTOB HM_LOOP
            HM_ERR:
            R200=9
            M0
            HM_END:
            M30
            """).ReplaceLineEndings("\r\n")+"\r\n";
    }
}
