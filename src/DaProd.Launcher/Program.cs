using System.Diagnostics;
using System.Drawing;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DaProd.Launcher;

public sealed class LauncherSettings
{
    public string ServerUrl { get; set; } = "";
    public string TailscaleAuthKey { get; set; } = "";
    public string GameDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaProdArcheage", "Game");
    public string Username { get; set; } = "";
    public bool Remember { get; set; }
    /// <summary>Password cifrata con DPAPI: leggibile solo da questo utente Windows su questo PC.</summary>
    public string SavedPassword { get; set; } = "";

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DaProdLauncher");
    static string FilePath => Path.Combine(Dir, "launcher.json");

    public static LauncherSettings Load()
    {
        LauncherSettings s;
        try { s = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath)) ?? new(); } catch { s = new(); }
        // server.json accanto all'exe (dal "Pacchetto amici") ha sempre la precedenza
        try
        {
            var pkg = Path.Combine(AppContext.BaseDirectory, "server.json");
            if (File.Exists(pkg))
            {
                using var d = JsonDocument.Parse(File.ReadAllText(pkg));
                if (d.RootElement.TryGetProperty("api", out var a)) s.ServerUrl = a.GetString() ?? s.ServerUrl;
                if (d.RootElement.TryGetProperty("tailscaleAuthKey", out var k)) s.TailscaleAuthKey = k.GetString() ?? "";
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(s.ServerUrl)) s.ServerUrl = "http://127.0.0.1:8080";
        return s;
    }

    public void Save() { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }

    public string GetPassword()
    {
        try { return SavedPassword == "" ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(SavedPassword), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    public void SetPassword(string pwd) =>
        SavedPassword = pwd == "" ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(pwd), null, DataProtectionScope.CurrentUser));

    /// <summary>File installati (percorso -> dimensione/data): serve a scaricare solo le patch.</summary>
    static string InstalledPath => Path.Combine(Dir, "installed.json");
    public static Dictionary<string, ManifestFile> LoadInstalled()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, ManifestFile>>(File.ReadAllText(InstalledPath)) ?? []; } catch { return []; }
    }
    public static void SaveInstalled(Dictionary<string, ManifestFile> m) { Directory.CreateDirectory(Dir); File.WriteAllText(InstalledPath, JsonSerializer.Serialize(m)); }
}

public sealed record Live(bool online, int players, string time);
public sealed record ServerInfo(string name, string ip, int port, string news, string clientUrl, string launchArgs, Live? live,
    int? gamePort = null, int? streamPort = null, bool maintenance = false, string? maintenanceMessage = null, bool registration = true);
public sealed record ManifestFile(string path, long size, long time);

static class Theme
{
    public static readonly Color Bg = Color.FromArgb(18, 20, 27);
    public static readonly Color Card = Color.FromArgb(30, 33, 43);
    public static readonly Color Text = Color.FromArgb(232, 234, 240);
    public static readonly Color Muted = Color.FromArgb(150, 155, 170);
    public static readonly Color Gold = Color.FromArgb(222, 178, 82);
    public static readonly Color Green = Color.FromArgb(52, 168, 110);
    public static readonly Color Red = Color.FromArgb(205, 75, 75);
    public static readonly Color Orange = Color.FromArgb(220, 135, 50);
    public static readonly Color Blue = Color.FromArgb(60, 120, 200);
}

public sealed class LauncherForm : Form
{
    readonly LauncherSettings _s = LauncherSettings.Load();
    HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    HttpClient _dl = new() { Timeout = Timeout.InfiniteTimeSpan };
    TailscaleTunnel? _tunnel;
    ServerInfo? _info;
    List<ManifestFile> _patch = [];
    bool _busy;

    readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI Semibold", 22), ForeColor = Theme.Gold, Text = "DaProd ArcheAge" };
    readonly Label _live = new() { AutoSize = false, Width = 300, Height = 30, TextAlign = ContentAlignment.MiddleCenter, Font = new Font("Segoe UI Semibold", 10), ForeColor = Color.White, BackColor = Theme.Orange, Text = "Connessione..." };
    readonly Label _news = new() { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = new Font("Segoe UI", 11), Padding = new Padding(14) };
    readonly TextBox _user = new() { Width = 230, Font = new Font("Segoe UI", 11) };
    readonly TextBox _pass = new() { Width = 230, UseSystemPasswordChar = true, Font = new Font("Segoe UI", 11) };
    readonly CheckBox _remember = new() { Text = "Ricorda credenziali", AutoSize = true, ForeColor = Theme.Text };
    readonly Label _update = new() { AutoSize = true, ForeColor = Theme.Muted, Text = "" };
    readonly ProgressBar _bar = new() { Dock = DockStyle.Fill };
    readonly Label _state = new() { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft };
    readonly Button _play, _register;

    public LauncherForm()
    {
        Text = "DaProd ArcheAge Launcher";
        ClientSize = new Size(900, 600);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Bg; ForeColor = Theme.Text; Font = new Font("Segoe UI", 10);

        _user.Text = _s.Username;
        _remember.Checked = _s.Remember;
        if (_s.Remember) _pass.Text = _s.GetPassword();
        _pass.KeyDown += async (_, e) => { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; await Run(_play!, Play); } };

        // intestazione
        var header = new Panel { Dock = DockStyle.Top, Height = 80, Padding = new Padding(28, 16, 28, 0) };
        _title.Location = new Point(26, 16);
        _live.Dock = DockStyle.Right;
        var liveHost = new Panel { Dock = DockStyle.Right, Width = 300, Padding = new Padding(0, 10, 0, 14) };
        liveHost.Controls.Add(_live);
        header.Controls.Add(_title); header.Controls.Add(liveHost);

