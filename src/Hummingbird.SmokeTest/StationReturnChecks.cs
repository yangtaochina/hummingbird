using System.Globalization;
using System.Text.Json;
using Hummingbird.Core;

internal static class StationReturnChecks
{
    public static async Task<int> Run()
    {
        int passed = 0;
        void Check(bool ok, string name) { if (!ok) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
        async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch { return true; } }
        StationReturnProfile Profile() => JsonSerializer.Deserialize<StationReturnProfile>(File.ReadAllText("src/Hummingbird.App/机床程序/工位恢复配置.json"))!;
        var p = Profile(); p.Validate();
        string nc = StationReturnNcProgram.Generate(p);
        Check(nc.IndexOf("Z=(-192.178030)", StringComparison.Ordinal) < nc.IndexOf("C=DC(254.824500)", StringComparison.Ordinal) &&
              nc.IndexOf("C=DC(254.824500)", StringComparison.Ordinal) < nc.IndexOf("X=(-135.027450) Y=(-188.575640)", StringComparison.Ordinal) &&
              nc.LastIndexOf("Z=(-201.873240)", StringComparison.Ordinal) > nc.IndexOf("X=(-135.027450) Y=(-188.575640)", StringComparison.Ordinal), "NC按退让、转C、移XY、工作Z顺序分段运动");
        Check(!nc.Contains("--") && !nc.Contains("Z2=") && !nc.Contains("M3\r") && !nc.Contains("M4\r"), "负操作数括起，无Z2或激光指令");
        Check(nc.IndexOf("IF R221==0 GOTOB", StringComparison.Ordinal) < nc.IndexOf("SUPA G1", StringComparison.Ordinal) && nc.Contains("R222=HR_REQ"), "面板启动只等待，软件请求后才运动，完成回传本次编号");
        Check(nc.Split("IF R226==9 GOTOF HR_STOP").Length >= 6, "每段运动间检查取消请求");
        var oldCulture = CultureInfo.CurrentCulture;
        try { CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE"); Check(nc == StationReturnNcProgram.Generate(p), "NC不受小数逗号区域设置影响"); }
        finally { CultureInfo.CurrentCulture = oldCulture; }
        var changed = Profile(); changed.ClearanceZ += .1;
        Check(p.Key != changed.Key && p.Key == Profile().Key && p.Key is > 0 and <= 2000000000 && changed.Key is > 0 and <= 2000000000,
            "配置编号稳定、适合NC整数范围，修改高度后改变");
        changed.ReturnPathApproved = false;
        Check(await Fails(() => Task.Run(() => StationReturnNcProgram.Generate(changed))), "未核定路径不生成可执行回位NC");
        changed = Profile(); changed.ClearanceZ = null;
        Check(await Fails(() => Task.Run(changed.Validate)), "未知安全高度不能默认回位");

        async Task<(StationReturnRecord Record, ReturnCnc Cnc, bool Failed, int Saved, int OffChecks)> Execute(
            Action<ReturnCnc>? setup = null, bool failSave = false, bool failOff = false, CancellationToken ct = default)
        {
            var machine = new ReturnCnc(p); setup?.Invoke(machine);
            var record = new StationReturnRecord(); int saved = 0, off = 0;
            var session = new StationReturnSession(machine, machine, p, token =>
            { token.ThrowIfCancellationRequested(); off++; if (failOff) throw new IOException("激光未关"); return Task.CompletedTask; });
            bool failed = await Fails(() => session.RunAsync(record, value =>
            { saved++; if (failSave) throw new IOException("磁盘不可写"); return Task.CompletedTask; }, ct));
            return (record, machine, failed, saved, off);
        }
        var ok = await Execute();
        Check(!ok.Failed && ok.Record.Completed && ok.Record.Before!.C == 90 && ok.Record.After!.C == p.WorkEnvelope!.C, "C不依赖人工输入，从90度恢复并核对NC完成快照");
        Check(ok.Cnc.Writes.Count(w => w.R == 221) == 1 && ok.OffChecks >= 4 && ok.Saved >= 3 && ok.Record.MotionRequestAttempted, "只提交一次运动，先持久记录并持续核对关光");
        Check(ok.Record.FinishedAt is not null && ok.Record.CSource.Contains("不是持续"), "归档结束时间及C快照来源，不冒充持续反馈");
        var offFailure = await Execute(failOff: true);
        Check(offFailure.Failed && !offFailure.Record.Completed && offFailure.Cnc.Writes.Count == 0, "未关光不提交任何R写入");
        var saveFailure = await Execute(failSave: true);
        Check(saveFailure.Failed && saveFailure.Cnc.Writes.Count == 0, "目录不可写时不提交运动");
        var wrongNc = await Execute(c => c.Regs[225]++);
        Check(wrongNc.Failed && wrongNc.Cnc.Writes.Count == 0, "其他配置或另一NC会话不可误动");
        var oldComplete = await Execute(c => c.Regs[220] = 15);
        Check(oldComplete.Failed && oldComplete.Cnc.Writes.Count == 0, "旧完成记录不能当作新回位");
        var outside = await Execute(c => c.Position = c.Position with { PosX = -170 });
        Check(outside.Failed && outside.Cnc.Writes.Count == 0, "未核定的其他加工位置不直接回位");
        var badZ = await Execute(c => c.Position = c.Position with { PosZ = -220 });
        Check(badZ.Failed && badZ.Cnc.Writes.Count == 0, "入口高度范围外不盲目退让");
        var wrongChannel = await Execute(c => c.Regs[227] = -238.4721);
        Check(wrongChannel.Failed && wrongChannel.Cnc.Writes.Count == 0, "NC与OPC读取的轴不对应时不提交运动");
        var wrongId = await Execute(c => c.OnSubmit = () => c.Regs[222]++);
        Check(wrongId.Failed && !wrongId.Record.Completed && wrongId.Cnc.Writes.Count(w => w.R == 221) == 1, "本次请求编号不符不确认且不重发");
        var wrongC = await Execute(c => c.OnSubmit = () => c.Regs[228] = 0);
        Check(wrongC.Failed && !wrongC.Record.Completed, "实际C完成快照错误时拦截完成");
        var fullTurn = await Execute(c => c.OnSubmit = () => c.Regs[228] += 360);
        Check(!fullTurn.Failed && fullTurn.Record.Completed, "C反馈相差整圈时按相同角度核对");
        var wrongPose = await Execute(c => c.OnSubmit = () => c.Position = c.Position with { PosZ = -192.17803 });
        Check(wrongPose.Failed && !wrongPose.Record.Completed, "NC声称完成但实际Z未恢复时不放行");
        var ncError = await Execute(c => c.OnSubmit = () => c.Regs[220] = 19);
        Check(ncError.Failed && !ncError.Record.Completed && ncError.Cnc.Writes.Count(w => w.R == 221) == 1, "NC故障不自动重跑");
        using var stop = new CancellationTokenSource();
        var canceled = await Execute(c => c.OnSubmit = () => { c.Regs[220] = 12; stop.Cancel(); }, ct: stop.Token);
        Check(canceled.Failed && canceled.Record.StopRequested && !canceled.Record.Completed && canceled.Cnc.Regs[226] == 9,
            "提交后取消仅请求段间停止，不宣称机床已停或回位完成");
        var switched = await Execute(c => c.OnSubmit = () => { c.Regs[225]++; c.Regs[220] = 12; });
        Check(switched.Failed && !switched.Record.StopRequested && !switched.Cnc.Writes.Any(w => w.R == 226), "会话改变后不向其他NC写停止");
        var uncertain = await Execute(c => c.ThrowAfterSubmit = true);
        Check(uncertain.Failed && !uncertain.Record.Completed && uncertain.Record.MotionRequestAttempted && uncertain.Cnc.Writes.Count(w => w.R == 221) == 1,
            "运动提交后通信中断保留不确定状态，绝不重发");
        string prior = JsonSerializer.Serialize(ok.Record); int priorWrites = ok.Cnc.Writes.Count;
        var repeat = new StationReturnSession(ok.Cnc, ok.Cnc, p, _ => Task.CompletedTask);
        Check(await Fails(() => repeat.RunAsync(ok.Record, _ => throw new Exception("不应覆盖旧记录"))) &&
            JsonSerializer.Serialize(ok.Record) == prior && ok.Cnc.Writes.Count == priorWrites, "同一回位记录拒绝重跑且不改写原成功状态");
        Console.WriteLine($"Station return: {passed} passed; simulated CNC/registers only."); return 0;
    }

    private sealed class ReturnCnc : ICncService, ISinumerikVarAccess
    {
        private readonly StationReturnProfile profile;
        public Dictionary<int, double> Regs { get; } = new();
        public List<(int R, double Value)> Writes { get; } = new();
        public MachineStatus Position { get; set; } = new(true, "AUTO", "HM_RETURN", -155.5332, -191.4308, -192.17803, "OK");
        public Action? OnSubmit { get; set; }
        public bool ThrowAfterSubmit { get; set; }
        public ReturnCnc(StationReturnProfile p)
        {
            profile = p;
            foreach (int r in Enumerable.Range(220, 10)) Regs[r] = 0;
            Regs[220] = 10; Regs[225] = p.Key; Regs[228] = 90;
            Regs[223] = Position.PosX; Regs[224] = Position.PosY; Regs[227] = Position.PosZ;
        }
        public string Endpoint => "sim://station-return";
        public bool IsConnected => true;
        public Task<bool> WriteRAsync(int r, double value, CancellationToken ct = default)
        {
            ct.ThrowIfCancellationRequested(); Writes.Add((r, value)); Regs[r] = value;
            if (r == 221)
            {
                Position = Position with { PosX = profile.TargetX, PosY = profile.TargetY, PosZ = profile.WorkEnvelope!.Z };
                Regs[223] = Position.PosX; Regs[224] = Position.PosY; Regs[227] = Position.PosZ; Regs[228] = profile.WorkEnvelope.C;
                Regs[222] = Regs[229]; Regs[220] = 15; OnSubmit?.Invoke();
                if (ThrowAfterSubmit) throw new IOException("提交后连接中断");
            }
            return Task.FromResult(true);
        }
        public Task<object?> ReadVarAsync(string node, CancellationToken ct = default)
        { ct.ThrowIfCancellationRequested(); int r = int.Parse(node[(node.LastIndexOf(',') + 1)..].Trim().TrimEnd(']')); return Task.FromResult<object?>(Regs[r]); }
        public Task<MachineStatus> GetStatusAsync(CancellationToken ct = default) { ct.ThrowIfCancellationRequested(); return Task.FromResult(Position); }
        public Task<bool> ConnectAsync(string endpoint, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DisconnectAsync() => Task.CompletedTask;
        public Task<bool> UploadProgramAsync(string name, string content, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<bool> WriteVarAsync(string path, object value, CancellationToken ct = default) => throw new NotSupportedException();
        public IDisposable SubscribePosition(Action<AxisPosition> onChange, int intervalMs = 200) => throw new NotSupportedException();
        public void Dispose() { }
    }
}
