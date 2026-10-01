using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DaProd.ServerPanel;

public sealed record ManifestFile(string path, long size, long time);

/// <summary>
/// API HTTP per il launcher:
///   /launcher.json  stato del server, notizie, eventi
///   /manifest.json  elenco dei file del gioco
///   /files/...      download (con ripresa: header Range)
///   /login /register  accesso e registrazione (il token di accesso è firmato)
///   /launcher.exe   ultima versione del launcher
/// </summary>
public sealed class LauncherApi(PanelSettings s, Action<string, string> log, Func<object> liveStatus, ServerManager srv, EventScheduler events)
{
    HttpListener? _l;
    List<ManifestFile>? _manifest;
    DateTime _manifestAt;
    readonly Dictionary<string, List<DateTime>> _fails = new();
    readonly Dictionary<string, DateTime> _lastRegister = new();
    static readonly string[] ServerOnly = ["Bin64/AAEmu.ZoneHost.exe", "Bin64/x2game-dev_dedicate.dll", "game/db/game_decrypted.sqlite3"];
    static readonly Regex UserRx = new("^[A-Za-z0-9_]{3,16}$", RegexOptions.Compiled);

    public void ResetManifest() => _manifest = null;

    public void Start()
    {
        if (!TryListen($"http://+:{s.LauncherApiPort}/"))
        {
            // una sola volta: permesso URL + regola firewall (chiede conferma a Windows)
            log("api", "Configuro permessi di rete e firewall (conferma la richiesta di Windows)...");
            var cmd = $"/c netsh http add urlacl url=http://+:{s.LauncherApiPort}/ user=Everyone & " +
                      $"netsh advfirewall firewall add rule name=DaProdArcheage dir=in action=allow protocol=TCP localport={s.LoginPort},{s.GamePort},{s.LauncherApiPort}";
            try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", cmd) { Verb = "runas", UseShellExecute = true, WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden })!.WaitForExit(); } catch { }
            if (!TryListen($"http://+:{s.LauncherApiPort}/") && !TryListen($"http://localhost:{s.LauncherApiPort}/"))
            { log("api", "Impossibile avviare l'API del launcher."); return; }
        }
        log("api", $"API del launcher attiva: http://{s.PublicIp}:{s.LauncherApiPort}/");
        _ = Task.Run(Loop);
    }

