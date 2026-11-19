using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hummingbird.Core;

/// <summary>同一机床参考下的工位回位，不是回参考点。只在核定的入口区域内使用；不控制第二根Z轴。</summary>
public sealed class StationReturnProfile
{
    public int Version { get; set; } = 1;
    public NcTravelEnvelope? WorkEnvelope { get; set; }
    public double TargetX { get; set; }
    public double TargetY { get; set; }
    public double? ClearanceZ { get; set; }
    public double? EntryMinZ { get; set; }
    public double? EntryMaxZ { get; set; }
    public bool R220To229Reserved { get; set; }
    public bool ReturnPathApproved { get; set; }
    public string SecondaryAxisCondition { get; set; } = "";
    public double LinearFeedMmPerMin { get; set; } = 30;
    public double RotaryFeedDegPerMin { get; set; } = 60;

    public void Validate()
    {
        if (Version != 1 || WorkEnvelope is null) throw new InvalidDataException("工位恢复配置版本或工作边界无效。");
        WorkEnvelope.Validate();
        if (!R220To229Reserved || !ReturnPathApproved || string.IsNullOrWhiteSpace(SecondaryAxisCondition))
            throw new InvalidDataException("尚未确认回位路径、专用R区或MZ2保持条件。");
        if (ClearanceZ is not { } z || EntryMinZ is not { } min || EntryMaxZ is not { } max ||
            new[] { z, min, max, LinearFeedMmPerMin, RotaryFeedDegPerMin }.Any(v => !double.IsFinite(v)) ||
            min >= max || z < min || z > max || WorkEnvelope.Z < min || WorkEnvelope.Z > max ||
            !WorkEnvelope.Contains(TargetX, TargetY) || WorkEnvelope.C < 0 || WorkEnvelope.C >= 360 ||
            LinearFeedMmPerMin <= 0 || LinearFeedMmPerMin > 100 || RotaryFeedDegPerMin <= 0 || RotaryFeedDegPerMin > 60)
            throw new InvalidDataException("回位目标、安全高度、入口高度范围或速度无效。");
    }

    // NC与界面以整个配置生成同一编号；改配置但没有重导入NC时不能误提交运动。
    public long Key => BitConverter.ToUInt32(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(this,
        new JsonSerializerOptions { IgnoreReadOnlyProperties = true }))), 0) % 2000000000U + 1L;

    public void CheckEntry(MachineStatus p)
    {
        Validate();
        if (!p.Connected || p.Message != "OK" || !WorkEnvelope!.Contains(p.PosX, p.PosY) ||
            !double.IsFinite(p.PosZ) || p.PosZ < EntryMinZ!.Value - .010 || p.PosZ > EntryMaxZ!.Value + .010)
            throw new InvalidOperationException("当前位置不在已核定的回位入口区域。先由机床人员送入该区域；程序不从任意加工位置盲目回位。");
    }
}

