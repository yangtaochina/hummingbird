using Hummingbird.Core;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    private Action<string>? _showProcessTrace;

    /// <summary>维护用：把一件已保存的记录按加工顺序逐步列出来核对。只读文件，不连接设备。</summary>
    private TabPage BuildProcessTraceTab()
    {
        var tab = new TabPage("流程检查");
        var folder = new TextBox { Name = "ProcessTraceFolder", ReadOnly = true, Width = 520, Margin = new Padding(3, 6, 3, 3) };
        var steps = new ListView
        {
            Name = "ProcessTraceSteps", Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, HideSelection = false,
            MultiSelect = false, ShowItemToolTips = true
        };
        steps.Columns.Add("步骤", 150); steps.Columns.Add("结果", 70); steps.Columns.Add("关键数值", 620);
        var photo = new PictureBox { Name = "ProcessTracePhoto", Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black };
        var data = new TextBox { Name = "ProcessTraceData", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false };
        var summary = new Label { Name = "ProcessTraceSummary", AutoSize = true, Margin = new Padding(8, 9, 3, 3), Text = "选择一件记录后，按加工顺序列出每一步保存下来的结果。" };

        void ShowPhoto(string? path)
        {
            var old = photo.Image; photo.Image = null; old?.Dispose();
            if (path is null || !File.Exists(path)) return;
            // 不占用原文件：读入内存后再显示。
            using var stream = File.OpenRead(path); using var image = Image.FromStream(stream);
            photo.Image = new Bitmap(image);
        }
        void ShowStep()
        {
            if (steps.SelectedItems.Count == 0 || steps.SelectedItems[0].Tag is not ProcessStep step) return;
            try
            {
                ShowPhoto(step.ImageFile);
                string header = step.Name + "：" + step.Summary + "\r\n";
                if (step.DataFile is null || !File.Exists(step.DataFile)) { data.Text = header + "（这一步没有对应的数据文件）"; return; }
                string text = File.ReadAllText(step.DataFile);
                const int limit = 200_000;
                data.Text = header + "数据文件：" + step.DataFile + "\r\n\r\n" +
                    (text.Length > limit ? text[..limit] + "\r\n……（文件较大，只显示前面一部分，完整内容请打开文件）" : text).ReplaceLineEndings("\r\n");
            }
            catch (Exception ex) { data.Text = "这一步的资料读不出来：" + ex.Message; }
        }
        void Load(string job)
        {
            steps.Items.Clear(); ShowPhoto(null); data.Clear(); folder.Text = job;
            IReadOnlyList<ProcessStep> list;
            try { list = ProcessTrace.Read(job); }
            catch (Exception ex) { summary.Text = "这件记录读不出来：" + ex.Message; return; }
            foreach (var step in list)
            {
                var item = new ListViewItem(step.Name) { Tag = step, ToolTipText = step.Summary, UseItemStyleForSubItems = false };
                var state = item.SubItems.Add(step.State switch { ProcessStepState.Passed => "通过", ProcessStepState.Failed => "未通过", _ => "未执行" });
                state.BackColor = step.State switch { ProcessStepState.Passed => Color.FromArgb(198, 239, 206), ProcessStepState.Failed => Color.FromArgb(255, 199, 206), _ => Color.Gainsboro };
                item.SubItems.Add(step.Summary);
                steps.Items.Add(item);
            }
            int failed = list.Count(s => s.State == ProcessStepState.Failed);
            summary.Text = $"共 {list.Count} 步：通过 {list.Count(s => s.State == ProcessStepState.Passed)}，未通过 {failed}，未执行 {list.Count(s => s.State == ProcessStepState.NotRun)}。" +
                "显示的是当时保存的结果，不重新计算。";
            var first = steps.Items.Cast<ListViewItem>().FirstOrDefault(i => ((ProcessStep)i.Tag!).State == ProcessStepState.Failed) ?? steps.Items.Cast<ListViewItem>().FirstOrDefault();
            if (first is not null) { first.Selected = true; first.EnsureVisible(); }
        }
        steps.SelectedIndexChanged += (_, _) => ShowStep();
        _showProcessTrace = job => { SelectPage(tab); Load(job); };

        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        bar.Controls.Add(Btn("最新一件", (_, _) =>
        {
            string? latest = ProcessTrace.LatestJobFolder(_state.CaptureRoot);
            if (latest is null) summary.Text = "采图目录下还没有工件记录。"; else Load(latest);
        }));
        bar.Controls.Add(Btn("选择记录…", (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { Description = "选择一件的记录目录", InitialDirectory = _state.CaptureRoot, UseDescriptionForTitle = true };
            if (dialog.ShowDialog(this) == DialogResult.OK) Load(dialog.SelectedPath);
        }));
        bar.Controls.Add(Btn("打开这一步的数据文件位置", (_, _) =>
        {
            if (steps.SelectedItems.Count > 0 && steps.SelectedItems[0].Tag is ProcessStep { DataFile: { } file } && Path.GetDirectoryName(file) is { } directory && Directory.Exists(directory))
                OpenLocalFolder(directory);
        }));
        bar.Controls.Add(folder); bar.Controls.Add(summary);

        var detail = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Horizontal };
        detail.Panel1.Controls.Add(photo); detail.Panel2.Controls.Add(data);
        var split = new SplitContainer { Dock = DockStyle.Fill, Orientation = Orientation.Vertical };
        split.Panel1.Controls.Add(steps); split.Panel2.Controls.Add(detail);
        tab.Controls.Add(split); tab.Controls.Add(bar);
        tab.Layout += (_, _) => { if (split.Width > 400 && split.SplitterDistance < 200) split.SplitterDistance = split.Width * 55 / 100; };
        return tab;
    }
}