    bool TryListen(string prefix)
    {
        try { _l = new HttpListener(); _l.Prefixes.Add(prefix); _l.Start(); return true; }
        catch { _l = null; return false; }
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

    List<ManifestFile> Manifest()
    {
        if (_manifest != null && DateTime.Now - _manifestAt < TimeSpan.FromMinutes(5)) return _manifest;
        _manifestAt = DateTime.Now;
        if (!Directory.Exists(s.ClientDir)) return _manifest = [];
        return _manifest = new DirectoryInfo(s.ClientDir).EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(f => new ManifestFile(Path.GetRelativePath(s.ClientDir, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc.Ticks))
            .Where(f => !ServerOnly.Contains(f.path, StringComparer.OrdinalIgnoreCase) && !f.path.EndsWith(".orig") && !f.path.EndsWith(".part") && !f.path.StartsWith("game/"))
            .ToList();
    }

    async Task Handle(HttpListenerContext ctx)
    {
        try
        {
            var path = Uri.UnescapeDataString(ctx.Request.Url!.AbsolutePath);
            var post = ctx.Request.HttpMethod == "POST";
            if (post && path is "/login" or "/register") { await Auth(ctx, path); return; }
            if (path.StartsWith("/dap/")) { await Dap(ctx, path); return; }
            if (path == "/ping") { ctx.Response.StatusCode = 204; ctx.Response.Close(); return; }
            if (path == "/launcher.exe")
            {
                // la versione personalizzata (con indirizzo e chiave): anche una chiave rinnovata arriva agli amici
                string exe; try { exe = FriendLauncher.EnsureBuilt(s); } catch { exe = Path.Combine(PanelSettings.Root, "launcher", "DaProdLauncher.exe"); }
                await SendFile(ctx, exe);
                return;
            }
            if (path.StartsWith("/files/"))
            {
                var full = Path.GetFullPath(Path.Combine(s.ClientDir, path["/files/".Length..]));
                // blocca path traversal: solo file dentro la cartella del gioco
                if (!full.StartsWith(Path.GetFullPath(s.ClientDir), StringComparison.OrdinalIgnoreCase)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
                await SendFile(ctx, full);
                return;
            }
            var launcherExe = FriendLauncher.BasePath;
            object body = path == "/manifest.json" ? Manifest() : new
            {
                name = s.ServerName, ip = s.PublicIp, port = s.LoginPort, gamePort = s.GamePort, streamPort = 1250, news = s.News,
                launchArgs = s.LaunchArgs, live = liveStatus(), events = events.Upcoming(),
                maintenance = s.Maintenance, maintenanceMessage = s.MaintenanceMessage, registration = s.AllowRegistration && !s.Maintenance,
                launcherVersion = File.Exists(launcherExe) ? System.Diagnostics.FileVersionInfo.GetVersionInfo(launcherExe).FileVersion : null,
            };
            var b = JsonSerializer.SerializeToUtf8Bytes(body);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = b.Length;
            await ctx.Response.OutputStream.WriteAsync(b);
            ctx.Response.Close();
        }
        catch { try { ctx.Response.Abort(); } catch { } }
    }

    /// <summary>Menu creativo /dap: richiesta dal gioco, elenco oggetti con anteprima, consegna in borsa.</summary>
    async Task Dap(HttpListenerContext ctx, string path)
    {
        async Task Json(object o, int code = 200)
        {
            var b = JsonSerializer.SerializeToUtf8Bytes(o);
            ctx.Response.StatusCode = code; ctx.Response.ContentType = "application/json"; ctx.Response.ContentLength64 = b.Length;
            await ctx.Response.OutputStream.WriteAsync(b); ctx.Response.Close();
        }
        if (path.StartsWith("/dap/icon/"))
        {
            var name = path["/dap/icon/".Length..].Replace(".png", "");
            var png = uint.TryParse(name, out var iid) ? ItemCatalog.Icon(iid) : null;
            if (png == null) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
            ctx.Response.ContentType = "image/png"; ctx.Response.ContentLength64 = png.Length;
            ctx.Response.Headers["Cache-Control"] = "max-age=86400";
            await ctx.Response.OutputStream.WriteAsync(png); ctx.Response.Close();
            return;
        }
        var q = ctx.Request.QueryString;
        var user = q["user"] ?? "";
        if (user.Length is < 1 or > 32 || !srv.VerifyToken(user, q["t"] ?? "")) { await Json(new { ok = false, message = "Accesso non valido: rientra dal launcher." }, 401); return; }
        if (!Tweaks.On("dapMenu")) { await Json(new { ok = false, message = "Il menu oggetti è disattivato." }, 403); return; }

        if (path == "/dap/poll")
        {
            await srv.ExecAsync("aaemu_game", "CREATE TABLE IF NOT EXISTS daprod_dap (char_id INT UNSIGNED NOT NULL PRIMARY KEY, at TIMESTAMP NOT NULL DEFAULT CURRENT_TIMESTAMP)");
            var t = await srv.QueryAsync("aaemu_game", "SELECT d.char_id, c.name FROM daprod_dap d JOIN characters c ON c.id=d.char_id JOIN aaemu_login.users u ON u.id=c.account_id " +
                "WHERE u.username=@u AND d.at > NOW() - INTERVAL 40 SECOND ORDER BY d.at DESC LIMIT 1", ("@u", user));
            if (t.Rows.Count == 0) { await Json(new { open = false }); return; }
            var cid = Convert.ToInt64(t.Rows[0][0]);
            await srv.ExecAsync("aaemu_game", "DELETE FROM daprod_dap WHERE char_id=@c", ("@c", cid));
            await Json(new { open = true, charId = cid, name = t.Rows[0][1]?.ToString() });
            return;
        }
        if (path == "/dap/items")
        {
            _ = uint.TryParse(q["cat"], out var cat); _ = int.TryParse(q["skip"], out var skip);
            var (total, page) = await Task.Run(() => ItemCatalog.Search(q["q"] ?? "", cat, Math.Max(0, skip), 200));
            await Json(new
            {
                total, items = page.Select(i => new { id = i.Id, name = i.Name, icon = i.Icon, cat = i.Cat, grade = i.Grade, level = i.Level }),
                cats = q["cats"] == "1" ? ItemCatalog.Categories().Select(c => new { id = c.Id, name = c.Name }) : null
            });
            return;
        }
        if (path == "/dap/give")
        {
            if (!uint.TryParse(q["item"], out var item) || !long.TryParse(q["char"], out var cid)) { await Json(new { ok = false, message = "Richiesta non valida." }, 400); return; }
            _ = int.TryParse(q["count"], out var count); count = Math.Clamp(count, 1, 9999);
            // il personaggio deve essere dell'account che ha fatto accesso
            var own = await srv.QueryAsync("aaemu_game", "SELECT c.id FROM characters c JOIN aaemu_login.users u ON u.id=c.account_id WHERE u.username=@u AND c.id=@c", ("@u", user), ("@c", cid));
            if (own.Rows.Count == 0) { await Json(new { ok = false, message = "Quel personaggio non è tuo." }, 403); return; }
            var msg = await srv.GiveItemAsync(cid, item, count);
            log("dap", $"{user} -> {item} x{count}: {msg}");
            await Json(new { ok = msg.StartsWith("Oggetto consegnato"), message = msg });
            return;
        }
        await Json(new { ok = false, message = "?" }, 404);
    }

    /// <summary>Invia un file, con supporto a Range (ripresa dei download).</summary>
    static async Task SendFile(HttpListenerContext ctx, string full)
    {
        if (!File.Exists(full)) { ctx.Response.StatusCode = 404; ctx.Response.Close(); return; }
        await using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 20, true);
        long start = 0, end = fs.Length - 1;
        var range = ctx.Request.Headers["Range"];
        var partial = false;
        if (range != null && range.StartsWith("bytes="))
        {
            var r = range[6..].Split('-');
            if (long.TryParse(r[0], out var st)) start = st;
            if (r.Length > 1 && long.TryParse(r[1], out var en)) end = Math.Min(en, fs.Length - 1);
            partial = true;
        }
        if (start >= fs.Length) { ctx.Response.StatusCode = 416; ctx.Response.Close(); return; }
        ctx.Response.StatusCode = partial ? 206 : 200;
        ctx.Response.ContentType = "application/octet-stream";
        ctx.Response.AddHeader("Accept-Ranges", "bytes");
        if (partial) ctx.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{fs.Length}");
        var count = end - start + 1;
        ctx.Response.ContentLength64 = count;
        if (ctx.Request.HttpMethod == "HEAD") { ctx.Response.Close(); return; }   // solo intestazioni
        fs.Seek(start, SeekOrigin.Begin);
        var buf = new byte[1 << 20];
        while (count > 0)
        {
            var n = await fs.ReadAsync(buf.AsMemory(0, (int)Math.Min(buf.Length, count)));
            if (n <= 0) break;
            await ctx.Response.OutputStream.WriteAsync(buf.AsMemory(0, n));
            count -= n;
        }
        ctx.Response.Close();
    }

    async Task Auth(HttpListenerContext ctx, string path)
    {
        var ip = ctx.Request.RemoteEndPoint.Address.ToString();
        var (code, msg, token) = await AuthCore(ctx, path, ip);
        var rb = JsonSerializer.SerializeToUtf8Bytes(new { ok = code == 200, message = msg, token });
        ctx.Response.StatusCode = code; ctx.Response.ContentType = "application/json";
        await ctx.Response.OutputStream.WriteAsync(rb);
        ctx.Response.Close();
    }

    async Task<(int, string, string?)> AuthCore(HttpListenerContext ctx, string path, string ip)
    {
        if (s.Maintenance) return (503, s.MaintenanceMessage, null);
        string user, pwd;
        try
        {
            using var doc = await JsonDocument.ParseAsync(ctx.Request.InputStream);
            user = doc.RootElement.GetProperty("user").GetString()?.Trim() ?? "";
            pwd = doc.RootElement.GetProperty("password").GetString() ?? "";
        }
        catch { return (400, "Richiesta non valida.", null); }

        if (path == "/register")
        {
            if (!s.AllowRegistration) return (403, "Le registrazioni sono chiuse.", null);
            if (!UserRx.IsMatch(user)) return (400, "Nome utente: 3-16 caratteri, solo lettere, numeri e _.", null);
            if (pwd.Length < 6) return (400, "La password deve avere almeno 6 caratteri.", null);
            lock (_lastRegister)
                if (_lastRegister.TryGetValue(ip, out var t) && DateTime.Now - t < TimeSpan.FromSeconds(30)) return (429, "Attendi qualche secondo e riprova.", null);
            if ((await srv.QueryAsync("aaemu_login", "SELECT id FROM users WHERE username=@u", ("@u", user))).Rows.Count > 0) return (409, "Nome utente già in uso.", null);
            await srv.CreateAccountAsync(user, pwd);
            lock (_lastRegister) _lastRegister[ip] = DateTime.Now;
            log("api", $"Nuovo account: {user} ({ip})");
            return (200, "Account creato! Ora puoi giocare.", srv.MakeToken(user));
        }

        // /login: verifica password e dà il token con cui il client entra nel Login del gioco
        lock (_fails)
        {
            if (!_fails.TryGetValue(ip, out var f)) _fails[ip] = f = [];
            f.RemoveAll(x => DateTime.Now - x > TimeSpan.FromMinutes(1));
            if (f.Count >= 6) return (429, "Troppi tentativi sbagliati: riprova tra un minuto.", null);
        }
        if (user.Length is < 1 or > 32) return (401, "Utente o password errati.", null);
        if (await srv.CheckPasswordAsync(user, pwd)) return (200, "Accesso riuscito.", srv.MakeToken(user));
        lock (_fails) _fails[ip].Add(DateTime.Now);
        var banned = (await srv.QueryAsync("aaemu_login", "SELECT banned FROM users WHERE username=@u", ("@u", user))).Rows;
        return banned.Count == 1 && Convert.ToInt32(banned[0][0]) != 0 ? (403, "Account sospeso.", null) : (401, "Utente o password errati.", null);
    }
}