public static class StationReturnNcProgram
{
    public static string Generate(StationReturnProfile p)
    {
        p.Validate(); var b = p.WorkEnvelope!;
        // C=DC使用最短转角。整个C转动及XY运动只在核定的退让高度执行。
        // R220:10等待/11退让/12转C/13移XY/14恢复Z/15完成/18取消/19故障。
        string guards = FormattableString.Invariant($"""
            IF $AA_IM[X]<({b.MinX:F6}) GOTOF HR_ERROR
            IF $AA_IM[X]>({b.MaxX:F6}) GOTOF HR_ERROR
            IF $AA_IM[Y]<({b.MinY:F6}) GOTOF HR_ERROR
            IF $AA_IM[Y]>({b.MaxY:F6}) GOTOF HR_ERROR
            IF $AA_IM[Z]<({p.EntryMinZ!.Value - .010:F6}) GOTOF HR_ERROR
            IF $AA_IM[Z]>({p.EntryMaxZ!.Value + .010:F6}) GOTOF HR_ERROR
            """);
        string cGuard = FormattableString.Invariant($"""
            HR_DC=ABS($AA_IM[C]-({b.C:F6}))
            WHILE HR_DC>=360
            HR_DC=HR_DC-360
            ENDWHILE
            IF HR_DC>180
            HR_DC=360-HR_DC
            ENDIF
            IF HR_DC>0.005 GOTOF HR_ERROR
            """);
        return FormattableString.Invariant($"""
            ; HUMMINGBIRD STATION RETURN - NO LASER COMMANDS, NO Z2 MOTION
            ; R220-R229 EXCLUSIVE; STOP/RESET OTHER NC BEFORE PANEL START
            ; PANEL START ONLY ARMS; SOFTWARE MUST CONFIRM LASER OFF AND REQUEST
            ; C USES SHORTEST PATH AT APPROVED CLEARANCE HEIGHT
            DEF INT HR_REQ
            DEF INT HR_WAIT
            DEF REAL HR_DC
            G90 G71 G94 G60
            STOPRE
            R221=0 R222=0 R226=0 R229=0
            R225={p.Key}
            R220=10
            HR_WAIT=0
            HR_WAITING:
            STOPRE
            G4 F0.1
            R223=$AA_IM[X] R224=$AA_IM[Y]
            R227=$AA_IM[Z] R228=$AA_IM[C]
            HR_WAIT=HR_WAIT+1
            IF R226==9 GOTOF HR_STOP
            IF HR_WAIT>6000 GOTOF HR_ERROR
            IF R221==0 GOTOB HR_WAITING
            IF R221<>1 GOTOF HR_ERROR
            IF R229<=0 GOTOF HR_ERROR
            HR_REQ=R229
            {guards}
            R220=11
            SUPA G1 Z=({p.ClearanceZ!.Value:F6}) F{p.LinearFeedMmPerMin:F3}
            STOPRE
            IF ABS($AA_IM[Z]-({p.ClearanceZ!.Value:F6}))>0.010 GOTOF HR_ERROR
            IF R226==9 GOTOF HR_STOP
            R220=12
            SUPA G1 C=DC({b.C:F6}) F{p.RotaryFeedDegPerMin:F3}
            STOPRE
            {cGuard}
            IF ABS($AA_IM[Z]-({p.ClearanceZ!.Value:F6}))>0.010 GOTOF HR_ERROR
            IF R226==9 GOTOF HR_STOP
            R220=13
            SUPA G1 X=({p.TargetX:F6}) Y=({p.TargetY:F6}) F{p.LinearFeedMmPerMin:F3}
            STOPRE
            IF ABS($AA_IM[X]-({p.TargetX:F6}))>0.005 GOTOF HR_ERROR
            IF ABS($AA_IM[Y]-({p.TargetY:F6}))>0.005 GOTOF HR_ERROR
            IF ABS($AA_IM[Z]-({p.ClearanceZ!.Value:F6}))>0.010 GOTOF HR_ERROR
            IF R226==9 GOTOF HR_STOP
            R220=14
            SUPA G1 Z=({b.Z:F6}) F{p.LinearFeedMmPerMin:F3}
            STOPRE
            IF ABS($AA_IM[X]-({p.TargetX:F6}))>0.005 GOTOF HR_ERROR
            IF ABS($AA_IM[Y]-({p.TargetY:F6}))>0.005 GOTOF HR_ERROR
            IF ABS($AA_IM[Z]-({b.Z:F6}))>0.010 GOTOF HR_ERROR
            {cGuard}
            IF R226==9 GOTOF HR_STOP
            R223=$AA_IM[X] R224=$AA_IM[Y]
            R227=$AA_IM[Z] R228=$AA_IM[C]
            R222=HR_REQ
            R220=15
            M30
            HR_STOP:
            R220=18
            M30
            HR_ERROR:
            R220=19
            M0
            M30
            """).ReplaceLineEndings("\r\n") + "\r\n";
    }
}

