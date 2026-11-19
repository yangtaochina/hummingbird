using Hummingbird.Devices.LaserRtu;

internal static class LaserCalibrationChecks
{
    public static async Task<int> Run()
    {
        int failed = 0, passed = 0;
        void Check(bool ok, string name) { Console.WriteLine($"[{(ok ? "PASS" : "FAIL")}] {name}"); if (ok) passed++; else failed++; }
        async Task<bool> Fails(Func<Task> action) { try { await action(); return false; } catch { return true; } }
        var f = new Fake();
        var c = f.Controller();
        Check(await Fails(() => c.ExposeAsync(50)) && f.Writes.Count == 0, "未读回参数不允许出光");
        var state = await c.ReadStateAsync();
        Check(state.CanExpose && state.Power == 80 && state.FrequencyKhz == 50 && f.Writes.Count == 0, "只读核对8%/50kHz/3000fs，不写参数");
        await c.ExposeAsync(50);
        Check(f.Writes.SequenceEqual(new[] { (1543, 1), (1543, 0) }) && f.Regs[1543] == 0 && c.ShutdownConfirmed, "正常试打只写开关寄存器并确认关光");
        int n = f.Writes.Count;
        Check(await Fails(() => c.ExposeAsync(50)) && f.Writes.Count == n, "每次试打需要重新读回");
        await c.ReadStateAsync(); f.Regs[1542] = 90;
        Check(await Fails(() => c.ExposeAsync(50)) && f.Writes.Count == n, "读回后参数改变不出光");
        f.Regs[1542] = 80; f.Regs[1548] = 1; await c.ReadStateAsync();
        Check(await Fails(() => c.ExposeAsync(50)) && f.Writes.Count == n, "外部Gate模式不能走软件定时试打");
        f.Regs[1548] = 0; f.Regs[1541] = 1; await c.ReadStateAsync();
        Check(await Fails(() => c.ExposeAsync(50)) && f.Writes.Count == n, "故障状态不能出光");
        f.Regs[1541] = 0; await c.ReadStateAsync();
        using (var cancel = new CancellationTokenSource())
        {
            cancel.Cancel();
            Check(await Fails(() => c.ExposeAsync(50, cancel.Token)) && f.Writes.Count == n, "预取消不发ON");
        }
        await c.ReadStateAsync();
        var running = c.ExposeAsync(3000);
        Check(c.IsRunning && await Fails(() => c.ExposeAsync(50)), "禁止重复触发");
        c.RequestStop();
        Check(await Fails(() => running) && f.Regs[1543] == 0 && !c.IsRunning, "取消会关光并释放试打状态");
        var lost = new Fake { LoseOnAck = true }; var lc = lost.Controller(); await lc.ReadStateAsync();
        Check(await Fails(() => lc.ExposeAsync(50)) && lost.Writes.Contains((1543, 0)) && lost.Regs[1543] == 0, "开光应答丢失仍执行关光");
        var retry = new Fake { OffFailures = 2 }; var rc = retry.Controller(); await rc.ReadStateAsync(); await rc.ExposeAsync(50);
        Check(retry.Writes.Count(x => x == (1543, 0)) == 3 && retry.Regs[1543] == 0, "关光失败重试后读回确认");
        var off = new Fake { OffFailures = 3 }; var oc = off.Controller(); await oc.ReadStateAsync();
        Check(await Fails(() => oc.ExposeAsync(50)) && off.Regs[1543] == 1 && !oc.IsRunning && !oc.ShutdownConfirmed, "关光无法确认时必须报错而非报成功");
        var stuck = new Fake { StuckOn = true }; var sc = stuck.Controller(); await sc.ReadStateAsync();
        Check(await Fails(() => sc.ExposeAsync(50)), "OFF有应答但仍读回出光时报告失败");
        stuck.Regs[1543] = 0; await sc.ReadStateAsync();
        Check(sc.ShutdownConfirmed, "设备自身关光后，可通过只读核实恢复状态");
        var bad = new Fake(); bad.Regs[1575] = 0; var bc = bad.Controller();
        Check(await Fails(() => bc.ReadStateAsync()) && !bc.ProtocolConfirmed && bad.Writes.Count == 0, "不合理读回不能确认协议");
        Check(await Fails(() => c.ExposeAsync(600001)) && await Fails(() => c.ExposeAsync(49)), "时长边界检查");
        var legacy = new Fake(legacy: true); var legacyController = legacy.Controller();
        legacyController.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy);
        var legacyState = await legacyController.ReadStateAsync();
        Check(legacyState.CanExpose && legacyState.IsLegacy && legacyState.Power == 4125
            && legacyState.Fault == null && legacyState.PulseWidthFs == null && legacy.Writes.Count == 0,
            "复现现场旧协议：0/0/0、50kHz、Burst1、功率原始4125；未读故障及脉宽不伪造数值");
        Check(legacyState.Description.Contains("4125") && !legacyState.Description.Contains("41.25%"),
            "旧协议功率按原始值显示，不套用PDF比例");
        await legacyController.ExposeAsync(50);
        Check(legacy.Writes.SequenceEqual(new[] { (7, 1), (7, 0) }) && legacyController.ShutdownConfirmed
            && legacy.Regs[0x0172] == 4125 && legacy.Regs[0x001C] == 50 && legacy.Regs[0x001D] == 1,
            "旧协议试打仅写0x0007开关，不写PDF出光地址、不改功率/重频/Burst");
        await legacyController.ReadStateAsync();
        var legacyRunning = legacyController.ExposeAsync(3000);
        Check(await Fails(() => Task.Run(() => legacyController.SelectProtocol(LaserCalibrationProtocol.Pdf))),
            "旧协议出光期间不能切换协议");
        legacyController.RequestStop();
        Check(await Fails(() => legacyRunning) && legacy.Regs[7] == 0 && legacyController.ShutdownConfirmed,
            "旧协议取消通过0x0007关光并读回");
        await legacyController.ReadStateAsync(); legacy.Regs[0x0172] = 5000;
        n = legacy.Writes.Count;
        Check(await Fails(() => legacyController.ExposeAsync(50)) && legacy.Writes.Count == n,
            "旧协议功率变化后拒绝沿用旧读回出光");
        legacy.Regs[1] = 1; await legacyController.ReadStateAsync();
        Check(await Fails(() => legacyController.ExposeAsync(50)) && legacy.Writes.Count == n,
            "旧协议外部控制模式不出光");
        var legacyLost = new Fake(legacy: true) { LoseOnAck = true }; var llc = legacyLost.Controller();
        llc.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy); await llc.ReadStateAsync();
        Check(await Fails(() => llc.ExposeAsync(50)) && legacyLost.Writes.Contains((7, 0))
            && legacyLost.Regs[7] == 0, "旧协议ON应答丢失仍用正确地址清理关光");
        var legacyOff = new Fake(legacy: true) { OffFailures = 3 }; var loc = legacyOff.Controller();
        loc.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy); await loc.ReadStateAsync();
        Check(await Fails(() => loc.ExposeAsync(50)) && !loc.ShutdownConfirmed
            && legacyOff.Writes.Count(x => x == (7, 0)) == 3, "旧协议关光重试失败时不伪报成功");
        var preset=LaserProcessPreset.Recorded20261113;
        var pf=new Fake(legacy:true);var pc=pf.Controller();pc.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy);
        await pc.ReadStateAsync();
        var restored=await pc.RestorePresetAsync(preset,()=>
        {
            pf.Regs[0x0172]=preset.Settings.Power;pf.Regs[0x001C]=preset.Settings.FrequencyKhz;
            return Task.CompletedTask;
        });
        Check(preset.Matches(restored) && restored.CanExpose && pf.Writes.Count==0 && preset.RequestedExposureMs==2000,
            "已记录预设恢复后读回匹配，恢复不触发出光");
        Check(await Fails(()=>pc.RestorePresetAsync(preset,()=>{pf.Regs[0x001C]=200;return Task.CompletedTask;})) && pc.LastState is null,
            "恢复读回不一致不能启用出光");
        pf.Regs[7]=1;bool wrote=false;
        Check(await Fails(()=>pc.RestorePresetAsync(preset,()=>{wrote=true;return Task.CompletedTask;})) && !wrote,
            "出光状态下拒绝恢复参数");
        Check(preset.Settings.Power==3950 && preset.Settings.PulseWidthFs is null && preset.Source.Contains("6%"),
            "保留原始功率和备注，不伪造百分比换算或脉宽");
        var custom=LaserProcessPreset.CustomerConfigured(12000,2800);
        var customer=LaserProcessPreset.CustomerConfirmed20261116;
        Check(customer.RequestedExposureMs==60000 && customer.Settings.Power==4420 && customer.Source.Contains("12%(4420)") &&
            LaserProcessPreset.CustomerConfigured(60000,4420)==customer,"保存客户连续60秒与本机12%(4420)来源，不推算全功率曲线");
        Check(custom.RequestedExposureMs==12000 && custom.Settings.Power==2800 && custom.Settings.FrequencyKhz==100 &&
            custom.Settings.BurstCount==1 && !custom.Matches(preset.Settings),"客户时长和功率替代固定历史值，保留既有重频及模式");
        var zeroPower=new Fake(legacy:true);zeroPower.Regs[0x0172]=0;
        var zeroControl=zeroPower.Controller();zeroControl.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy);await zeroControl.ReadStateAsync();
        string? zeroError=null;
        try{await zeroControl.RestorePresetAsync(LaserProcessPreset.CustomerConfigured(1000,3950),()=>
            {zeroPower.Regs[0x001C]=100;return Task.CompletedTask;});}
        catch(IOException ex){zeroError=ex.Message;}
        Check(zeroError?.Contains("功率设定值：请求3950，读回0")==true && zeroControl.LastState is null && zeroPower.Writes.Count==0,
            "复现现场1秒/3950写后仍读0：准确指出功率差异并保持禁止出光");
        Check(zeroControl.LastPresetCheck is {AllWritesAcknowledged:true,Matched:false,After.Power:0} &&
            zeroControl.LastPresetCheck.Differences.Count==1 && zeroControl.LastPresetCheck.Requested.RequestedExposureMs==1000,
            "分别记录写入已应答与读回未匹配，不把应答当设定成功");
        Check(await Fails(()=>zeroControl.ExposeAsync(1000)) && zeroPower.Writes.Count==0,
            "参数应用失败后不能沿用旧状态直接触发出光");
        Check(await Fails(()=>zeroControl.RestorePresetAsync(preset,()=>throw new IOException("功率写入超时"))) &&
            zeroControl.LastPresetCheck is {AllWritesAcknowledged:false,After:null,Matched:false},
            "部分写入异常不伪造完整应答或实际读回");
        var emissionMismatch=preset.Differences(preset.Settings with {Emission=1});
        Check(emissionMismatch.Count==1 && emissionMismatch[0].Contains("出光状态(0=关光)：请求0，读回1"),
            "关光状态不一致有独立明确的诊断");
        Check(await Fails(()=>Task.Run(()=>LaserProcessPreset.CustomerConfigured(12000,0))) &&
            await Fails(()=>Task.Run(()=>LaserProcessPreset.CustomerConfigured(12000,10001))),"客户功率超范围拒绝，不悄悄钳位");
        var longFake=new Fake(legacy:true);var longControl=longFake.Controller();longControl.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy);
        await longControl.ReadStateAsync();var longTask=longControl.ExposeAsync(600000);longControl.RequestStop();
        Check(await Fails(()=>longTask) && longControl.ShutdownConfirmed && longFake.Regs[7]==0,"600秒请求可以启动且立即响应停止并确认关光");
        var pollFake=new Fake(legacy:true){FailEmissionReadDuringOn=true};var pollControl=pollFake.Controller();
        pollControl.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy);await pollControl.ReadStateAsync();
        Check(await Fails(()=>pollControl.ExposeAsync(1000)) && pollControl.ShutdownConfirmed && pollFake.Regs[7]==0,
            "长时出光期间通信读回失败会提前关光并报错");
        var positionFake=new Fake(legacy:true);var positionControl=positionFake.Controller();positionControl.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy);
        await positionControl.ReadStateAsync();
        Check(await Fails(()=>positionControl.ExposeAsync(1000,default,()=>throw new IOException("轴位置变化"))) && positionControl.ShutdownConfirmed,
            "持续出光期间轴位校验失败会提前关光");
        var completionFake=new Fake(legacy:true);var completionControl=completionFake.Controller();completionControl.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy);
        await completionControl.ReadStateAsync();double longWindow=await completionControl.ExposeAsync(3100);
        Check(longWindow>=3100 && completionControl.ShutdownConfirmed && completionFake.Writes.SequenceEqual(new[]{(7,1),(7,0)}),
            "超过旧3秒限制的请求连续执行完毕，监测不切成重复出光");
        var minuteFake=new Fake(legacy:true);minuteFake.Regs[0x0172]=4420;minuteFake.Regs[0x001C]=100;
        var minuteControl=minuteFake.Controller();minuteControl.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy);await minuteControl.ReadStateAsync();
        Console.WriteLine("[RUN] 模拟激光器连续60秒时序检查，期间只使用内存寄存器……");
        double minuteWindow=await minuteControl.ExposeAsync(60000);
        Check(minuteWindow>=60000 && minuteControl.ShutdownConfirmed && minuteFake.Writes.SequenceEqual(new[]{(7,1),(7,0)}),
            "客户60秒请求持续等待后只关光一次，非分段重复ON");
        Console.WriteLine($"Laser calibration: {passed} passed, {failed} failed; no hardware accessed.");
        return failed == 0 ? 0 : 1;
    }

    private sealed class Fake
    {
        public Dictionary<ushort, ushort> Regs { get; } = new() { [1541] = 0, [1542] = 80, [1543] = 0,
            [1547] = 2, [1548] = 0, [1504] = 3000, [1575] = 50, [1507] = 1 };
        public List<(int, int)> Writes { get; } = [];
        public bool LoseOnAck, StuckOn, FailEmissionReadDuringOn;
        public int OffFailures;
        public Fake(bool legacy = false)
        {
            if (!legacy) return;
            Regs.Clear();
            Regs[1] = 0; Regs[2] = 0; Regs[7] = 0;
            Regs[0x001C] = 50; Regs[0x001D] = 1; Regs[0x0172] = 4125;
        }
        public LaserCalibrationController Controller() => new(
            reg => FailEmissionReadDuringOn && reg==7 && Regs[7]==1 ? throw new IOException("read timeout during ON") : Task.FromResult(Regs[reg]),
            (reg, value) =>
            {
                Writes.Add((reg, value));
                if (value == 0 && OffFailures-- > 0) throw new IOException("OFF timeout");
                if (!(value == 0 && StuckOn)) Regs[reg] = value;
                if (value == 1 && LoseOnAck) throw new IOException("ON ack lost");
                return Task.CompletedTask;
            }, _ => { }, () => Task.FromResult(Regs[0x0172]));
    }
}
