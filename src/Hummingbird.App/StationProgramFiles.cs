using System.Text.Json;
using Hummingbird.Core;

namespace Hummingbird.App;

/// <summary>随源码和完整编译输出交付的已核定配置及原NC备份。仅读本地文件。</summary>
internal static class StationProgramFiles
{
    internal static string Folder => Path.Combine(AppContext.BaseDirectory,"机床程序");
    internal static string ReturnProfile => Path.Combine(Folder,"工位恢复配置.json");
    internal static string ProductionProfile => Path.Combine(Folder,"整件加工配置.json");
    internal static string SelectProfile(string remembered,string bundled) => File.Exists(remembered)?remembered:bundled;
    internal static void VerifyPairedProgram(string profilePath,string bundledProfile,string program,string generated)
    {
        if(!Path.GetFullPath(profilePath).Equals(Path.GetFullPath(bundledProfile),StringComparison.OrdinalIgnoreCase))return;
        string path=Path.Combine(Folder,program);
        if(!File.Exists(path))throw new FileNotFoundException("交付机床程序缺失，请复制完整编译输出。",path);
        if(File.ReadAllText(path).ReplaceLineEndings("\n")!=generated.ReplaceLineEndings("\n"))
            throw new InvalidDataException("交付配置与配套"+program+"内容不一致，请核对整套机床程序备份。");
    }
}

public sealed partial class MainForm
{
    private string? _ncUploadInitialFile;
    private void ShowNcUpload(string name)
    {
        _ncUploadInitialFile=Path.Combine(StationProgramFiles.Folder,name);
        _tabs.SelectedTab=_toolsPage;
        var tools=(TabControl)_toolsPage.Controls[0];
        tools.SelectedTab=tools.TabPages.Cast<TabPage>().Single(t=>t.Text=="NC文件");
    }
}
