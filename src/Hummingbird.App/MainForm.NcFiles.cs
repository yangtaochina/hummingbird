using Hummingbird.Core;
using Hummingbird.Devices.OpcUa;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private OpcUaCncService CreateNcFileSession() => new(_log)
    {
        VarNamespaceKeyword = _cfg.CncVarNamespace,
        UserName = _cfg.CncUserName,
        Password = _cfg.CncPassword,
        UseSecureEndpoint = _cfg.CncUseSecurity,
        RunConnectDiagnostics = false
    };

    private TabPage BuildNcFilesTab()
    {
        var tab = new TabPage("NC文件");
        var panel = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(8),
            AutoScroll = true,
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false
        };
        var commands = new FlowLayoutPanel { AutoSize = true, WrapContents = false };
        var source = new TextBox
        {
            Width = 720,
            Text = "BIAOPAN"
        };
        var output = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Width = 850,
            Height = 200
        };

        async Task UseSessionAsync(Func<OpcUaCncService, Task> operation)
        {
            if (_cfg.DeviceMode != "Real") { output.Text = "离线演示不连接真机NC文件服务。"; return; }
            if (_workspaceBusy || _laserExposureTask is { IsCompleted: false }) return;
            commands.Enabled = false;
            output.Clear();
            var cnc = CreateNcFileSession();
            try
            {
                output.Text = "正在连接 " + _cfg.CncEndpoint + Environment.NewLine;
                if (!await Task.Run(() => cnc.ConnectAsync(_cfg.CncEndpoint)))
                    throw new InvalidOperationException("OPC UA 连接失败，请检查端点和登录配置。");
                await operation(cnc);
            }
            catch (Exception ex)
            {
                output.AppendText(ex.Message + Environment.NewLine);
                _log.Error("[NC 文件传输] " + ex.Message);
            }
            finally
            {
                await cnc.DisconnectAsync();
                commands.Enabled = true;
            }
        }

        commands.Controls.Add(Btn("查找 DK 中的程序", async (_, _) => await UseSessionAsync(async cnc =>
        {
            var paths = await cnc.BrowseWorkpieceFilesAsync("DK");
            output.AppendText(paths.Count == 0 ? "当前账号未列出 DK 中的文件。" : string.Join(Environment.NewLine, paths));
        })));
        commands.Controls.Add(Btn("下载 NC 原文件…", async (_, _) => await UseSessionAsync(async cnc =>
        {
            string path = source.Text.Trim();
            if (path.Length == 0) { output.Text = "请输入 NC 文件路径。"; return; }
            var file = string.Equals(path, "BIAOPAN", StringComparison.OrdinalIgnoreCase)
                ? await cnc.DownloadWorkpieceProgramAsync("DK", "BIAOPAN")
                : (Name: Path.GetFileName(path), Data: await cnc.DownloadNcFileAsync(path));
            output.AppendText($"已读取 {file.Name}，{file.Data.Length} 字节。" + Environment.NewLine);
            using var dialog = new SaveFileDialog
            {
                Filter = "NC 文件 (*.mpf;*.spf)|*.mpf;*.spf|所有文件 (*.*)|*.*",
                FileName = file.Name
            };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await File.WriteAllBytesAsync(dialog.FileName, file.Data);
            output.AppendText($"已保存到 {dialog.FileName}");
        })));
        commands.Controls.Add(Btn("上传新的 NC 文件…", async (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "NC 程序|*.mpf;*.spf", InitialDirectory=StationProgramFiles.Folder, FileName=_ncUploadInitialFile??"" };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            await UseSessionAsync(async cnc =>
            {
                byte[] data = await File.ReadAllBytesAsync(dialog.FileName);
                if (data.Length == 0 || data.Length > 1024 * 1024)
                    throw new InvalidDataException("请选择1MB以内的有效NC文本文件。");
                string extension = Path.GetExtension(dialog.FileName).ToUpperInvariant();
                string name = $"HM_CHECK_{DateTime.Now:HHmmssfff}{extension}";
                string path = "Sinumerik/FileSystem/Part Program/" + name;
                await cnc.UploadNewNcFileAsync(path, data);
                output.AppendText($"机床已接受 {name}。" + Environment.NewLine);
                byte[] readback = await cnc.DownloadNcFileAsync(path);
                if (!data.SequenceEqual(readback)) throw new InvalidDataException("回读内容与发送文件不同，请在面板核对。");
                output.AppendText("回读一致。请在机床面板核对程序内容并选择启动。");
            });
        }));

        commands.Enabled = _cfg.DeviceMode == "Real";
        panel.Controls.Add(Lbl("配套HM_RETURN、HM_MASTER与HM_OPTICAL在程序目录的“机床程序”文件夹。机床已有对应程序无需重传；上传会命名为HM_CHECK_时间，请按回读成功后显示的名称在面板选择。"));
        panel.Controls.Add(Lbl("真机模式使用已配置的OPC UA连接；上传不会启动程序。离线演示禁止连接真机。"));
        panel.Controls.Add(Lbl("NC 源文件（填 BIAOPAN 自动查找 DK 工件，或填完整 OPC UA 路径）"));
        panel.Controls.Add(source);
        panel.Controls.Add(commands);
        panel.Controls.Add(Lbl("采图路线由“定位采集 → 复用采图路线”导出，在机床面板核对并启动。"));
        panel.Controls.Add(output);
        tab.Controls.Add(panel);
        return tab;
    }
}
