using System.Drawing;
using System.Drawing.Drawing2D;

namespace DaProd.Launcher;

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(17, 19, 26), Card = Color.FromArgb(29, 32, 42), Field = Color.FromArgb(40, 44, 57),
        Text = Color.FromArgb(232, 234, 240), Muted = Color.FromArgb(146, 152, 168), Gold = Color.FromArgb(226, 182, 84),
        Green = Color.FromArgb(48, 170, 108), Red = Color.FromArgb(204, 76, 76), Orange = Color.FromArgb(222, 136, 50),
        Blue = Color.FromArgb(62, 122, 204), Grey = Color.FromArgb(70, 75, 90);
    public static readonly Font Normal = new("Segoe UI", 10), Semi = new("Segoe UI Semibold", 10), Title = new("Segoe UI Semibold", 22),
        Small = new("Segoe UI", 9), Play = new("Segoe UI Semibold", 15), Section = new("Segoe UI Semibold", 9.5f);

    public static GraphicsPath Round(Rectangle r, int radius)
    {
        var d = radius * 2; var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90); p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90); p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure(); return p;
    }
}

/// <summary>Pulsante con angoli arrotondati e effetto al passaggio del mouse.</summary>
sealed class RoundButton : Button
{
    bool _hover;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public Color Fill { get; set; } = Theme.Blue;
    public RoundButton()
    {
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0; ForeColor = Color.White; Font = Theme.Semi; Cursor = Cursors.Hand;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }
    protected override void OnMouseEnter(EventArgs e) { _hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { _hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
        var c = !Enabled ? Theme.Grey : _hover ? ControlPaint.Light(Fill, 0.15f) : Fill;
        using var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 10);
        using var b = new SolidBrush(c);
        g.FillPath(b, path);
        TextRenderer.DrawText(g, Text, Font, ClientRectangle, Enabled ? ForeColor : Color.FromArgb(170, 174, 186),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}

/// <summary>Riquadro con angoli arrotondati.</summary>
sealed class Card : Panel
{
    public Card() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Parent?.BackColor ?? Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var path = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), 12);
        using var b = new SolidBrush(Theme.Card);
        g.FillPath(b, path);
    }
}

/// <summary>Barra di avanzamento sottile in tinta con il tema (la ProgressBar di Windows è sempre bianca).</summary>
sealed class SlimBar : Control
{
    int _value;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Maximum { get; set; } = 1000;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Value { get => _value; set { _value = Math.Clamp(value, 0, Maximum); Invalidate(); } }
    public SlimBar() { SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true); Height = 8; }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics; g.Clear(Theme.Bg); g.SmoothingMode = SmoothingMode.AntiAlias;
        using var track = new SolidBrush(Theme.Field);
        using var tp = Theme.Round(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2);
        g.FillPath(track, tp);
        var w = (int)((Width - 1) * (long)_value / Math.Max(1, Maximum));
        if (w < Height) return;
        using var fill = new SolidBrush(Theme.Gold);
        using var fp = Theme.Round(new Rectangle(0, 0, w, Height - 1), Height / 2);
        g.FillPath(fill, fp);
    }
}

/// <summary>Etichetta-pillola colorata (ONLINE, 12 giocatori...).</summary>
sealed class Chip : Label
{
    public Chip() { AutoSize = false; Height = 28; Width = 120; TextAlign = ContentAlignment.MiddleCenter; ForeColor = Color.White; Font = Theme.Section; Margin = new Padding(8, 0, 0, 0); }
    public void Set(string text, Color c) { if (Text != text) Text = text; if (BackColor != c) BackColor = c; }
}