public sealed record StationReturnPose(double X, double Y, double Z, double C);

public sealed class StationReturnRecord
{
    public Guid RunId { get; set; } = Guid.NewGuid();
    public DateTimeOffset StartedAt { get; set; } = DateTimeOffset.Now;
    public DateTimeOffset? FinishedAt { get; set; }
    public StationReturnProfile? Profile { get; set; }
    public string NcProgramSha256 { get; set; } = "";
    public int RequestId { get; set; }
    public bool MotionRequestAttempted { get; set; }
    public bool StopRequested { get; set; }
    public bool Completed { get; set; }
    public StationReturnPose? Before { get; set; }
    public StationReturnPose? After { get; set; }
    public string CSource { get; set; } = "NC $AA_IM[C]完成快照；不是持续OPC UA角度反馈";
    public string? Error { get; set; }
    public string? CleanupError { get; set; }
}

/// <summary>只对已由面板启动的HM_RETURN提交一次请求；失败不重发，不启动其他NC，不出光。</summary>
public sealed class StationReturnSession(ICncService cnc, ISinumerikVarAccess vars, StationReturnProfile profile,
    Func<CancellationToken, Task> ensureOff, Action<string>? progress = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private async Task<double> Read(int r, CancellationToken ct)
    {
        var v = await vars.ReadVarAsync($"/Channel/Parameter/R[u1, {r}]", ct);
        if (v is null) throw new IOException($"工位回位R{r}读回为空。");
        double n = Convert.ToDouble(v, CultureInfo.InvariantCulture);
        if (!double.IsFinite(n)) throw new IOException($"工位回位R{r}读回无效。");
        return n;
    }
    private async Task Write(int r, double n, CancellationToken ct)
    { if (!await cnc.WriteRAsync(r, n, ct)) throw new IOException($"工位回位R{r}写入失败。"); }

    public async Task RunAsync(StationReturnRecord record, Func<StationReturnRecord, Task> persist, CancellationToken ct = default)
    {
        if (!await gate.WaitAsync(0, ct)) throw new InvalidOperationException("工位回位已经进行中。");
        bool ownsRecord = false;
        try
        {
            if (record.MotionRequestAttempted || record.FinishedAt is not null) throw new InvalidOperationException("回位记录不可重复执行。");
            profile.Validate(); ownsRecord = true; record.Profile = profile; record.RequestId = Random.Shared.Next(1, 100000000);
            record.NcProgramSha256 = Convert.ToHexString(SHA256.HashData(Encoding.ASCII.GetBytes(StationReturnNcProgram.Generate(profile))));
            await persist(record); // 目录不可写则不触碰运动请求。
            await ensureOff(ct);
            if (await Read(225, ct) != profile.Key || await Read(220, ct) != 10 || await Read(221, ct) != 0 ||
                await Read(222, ct) != 0 || await Read(226, ct) != 0)
                throw new InvalidOperationException("回位NC未等待本次请求。请先结束其他NC，再在面板从头启动此配置导出的HM_RETURN；不要修改R值绕过。");
            var first = await cnc.GetStatusAsync(ct); profile.CheckEntry(first);
            await Task.Delay(300, ct);
            var p = await cnc.GetStatusAsync(ct); profile.CheckEntry(p);
            if (Math.Abs(p.PosX - first.PosX) > .005 || Math.Abs(p.PosY - first.PosY) > .005 || Math.Abs(p.PosZ - first.PosZ) > .010)
                throw new InvalidOperationException("机床尚未停稳，未提交回位。");
            var startSnapshot = new StationReturnPose(await Read(223, ct), await Read(224, ct), await Read(227, ct), await Read(228, ct));
            if (Math.Abs(p.PosX - startSnapshot.X) > .005 || Math.Abs(p.PosY - startSnapshot.Y) > .005 || Math.Abs(p.PosZ - startSnapshot.Z) > .010)
                throw new IOException("回位NC与软件的实际XYZ读数不一致，请核对机床通道和MZ1轴对应；未提交运动。");
            record.Before = new(p.PosX, p.PosY, p.PosZ, startSnapshot.C);
            await Write(229, record.RequestId, ct);
            if (await Read(229, ct) != record.RequestId || await Read(225, ct) != profile.Key || await Read(220, ct) != 10)
                throw new IOException("回位请求或配置编号读回不符，未提交运动。");
            await ensureOff(ct); ct.ThrowIfCancellationRequested();
            record.MotionRequestAttempted = true; await persist(record);
            await Write(221, 1, ct); // 唯一提交点；从不自动重发。
            var deadline = DateTimeOffset.UtcNow.AddMinutes(10);
            int previous = -1; StationReturnPose? last = null;
            while (DateTimeOffset.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (await Read(225, ct) != profile.Key) throw new IOException("工位回位NC会话已变化。");
                await ensureOff(ct);
                double state = await Read(220, ct);
                if (state != previous)
                {
                    progress?.Invoke(state switch { 10 => "等待NC接收请求", 11 => "退到安全高度", 12 => "恢复C角度", 13 => "恢复XY采图起点", 14 => "恢复工作高度", 15 => "NC完成，正在核对实际位置", _ => "NC状态：" + state });
                    previous = (int)state;
                }
                if (state is 18 or 19) throw new IOException(state == 18 ? "回位NC已取消；未完成工位恢复。" : "回位NC故障暂停，请核对机床面板。");
                if (state is not (10 or 11 or 12 or 13 or 14 or 15)) throw new IOException("回位NC状态无效。");
                if (state == 15)
                {
                    if (await Read(222, ct) != record.RequestId) throw new IOException("回位完成编号不符，不能沿用旧完成记录。");
                    var actual = await cnc.GetStatusAsync(ct);
                    var nc = new StationReturnPose(await Read(223, ct), await Read(224, ct), await Read(227, ct), await Read(228, ct));
                    var b = profile.WorkEnvelope!;
                    if (!actual.Connected || actual.Message != "OK" || !AtTarget(actual.PosX, actual.PosY, actual.PosZ) ||
                        !AtTarget(nc.X, nc.Y, nc.Z) || Math.Abs(Math.IEEERemainder(nc.C - b.C, 360)) > .005)
                        throw new IOException("NC完成状态与实际XYZ或C完成快照不符，未确认回位。");
                    var current = new StationReturnPose(actual.PosX, actual.PosY, actual.PosZ, nc.C);
                    if (last is not null && Math.Abs(current.X - last.X) <= .005 && Math.Abs(current.Y - last.Y) <= .005 && Math.Abs(current.Z - last.Z) <= .010)
                    { record.After = current; record.Completed = true; break; }
                    last = current;
                }
                await Task.Delay(250, ct);
            }
            if (!record.Completed) throw new TimeoutException("工位回位超时；不自动重发，请在面板核对停止状态。");
            bool AtTarget(double x, double y, double z) => new[] { x, y, z }.All(double.IsFinite) &&
                Math.Abs(x - profile.TargetX) <= .005 && Math.Abs(y - profile.TargetY) <= .005 && Math.Abs(z - profile.WorkEnvelope!.Z) <= .010;
        }
        catch (Exception ex)
        {
            if (ownsRecord) record.Error = ex.Message;
            if (ownsRecord && record.MotionRequestAttempted)
            {
                try
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    if (await Read(225, cleanup.Token) == profile.Key && await Read(220, cleanup.Token) is >= 10 and <= 14)
                    { await Write(226, 9, cleanup.Token); record.StopRequested = true; }
                }
                catch (Exception stop) { record.CleanupError = stop.Message; }
            }
            throw;
        }
        finally
        {
            try { if (ownsRecord) { record.FinishedAt = DateTimeOffset.Now; await persist(record); } }
            finally { gate.Release(); }
        }
    }
}