        // notizie (sinistra) + login (destra)
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(28, 8, 28, 8) };
        var newsCard = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Card };
        newsCard.Controls.Add(_news);
        newsCard.Controls.Add(new Label { Text = "NOTIZIE", Dock = DockStyle.Top, Height = 34, ForeColor = Theme.Gold, Font = new Font("Segoe UI Semibold", 10), Padding = new Padding(14, 12, 0, 0) });

        var login = new FlowLayoutPanel { Dock = DockStyle.Right, Width = 290, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Card, Padding = new Padding(24, 16, 16, 16) };
        _play = Btn("GIOCA", Theme.Green, 230, 52, 14);
        _register = Btn("Registrati", Theme.Blue, 230, 38, 10);
        _play.Click += async (_, _) => await Run(_play, Play);
        _register.Click += async (_, _) => await Run(_register, Register);
        var server = new LinkLabel { Text = "Cambia server", AutoSize = true, LinkColor = Theme.Muted, Margin = new Padding(3, 10, 0, 0) };
        server.Click += (_, _) => ChangeServer();
        var folder = new LinkLabel { Text = "Apri cartella di gioco", AutoSize = true, LinkColor = Theme.Muted, Margin = new Padding(3, 4, 0, 0) };
        folder.Click += (_, _) => { Directory.CreateDirectory(_s.GameDir); Process.Start("explorer.exe", _s.GameDir); };
        login.Controls.AddRange([
            new Label { Text = "ACCEDI", AutoSize = true, ForeColor = Theme.Gold, Font = new Font("Segoe UI Semibold", 10), Margin = new Padding(3, 0, 0, 10) },
            new Label { Text = "Utente", AutoSize = true, ForeColor = Theme.Muted }, _user,
            new Label { Text = "Password", AutoSize = true, ForeColor = Theme.Muted, Margin = new Padding(3, 8, 0, 0) }, _pass,
            _remember, new Panel { Height = 6, Width = 1 }, _play, _register, _update, server, folder
        ]);
        body.Controls.Add(newsCard); body.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 16 }); body.Controls.Add(login);

        // barra di stato
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 64, Padding = new Padding(28, 6, 28, 14) };
        var barHost = new Panel { Dock = DockStyle.Bottom, Height = 12 };
        barHost.Controls.Add(_bar);
        footer.Controls.Add(_state); footer.Controls.Add(barHost);

        Controls.Add(body); Controls.Add(footer); Controls.Add(header);

        Shown += async (_, _) => await Connect();
        FormClosed += (_, _) => _tunnel?.Dispose();
        var t = new System.Windows.Forms.Timer { Interval = 5000 };
        t.Tick += async (_, _) => { if (!_busy) await RefreshLive(); };
        t.Start();
    }

    static Button Btn(string text, Color c, int w, int h, float size)
    {
        var b = new Button { Text = text, Width = w, Height = h, BackColor = c, ForeColor = Color.White, FlatStyle = FlatStyle.Flat, Font = new Font("Segoe UI Semibold", size), Margin = new Padding(3, 6, 0, 0) };
        b.FlatAppearance.BorderSize = 0;
        return b;
    }

    async Task Run(Button b, Func<Task> action)
    {
        if (_busy) return;
        _busy = true; _play.Enabled = _register.Enabled = false;
        try { await action(); }
        catch (Exception ex) { _state.Text = "Errore: " + ex.Message; }
        finally { _busy = false; _play.Enabled = _register.Enabled = true; }
    }

    string Api(string path) => _s.ServerUrl.TrimEnd('/') + path;

    /// <summary>All'apertura: collega la rete privata e legge lo stato. NON scarica niente.</summary>
    async Task Connect()
    {
        _busy = true;
        try
        {
            await EnsureTailscale();
            _state.Text = "Contatto il server...";
            if (await RefreshLive()) await CheckUpdates();
            else _state.Text = "Server non raggiungibile. Riprovo in automatico...";
        }
        catch (Exception ex) { _state.Text = "Errore: " + ex.Message; }
        finally { _busy = false; }
    }

    async Task EnsureTailscale()
    {
        var serverIp = new Uri(_s.ServerUrl).Host;
        if (_tunnel != null || string.IsNullOrWhiteSpace(_s.TailscaleAuthKey) || !TailscaleTunnel.Available || TailscaleTunnel.IsLocal(serverIp)) return;
        _tunnel = new TailscaleTunnel();
        await _tunnel.StartAsync(_s.TailscaleAuthKey, t => _state.Text = t);
        // da ora tutto il traffico verso il server passa dalla rete privata
        _http = new HttpClient(new HttpClientHandler { Proxy = TailscaleTunnel.Proxy, UseProxy = true }) { Timeout = TimeSpan.FromSeconds(15) };
        _dl = new HttpClient(new HttpClientHandler { Proxy = TailscaleTunnel.Proxy, UseProxy = true }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    async Task<bool> RefreshLive()
    {
        try
        {
            var wasOffline = _info == null;
            _info = await _http.GetFromJsonAsync<ServerInfo>(Api("/launcher.json"));
            _title.Text = _info!.name;
            _news.Text = _info.maintenance ? (_info.maintenanceMessage ?? "Manutenzione in corso.") + "\n\n" + _info.news : _info.news;
            var l = _info.live;
            (_live.Text, _live.BackColor) =
                _info.maintenance ? ("IN MANUTENZIONE", Theme.Orange) :
                l?.online == true ? ($"ONLINE  -  {l.players} in gioco", Theme.Green) :
                ("SERVER AVVIATO, GIOCO SPENTO", Theme.Orange);
            _register.Visible = _info.registration;
            if (wasOffline && !_busy) _ = CheckUpdates();
            return true;
        }
        catch
        {
            _info = null;
            (_live.Text, _live.BackColor) = ("OFFLINE", Theme.Red);
            return false;
        }
    }

    /// <summary>Confronta i file del server con quelli installati e mostra la dimensione della patch.</summary>
    async Task CheckUpdates()
    {
        try
        {
            var files = await _http.GetFromJsonAsync<List<ManifestFile>>(Api("/manifest.json")) ?? [];
            var installed = LauncherSettings.LoadInstalled();
            _patch = files.Where(f =>
            {
                var fi = new FileInfo(Path.Combine(_s.GameDir, f.path));
                return !fi.Exists || fi.Length != f.size || !installed.TryGetValue(f.path, out var i) || i.time != f.time;
            }).ToList();
            var mb = _patch.Sum(f => f.size) / 1048576.0;
            _update.Text = files.Count == 0 ? "Il server non condivide il gioco."
                : _patch.Count == 0 ? "Gioco aggiornato."
                : installed.Count == 0 ? $"Da installare: {mb / 1024:F1} GB (premi GIOCA)"
                : $"Aggiornamento disponibile: {mb:F0} MB";
            _update.ForeColor = _patch.Count == 0 ? Theme.Green : Theme.Gold;
            _state.Text = "Pronto.";
        }
        catch (Exception ex) { _update.Text = "Impossibile controllare gli aggiornamenti: " + ex.Message; }
    }

    async Task DownloadPatch()
    {
        var installed = LauncherSettings.LoadInstalled();
        long total = _patch.Sum(f => f.size), done = 0;
        var sw = Stopwatch.StartNew();
        foreach (var f in _patch.ToList())
        {
            var dest = Path.Combine(_s.GameDir, f.path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var url = Api("/files/" + string.Join('/', f.path.Split('/').Select(Uri.EscapeDataString)));
            using (var resp = await _dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead))
            {
                resp.EnsureSuccessStatusCode();
                await using var src = await resp.Content.ReadAsStreamAsync();
                await using var dst = File.Create(dest + ".part");
                var buf = new byte[1 << 20]; int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n)); done += n;
                    var mbps = done / 1048576.0 / Math.Max(1, sw.Elapsed.TotalSeconds);
                    _bar.Value = (int)Math.Min(100, done * 100 / Math.Max(1, total));
                    _state.Text = $"Download {done >> 20} / {total >> 20} MB  -  {mbps:F1} MB/s  -  {f.path}";
                }
            }
            File.Move(dest + ".part", dest, true);
            installed[f.path] = f;
            _patch.Remove(f);
            // salvo spesso: se il download si interrompe riparte da dove era
            if (installed.Count % 25 == 0) LauncherSettings.SaveInstalled(installed);
        }
        LauncherSettings.SaveInstalled(installed);
        _bar.Value = 100;
        _update.Text = "Gioco aggiornato."; _update.ForeColor = Theme.Green;
    }

    async Task<(bool ok, string msg)> PostAuth(string path)
    {
        var resp = await _http.PostAsJsonAsync(Api(path), new { user = _user.Text.Trim(), password = _pass.Text });
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        return (resp.IsSuccessStatusCode, doc.RootElement.GetProperty("message").GetString() ?? "");
    }

    /// <summary>GIOCA: verifica login, scarica la patch se c'è, avvia il gioco.</summary>
    async Task Play()
    {
        if (_info == null && !await RefreshLive()) { _state.Text = "Server offline."; return; }
        if (_info!.maintenance) { _state.Text = _info.maintenanceMessage ?? "Server in manutenzione."; return; }
        if (string.IsNullOrWhiteSpace(_user.Text) || string.IsNullOrWhiteSpace(_pass.Text)) { _state.Text = "Inserisci utente e password."; return; }

        _state.Text = "Verifico l'account...";
        var (ok, msg) = await PostAuth("/login");
        if (!ok) { _state.Text = msg; return; }
        SaveLogin();

        await CheckUpdates();
        if (_patch.Count > 0) await DownloadPatch();

        var exe = Directory.EnumerateFiles(_s.GameDir, "archeage.exe", SearchOption.AllDirectories)
            .OrderBy(p => p.Contains("bin32", StringComparison.OrdinalIgnoreCase) ? 1 : 0).FirstOrDefault();
        if (exe == null) { _state.Text = "archeage.exe non trovato: il download non è completo."; return; }

        var ip = _info.ip;
        if (_tunnel != null)
        {
            // il client si collega a localhost, il tunnel porta tutto al server
            _tunnel.Forward(ip, _info.port, _info.gamePort ?? 1239, _info.streamPort ?? 1250);
            ip = "127.0.0.1";
        }
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(_pass.Text)));
        var args = _info.launchArgs.Replace("{ip}", ip).Replace("{port}", _info.port.ToString())
            .Replace("{user}", _user.Text.Trim()).Replace("{token}", _pass.Text).Replace("{pwhash}", hash);
        Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = Path.GetDirectoryName(exe)! });
        _state.Text = "Gioco avviato. Buon divertimento!";
    }

    /// <summary>Registrazione semplice: utente + password (+ conferma).</summary>
    async Task Register()
    {
        using var f = new Form { Text = "Crea account", ClientSize = new Size(320, 250), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.Bg, ForeColor = Theme.Text, Font = new Font("Segoe UI", 10) };
        var u = new TextBox { Left = 20, Top = 36, Width = 280, Text = _user.Text };
        var p1 = new TextBox { Left = 20, Top = 94, Width = 280, UseSystemPasswordChar = true };
        var p2 = new TextBox { Left = 20, Top = 152, Width = 280, UseSystemPasswordChar = true };
        var ok = Btn("Crea account", Theme.Green, 280, 38, 10); ok.Left = 20; ok.Top = 196; ok.DialogResult = DialogResult.OK;
        f.Controls.AddRange([
            new Label { Text = "Nome utente (3-16 lettere/numeri)", Left = 20, Top = 12, AutoSize = true, ForeColor = Theme.Muted }, u,
            new Label { Text = "Password (almeno 6 caratteri)", Left = 20, Top = 70, AutoSize = true, ForeColor = Theme.Muted }, p1,
            new Label { Text = "Ripeti password", Left = 20, Top = 128, AutoSize = true, ForeColor = Theme.Muted }, p2, ok
        ]);
        f.AcceptButton = ok;
        if (f.ShowDialog(this) != DialogResult.OK) return;
        if (p1.Text != p2.Text) { _state.Text = "Le password non coincidono."; return; }

        _user.Text = u.Text.Trim(); _pass.Text = p1.Text;
        var (success, msg) = await PostAuth("/register");
        _state.Text = msg;
        if (success) SaveLogin();
    }

    void SaveLogin()
    {
        _s.Username = _user.Text.Trim();
        _s.Remember = _remember.Checked;
        _s.SetPassword(_remember.Checked ? _pass.Text : "");
        _s.Save();
    }

    void ChangeServer()
    {
        using var f = new Form { Text = "Indirizzo server", ClientSize = new Size(420, 96), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false, BackColor = Theme.Bg, Font = new Font("Segoe UI", 10) };
        var tb = new TextBox { Left = 14, Top = 14, Width = 392, Text = _s.ServerUrl };
        var ok = Btn("OK", Theme.Blue, 100, 34, 10); ok.Left = 306; ok.Top = 50; ok.DialogResult = DialogResult.OK;
        f.Controls.AddRange([tb, ok]); f.AcceptButton = ok;
        if (f.ShowDialog(this) != DialogResult.OK) return;
        _s.ServerUrl = tb.Text.Trim(); _s.Save(); _info = null;
        _ = Connect();
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // nessuna finestra di "eccezione non gestita": gli errori vanno nella barra di stato
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "DaProd Launcher");
        Application.Run(new LauncherForm());
    }
}
