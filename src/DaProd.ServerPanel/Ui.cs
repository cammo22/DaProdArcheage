using System.Drawing;
using System.Reflection;

namespace DaProd.ServerPanel;

/// <summary>Tema scuro e controlli comuni. Font e colori sono statici: niente oggetti GDI creati a ogni aggiornamento.</summary>
static class Ui
{
    public static readonly Color Bg = Color.FromArgb(22, 24, 31), Side = Color.FromArgb(16, 17, 23), Card = Color.FromArgb(32, 35, 45),
        Text = Color.FromArgb(230, 232, 238), Muted = Color.FromArgb(150, 155, 170), Accent = Color.FromArgb(214, 170, 76),
        Green = Color.FromArgb(52, 168, 110), Red = Color.FromArgb(200, 70, 70), Blue = Color.FromArgb(60, 120, 200),
        Orange = Color.FromArgb(215, 130, 50), Purple = Color.FromArgb(130, 95, 200), Yellow = Color.FromArgb(190, 150, 30), Grey = Color.FromArgb(70, 74, 86);

    public static readonly Font Normal = new("Segoe UI", 10), Semi = new("Segoe UI Semibold", 10), Big = new("Segoe UI Semibold", 16),
        Huge = new("Segoe UI Semibold", 15), Small = new("Segoe UI", 8.5f), Mono = new("Consolas", 9.5f), Nav = new("Segoe UI", 11);

    static readonly PropertyInfo? Dbl = typeof(Control).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic);
    public static T Buffered<T>(T c) where T : Control { Dbl?.SetValue(c, true); return c; }

    public static DataGridView Grid()
    {
        var g = Buffered(new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, AllowUserToDeleteRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Card, BorderStyle = BorderStyle.None, GridColor = Bg, EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 34, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing,
            RowTemplate = { Height = 28 }, MultiSelect = false
        });
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Side, ForeColor = Accent, Font = Semi };
        g.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Card, ForeColor = Text, SelectionBackColor = Blue, SelectionForeColor = Color.White };
        return g;
    }

    public static Label Title(string t) => new() { Text = t, Dock = DockStyle.Top, Height = 44, Font = Big, ForeColor = Text };
    public static Label Hint(string t, int h = 40) => new() { Text = t, Dock = DockStyle.Top, Height = h, ForeColor = Muted };
    public static FlowLayoutPanel Bar() => Buffered(new FlowLayoutPanel { Dock = DockStyle.Top, Height = 54, Padding = new Padding(0, 4, 0, 4), WrapContents = false });
    public static Panel Gap(int h = 8) => new() { Dock = DockStyle.Top, Height = h };

    public static Button Btn(string text, Color c, Func<Task> onClick, int width = 150)
    {
        var b = new Button { Text = text, Width = width, Height = 40, BackColor = c, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 8, 0), Font = Semi };
        b.FlatAppearance.BorderSize = 0;
        b.Click += async (_, _) =>
        {
            b.Enabled = false;
            try { await onClick(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { b.Enabled = true; }
        };
        return b;
    }

    public static string? Prompt(IWin32Window owner, string text, string value = "")
    {
        using var f = new Form { Text = "DaProd", Size = new Size(440, 160), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Bg, ForeColor = Text, Font = Normal };
        var tb = new TextBox { Left = 14, Top = 38, Width = 395, Text = value };
        var ok = new Button { Text = "OK", Left = 309, Top = 72, Width = 100, Height = 32, DialogResult = DialogResult.OK, BackColor = Blue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        f.Controls.AddRange([new Label { Text = text, Left = 14, Top = 12, AutoSize = true }, tb, ok]);
        f.AcceptButton = ok;
        return f.ShowDialog(owner) == DialogResult.OK ? tb.Text : null;
    }
}

/// <summary>Riquadro di stato: si aggiorna sul posto (nessun controllo ricreato).</summary>
sealed class MiniCard : Panel
{
    readonly Label _title, _state, _detail;
    public MiniCard(string title)
    {
        Width = 168; Height = 64; Margin = new Padding(0, 0, 8, 0); Padding = new Padding(8, 4, 4, 4);
        Ui.Buffered(this);
        _detail = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.White, Font = Ui.Small };
        _state = new Label { Dock = DockStyle.Top, Height = 20, ForeColor = Color.White, Font = Ui.Semi };
        _title = new Label { Dock = DockStyle.Top, Height = 18, ForeColor = Color.White, Font = Ui.Small, Text = title };
        Controls.Add(_detail); Controls.Add(_state); Controls.Add(_title);
    }
    public void Set(string state, string detail, Color c)
    {
        var d = ControlPaint.Dark(c, 0.15f);
        if (BackColor != d) BackColor = d;
        if (_state.Text != state) _state.Text = state;
        if (_detail.Text != detail) _detail.Text = detail;
    }
}

/// <summary>Etichetta colorata dell'intestazione (MySQL ON, Login OFF...).</summary>
sealed class Pill : Label
{
    public Pill() { AutoSize = false; Width = 116; Height = 28; TextAlign = ContentAlignment.MiddleCenter; ForeColor = Color.White; Margin = new Padding(0, 0, 8, 0); Font = Ui.Semi; }
    public void Set(string text, Color c) { if (Text != text) Text = text; if (BackColor != c) BackColor = c; }
}
