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

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DaProdLauncher", "launcher.json");

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

    public void Save() { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
}

public sealed record Live(bool online, int players, string time);
public sealed record ServerInfo(string name, string ip, int port, string news, string clientUrl, string launchArgs, Live? live);
public sealed record ManifestFile(string path, long size, long time);

public sealed class LauncherForm : Form
{
    const string TailscaleExe = @"C:\Program Files\Tailscale\tailscale.exe";
    readonly LauncherSettings _s = LauncherSettings.Load();
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    readonly HttpClient _dl = new() { Timeout = Timeout.InfiniteTimeSpan };
    ServerInfo? _info;
    bool _busy, _ready;

    readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI", 20, FontStyle.Bold), ForeColor = Color.Gold, Text = "DaProd ArcheAge" };
    readonly Label _live = new() { AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), ForeColor = Color.Orange, Text = "⏳ Connessione..." };
    readonly Label _news = new() { Width = 640, Height = 130, ForeColor = Color.White, Font = new Font("Segoe UI", 11) };
    readonly TextBox _user = new() { Width = 200 };
    readonly TextBox _pass = new() { Width = 200, UseSystemPasswordChar = true };
    readonly ProgressBar _bar = new() { Width = 640, Height = 18 };
    readonly Label _state = new() { Width = 640, Height = 40, ForeColor = Color.LightGray };
    readonly Button _play;

    public LauncherForm()
    {
        Text = "DaProd ArcheAge Launcher";
        ClientSize = new Size(700, 460);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 26, 34); Font = new Font("Segoe UI", 10);

        _user.Text = _s.Username;
        _play = Btn("▶ GIOCA", Play, 200, Color.SeaGreen);
        _play.Enabled = false;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(28), WrapContents = false };
        flow.Controls.Add(_title);
        flow.Controls.Add(_live);
        flow.Controls.Add(_news);
        flow.Controls.Add(Row(L("Utente:"), _user, L("Password:"), _pass));
        flow.Controls.Add(Row(_play, Btn("⚙ Server", ChangeServer, 110), Btn("📁 Cartella", () => { Process.Start("explorer.exe", _s.GameDir); return Task.CompletedTask; }, 110)));
        flow.Controls.Add(_bar); flow.Controls.Add(_state);
        Controls.Add(flow);

        Shown += async (_, _) => await Prepare();
        var t = new System.Windows.Forms.Timer { Interval = 5000 };
        t.Tick += async (_, _) => { if (!_busy) await RefreshLive(); };
        t.Start();
    }

    static Label L(string t) => new() { Text = t, AutoSize = true, ForeColor = Color.White, Margin = new Padding(3, 8, 3, 3) };
    static FlowLayoutPanel Row(params Control[] c) { var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; r.Controls.AddRange(c); return r; }

    Button Btn(string t, Func<Task> a, int w = 100, Color? c = null)
    {
        var b = new Button { Text = t, Width = w, Height = 40, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = c ?? Color.SteelBlue };
        b.Click += async (_, _) => { b.Enabled = false; try { await a(); } catch (Exception ex) { _state.Text = "Errore: " + ex.Message; } finally { b.Enabled = b != _play || _ready; } };
        return b;
    }

    /// <summary>Tutto automatico: rete Tailscale → server → download/aggiornamento gioco.</summary>
    async Task Prepare()
    {
        _busy = true;
        try
        {
            await EnsureTailscale();
            _state.Text = "Contatto il server...";
            if (!await RefreshLive()) { _state.Text = "Server non raggiungibile. Riprovo in automatico ogni 5 secondi..."; return; }
            await SyncGame();
        }
        catch (Exception ex) { _state.Text = "Errore: " + ex.Message; }
        finally { _busy = false; }
    }

    async Task EnsureTailscale()
    {
        if (string.IsNullOrWhiteSpace(_s.TailscaleAuthKey)) return;
        if (!File.Exists(TailscaleExe))
        {
            _state.Text = "Installo Tailscale (serve per collegarsi al server via internet)...";
            var setup = Path.Combine(Path.GetTempPath(), "tailscale-setup.exe");
            await File.WriteAllBytesAsync(setup, await _dl.GetByteArrayAsync("https://pkgs.tailscale.com/stable/tailscale-setup-latest.exe"));
            await Process.Start(new ProcessStartInfo(setup, "/quiet") { UseShellExecute = true, Verb = "runas" })!.WaitForExitAsync();
            for (var i = 0; i < 20 && !File.Exists(TailscaleExe); i++) await Task.Delay(1000);
        }
        // già collegato alla rete del server? allora non tocco niente
        if (await CanReach()) return;
        _state.Text = "Mi collego alla rete del server (Tailscale)...";
        var p = Process.Start(new ProcessStartInfo(TailscaleExe, $"up --auth-key={_s.TailscaleAuthKey} --unattended")
        { UseShellExecute = false, CreateNoWindow = true })!;
        await p.WaitForExitAsync();
    }

    async Task<bool> CanReach()
    {
        try { await _http.GetStringAsync(_s.ServerUrl.TrimEnd('/') + "/launcher.json"); return true; } catch { return false; }
    }

    async Task<bool> RefreshLive()
    {
        try
        {
            _info = await _http.GetFromJsonAsync<ServerInfo>(_s.ServerUrl.TrimEnd('/') + "/launcher.json");
            _title.Text = _info!.name; _news.Text = _info.news;
            var l = _info.live;
            _live.ForeColor = l?.online == true ? Color.LimeGreen : Color.Orange;
            _live.Text = l == null ? "🟢 Server raggiungibile"
                : l.online ? $"🟢 Server ONLINE · {l.players} giocatori in gioco · {l.time}"
                : $"🟠 Server raggiungibile ma il gioco è spento · {l.time}";
            if (!_ready && !_busy) _ = Prepare();
            return true;
        }
        catch
        {
            _live.ForeColor = Color.IndianRed; _live.Text = "🔴 Server OFFLINE";
            return false;
        }
    }

    async Task SyncGame()
    {
        _state.Text = "Controllo i file del gioco...";
        var files = await _http.GetFromJsonAsync<List<ManifestFile>>(_s.ServerUrl.TrimEnd('/') + "/manifest.json") ?? [];
        if (files.Count == 0) { _state.Text = "Il server non ha ancora il client da condividere."; return; }
        var todo = files.Where(f => { var fi = new FileInfo(Path.Combine(_s.GameDir, f.path)); return !fi.Exists || fi.Length != f.size; }).ToList();
        long total = todo.Sum(f => f.size), done = 0;
        var sw = Stopwatch.StartNew();
        foreach (var f in todo)
        {
            var dest = Path.Combine(_s.GameDir, f.path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            var url = _s.ServerUrl.TrimEnd('/') + "/files/" + string.Join('/', f.path.Split('/').Select(Uri.EscapeDataString));
            using var resp = await _dl.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            await using (var src = await resp.Content.ReadAsStreamAsync())
            await using (var dst = File.Create(dest + ".part"))
            {
                var buf = new byte[1 << 20]; int n;
                while ((n = await src.ReadAsync(buf)) > 0)
                {
                    await dst.WriteAsync(buf.AsMemory(0, n)); done += n;
                    var mbps = done / 1048576.0 / Math.Max(1, sw.Elapsed.TotalSeconds);
                    _bar.Value = (int)(done * 100 / Math.Max(1, total));
                    _state.Text = $"Download {done >> 20} / {total >> 20} MB · {mbps:F1} MB/s · {f.path}";
                }
            }
            File.Move(dest + ".part", dest, true);
        }
        _bar.Value = 100;
        _ready = true; _play.Enabled = true;
        _state.Text = "Gioco pronto ✔ Inserisci utente e password e premi GIOCA.";
    }

    async Task Play()
    {
        if (_info == null && !await RefreshLive()) { _state.Text = "Server offline."; return; }
        if (string.IsNullOrWhiteSpace(_user.Text) || string.IsNullOrWhiteSpace(_pass.Text)) { _state.Text = "Inserisci utente e password."; return; }
        var exe = Directory.Exists(_s.GameDir)
            ? Directory.EnumerateFiles(_s.GameDir, "archeage.exe", SearchOption.AllDirectories).OrderBy(p => p.Contains("Bin32") ? 1 : 0).FirstOrDefault()
            : null;
        if (exe == null) { _state.Text = "archeage.exe non trovato: il download non è completo."; return; }
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(_pass.Text)));
        var args = _info!.launchArgs.Replace("{ip}", _info.ip).Replace("{port}", _info.port.ToString())
            .Replace("{user}", _user.Text).Replace("{token}", _pass.Text).Replace("{pwhash}", hash);
        Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = Path.GetDirectoryName(exe)! });
        _s.Username = _user.Text; _s.Save();
        _state.Text = "Gioco avviato. Buon divertimento!";
    }

    Task ChangeServer()
    {
        using var f = new Form { Text = "Indirizzo server", Size = new Size(420, 150), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog };
        var tb = new TextBox { Left = 12, Top = 12, Width = 380, Text = _s.ServerUrl };
        var ok = new Button { Text = "OK", Left = 312, Top = 50, DialogResult = DialogResult.OK };
        f.Controls.AddRange([tb, ok]); f.AcceptButton = ok;
        if (f.ShowDialog(this) == DialogResult.OK) { _s.ServerUrl = tb.Text.Trim(); _s.Save(); _ready = false; _ = Prepare(); }
        return Task.CompletedTask;
    }
}

static class Program
{
    [STAThread]
    static void Main()
    {
        ApplicationConfiguration.Initialize();
        // Mai più finestre di "eccezione non gestita": gli errori finiscono nella riga di stato.
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "DaProd Launcher");
        Application.Run(new LauncherForm());
    }
}
