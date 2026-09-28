using System.Net;
using System.Text;
using System.Text.Json;

namespace DaProd.ServerPanel;

public enum EventAction { News, Sql, ConsoleCommand, RestartServer }

/// <summary>Un evento programmato: una volta (Date) o ogni giorno all'ora indicata.</summary>
public sealed class GameEvent
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "Nuovo evento";
    public EventAction Action { get; set; } = EventAction.News;
    public DateTime When { get; set; } = DateTime.Now.AddHours(1);
    public bool Daily { get; set; }
    public string Payload { get; set; } = "";
    public DateTime? LastRun { get; set; }
}

/// <summary>Salva gli eventi in data\events.json e li esegue quando è il momento.</summary>
public sealed class EventScheduler
{
    static string FilePath => Path.Combine(PanelSettings.DataDir, "events.json");
    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true, Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };

    public List<GameEvent> Events { get; private set; } = [];
    readonly System.Threading.Timer _timer;
    readonly Func<GameEvent, Task> _run;

    public EventScheduler(Func<GameEvent, Task> run)
    {
        _run = run;
        try { if (File.Exists(FilePath)) Events = JsonSerializer.Deserialize<List<GameEvent>>(File.ReadAllText(FilePath), Opts) ?? []; } catch { }
        _timer = new System.Threading.Timer(_ => Tick(), null, 10_000, 30_000);
    }

    public void Save()
    {
        Directory.CreateDirectory(PanelSettings.DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(Events, Opts));
    }

    void Tick()
    {
        var now = DateTime.Now;
        foreach (var e in Events.ToList())
        {
            if (!e.Enabled) continue;
            var due = e.Daily ? now.Date + e.When.TimeOfDay : e.When;
            if (now < due || (e.LastRun.HasValue && e.LastRun.Value >= due)) continue;
            e.LastRun = now;
            if (!e.Daily) e.Enabled = false;
            _ = _run(e);
        }
        Save();
    }
}

public sealed record ManifestFile(string path, long size, long time);

/// <summary>
/// API HTTP letta dal launcher:
///   /launcher.json      info server + stato live
///   /manifest.json      elenco file del client (per download/aggiornamento)
///   /files/{percorso}   download di un file del client
/// </summary>
public sealed class LauncherApi(PanelSettings s, Action<string, string> log, Func<object> liveStatus, ServerManager srv)
{
    readonly Dictionary<string, DateTime> _lastRegister = new();

    /// <summary>POST /register {user, password}: gli utenti si creano l'account dal launcher.</summary>
    async Task<(int code, string msg)> Register(HttpListenerContext ctx)
    {
        if (s.Maintenance) return (503, s.MaintenanceMessage);
        if (!s.AllowRegistration) return (403, "Le registrazioni sono chiuse.");
        var ip = ctx.Request.RemoteEndPoint.Address.ToString();
        var (user, pwd) = await ReadCredentials(ctx);
        if (!System.Text.RegularExpressions.Regex.IsMatch(user, "^[A-Za-z0-9_]{3,16}$")) return (400, "Nome utente: 3-16 caratteri, solo lettere, numeri e _.");
        if (pwd.Length < 6) return (400, "La password deve avere almeno 6 caratteri.");
        lock (_lastRegister)
        {
            // anti-spam: un account ogni 30 secondi per indirizzo
            if (_lastRegister.TryGetValue(ip, out var t) && DateTime.Now - t < TimeSpan.FromSeconds(30)) return (429, "Attendi qualche secondo e riprova.");
        }
        var exists = await srv.QueryAsync("aaemu_login", $"SELECT id FROM users WHERE username='{user}'");
        if (exists.Rows.Count > 0) return (409, "Nome utente già in uso.");
        await srv.CreateAccountAsync(user, pwd);
        lock (_lastRegister) _lastRegister[ip] = DateTime.Now;
        log("api", $"Nuovo account registrato dal launcher: {user} ({ip})");
        return (200, "Account creato! Ora puoi giocare.");
    }

    /// <summary>POST /login {user, password}: verifica le credenziali prima di avviare il gioco.</summary>
    async Task<(int code, string msg)> Login(HttpListenerContext ctx)
    {
        if (s.Maintenance) return (503, s.MaintenanceMessage);
        var (user, pwd) = await ReadCredentials(ctx);
        if (!System.Text.RegularExpressions.Regex.IsMatch(user, "^[A-Za-z0-9_]{1,32}$")) return (401, "Utente o password errati.");
        var t = await srv.QueryAsync("aaemu_login", $"SELECT password FROM users WHERE username='{user}'");
        if (t.Rows.Count == 0)
            return s.AutoAccount ? (200, "Nuovo account: verrà creato al primo accesso.") : (401, "Utente non trovato. Premi Registrati per crearlo.");
        return (string)t.Rows[0]["password"] == ServerManager.HashPassword(pwd) ? (200, "Accesso riuscito.") : (401, "Utente o password errati.");
    }

