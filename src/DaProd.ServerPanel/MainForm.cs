using System.Drawing;

namespace DaProd.ServerPanel;

/// <summary>Colori e controlli comuni del tema scuro.</summary>
static class Theme
{
    public static readonly Color Bg = Color.FromArgb(22, 24, 31);
    public static readonly Color Side = Color.FromArgb(16, 17, 23);
    public static readonly Color Card = Color.FromArgb(32, 35, 45);
    public static readonly Color Text = Color.FromArgb(230, 232, 238);
    public static readonly Color Muted = Color.FromArgb(150, 155, 170);
    public static readonly Color Accent = Color.FromArgb(214, 170, 76);
    public static readonly Color Green = Color.FromArgb(52, 168, 110);
    public static readonly Color Red = Color.FromArgb(200, 70, 70);
    public static readonly Color Blue = Color.FromArgb(60, 120, 200);
    public static readonly Color Orange = Color.FromArgb(215, 130, 50);
    public static readonly Color Purple = Color.FromArgb(130, 95, 200);

    public static DataGridView Grid()
    {
        var g = new DataGridView
        {
            Dock = DockStyle.Fill, ReadOnly = true, AllowUserToAddRows = false, RowHeadersVisible = false,
            SelectionMode = DataGridViewSelectionMode.FullRowSelect, AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill,
            BackgroundColor = Card, BorderStyle = BorderStyle.None, GridColor = Bg, EnableHeadersVisualStyles = false,
            ColumnHeadersHeight = 34, ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing
        };
        g.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Side, ForeColor = Accent, Font = new Font("Segoe UI Semibold", 10) };
        g.DefaultCellStyle = new DataGridViewCellStyle { BackColor = Card, ForeColor = Text, SelectionBackColor = Blue, SelectionForeColor = Color.White };
        g.RowTemplate.Height = 30;
        return g;
    }

    public static Label Title(string t) => new() { Text = t, Dock = DockStyle.Top, Height = 44, Font = new Font("Segoe UI Semibold", 16), ForeColor = Text };
    public static Label Hint(string t, int h = 44) => new() { Text = t, Dock = DockStyle.Top, Height = h, ForeColor = Muted };
    public static FlowLayoutPanel Bar() => new() { Dock = DockStyle.Top, Height = 56, Padding = new Padding(0, 4, 0, 4) };
}

