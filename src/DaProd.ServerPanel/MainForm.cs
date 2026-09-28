using System.Drawing;

namespace DaProd.ServerPanel;

public sealed class MainForm : Form
{
    readonly PanelSettings _s = PanelSettings.Load();
    readonly ServerManager _srv;
    readonly EventScheduler _events;
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9), BackColor = Color.Black, ForeColor = Color.Gainsboro };
    readonly Label _status = new() { AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold) };
    readonly DataGridView _accounts = Grid();
    readonly ListBox _evList = new() { Dock = DockStyle.Left, Width = 260 };
    readonly PropertyGrid _evProps = new() { Dock = DockStyle.Fill };
    readonly DataGridView _players = Grid();
    LauncherApi? _api;

    public MainForm()
    {
        Text = "DaProd ArcheAge â€” Pannello Server";
        Size = new Size(1100, 720);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10);

        _s.AutoDetect();
        _srv = new ServerManager(_s, Log);
        _events = new EventScheduler(RunEventAsync);

        var tabs = new TabControl { Dock = DockStyle.Fill };
        tabs.TabPages.Add(DashboardTab());
        tabs.TabPages.Add(LiveTab());
        tabs.TabPages.Add(AccountsTab());
        tabs.TabPages.Add(EventsTab());
        tabs.TabPages.Add(SqlTab());
        tabs.TabPages.Add(SettingsTab());
        Controls.Add(tabs);

        _api = new LauncherApi(_s, Log, () => Live.Snapshot(_s, _srv), _srv);
        _api.Start();
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) => UpdateStatus();
        t.Start();
        Shown += async (_, _) =>
        {
            if (!_s.AutoStart) return;
            try { await _srv.FirstSetupAsync(); await _srv.StartAllAsync(); }
            catch (Exception ex) { Log("panel", "Avvio automatico fallito: " + ex.Message); }
        };
        FormClosing += (_, e) =>
        {
            if (_srv.IsRunning("game") || _srv.IsRunning("login") || _srv.IsRunning("mysql"))
            {
                if (MessageBox.Show("Chiudendo il pannello si ferma il server. Continuare?", "Esci", MessageBoxButtons.YesNo) == DialogResult.No) { e.Cancel = true; return; }
                _srv.StopAll();
            }
        };
    }

    static DataGridView Grid() => new() { Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill };

    static Button Btn(string text, Color c, Func<Task> onClick)
    {
        var b = new Button { Text = text, Width = 200, Height = 50, BackColor = c, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(6) };
        b.Click += async (_, _) =>
        {
            b.Enabled = false;
            try { await onClick(); }
            catch (Exception ex) { MessageBox.Show(ex.Message, "Errore", MessageBoxButtons.OK, MessageBoxIcon.Error); }
            finally { b.Enabled = true; }
        };
        return b;
    }

    void Log(string src, string line)
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] [{src}] {line}\r\n";
        if (IsHandleCreated) BeginInvoke(() => { if (_log.TextLength > 500_000) _log.Clear(); _log.AppendText(text); });
    }

    void UpdateStatus()
    {
        string Dot(string n) => (_srv.IsRunning(n) ? "ðŸŸ¢ " : "ðŸ”´ ") + n.ToUpperInvariant();
        _status.Text = $"{Dot("mysql")}    {Dot("login")}    {Dot("game")}        IP pubblico: {_s.PublicIp}:{_s.LoginPort}";
    }

    TabPage DashboardTab()
    {
        var p = new TabPage("ðŸ  Server");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70 };
        bar.Controls.Add(Btn("âš™ Primo setup", Color.SteelBlue, _srv.FirstSetupAsync));
        bar.Controls.Add(Btn("â–¶ Avvia server", Color.SeaGreen, _srv.StartAllAsync));
        bar.Controls.Add(Btn("â–  Ferma server", Color.Firebrick, () => { _srv.StopAll(); return Task.CompletedTask; }));
        bar.Controls.Add(Btn("â†» Riavvia", Color.DarkOrange, async () => { _srv.StopAll(); await Task.Delay(2000); await _srv.StartAllAsync(); }));
        bar.Controls.Add(Btn("📦 Pacchetto amici", Color.MediumPurple, () => { FriendPackage.Create(_s, Log); return Task.CompletedTask; }));
        var statusBar = new Panel { Dock = DockStyle.Top, Height = 34, Padding = new Padding(8) };
        statusBar.Controls.Add(_status);

        var cmdBar = new Panel { Dock = DockStyle.Bottom, Height = 34 };
        var cmd = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Comando console Game server (Invio per inviare)" };
        cmd.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { _srv.SendConsole("game", cmd.Text); cmd.Clear(); e.SuppressKeyPress = true; } };
        cmdBar.Controls.Add(cmd);

        p.Controls.Add(_log); p.Controls.Add(cmdBar); p.Controls.Add(statusBar); p.Controls.Add(bar);
        return p;
    }

    TabPage LiveTab()
    {
        var p = new TabPage("🌐 Giocatori live");
        var info = new Label { Dock = DockStyle.Top, Height = 40, Text = "Aggiornato ogni 3 secondi: connessioni attive a Login/Game e dispositivi Tailscale online." };
        var t = new System.Windows.Forms.Timer { Interval = 3000 };
        t.Tick += async (_, _) =>
        {
            if (!p.Visible) return;
            var rows = await Task.Run(() => Live.Rows(_s));
            _players.DataSource = rows;
        };
        t.Start();
        p.Controls.Add(_players); p.Controls.Add(info);
        return p;
    }

    TabPage AccountsTab()
    {
        var p = new TabPage("ðŸ‘¤ Account");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70 };
        bar.Controls.Add(Btn("â†» Aggiorna", Color.SteelBlue, RefreshAccounts));
        bar.Controls.Add(Btn("ï¼‹ Nuovo account", Color.SeaGreen, async () =>
        {
            var u = Prompt("Nome utente:"); if (string.IsNullOrWhiteSpace(u)) return;
            var pw = Prompt("Password:"); if (string.IsNullOrWhiteSpace(pw)) return;
            await _srv.CreateAccountAsync(u, pw); await RefreshAccounts();
        }));
        bar.Controls.Add(Btn("ðŸ”‘ Cambia password", Color.DarkOrange, async () =>
        {
            if (SelectedId() is not { } id) return;
            var pw = Prompt("Nuova password:"); if (string.IsNullOrWhiteSpace(pw)) return;
            await _srv.SetPasswordAsync(id, pw);
        }));
        bar.Controls.Add(Btn("ðŸ—‘ Elimina", Color.Firebrick, async () =>
        {
            if (SelectedId() is not { } id) return;
            if (MessageBox.Show("Eliminare l'account?", "Conferma", MessageBoxButtons.YesNo) == DialogResult.Yes) { await _srv.DeleteAccountAsync(id); await RefreshAccounts(); }
        }));
        p.Controls.Add(_accounts); p.Controls.Add(bar);
        return p;
    }

    long? SelectedId() => _accounts.CurrentRow?.Cells["id"].Value is { } v ? Convert.ToInt64(v) : null;

    async Task RefreshAccounts() =>
        _accounts.DataSource = await _srv.QueryAsync("aaemu_login", "SELECT id, username, email, last_ip, FROM_UNIXTIME(last_login) AS ultimo_accesso FROM users ORDER BY id");

    TabPage EventsTab()
    {
        var p = new TabPage("ðŸ“… Eventi");
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = 70 };
        bar.Controls.Add(Btn("ï¼‹ Nuovo evento", Color.SeaGreen, () => { _events.Events.Add(new GameEvent()); _events.Save(); RefreshEvents(); return Task.CompletedTask; }));
        bar.Controls.Add(Btn("â–¶ Esegui ora", Color.SteelBlue, async () => { if (_evList.SelectedItem is GameEvent e) await RunEventAsync(e); }));
        bar.Controls.Add(Btn("ðŸ—‘ Elimina", Color.Firebrick, () => { if (_evList.SelectedItem is GameEvent e) { _events.Events.Remove(e); _events.Save(); RefreshEvents(); } return Task.CompletedTask; }));
        var help = new Label
        {
            Dock = DockStyle.Bottom, Height = 60,
            Text = "Azioni: News = cambia le notizie del launcher Â· Sql = esegue SQL sul database di gioco (es. doppia XP, negozi, spawn) Â· " +
                   "ConsoleCommand = invia un comando alla console del Game Â· RestartServer = riavvio programmato. Daily = ripeti ogni giorno all'ora di 'When'."
        };
        _evList.DisplayMember = nameof(GameEvent.Name);
        _evList.SelectedIndexChanged += (_, _) => _evProps.SelectedObject = _evList.SelectedItem;
        _evProps.PropertyValueChanged += (_, _) => { _events.Save(); RefreshEvents(); };
        p.Controls.Add(_evProps); p.Controls.Add(_evList); p.Controls.Add(help); p.Controls.Add(bar);
        RefreshEvents();
        return p;
    }

    void RefreshEvents()
    {
        var sel = _evList.SelectedItem;
        _evList.DataSource = null; _evList.DataSource = _events.Events; _evList.DisplayMember = nameof(GameEvent.Name);
        if (sel != null && _events.Events.Contains(sel)) _evList.SelectedItem = sel;
    }

    async Task RunEventAsync(GameEvent e)
    {
        Log("eventi", $"Eseguo '{e.Name}' ({e.Action})");
        switch (e.Action)
        {
            case EventAction.News: _s.News = e.Payload; _s.Save(); break;
            case EventAction.Sql: Log("eventi", $"Righe modificate: {await _srv.ExecAsync("aaemu_game", e.Payload)}"); break;
            case EventAction.ConsoleCommand: _srv.SendConsole("game", e.Payload); break;
            case EventAction.RestartServer: _srv.StopAll(); await Task.Delay(3000); await _srv.StartAllAsync(); break;
        }
    }

    TabPage SqlTab()
    {
        var p = new TabPage("ðŸ—„ Database");
        var top = new Panel { Dock = DockStyle.Top, Height = 140 };
        var db = new ComboBox { Dock = DockStyle.Left, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        db.Items.AddRange(["aaemu_game", "aaemu_login"]); db.SelectedIndex = 0;
        var q = new TextBox { Dock = DockStyle.Fill, Multiline = true, Font = new Font("Consolas", 10), Text = "SELECT id, name, level FROM characters LIMIT 50" };
        var grid = Grid();
        var run = Btn("â–¶ Esegui", Color.SteelBlue, async () =>
        {
            if (q.Text.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase)) grid.DataSource = await _srv.QueryAsync((string)db.SelectedItem!, q.Text);
            else MessageBox.Show($"Righe modificate: {await _srv.ExecAsync((string)db.SelectedItem!, q.Text)}");
        });
        run.Dock = DockStyle.Right;
        top.Controls.Add(q); top.Controls.Add(db); top.Controls.Add(run);
        p.Controls.Add(grid); p.Controls.Add(top);
        return p;
    }

    TabPage SettingsTab()
    {
        var p = new TabPage("âš™ Impostazioni");
        var pg = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = _s };
        pg.PropertyValueChanged += (_, e) => { _s.Save(); if (e.ChangedItem?.Label == nameof(PanelSettings.ClientDir)) { _s.GamePakDir = ""; _s.AutoDetect(); _api?.ResetManifest(); } };
        var info = new Label { Dock = DockStyle.Top, Height = 50, Text = "PublicIp = il tuo IP (o Radmin/ZeroTier) che danno gli amici Â· GamePakDir = cartella game_pak del client Â· Apri sul router le porte 1237, 1239 e la porta API del launcher." };
        p.Controls.Add(pg); p.Controls.Add(info);
        return p;
    }

    static string? Prompt(string text)
    {
        using var f = new Form { Text = "DaProd", Size = new Size(380, 150), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
        var tb = new TextBox { Left = 12, Top = 32, Width = 340 };
        var ok = new Button { Text = "OK", Left = 272, Top = 64, DialogResult = DialogResult.OK };
        f.Controls.AddRange([new Label { Text = text, Left = 12, Top = 8, AutoSize = true }, tb, ok]);
        f.AcceptButton = ok;
        return f.ShowDialog() == DialogResult.OK ? tb.Text : null;
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

