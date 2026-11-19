using System.Text.Json;
using Hummingbird.Core;
using Hummingbird.Devices.LaserRtu;

internal static class SpotTrialChecks
{
    public static async Task<int> Run()
    {
        int passed=0,failed=0;
        void Check(bool ok,string name) {Console.WriteLine($"[{(ok?"PASS":"FAIL")}] {name}");if(ok)passed++;else failed++;}
        async Task<bool> Fails(Func<Task> action) {try{await action();return false;}catch{return true;}}
        foreach(string value in new[]{""," ","0","49","600001","1.5","abc","-50"})
            Check(await Fails(()=>Task.Run(()=>LaserCalibrationController.ParseRequestMilliseconds(value))),$"时长输入“{value}”不能触发真实出光");
        Check(LaserCalibrationController.ParseRequestMilliseconds("50")==50 && LaserCalibrationController.ParseRequestMilliseconds(" 3000 ")==3000,
            "只接受范围内整数毫秒，不推定工艺适用性");
        Check(LaserCalibrationController.ParseRequestMilliseconds("12000")==12000 && LaserCalibrationController.ParseRequestMilliseconds("600000")==600000,
            "客户可以选择超过3秒的时长，最大600秒");
        var dry=new Fixture();dry.Regs[0x172]=0;var dryRecord=new SpotTrialRecord{DryRun=true};
        await dry.Workflow().RunAsync(dryRecord);
        Check(dryRecord.Completed && dryRecord.BeforeImage is not null && dryRecord.AfterImage is not null && dry.Reads==0 && dry.Writes.Count==0,
            "空时长演练生成前后图与记录，不调用激光读写");
        Check(!dryRecord.EmissionRequested && dryRecord.OpticalExposureMs is null && dryRecord.ShutdownConfirmed is null,
            "演练不伪造出光、实际光学时长或关光已验证");
        var unset=new Fixture();await unset.Laser.ReadStateAsync();
        Check(await Fails(()=>unset.Workflow().RunAsync(new(){DryRun=false})) && unset.Writes.Count==0 && unset.Captures==0,
            "真实流程空时长在采图和写设备前就拒绝");
        var zero=new Fixture();zero.Regs[0x172]=0;await zero.Laser.ReadStateAsync();
        Check(await Fails(()=>zero.Workflow().RunAsync(new(){RequestedExposureMs=50})) && zero.Writes.Count==0,
            "复现今日功率原始0：禁止真实出光");
        var mismatch=new Fixture();await mismatch.Laser.ReadStateAsync();
        Check(await Fails(()=>mismatch.Workflow().RunAsync(new(){RequestedExposureMs=50,RequestedPreset=LaserProcessPreset.CustomerConfigured(50,1000)})) && mismatch.Writes.Count==0,
            "客户选择功率与读回不同，在出光前拒绝");
        var ready=new Fixture();await ready.Laser.ReadStateAsync();var record=new SpotTrialRecord{RequestedExposureMs=50};
        await ready.Workflow().RunAsync(record);
        int before=ready.Events.IndexOf("capture:before"),on=ready.Events.IndexOf("ON"),off=ready.Events.IndexOf("OFF"),after=ready.Events.IndexOf("capture:after");
        Check(record.Completed && before<on && on<off && off<after && ready.Events.IndexOf("persist:BeforeImageSaved")<on,
            "真实流程先保存基准，再ON/OFF及确认关光，最后打后图");
        Check(record.ShutdownConfirmed==true && record.CommandWindowMs>=50 && record.OpticalExposureMs is null,
            "归档通信窗口与请求时长，实际光学时长保持未知");
        Check(record.LaserStateBefore!.IsLegacy && record.LaserStateBefore.Power==4125 && ready.Regs[0x172]==4125 && ready.Regs[0x1c]==100,
            "记录旧协议原始功率和100kHz，不自动换算或重写参数");
        Check(ready.Writes.SequenceEqual(new[]{(7,1),(7,0)}) && ready.Laser.LastState is null,
            "仅写已确认出光地址，结束后必须重新读回才能再打");
        Check(record.StartPosition!.CSource==AxisCoordinateSource.ManualPanelEntry && record.StartPosition.CNode is null,
            "手填C明确记录来源，不假装自动监测");
        var restored=JsonSerializer.Deserialize<SpotTrialRecord>(JsonSerializer.Serialize(record))!;
        Check(restored.Completed && restored.BeforeImage!.FileName=="before.bmp" && restored.LaserStateBefore!.Power==4125,
            "完整试打记录可序列化重载");
        var missingBefore=new Fixture{FailBefore=true};await missingBefore.Laser.ReadStateAsync();
        Check(await Fails(()=>missingBefore.Workflow().RunAsync(new(){RequestedExposureMs=50})) && missingBefore.Writes.Count==0,
            "打前采图失败不出光");
        var disk=new Fixture{FailPersist=1};await disk.Laser.ReadStateAsync();
        Check(await Fails(()=>disk.Workflow().RunAsync(new(){RequestedExposureMs=50})) && disk.Writes.Count==0,
            "基准归档失败不出光");
        var moved=new Fixture{MoveAtRead=4};await moved.Laser.ReadStateAsync();
        Check(await Fails(()=>moved.Workflow().RunAsync(new(){RequestedExposureMs=50})) && moved.Writes.Count==0,
            "基准归档后、开光前检测到XYZ变化时拒绝出光");
        var invalid=new Fixture{InvalidPosition=true};await invalid.Laser.ReadStateAsync();
        Check(await Fails(()=>invalid.Workflow().RunAsync(new(){RequestedExposureMs=50})) && invalid.Captures==0 && invalid.Writes.Count==0,
            "无效位置读数不会采图或出光");
        var changed=new Fixture{ChangeParameterAfterBaseline=true};await changed.Laser.ReadStateAsync();
        Check(await Fails(()=>changed.Workflow().RunAsync(new(){RequestedExposureMs=50})) && changed.Writes.Count==0,
            "归档后激光参数改变，底层复读拒绝沿用旧参数");
        var afterFail=new Fixture{FailAfter=true};await afterFail.Laser.ReadStateAsync();var incomplete=new SpotTrialRecord{RequestedExposureMs=50};
        Check(await Fails(()=>afterFail.Workflow().RunAsync(incomplete)) && incomplete.ShutdownConfirmed==true && !incomplete.Completed && incomplete.BeforeImage is not null,
            "打后采图失败仍关光，保留基准并报告未完成");
        var offFail=new Fixture{FailOff=true};await offFail.Laser.ReadStateAsync();var offRecord=new SpotTrialRecord{RequestedExposureMs=50};
        Check(await Fails(()=>offFail.Workflow().RunAsync(offRecord)) && offRecord.ShutdownConfirmed==false && offRecord.AfterImage is null && !offRecord.Completed,
            "无法确认关光时不采打后图、不发布成功");
        using var canceled=new CancellationTokenSource();canceled.Cancel();
        var cancelBefore=new Fixture();await cancelBefore.Laser.ReadStateAsync();
        Check(await Fails(()=>cancelBefore.Workflow().RunAsync(new(){RequestedExposureMs=50},canceled.Token)) && cancelBefore.Writes.Count==0,
            "预取消不出光");
        using var during=new CancellationTokenSource();var cancelOn=new Fixture{CancelOn= during};await cancelOn.Laser.ReadStateAsync();
        var canceledRecord=new SpotTrialRecord{RequestedExposureMs=3000};
        Check(await Fails(()=>cancelOn.Workflow().RunAsync(canceledRecord,during.Token)) && cancelOn.Regs[7]==0 && canceledRecord.ShutdownConfirmed==true && !canceledRecord.Completed,
            "出光中取消执行OFF，并归档取消状态");
        using var prepareCancel=new CancellationTokenSource();var prepare=new Fixture{CancelAfterBaseline=prepareCancel};await prepare.Laser.ReadStateAsync();
        Check(await Fails(()=>prepare.Workflow().RunAsync(new(){RequestedExposureMs=50},prepareCancel.Token)) && prepare.Writes.Count==0,
            "基准保存后收到停止请求不会继续发ON");
        using var dryCancel=new CancellationTokenSource();var dryStop=new Fixture{CancelAfterBaseline=dryCancel};
        Check(await Fails(()=>dryStop.Workflow().RunAsync(new(){DryRun=true},dryCancel.Token)) && dryStop.Writes.Count==0 && dryStop.Reads==0,
            "停止演练也不发送激光读写");
        var finalDisk=new Fixture{FailPersist=3};await finalDisk.Laser.ReadStateAsync();var diskRecord=new SpotTrialRecord{RequestedExposureMs=50};
        Check(await Fails(()=>finalDisk.Workflow().RunAsync(diskRecord)) && diskRecord.ShutdownConfirmed==true && !diskRecord.Completed && diskRecord.Phase=="Failed",
            "打后归档失败保留关光状态，不能报告流程成功或自动重打");
        var lostAck=new Fixture{LoseOnAck=true};await lostAck.Laser.ReadStateAsync();var lostRecord=new SpotTrialRecord{RequestedExposureMs=50};
        Check(await Fails(()=>lostAck.Workflow().RunAsync(lostRecord)) && lostAck.Regs[7]==0 && lostRecord.ShutdownConfirmed==true && lostRecord.AfterImage is null,
            "开光应答丢失仍关光，保留不完整记录而不伪造打后成功");
        Console.WriteLine($"SpotTrial: {passed} passed, {failed} failed; no hardware accessed.");
        return failed==0?0:1;
    }

