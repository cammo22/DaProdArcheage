using System.Diagnostics;
using System.Drawing;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DaProd.Launcher;

public sealed class LauncherSettings
{
    public string ServerUrl { get; set; } = "http://127.0.0.1:8080";
    public string GameDir { get; set; } = "";
    public string Username { get; set; } = "";

    static string FilePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DaProdLauncher", "launcher.json");
    public static LauncherSettings Load() { try { return JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath)) ?? new(); } catch { return new(); } }
    public void Save() { Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }
}

public sealed record ServerInfo(string name, string ip, int port, string news, string clientUrl, string launchArgs);

public sealed class LauncherForm : Form
{
    readonly LauncherSettings _s = LauncherSettings.Load();
    readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
    ServerInfo? _info;

    readonly TextBox _url = new() { Width = 300 };
    readonly Label _title = new() { AutoSize = true, Font = new Font("Segoe UI", 20, FontStyle.Bold), ForeColor = Color.Gold };
    readonly Label _news = new() { Width = 640, Height = 150, ForeColor = Color.White, Font = new Font("Segoe UI", 11) };
    readonly TextBox _user = new() { Width = 200 };
    readonly TextBox _pass = new() { Width = 200, UseSystemPasswordChar = true };
    readonly TextBox _dir = new() { Width = 430 };
    readonly ProgressBar _bar = new() { Width = 640, Height = 18 };
    readonly Label _state = new() { AutoSize = true, ForeColor = Color.LightGray };

    public LauncherForm()
    {
        Text = "DaProd ArcheAge Launcher";
        ClientSize = new Size(700, 470);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = Color.FromArgb(24, 26, 34); Font = new Font("Segoe UI", 10);

        _url.Text = _s.ServerUrl; _user.Text = _s.Username; _dir.Text = _s.GameDir;
        var flow = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(28), WrapContents = false };
        flow.Controls.Add(_title);
        flow.Controls.Add(Row(L("Server:"), _url, Btn("Connetti", async () => await LoadInfo())));
        flow.Controls.Add(_news);
        flow.Controls.Add(Row(L("Cartella gioco:"), _dir, Btn("Sfoglia", () => { using var d = new FolderBrowserDialog(); if (d.ShowDialog() == DialogResult.OK) _dir.Text = d.SelectedPath; return Task.CompletedTask; })));
        flow.Controls.Add(Row(L("Utente:"), _user, L("Password:"), _pass));
        flow.Controls.Add(Row(Btn("⬇ Scarica / Aggiorna gioco", Download, 230), Btn("▶ GIOCA", Play, 180, Color.SeaGreen)));
        flow.Controls.Add(_bar); flow.Controls.Add(_state);
        Controls.Add(flow);
        Shown += async (_, _) => await LoadInfo();
    }

    static Label L(string t) => new() { Text = t, AutoSize = true, ForeColor = Color.White, Margin = new Padding(3, 8, 3, 3) };
    static FlowLayoutPanel Row(params Control[] c) { var r = new FlowLayoutPanel { AutoSize = true, WrapContents = false }; r.Controls.AddRange(c); return r; }

    Button Btn(string t, Func<Task> a, int w = 100, Color? c = null)
    {
        var b = new Button { Text = t, Width = w, Height = 36, FlatStyle = FlatStyle.Flat, ForeColor = Color.White, BackColor = c ?? Color.SteelBlue };
        b.Click += async (_, _) => { b.Enabled = false; try { await a(); } catch (Exception ex) { _state.Text = "Errore: " + ex.Message; } finally { b.Enabled = true; } };
        return b;
    }

    async Task LoadInfo()
    {
        _state.Text = "Connessione al server...";
        _info = await _http.GetFromJsonAsync<ServerInfo>(_url.Text.TrimEnd('/') + "/launcher.json");
        _title.Text = _info!.name; _news.Text = _info.news;
        _state.Text = $"Server online · {_info.ip}:{_info.port}";
        SaveSettings();
    }

    async Task Download()
    {
        if (_info == null) await LoadInfo();
        if (string.IsNullOrWhiteSpace(_info!.clientUrl)) { _state.Text = "Il server non fornisce un download: seleziona la tua cartella di gioco."; return; }
        if (string.IsNullOrWhiteSpace(_dir.Text)) { _state.Text = "Scegli prima dove installare il gioco."; return; }
        var zip = Path.Combine(_dir.Text, "client_download.zip");
        Directory.CreateDirectory(_dir.Text);
        using (var resp = await _http.GetAsync(_info.clientUrl, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(zip);
            var buf = new byte[1 << 20]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf)) > 0)
            {
                await dst.WriteAsync(buf.AsMemory(0, n)); done += n;
                if (total > 0) { _bar.Value = (int)(done * 100 / total); _state.Text = $"Download {done >> 20} / {total >> 20} MB"; }
            }
        }
        _state.Text = "Estrazione...";
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, _dir.Text, true));
        File.Delete(zip);
        _state.Text = "Gioco installato ✔"; SaveSettings();
    }

    async Task Play()
    {
        if (_info == null) await LoadInfo();
        var exe = new[] { @"Bin64\archeage.exe", @"bin64\archeage.exe", @"Bin32\archeage.exe" }
            .Select(p => Path.Combine(_dir.Text, p)).FirstOrDefault(File.Exists);
        if (exe == null) { _state.Text = "archeage.exe non trovato nella cartella di gioco (Bin64)."; return; }
        var hash = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(_pass.Text)));
        var args = _info!.launchArgs.Replace("{ip}", _info.ip).Replace("{port}", _info.port.ToString())
            .Replace("{user}", _user.Text).Replace("{token}", _pass.Text).Replace("{pwhash}", hash);
        Process.Start(new ProcessStartInfo(exe, args) { WorkingDirectory = Path.GetDirectoryName(exe)! });
        SaveSettings();
        _state.Text = "Gioco avviato. Buon divertimento!";
    }

    void SaveSettings() { _s.ServerUrl = _url.Text; _s.GameDir = _dir.Text; _s.Username = _user.Text; _s.Save(); }
}

static class Program
{
    [STAThread]
    static void Main() { ApplicationConfiguration.Initialize(); Application.Run(new LauncherForm()); }
}
