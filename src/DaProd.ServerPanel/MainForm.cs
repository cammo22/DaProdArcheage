using System.Collections.Concurrent;
using System.Drawing;
using System.Text;
using System.Text.RegularExpressions;

namespace DaProd.ServerPanel;

/// <summary>Finestra principale: guscio leggero (menu, intestazione, log). Le pagine sono in MainForm.Pages.cs e nascono solo quando servono.</summary>
public sealed partial class MainForm : Form
{
    readonly PanelSettings _s = PanelSettings.Load();
    readonly ServerManager _srv;
    readonly EventScheduler _events;
    readonly HealthMonitor _health;
    readonly LauncherApi _api;

    readonly Dictionary<string, Func<Control>> _factories = new();
    readonly Dictionary<string, Control> _pages = new();
    readonly Dictionary<string, Func<Task>> _refreshers = new();
    readonly List<Button> _nav = [];
    readonly Panel _content = Ui.Buffered(new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 12, 20, 14) });
    readonly Pill _pMy = new(), _pLogin = new(), _pWorld = new(), _pZone = new() { Width = 150 }, _pMaint = new() { Width = 130, Visible = false };
    readonly Label _addr = new() { AutoSize = true, ForeColor = Ui.Muted, Margin = new Padding(12, 5, 0, 0) };
    readonly Label _banner = new() { Dock = DockStyle.Top, Height = 0, TextAlign = ContentAlignment.MiddleCenter, BackColor = Color.FromArgb(120, 70, 20), ForeColor = Color.White, Font = Ui.Semi };
    readonly TextBox _log = new() { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, Font = Ui.Mono, BackColor = Color.FromArgb(12, 13, 17), ForeColor = Color.FromArgb(200, 205, 215), BorderStyle = BorderStyle.None };

    readonly ConcurrentQueue<string> _logQ = new();
    static readonly string LogPath = Path.Combine(ServerManager.ServerDir, "logs", "panel.log");
    static readonly Regex Noise = new("GameData|RandomMerchant|Sailing activity|Reopen box|UccGameData|SkillManager|CollectionGameData", RegexOptions.Compiled);
    static readonly Regex KeepWorld = new(@"\[(WARN|ERROR|FATAL)\]|ZoneLoaded|Zone lost|Zone disconnect|DAPROD_ZONE|listening|Registered|Successfully|refused|Returning|Character|Loaded \d+ crafts", RegexOptions.Compiled);
    readonly object _rl = new();
    long _rlWindow; int _rlCount, _rlDropped;

    volatile HealthReport? _last;
    bool _checking, _closing;
    int _tick;
    string _current = "";

    public MainForm()
    {
        Text = "DaProd ArcheAge - Pannello Server";
        Size = new Size(1220, 780); MinimumSize = new Size(1000, 620);
        StartPosition = FormStartPosition.CenterScreen;
        Font = Ui.Normal; BackColor = Ui.Bg; ForeColor = Ui.Text;
        DoubleBuffered = true;

        _s.AutoDetect();
        _srv = new ServerManager(_s, Log);
        _health = new HealthMonitor(_s, _srv);
        _events = new EventScheduler(RunEventAsync);
        _api = new LauncherApi(_s, Log, () => Live.Snapshot(_s, _srv, _last), _srv, _events);

        // pagine: create solo alla prima visita
        _factories["Server"] = ServerPage; _factories["Zone"] = ZonesPage; _factories["Giocatori"] = PlayersPage; _factories["Mondo"] = WorldPage; _factories["Regole"] = RulesPage;
        _factories["Eventi"] = EventsPage; _factories["Backup"] = BackupPage; _factories["Database"] = SqlPage; _factories["Impostazioni"] = SettingsPage;

        var side = new Panel { Dock = DockStyle.Left, Width = 190, BackColor = Ui.Side };
        var nav = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(0, 6, 0, 0), WrapContents = false };
        foreach (var name in _factories.Keys)
        {
            var b = new Button { Text = "   " + name, Width = 190, Height = 44, FlatStyle = FlatStyle.Flat, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Ui.Text, BackColor = Ui.Side, Margin = new Padding(0), Font = Ui.Nav, Tag = name };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => Show(name);
            _nav.Add(b); nav.Controls.Add(b);
        }
        side.Controls.Add(nav);
        side.Controls.Add(new Label { Text = "DaProd\nArcheAge", Dock = DockStyle.Top, Height = 78, TextAlign = ContentAlignment.MiddleCenter, Font = Ui.Huge, ForeColor = Ui.Accent });

        var pills = Ui.Buffered(new FlowLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12, 13, 0, 0), WrapContents = false });
        pills.Controls.AddRange([_pMy, _pLogin, _pWorld, _pZone, _pMaint, _addr]);
        var header = new Panel { Dock = DockStyle.Top, Height = 54, BackColor = Ui.Card };
        header.Controls.Add(pills);

        Controls.Add(_content); Controls.Add(_banner); Controls.Add(header); Controls.Add(side);
        Show("Server");

        _api.Start();
        _ = Task.Run(() => { try { FriendLauncher.EnsureBuilt(_s); } catch { } }); // prepara il file per gli amici
        var t = new System.Windows.Forms.Timer { Interval = 1000 };
        t.Tick += async (_, _) => await OnTick();
        t.Start();

        Shown += async (_, _) =>
        {
            try
            {
                await EnsureGameData();
                if (_s.AutoStart && !_s.Maintenance) await Task.Run(() => _srv.StartAllAsync());
            }
            catch (Exception ex) { Log("panel", "Avvio automatico fallito: " + ex.Message); }
        };
        FormClosing += OnClosing;
        AppDomain.CurrentDomain.ProcessExit += (_, _) => { try { _srv.StopAll(); } catch { } };
    }

    // ------------------------------------------------------------------ chiusura ordinata
    async void OnClosing(object? sender, FormClosingEventArgs e)
    {
        if (_closing) return;
        var running = _srv.IsRunning("mysql") || _srv.IsRunning("login") || _srv.IsRunning("game");
        if (running && MessageBox.Show("Chiudendo il pannello si ferma tutto il server e i giocatori vengono scollegati.\nChiudere?", "Esci", MessageBoxButtons.YesNo) == DialogResult.No)
        { e.Cancel = true; return; }
        e.Cancel = true; _closing = true;
        Enabled = false; Text = "DaProd ArcheAge - chiusura in corso...";
        await Task.Run(() => { try { _srv.StopAll(); } catch { } });
        _srv.Dispose();
        Application.Exit();
    }

    // ------------------------------------------------------------------ navigazione
    void Show(string name)
    {
        if (!_pages.TryGetValue(name, out var page)) _pages[name] = page = _factories[name]();
        _content.SuspendLayout();
        _content.Controls.Clear();
        _content.Controls.Add(page);
        _content.ResumeLayout();
        _current = name;
        foreach (var b in _nav) { var on = (string)b.Tag! == name; b.BackColor = on ? Ui.Card : Ui.Side; b.ForeColor = on ? Ui.Accent : Ui.Text; }
        if (_refreshers.TryGetValue(name, out var r)) _ = SafeRun(r);
    }

    async Task SafeRun(Func<Task> f) { try { await f(); } catch (Exception ex) { Log("panel", ex.Message); } }

    // ------------------------------------------------------------------ log (a prova di scatti)
    void Log(string src, string line)
    {
        _health?.OnLog(line);
        if (Noise.IsMatch(line)) return;
        if (src == "game" && !KeepWorld.IsMatch(line)) return;
        lock (_rl)
        {
            var now = Environment.TickCount64;
            if (now - _rlWindow > 1000)
            {
                if (_rlDropped > 0) _logQ.Enqueue($"[{DateTime.Now:HH:mm:ss}] [panel] ({_rlDropped} righe omesse: troppo rapide)");
                _rlWindow = now; _rlCount = 0; _rlDropped = 0;
            }
            if (++_rlCount > 30) { _rlDropped++; return; }
        }
        _logQ.Enqueue($"[{DateTime.Now:HH:mm:ss}] [{src}] {line}");
    }

    void DrainLog()
    {
        if (_logQ.IsEmpty) return;
        var sb = new StringBuilder(); var n = 0;
        while (n < 200 && _logQ.TryDequeue(out var l)) { sb.AppendLine(l); n++; }
        var txt = sb.ToString();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LogPath)!);
            if (File.Exists(LogPath) && new FileInfo(LogPath).Length > 10_000_000) File.Move(LogPath, LogPath + ".old", true);
            File.AppendAllText(LogPath, txt);
        }
        catch { }
        _log.AppendText(txt);
        if (_log.TextLength > 120_000) { _log.Text = _log.Text[^60_000..]; _log.SelectionStart = _log.TextLength; _log.ScrollToCaret(); }
    }

    // ------------------------------------------------------------------ ciclo di aggiornamento (1 al secondo)
    async Task OnTick()
    {
        if (_closing) return;
        DrainLog();
        UpdateHeader();
        _tick++;
        if (_tick % 3 == 0 && !_checking)
        {
            _checking = true;
            try { var r = await Task.Run(_health.Check); _last = r; ApplyHealth(r); }
            catch { }
            finally { _checking = false; }
        }
        if (_tick % 4 == 0 && _refreshers.TryGetValue(_current, out var refresh)) await SafeRun(refresh);
    }

    void UpdateHeader()
    {
        _pMy.Set(_srv.IsRunning("mysql") ? "MySQL ON" : "MySQL OFF", _srv.IsRunning("mysql") ? Ui.Green : Ui.Red);
        _pLogin.Set(_srv.IsRunning("login") ? "Login ON" : "Login OFF", _srv.IsRunning("login") ? Ui.Green : Ui.Red);
        _pWorld.Set(_srv.IsRunning("game") ? "World ON" : "World OFF", _srv.IsRunning("game") ? Ui.Green : Ui.Red);
        var (l, t) = (_srv.ZonesLoaded, _srv.ZonesTotal);
        _pZone.Set($"Zone {l}/{t}", !_srv.IsRunning("game") ? Ui.Grey : l >= t ? Ui.Green : Ui.Yellow);
        if (_pMaint.Visible != _s.Maintenance) _pMaint.Visible = _s.Maintenance;
        _pMaint.Set("MANUTENZIONE", Ui.Orange);
        var a = $"launcher: http://{_s.PublicIp}:{_s.LauncherApiPort}";
        if (_addr.Text != a) _addr.Text = a;
        var bh = _s.Maintenance ? 32 : 0;
        if (_banner.Height != bh) { _banner.Height = bh; _banner.Text = "MODALITA' MANUTENZIONE ATTIVA - i giocatori non possono entrare"; }
    }

    // ------------------------------------------------------------------ eventi programmati
    async Task RunEventAsync(GameEvent e)
    {
        Log("eventi", $"Eseguo '{e.Name}' ({e.Action})");
        switch (e.Action)
        {
            case EventAction.News: _s.News = e.Payload; _s.Save(); break;
            case EventAction.Sql: Log("eventi", $"Righe modificate: {await _srv.ExecAsync("aaemu_game", e.Payload)}"); break;
            case EventAction.ConsoleCommand: _srv.SendConsole("game", e.Payload); break;
            case EventAction.RestartServer: await Task.Run(() => _srv.StopAll()); await Task.Delay(3000); await Task.Run(() => _srv.StartAllAsync()); break;
            case EventAction.Backup: await _srv.BackupNowAsync(); break;
        }
    }

    // ------------------------------------------------------------------ primo avvio: pacchetto dati
    async Task EnsureGameData()
    {
        if (!GameDataPack.Missing(_s)) return;
        var pack = GameDataPack.Find();
        if (pack == null)
        {
            MessageBox.Show("Primo avvio: mancano i dati del gioco.\n\nSeleziona il file " + GameDataPack.FileName +
                            " (si crea dal pannello di un altro PC col pulsante 'Pacchetto dati').", "DaProd - Primo avvio");
            using var d = new OpenFileDialog { Filter = "Pacchetto dati|" + GameDataPack.FileName + ";*.7z", Title = "Seleziona " + GameDataPack.FileName };
            if (d.ShowDialog(this) != DialogResult.OK) { Log("panel", "Dati del gioco mancanti: il server non potrà partire."); return; }
            pack = d.FileName;
        }
        else if (MessageBox.Show($"Trovato {pack}.\nInstallo i dati del gioco ora? Ci vogliono alcuni minuti.", "DaProd - Primo avvio", MessageBoxButtons.YesNo) == DialogResult.No) return;
        _srv.Activity = "Estraggo i dati del gioco...";
        try { await GameDataPack.InstallAsync(pack, t => _srv.Activity = t); _s.MySqlBinDir = ""; _api.ResetManifest(); }
        finally { _srv.Activity = ""; }
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "DaProdServerPanel", out var first);
        if (!first) { MessageBox.Show("Il pannello DaProd è già aperto.", "DaProd"); return; }
        ApplicationConfiguration.Initialize();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "DaProd Server");
        Application.Run(new MainForm());
    }
}