public sealed class MainForm : Form
{
    readonly PanelSettings _s = PanelSettings.Load();
    readonly ServerManager _srv;
    readonly EventScheduler _events;
    LauncherApi? _api;

    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = new Font("Consolas", 9.5f), BackColor = Color.FromArgb(12, 13, 17), ForeColor = Color.FromArgb(200, 205, 215), BorderStyle = BorderStyle.None };
    readonly FlowLayoutPanel _pills = new() { Dock = DockStyle.Fill, Padding = new Padding(12, 14, 0, 0) };
    readonly Label _maintBanner = new() { Dock = DockStyle.Top, Height = 0, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(120, 70, 20), ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 11) };
    readonly DataGridView _accounts = Theme.Grid();
    readonly DataGridView _players = Theme.Grid();
    readonly ListBox _evList = new() { Dock = DockStyle.Left, Width = 260, BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None, Font = new Font("Segoe UI", 11) };
    readonly PropertyGrid _evProps = new() { Dock = DockStyle.Fill };
    readonly Panel _content = new() { Dock = DockStyle.Fill, Padding = new Padding(20, 12, 20, 16) };
    readonly Dictionary<string, Control> _pages = new();
    readonly List<Button> _nav = [];
    Button? _maintBtn;
    HealthMonitor? _health;
    volatile HealthReport? _lastHealth;
    readonly Panel _card = new() { Dock = DockStyle.Top, Height = 150, Padding = new Padding(18, 12, 18, 12), BackColor = Color.DimGray };
    readonly Label _cardHead = new() { Dock = DockStyle.Top, Height = 34, Font = new Font("Segoe UI Semibold", 15), ForeColor = Color.White };
    readonly Label _cardAdvice = new() { Dock = DockStyle.Top, Height = 26, ForeColor = Color.White };
    readonly FlowLayoutPanel _svcCards = new() { Dock = DockStyle.Fill, Padding = new Padding(0, 8, 0, 0) };

    public MainForm()
    {
        Text = "DaProd ArcheAge - Pannello Server";
        Size = new Size(1200, 760); MinimumSize = new Size(980, 600);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 10); BackColor = Theme.Bg; ForeColor = Theme.Text;

        _s.AutoDetect();
        _srv = new ServerManager(_s, Log);
        _health = new HealthMonitor(_s, _srv);
        _events = new EventScheduler(RunEventAsync);

        _pages["Server"] = DashboardPage();
        _pages["Giocatori"] = PlayersPage();
        _pages["Account"] = AccountsPage();
        _pages["Eventi"] = EventsPage();
        _pages["Database"] = SqlPage();
        _pages["Impostazioni"] = SettingsPage();

        var side = new Panel { Dock = DockStyle.Left, Width = 200, BackColor = Theme.Side };
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 10, 0, 0) };
        foreach (var name in _pages.Keys)
        {
            var b = new Button { Text = "   " + name, Width = 200, Height = 46, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, BackColor = Theme.Side, Margin = new Padding(0), Font = new Font("Segoe UI", 11) };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => Show(name);
            _nav.Add(b); nav.Controls.Add(b);
        }
        var logo = new Label { Text = "DaProd\nArcheAge", Dock = DockStyle.Top, Height = 80, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI Semibold", 15), ForeColor = Theme.Accent };
        side.Controls.Add(nav); side.Controls.Add(logo);

        var header = new Panel { Dock = DockStyle.Top, Height = 56, BackColor = Theme.Card };
        header.Controls.Add(_pills);

        Controls.Add(_content); Controls.Add(_maintBanner); Controls.Add(header); Controls.Add(side);
        Show("Server");

        _api = new LauncherApi(_s, Log, () => Live.Snapshot(_s, _srv, _lastHealth), _srv);
        _api.Start();
        var t = new System.Windows.Forms.Timer { Interval = 1500 };
        t.Tick += (_, _) => UpdateStatus();
        var ht = new System.Windows.Forms.Timer { Interval = 2000 };
        ht.Tick += async (_, _) => { try { ShowHealth(_lastHealth = await Task.Run(() => _health!.Check())); } catch { } };
        ht.Start();
        t.Start();
        UpdateStatus();

        Shown += async (_, _) =>
        {
            await EnsureGameData();
            if (!_s.AutoStart) return;
            try
            {
                await _srv.FirstSetupAsync();
                if (_s.Maintenance) Log("panel", "Manutenzione attiva: Login e Game restano spenti.");
                else await _srv.StartAllAsync();
            }
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

    void Show(string name)
    {
        _content.Controls.Clear();
        _content.Controls.Add(_pages[name]);
        foreach (var b in _nav)
        {
            var active = b.Text.Trim() == name;
            b.BackColor = active ? Theme.Card : Theme.Side;
            b.ForeColor = active ? Theme.Accent : Theme.Text;
        }
        if (name == "Account") _ = SafeRun(RefreshAccounts);
    }

    static Button Btn(string text, Color c, Func<Task> onClick, int width = 170)
    {
        var b = new Button { Text = text, Width = width, Height = 42, BackColor = c, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Margin = new Padding(0, 0, 10, 0), Font = new Font("Segoe UI Semibold", 10) };
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

    async Task SafeRun(Func<Task> f) { try { await f(); } catch (Exception ex) { Log("panel", ex.Message); } }

    void Log(string src, string line)
    {
        _health?.OnLog(line);
        var text = $"[{DateTime.Now:HH:mm:ss}] [{src}] {line}\r\n";
        if (IsHandleCreated) BeginInvoke(() => { if (_log.TextLength > 500_000) _log.Clear(); _log.AppendText(text); });
    }

    static Label Pill(string text, Color c) => new()
    {
        Text = text, AutoSize = false, Width = 120, Height = 28, TextAlign = ContentAlignment.MiddleCenter,
        BackColor = c, ForeColor = Color.White, Margin = new Padding(0, 0, 8, 0), Font = new Font("Segoe UI Semibold", 9.5f)
    };

    void UpdateStatus()
    {
        _pills.SuspendLayout();
        _pills.Controls.Clear();
        foreach (var n in new[] { "mysql", "login", "game" })
            _pills.Controls.Add(Pill((n == "mysql" ? "MySQL" : n == "login" ? "Login" : "World") + (_srv.IsRunning(n) ? "  ON" : "  OFF"), _srv.IsRunning(n) ? Theme.Green : Theme.Red));
        if (_s.Maintenance) _pills.Controls.Add(Pill("MANUTENZIONE", Theme.Orange));
        _pills.Controls.Add(new Label { AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(12, 5, 0, 0), Text = $"Indirizzo launcher: http://{_s.PublicIp}:{_s.LauncherApiPort}" });
        _pills.ResumeLayout();
        _maintBanner.Height = _s.Maintenance ? 34 : 0;
        _maintBanner.Text = "MODALITA' MANUTENZIONE ATTIVA - i giocatori non possono entrare";
        if (_maintBtn != null) { _maintBtn.Text = _s.Maintenance ? "Fine manutenzione" : "Manutenzione"; _maintBtn.BackColor = _s.Maintenance ? Theme.Green : Theme.Orange; }
    }

    static Color StateColor(HealthState st) => st switch
    {
        HealthState.Operativo => Theme.Green, HealthState.Avvio => Color.FromArgb(200, 160, 30),
        HealthState.Problema => Theme.Red, _ => Color.FromArgb(80, 84, 96)
    };

    void ShowHealth(HealthReport r)
    {
        _card.BackColor = StateColor(r.Overall);
        _cardHead.Text = r.Headline;
        _cardAdvice.Text = r.Advice;
        _svcCards.SuspendLayout(); _svcCards.Controls.Clear();
        foreach (var sv in r.Services)
            _svcCards.Controls.Add(MiniCard(sv.Name, sv.State.ToString().ToUpperInvariant(), sv.Detail, StateColor(sv.State)));
        var pcBad = r.CpuPc > 95 || r.RamPc > 92 || r.DiskFreeGb < 5;
        _svcCards.Controls.Add(MiniCard("Il tuo PC", pcBad ? "SOTTO SFORZO" : "OK", $"CPU {r.CpuPc:F0}%  RAM {r.RamPc:F0}%  Disco {r.DiskFreeGb:F0} GB", pcBad ? Theme.Red : Color.FromArgb(50, 54, 66)));
        _svcCards.ResumeLayout();
    }

    static Panel MiniCard(string title, string state, string detail, Color c)
    {
        var p = new Panel { Width = 176, Height = 62, BackColor = Color.FromArgb(40, 0, 0, 0), Margin = new Padding(0, 0, 8, 0), Padding = new Padding(8, 4, 4, 4) };
        p.BackColor = ControlPaint.Dark(c, 0.1f);
        p.Controls.Add(new Label { Text = detail, Dock = DockStyle.Top, Height = 20, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5f) });
        p.Controls.Add(new Label { Text = state, Dock = DockStyle.Top, Height = 18, ForeColor = Color.White, Font = new Font("Segoe UI Semibold", 9) });
        p.Controls.Add(new Label { Text = title, Dock = DockStyle.Top, Height = 18, ForeColor = Color.White, Font = new Font("Segoe UI", 8.5f) });
        return p;
    }

    /// <summary>Su un PC nuovo chiede il pacchetto dati del gioco (unica domanda del setup).</summary>
    async Task EnsureGameData()
    {
        if (!GameDataPack.Missing(_s)) return;
        MessageBox.Show("Mancano i dati del gioco su questo PC.\nSeleziona il file " + GameDataPack.FileName +
                        " (creato col pulsante Pacchetto dati sul PC dove il server funziona).", "Primo avvio");
        using var d = new OpenFileDialog { Filter = "Pacchetto dati|*.zip", Title = "Seleziona " + GameDataPack.FileName };
        if (d.ShowDialog(this) != DialogResult.OK) { Log("panel", "Dati del gioco mancanti: il server non potrà caricare le mappe."); return; }
        await GameDataPack.InstallAsync(_s, d.FileName, t => Log("dati", t));
        _api?.ResetManifest();
    }

    async Task CheckUpdates()
    {
        Log("panel", $"Versione attuale {Updater.Current}. Controllo GitHub ({_s.GitHubRepo})...");
        var upd = await Updater.CheckAsync(_s);
        if (upd == null) { MessageBox.Show($"Hai già l'ultima versione ({Updater.Current}).", "Aggiornamenti"); return; }
        if (MessageBox.Show($"Disponibile la versione {upd.Value.ver}. Aggiornare ora?\nIl server verrà fermato e il pannello si riaprirà da solo.",
                "Aggiornamenti", MessageBoxButtons.YesNo) != DialogResult.Yes) return;
        await Updater.InstallAsync(_s, upd.Value.assetApiUrl, t => Log("update", t));
        _srv.StopAll();
        Environment.Exit(0);
    }

    async Task ToggleMaintenance()
    {
        if (!_s.Maintenance)
        {
            var msg = Prompt("Messaggio per i giocatori:", _s.MaintenanceMessage);
            if (msg == null) return;
            _s.MaintenanceMessage = msg; _s.Maintenance = true; _s.Save();
            // spengo Login e Game, MySQL resta acceso per lavorare sul database
            _srv.Stop("game"); _srv.Stop("login");
            Log("panel", "Manutenzione ATTIVATA.");
        }
        else
        {
            _s.Maintenance = false; _s.Save();
            Log("panel", "Manutenzione terminata: riavvio il server.");
            await _srv.StartAllAsync();
        }
        UpdateStatus();
    }

    Control DashboardPage()
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var bar = Theme.Bar();
        bar.Controls.Add(Btn("Avvia server", Theme.Green, async () => { if (_s.Maintenance) { MessageBox.Show("Disattiva prima la manutenzione."); return; } await _srv.StartAllAsync(); }, 150));
        bar.Controls.Add(Btn("Ferma server", Theme.Red, () => { _srv.StopAll(); return Task.CompletedTask; }, 150));
        bar.Controls.Add(Btn("Riavvia", Theme.Blue, async () => { _srv.StopAll(); await Task.Delay(2000); await _srv.StartAllAsync(); }, 120));
        bar.Controls.Add(_maintBtn = Btn("Manutenzione", Theme.Orange, ToggleMaintenance, 170));
        bar.Controls.Add(Btn("Pacchetto amici", Theme.Purple, () => { FriendPackage.Create(_s, Log); return Task.CompletedTask; }, 170));
        bar.Controls.Add(Btn("Aggiornamenti", Theme.Blue, CheckUpdates, 150));
        bar.Controls.Add(Btn("Pacchetto dati", Theme.Card, () => GameDataPack.CreateAsync(_s, t => Log("dati", t)), 140));

        var cmdBar = new Panel { Dock = DockStyle.Bottom, Height = 36, Padding = new Padding(0, 6, 0, 0) };
        var cmd = new TextBox { Dock = DockStyle.Fill, PlaceholderText = "Comando per la console del Game server (Invio per inviare)", BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.FixedSingle };
        cmd.KeyDown += (_, e) => { if (e.KeyCode == Keys.Enter) { _srv.SendConsole("game", cmd.Text); cmd.Clear(); e.SuppressKeyPress = true; } };
        cmdBar.Controls.Add(cmd);

        _card.Controls.Add(_svcCards); _card.Controls.Add(_cardAdvice); _card.Controls.Add(_cardHead);
        p.Controls.Add(_log); p.Controls.Add(cmdBar); p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 }); p.Controls.Add(bar);
        p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10 }); p.Controls.Add(_card);
        p.Controls.Add(Theme.Title("Server"));
        return p;
    }

    Control PlayersPage()
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var t = new System.Windows.Forms.Timer { Interval = 3000 };
        t.Tick += async (_, _) =>
        {
            if (!p.Visible) return;
            try { _players.DataSource = await Task.Run(() => Live.Rows(_s)); } catch { }
        };
        t.Start();
        p.Controls.Add(_players);
        p.Controls.Add(Theme.Hint("Aggiornato ogni 3 secondi: chi è in gioco, al login o sta scaricando, e i dispositivi collegati."));
        p.Controls.Add(Theme.Title("Giocatori in tempo reale"));
        return p;
    }

    Control AccountsPage()
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var bar = Theme.Bar();
        bar.Controls.Add(Btn("Aggiorna", Theme.Blue, RefreshAccounts, 120));
        bar.Controls.Add(Btn("Nuovo account", Theme.Green, async () =>
        {
            var u = Prompt("Nome utente:"); if (string.IsNullOrWhiteSpace(u)) return;
            var pw = Prompt("Password:"); if (string.IsNullOrWhiteSpace(pw)) return;
            await _srv.CreateAccountAsync(u, pw); await RefreshAccounts();
        }, 150));
        bar.Controls.Add(Btn("Cambia password", Theme.Orange, async () =>
        {
            if (SelectedId() is not { } id) return;
            var pw = Prompt("Nuova password:"); if (string.IsNullOrWhiteSpace(pw)) return;
            await _srv.SetPasswordAsync(id, pw);
        }, 160));
        bar.Controls.Add(Btn("Elimina", Theme.Red, async () =>
        {
            if (SelectedId() is not { } id) return;
            if (MessageBox.Show("Eliminare l'account?", "Conferma", MessageBoxButtons.YesNo) == DialogResult.Yes) { await _srv.DeleteAccountAsync(id); await RefreshAccounts(); }
        }, 110));
        p.Controls.Add(_accounts); p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 }); p.Controls.Add(bar);
        p.Controls.Add(Theme.Hint("Gli utenti possono anche registrarsi da soli dal launcher (Impostazioni > AllowRegistration)."));
        p.Controls.Add(Theme.Title("Account"));
        return p;
    }

    long? SelectedId() => _accounts.CurrentRow?.Cells["id"].Value is { } v ? Convert.ToInt64(v) : null;

    async Task RefreshAccounts() =>
        _accounts.DataSource = await _srv.QueryAsync("aaemu_login", "SELECT id, username AS utente, last_ip AS ultimo_ip, FROM_UNIXTIME(last_login) AS ultimo_accesso FROM users ORDER BY id");

    Control EventsPage()
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var bar = Theme.Bar();
        bar.Controls.Add(Btn("Nuovo evento", Theme.Green, () => { _events.Events.Add(new GameEvent()); _events.Save(); RefreshEvents(); return Task.CompletedTask; }, 150));
        bar.Controls.Add(Btn("Esegui ora", Theme.Blue, async () => { if (_evList.SelectedItem is GameEvent e) await RunEventAsync(e); }, 130));
        bar.Controls.Add(Btn("Elimina", Theme.Red, () => { if (_evList.SelectedItem is GameEvent e) { _events.Events.Remove(e); _events.Save(); RefreshEvents(); } return Task.CompletedTask; }, 110));
        _evList.DisplayMember = nameof(GameEvent.Name);
        _evList.SelectedIndexChanged += (_, _) => _evProps.SelectedObject = _evList.SelectedItem;
        _evProps.PropertyValueChanged += (_, _) => { _events.Save(); RefreshEvents(); };
        var body = new Panel { Dock = DockStyle.Fill };
        body.Controls.Add(_evProps); body.Controls.Add(new Panel { Dock = DockStyle.Left, Width = 10 }); body.Controls.Add(_evList);
        p.Controls.Add(body); p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 8 }); p.Controls.Add(bar);
        p.Controls.Add(Theme.Hint("News = cambia le notizie del launcher  |  Sql = esegue SQL sul database di gioco  |  ConsoleCommand = comando alla console del Game  |  " +
                                  "RestartServer = riavvio.  Daily = ripeti ogni giorno all'ora indicata in When.", 50));
        p.Controls.Add(Theme.Title("Eventi programmati"));
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

    Control SqlPage()
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var top = new Panel { Dock = DockStyle.Top, Height = 130 };
        var db = new ComboBox { Dock = DockStyle.Left, Width = 140, DropDownStyle = ComboBoxStyle.DropDownList };
        db.Items.AddRange(["aaemu_game", "aaemu_login"]); db.SelectedIndex = 0;
        var q = new TextBox { Dock = DockStyle.Fill, Multiline = true, Font = new Font("Consolas", 10.5f), Text = "SELECT id, name, level FROM characters LIMIT 50", BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None };
        var grid = Theme.Grid();
        var run = Btn("Esegui", Theme.Blue, async () =>
        {
            if (q.Text.TrimStart().StartsWith("select", StringComparison.OrdinalIgnoreCase)) grid.DataSource = await _srv.QueryAsync((string)db.SelectedItem!, q.Text);
            else MessageBox.Show($"Righe modificate: {await _srv.ExecAsync((string)db.SelectedItem!, q.Text)}");
        }, 110);
        run.Dock = DockStyle.Right;
        top.Controls.Add(q); top.Controls.Add(db); top.Controls.Add(run);
        p.Controls.Add(grid); p.Controls.Add(new Panel { Dock = DockStyle.Top, Height = 10 }); p.Controls.Add(top);
        p.Controls.Add(Theme.Title("Database"));
        return p;
    }

    Control SettingsPage()
    {
        var p = new Panel { Dock = DockStyle.Fill };
        var pg = new PropertyGrid { Dock = DockStyle.Fill, SelectedObject = _s };
        pg.PropertyValueChanged += (_, e) =>
        {
            _s.Save();
            if (e.ChangedItem?.Label == nameof(PanelSettings.ClientDir)) { _s.GamePakDir = ""; _s.AutoDetect(); _api?.ResetManifest(); }
            UpdateStatus();
        };
        p.Controls.Add(pg);
        p.Controls.Add(Theme.Hint("ClientDir = cartella del client da condividere con i launcher  |  TailscaleAuthKey = chiave Reusable + Ephemeral da login.tailscale.com (gli amici non usano email)  |  " +
                                  "AllowRegistration = registrazione dal launcher  |  Le modifiche a porte e rete valgono dal prossimo riavvio.", 50));
        p.Controls.Add(Theme.Title("Impostazioni"));
        return p;
    }

    static string? Prompt(string text, string value = "")
    {
        using var f = new Form { Text = "DaProd", Size = new Size(440, 160), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = new Font("Segoe UI", 10) };
        var tb = new TextBox { Left = 14, Top = 38, Width = 395, Text = value };
        var ok = new Button { Text = "OK", Left = 309, Top = 72, Width = 100, Height = 32, DialogResult = DialogResult.OK, BackColor = Theme.Blue, ForeColor = Color.White, FlatStyle = FlatStyle.Flat };
        f.Controls.AddRange([new Label { Text = text, Left = 14, Top = 12, AutoSize = true }, tb, ok]);
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
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "DaProd Server");
        Application.Run(new MainForm());
    }
}
