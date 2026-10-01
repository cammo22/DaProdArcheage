using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;

namespace DaProd.Launcher;

/// <summary>Schermata iniziale (Ponticheage): compare con una dissolvenza, mostra una barra e poi lascia il posto al launcher.</summary>
sealed class SplashForm : Form
{
    readonly Bitmap _img;
    double _progress;

    public SplashForm()
    {
        using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream("splash.png")!;
        using var raw = Image.FromStream(st);
        _img = new Bitmap(raw);
        FormBorderStyle = FormBorderStyle.None; StartPosition = FormStartPosition.CenterScreen; ShowInTaskbar = false; TopMost = true;
        AutoScaleMode = AutoScaleMode.None; DoubleBuffered = true; BackColor = Theme.Bg; Opacity = 0;
        var wa = Screen.PrimaryScreen?.WorkingArea ?? new Rectangle(0, 0, 1280, 720);
        var k = Math.Min(1.0, Math.Min(wa.Width * 0.8 / _img.Width, wa.Height * 0.8 / _img.Height));
        ClientSize = new Size((int)(_img.Width * k), (int)(_img.Height * k));
        try { Icon = Icon.ExtractAssociatedIcon(Environment.ProcessPath!); } catch { }
    }

    /// <summary>Aggiorna dissolvenza e barra: ms trascorsi su durata totale.</summary>
    public void Step(long ms, int total)
    {
        _progress = Math.Clamp(ms / (double)total, 0, 1);
        const int fade = 350;
        Opacity = Math.Min(1, Math.Min(ms / (double)fade, (total - ms) / (double)fade));
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic; g.SmoothingMode = SmoothingMode.AntiAlias;
        g.DrawImage(_img, ClientRectangle);
        // barra sottile in basso e scritta
        var barH = Math.Max(4, Height / 120); var y = Height - barH * 3;
        using (var track = new SolidBrush(Color.FromArgb(90, 0, 0, 0))) g.FillRectangle(track, Width / 8, y, Width * 3 / 4, barH);
        using (var fill = new SolidBrush(Theme.Gold)) g.FillRectangle(fill, Width / 8, y, (int)(Width * 3 / 4 * _progress), barH);
        TextRenderer.DrawText(g, "Preparo il launcher...", Theme.Small, new Rectangle(0, y - barH * 5, Width, barH * 4), Color.FromArgb(235, 226, 200),
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        using var border = new Pen(Color.FromArgb(160, Theme.Gold), 2);
        g.DrawRectangle(border, 1, 1, Width - 3, Height - 3);
    }

    protected override void Dispose(bool disposing) { if (disposing) _img.Dispose(); base.Dispose(disposing); }
}