    private sealed class Fixture
    {
        public Dictionary<ushort,ushort> Regs=new(){[1]=0,[2]=0,[7]=0,[0x1c]=100,[0x1d]=1,[0x172]=4125};
        public List<(int,int)> Writes=new();public List<string> Events=new();
        public int Reads,PositionReads,Captures,Persists,MoveAtRead,FailPersist;
        public bool FailBefore,FailAfter,FailOff,InvalidPosition,ChangeParameterAfterBaseline,LoseOnAck;
        public CancellationTokenSource? CancelOn,CancelAfterBaseline;
        public LaserCalibrationController Laser;
        public Fixture()
        {
            Laser=new(reg=>{Reads++;return Task.FromResult(Regs[reg]);},(reg,value)=>
            {
                Writes.Add((reg,value));Events.Add(value==1?"ON":"OFF");
                if(value==0 && FailOff)throw new IOException("fake OFF failure");
                Regs[reg]=value;if(value==1)CancelOn?.Cancel();
                if(value==1 && LoseOnAck)throw new IOException("fake ON acknowledgement lost");
                return Task.CompletedTask;
            },_=>{},()=>{Reads++;return Task.FromResult(Regs[0x172]);});
            Laser.SelectProtocol(LaserCalibrationProtocol.ExcelLegacy);
        }
        public StationarySpotTrial Workflow()=>new(()=>
        {
            PositionReads++;
            return Task.FromResult(new SpotTrialPosition(InvalidPosition?double.NaN:PositionReads==MoveAtRead?1.1:1,2,3,254.8,
                AxisCoordinateSource.ManualPanelEntry,null,DateTimeOffset.Now));
        },(phase,position)=>
        {
            Captures++;Events.Add("capture:"+phase);
            if(phase=="before" && FailBefore || phase=="after" && FailAfter)throw new IOException("fake camera failure");
            return Task.FromResult(new SpotTrialImage(phase+".bmp","fakehash",1,1,DateTime.Now,position));
        },r=>
        {
            Persists++;Events.Add("persist:"+r.Phase);
            if(Persists==FailPersist)throw new IOException("fake archive failure");
            if(ChangeParameterAfterBaseline && r.Phase=="BeforeImageSaved")Regs[0x1c]=200;
            if(r.Phase=="BeforeImageSaved")CancelAfterBaseline?.Cancel();
            return Task.CompletedTask;
        },Laser,_=>{});
    }
}