    static async Task<(string user, string pwd)> ReadCredentials(HttpListenerContext ctx)
    {
        using var doc = await JsonDocument.ParseAsync(ctx.Request.InputStream);
        return (doc.RootElement.GetProperty("user").GetString()?.Trim() ?? "", doc.RootElement.GetProperty("password").GetString() ?? "");
    }
    HttpListener? _l;
    List<ManifestFile>? _manifest;
    DateTime _manifestAt;

    public void Start()
    {
        if (!TryListen($"http://+:{s.LauncherApiPort}/"))
        {
            // Serve una sola volta: permesso URL + regola firewall (chiede conferma UAC).
            log("api", "Configuro permessi rete e firewall (conferma la richiesta di Windows)...");
            var cmd = $"/c netsh http add urlacl url=http://+:{s.LauncherApiPort}/ user=Everyone & " +
                      $"netsh advfirewall firewall add rule name=DaProdArcheage dir=in action=allow protocol=TCP localport={s.LoginPort},{s.GamePort},{s.LauncherApiPort}";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", cmd) { Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden })!.WaitForExit(); } catch { }
            if (!TryListen($"http://+:{s.LauncherApiPort}/") && !TryListen($"http://localhost:{s.LauncherApiPort}/"))
            { log("api", "Impossibile avviare l'API launcher."); return; }
        }
        log("api", $"API launcher attiva: http://{s.PublicIp}:{s.LauncherApiPort}/");
        _ = Task.Run(Loop);
    }

    bool TryListen(string prefix)
    {
        try { _l = new HttpListener(); _l.Prefixes.Add(prefix); _l.Start(); return true; }
        catch { _l = null; return false; }
    }

    public void ResetManifest() => _manifest = null;

    List<ManifestFile> Manifest()
    {
        // ricalcolo ogni 5 minuti: così un client aggiornato viene rilevato da solo
        if (_manifest != null && DateTime.Now - _manifestAt < TimeSpan.FromMinutes(5)) return _manifest;
        _manifestAt = DateTime.Now;
        if (!Directory.Exists(s.ClientDir)) return _manifest = [];
        return _manifest = new DirectoryInfo(s.ClientDir).EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => new ManifestFile(Path.GetRelativePath(s.ClientDir, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc.Ticks))
            .ToList();
    }

    async Task Loop()
    {
        while (_l is { IsListening: true })
        {
            HttpListenerContext ctx;
            try { ctx = await _l.GetContextAsync(); } catch { continue; }
            _ = Task.Run(() => Handle(ctx));
        }
    }

    async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath);
            if (path is "/register" or "/login" && ctx.Request.HttpMethod == "POST")
            {
                var (code, msg) = path == "/login" ? await Login(ctx) : await Register(ctx);
                var rb = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = code == 200, message = msg }));
                ctx.Response.StatusCode = code; ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(rb); ctx.Response.Close();
                return;
            }
            if (path.StartsWith("/files/"))
            {
                var rel = path["/files/".Length..];
                var full = Path.GetFullPath(Path.Combine(s.ClientDir, rel));
                // blocca path traversal: si servono solo file dentro ClientDir
                if (!full.StartsWith(Path.GetFullPath(s.ClientDir), StringComparison.OrdinalIgnoreCase) || !File.Exists(full))
                { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
                await using var fs = File.OpenRead(full);
                ctx.Response.ContentType = "application/octet-stream";
                ctx.Response.ContentLength64 = fs.Length;
                await fs.CopyToAsync(ctx.Response.OutputStream);
                ctx.Response.Close();
                return;
            }
            object body = path switch
            {
                "/manifest.json" => Manifest(),
                _ => new
                {
                    name = s.ServerName, ip = s.PublicIp, port = s.LoginPort, gamePort = s.GamePort, streamPort = 1250, news = s.News,
                    clientUrl = s.ClientDownloadUrl, launchArgs = s.LaunchArgs, live = liveStatus(),
                    maintenance = s.Maintenance, maintenanceMessage = s.MaintenanceMessage, registration = s.AllowRegistration && !s.Maintenance
                }
            };
            var b = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(body));
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = b.Length;
            await ctx.Response.OutputStream.WriteAsync(b);
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }
}
