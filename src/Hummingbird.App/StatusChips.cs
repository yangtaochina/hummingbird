namespace Hummingbird.App;

/// <summary>一排带文字的状态色块，用于日常加工页显示六步采图和18孔进度。</summary>
internal sealed class StatusChips : Control
{
    public static readonly Color Idle = Color.FromArgb(222, 226, 230), Active = Color.FromArgb(28, 115, 165),
        Done = Color.FromArgb(46, 139, 87), Fault = Color.FromArgb(200, 40, 40);
    private const int ChipHeight = 26, Gap = 4;
    private readonly string[] _labels;
    private readonly Color[] _colors;
    private readonly int _chipWidth;

    public StatusChips(IEnumerable<string> labels, int chipWidth)
    {
        _labels = labels.ToArray(); _colors = Enumerable.Repeat(Idle, _labels.Length).ToArray(); _chipWidth = chipWidth;
        DoubleBuffered = true; ResizeRedraw = true; Dock = DockStyle.Fill; Margin = new Padding(3, 2, 3, 6);
        Height = ChipHeight;
    }

    public int Count => _labels.Length;
    public Color ColorAt(int index) => _colors[index];
    public void Set(int index, Color color)
    {
        if (_colors[index] == color) return;
        _colors[index] = color; Invalidate();
    }
    public void Reset() { for (int i = 0; i < _colors.Length; i++) Set(i, Idle); }

    private int Columns(int width) => Math.Clamp((width + Gap) / (_chipWidth + Gap), 1, _labels.Length);
    private int HeightFor(int width) { int rows = (_labels.Length + Columns(width) - 1) / Columns(width); return rows * ChipHeight + (rows - 1) * Gap; }
    public override Size GetPreferredSize(Size proposedSize)
    {
        int width = proposedSize.Width is > 0 and < 10000 ? proposedSize.Width : Math.Max(Width, _chipWidth);
        return new Size(width, HeightFor(width));
    }
    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        int height = HeightFor(Width); if (Height != height) Height = height;
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        int columns = Columns(Width);
        for (int i = 0; i < _labels.Length; i++)
        {
            var box = new Rectangle(i % columns * (_chipWidth + Gap), i / columns * (ChipHeight + Gap), _chipWidth, ChipHeight);
            using var fill = new SolidBrush(_colors[i]); e.Graphics.FillRectangle(fill, box);
            TextRenderer.DrawText(e.Graphics, _labels[i], Font, box, _colors[i] == Idle ? Color.FromArgb(70, 70, 70) : Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }
}
