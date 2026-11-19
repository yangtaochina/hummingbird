using System.Text.Json;
using Hummingbird.Core;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private void CheckBundledStationPrograms(string folder)
    {
        var checks=new List<object>();
        void Check(bool value,string name){checks.Add(new{Passed=value,Name=name});if(!value)throw new InvalidOperationException(name);}
        IEnumerable<Control> Children(Control root)=>root.Controls.Cast<Control>().SelectMany(c=>new[]{c}.Concat(Children(c)));
        Check(new[]{"工位恢复配置.json","整件加工配置.json","HM_RETURN.MPF","HM_MASTER.MPF","机床程序说明.md"}
            .All(f=>File.Exists(Path.Combine(StationProgramFiles.Folder,f))),"完整输出包含两套已核定JSON、原MPF及使用说明");
        var returned=JsonSerializer.Deserialize<StationReturnProfile>(File.ReadAllText(StationProgramFiles.ReturnProfile))!;
        var production=JsonSerializer.Deserialize<ProductionStationProfile>(File.ReadAllText(StationProgramFiles.ProductionProfile))!;
        returned.Validate();production.Validate();
        StationProgramFiles.VerifyPairedProgram(StationProgramFiles.ReturnProfile,StationProgramFiles.ReturnProfile,"HM_RETURN.MPF",StationReturnNcProgram.Generate(returned));
        StationProgramFiles.VerifyPairedProgram(StationProgramFiles.ProductionProfile,StationProgramFiles.ProductionProfile,"HM_MASTER.MPF",ContinuousNcProgram.Generate(production.Envelope!,production.SessionToken));
        Check(production.AllowMachining && production.SessionToken==161339 && production.Envelope==returned.WorkEnvelope,
            "主控与回位沿用同一已核定工位、原会话及加工许可，配套MPF与生成器逐字一致");
        Check(LaserSpotCalibration.Hash(Path.Combine(StationProgramFiles.Folder,"HM_MASTER.MPF"))=="3F4BD1E2EC8E3DF3FCE5889801135101C6CC99ADE10FB9054802A341A05D6D25" &&
            LaserSpotCalibration.Hash(Path.Combine(StationProgramFiles.Folder,"HM_RETURN.MPF"))=="10061F2695D3DD586B4894B5954B7E3AD09342DD15C41BE9848BA8E0C22479D1",
            "随源码交付的两份MPF与此前现场版本SHA256相同，无需因本次更新重新上传");
        Check(File.Exists(_state.LastStationReturnProfilePath) && File.Exists(_state.LastProductionProfilePath) &&
            Children(_returnPage).OfType<TextBox>().Single(c=>c.Name=="StationReturnStatus").Text.Contains("已自动使用") &&
            Children(_machiningPage).OfType<Label>().Single(c=>c.Name=="ProductionDefaultConfiguration").Text.Contains("已自动读取"),
            "初次启动已经自动读取两套本地配置，无需人工选JSON");
        Check(!_cnc.IsConnected && !_laser.IsOpen && !_fixedCConfirmed.Checked &&
            !Children(_returnPage).OfType<CheckBox>().Single().Checked &&
            !_guidedOperationConfirmed && _guidedOperationC is null,
            "自动读取配置不连接设备，不确认参考装夹，不启动NC或出光");
        Check(StationProgramFiles.SelectProfile(Path.Combine(folder,"missing-old-config.json"),StationProgramFiles.ReturnProfile)==StationProgramFiles.ReturnProfile &&
            StationProgramFiles.SelectProfile(StationProgramFiles.ProductionProfile,StationProgramFiles.ReturnProfile)==StationProgramFiles.ProductionProfile,
            "旧路径失效时使用交付备份，已存在的维护指定配置仍保留");
        bool rejected=false;
        try{StationProgramFiles.VerifyPairedProgram(StationProgramFiles.ReturnProfile,StationProgramFiles.ReturnProfile,"HM_RETURN.MPF","mismatched program");}
        catch(InvalidDataException){rejected=true;}
        Check(rejected,"本地交付JSON与MPF不匹配时拒绝作为默认配置，不静默放行");
        Check(Children(_returnPage).OfType<Label>().Single(c=>c.Name=="StationReturnInstructions").Text.Contains("无需重新上传") &&
            Children(_machiningPage).OfType<Label>().Single(c=>c.Name=="MasterProgramInstructions").Text.Contains("从头Cycle Start"),
            "界面说明已有程序直接面板启动、没有程序上传及实际命名");
        var previous=_tabs.SelectedTab;
        ShowNcUpload("HM_RETURN.MPF");Check(_ncUploadInitialFile==Path.Combine(StationProgramFiles.Folder,"HM_RETURN.MPF") && !_cnc.IsConnected,"回位上传入口仅切页并预选原MPF，不连接或上传");
        ShowNcUpload("HM_MASTER.MPF");Check(_ncUploadInitialFile==Path.Combine(StationProgramFiles.Folder,"HM_MASTER.MPF") && !_cnc.IsConnected,"主控上传入口仅切页并预选原MPF，不连接或上传");
        _tabs.SelectedTab=previous;
        File.WriteAllText(Path.Combine(folder,"bundled-program-checks.json"),JsonSerializer.Serialize(checks,new JsonSerializerOptions{WriteIndented=true}));
    }
}
