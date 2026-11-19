using Hummingbird.Core;

namespace Hummingbird.App;

public sealed partial class MainForm
{
    /// <summary>只选择原图中的初始范围。返回后仍由实体圆边算法独立求心和筛选。</summary>
    private sealed class FaceRegionDialog : Form
    {
        public RectangleF? Selection { get; private set; }
        private readonly PictureBox _picture;
        private readonly TextBox _result;
        private readonly Button _confirm;
        private readonly Func<RectangleF,Task<DatumHoleInspection>>? _inspect;
        private DatumHoleInspection? _review;
        private bool _checking;

        public FaceRegionDialog(CameraFrame frame, string name,
            Func<RectangleF,Task<DatumHoleInspection>>? inspect = null, RectangleF? initialSelection = null)
        {
            _inspect = inspect; Selection = initialSelection;
            Text = name + " · 放大框选实体圆边"; Font = new Font("Microsoft YaHei UI", 10F);
            WindowState = FormWindowState.Maximized; StartPosition = FormStartPosition.CenterParent;
            MinimumSize = new Size(800, 600); KeyPreview = true;
            var hint = new Label
            {
                Dock = DockStyle.Top,
                Height = 54,
                Padding = new Padding(8),
                Text = "紧贴中间实体参考面框选，包含完整圆／椭圆边界，排除外侧黑影。松开后检查；绿色圆边通过后点“确认并保存框选”。Esc取消。"
            };
            var picture = new PictureBox { Dock = DockStyle.Fill, SizeMode = PictureBoxSizeMode.Zoom, BackColor = Color.Black, Cursor = Cursors.Cross };
            _picture = picture;
            _result = new TextBox { Dock = DockStyle.Bottom, Height = 100, Multiline = true, ReadOnly = true,
                ScrollBars = ScrollBars.Vertical, Text = "尚未检查；请在照片上框选实体圆边。" };
            _confirm = new Button { Text = "确认并保存框选", AutoSize = true, Enabled = false };
            _confirm.Click += (_,_) => { if (_review?.Status != DatumHoleInspectionStatus.Detected || _checking) return; DialogResult = DialogResult.OK; Close(); };
            var cancel = new Button {Text="取消",AutoSize=true};cancel.Click+=(_,_)=>{DialogResult=DialogResult.Cancel;Close();};
            var actions = new FlowLayoutPanel { Dock=DockStyle.Bottom,AutoSize=true };
            actions.Controls.AddRange([_confirm,cancel]);
            Controls.Add(picture); Controls.Add(hint); Controls.Add(_result); Controls.Add(actions); ShowFrame(picture, frame, null);
            PointF? start = null, end = null;
            float Scale() => Math.Min((float)picture.ClientSize.Width / frame.Width, (float)picture.ClientSize.Height / frame.Height);
            PointF? ImagePoint(Point point)
            {
                float scale = Scale(), left = (picture.ClientSize.Width - frame.Width * scale) / 2, top = (picture.ClientSize.Height - frame.Height * scale) / 2;
                if (scale <= 0) return null;
                float x = (point.X - left) / scale, y = (point.Y - top) / scale;
                return x >= 0 && y >= 0 && x < frame.Width && y < frame.Height ? new PointF(x, y) : null;
            }
            picture.MouseDown += (_, e) => { if (_checking || e.Button != MouseButtons.Left) return; start = ImagePoint(e.Location); end = start;
                if(start is not null) {_review=null;_confirm.Enabled=false;Selection=null;} picture.Capture = start is not null; };
            picture.MouseMove += (_, e) => { if (start is null) return; end = ImagePoint(e.Location); picture.Invalidate(); };
            picture.MouseUp += async (_, e) =>
            {
                if (start is not { } first || e.Button != MouseButtons.Left) return;
                picture.Capture = false; end = ImagePoint(e.Location); start = null;
                if (end is not { } last) { hint.Text = "框选需要位于照片内，请重新拖框。"; picture.Invalidate(); return; }
                float width = Math.Abs(first.X - last.X), height = Math.Abs(first.Y - last.Y);
                if (Math.Min(width, height) < 20 || Math.Max(width, height) / Math.Min(width, height) > 1.4)
                { hint.Text = "请拖出紧贴完整实体小圆面的近似方框，不能只单击一个点。"; picture.Invalidate(); return; }
                await CheckSelectionAsync(new(Math.Min(first.X, last.X), Math.Min(first.Y, last.Y), width, height));
            };
            picture.Paint += (_, e) =>
            {
                float scale = Scale(), left = (picture.ClientSize.Width - frame.Width * scale) / 2, top = (picture.ClientSize.Height - frame.Height * scale) / 2;
                RectangleF? rectangle = start is { } first && end is { } last
                    ? new(Math.Min(first.X,last.X),Math.Min(first.Y,last.Y),Math.Abs(first.X-last.X),Math.Abs(first.Y-last.Y)) : Selection;
                if(rectangle is { } region)
                {
                    using var pen = new Pen(_review?.Status==DatumHoleInspectionStatus.Detected?Color.Lime:Color.Orange, 2);
                    e.Graphics.DrawRectangle(pen,left+region.X*scale,top+region.Y*scale,region.Width*scale,region.Height*scale);
                }
                if(_review?.Status==DatumHoleInspectionStatus.Detected)
                {
                    var state=e.Graphics.Save();e.Graphics.TranslateTransform(left,top);e.Graphics.ScaleTransform(scale,scale);
                    foreach(var hole in _review.Candidates)DrawReference(e.Graphics,hole,Color.Lime);
                    e.Graphics.Restore(state);
                }
            };
            Shown += async (_,_) => { if(Selection is { } selected && _inspect is not null) await CheckSelectionAsync(selected); };
            KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) { DialogResult = DialogResult.Cancel; Close(); } };
            Disposed += (_, _) => picture.Image?.Dispose();
        }

        // 检查只读原图；只有操作者确认通过结果后，主界面才写入框选与采集记录。
        public async Task CheckSelectionAsync(RectangleF region)
        {
            if(_checking || IsDisposed)return;
            Selection=region;_review=null;_confirm.Enabled=false;_checking=true;_picture.Enabled=false;
            _result.ForeColor=Color.DarkOrange;_result.Text="正在检查框选内的实体圆边……";_picture.Invalidate();
            try
            {
                var review=_inspect is null?null:await _inspect(region);
                if(IsDisposed)return;
                _review=review;
                bool passed=review?.Status==DatumHoleInspectionStatus.Detected;
                _result.ForeColor=passed?Color.DarkGreen:Color.DarkRed;
                _result.Text=(passed?"实体圆边通过；请核对绿色圆边是否贴合目标，再确认保存。":"框选未通过；请在当前照片上重新拖框，无需重新拍照。")
                    + (review is null?"":"\r\n"+review.Message+"\r\n"+string.Join("\r\n",review.Diagnostics));
                _confirm.Enabled=passed;
            }
            catch(Exception ex)
            {
                if(!IsDisposed){_result.ForeColor=Color.DarkRed;_result.Text="框选检查未完成："+ex.Message;}
            }
            finally
            {
                _checking=false;if(!IsDisposed){_picture.Enabled=true;_picture.Invalidate();}
            }
        }
    }
}
