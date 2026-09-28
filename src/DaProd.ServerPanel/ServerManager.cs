using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MySqlConnector;

namespace DaProd.ServerPanel;

/// <summary>Avvia/ferma MySQL, Login e Game; prepara database e configurazioni.</summary>
public sealed class ServerManager(PanelSettings s, Action<string, string> log)
{
    readonly Dictionary<string, Process> _procs = new();

    static string ServerDir => Path.Combine(PanelSettings.Root, "server");
    static string MySqlData => Path.Combine(ServerDir, "mysql-data");
    static string LoginDir => Path.Combine(ServerDir, "bin", "login");
    static string GameDir => Path.Combine(ServerDir, "bin", "game");
    static string SqlDir => Path.Combine(ServerDir, "sql");
    static string WorldDir => Path.Combine(ServerDir, "bin", "world");
    static string ZoneFiles => Path.Combine(ServerDir, "zonehost");
    /// <summary>
    /// Copia del client solo per il server di zona: Bin64 + game_pak estratto in game\.
    /// Il client in gioco\ resta intatto per i launcher degli amici.
    /// </summary>
    static string ZoneClient => Path.Combine(ServerDir, "zoneclient");
    static string Bin64 => Path.Combine(ZoneClient, "Bin64");

    /// <summary>Quando è stato avviato ogni processo (per capire se è ancora "in caricamento").</summary>
    public DateTime? StartedAt(string name) => _procs.TryGetValue(name, out var p) && !p.HasExited ? p.StartTime : null;
    public Process? Proc(string name) => _procs.TryGetValue(name, out var p) && !p.HasExited ? p : null;
    public int Crashes { get; private set; }
    readonly HashSet<string> _stopping = [];

    /// <summary>Copia nel client i file del server di zona (ZoneHost, dll, database).</summary>
    async Task PrepareZoneHostAsync()
    {
        var clientBin = Path.Combine(s.ClientDir, "Bin64");
        if (!Directory.Exists(clientBin)) { log("zone", $"Client non trovato in {s.ClientDir}"); return; }
        if (!Directory.Exists(Bin64))
        {
            log("zone", "Copio Bin64 del client per il server di zona...");
            await Task.Run(() => CopyDir(clientBin, Bin64));
        }
        // prima volta: estraggo game_pak (lungo, ma una volta sola; riprende se interrotto)
        var pak = Path.Combine(s.ClientDir, "game_pak");
        var extractor = Path.Combine(ServerDir, "tools", "PakExtract", "PakExtract.exe");
        if (!File.Exists(Path.Combine(ZoneClient, "game", ".estratto")) && File.Exists(pak) && File.Exists(extractor))
        {
            log("zone", "Estraggo game_pak per il server di zona: può richiedere 20-60 minuti la prima volta...");
            await RunToEndAsync(extractor, $"\"{pak}\" \"{ZoneClient}\"", "zone");
            File.WriteAllText(Path.Combine(ZoneClient, "game", ".estratto"), DateTime.Now.ToString("s"));
        }
        foreach (var f in new[] { "AAEmu.ZoneHost.exe", "x2game-dev_dedicate.dll" })
        {
            var src = Path.Combine(ZoneFiles, f); var dst = Path.Combine(Bin64, f);
            if (File.Exists(src) && (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(src).Length)) File.Copy(src, dst, true);
        }
        var db = Path.Combine(ZoneClient, "game", "db", "game_decrypted.sqlite3");
        var dbSrc = Path.Combine(ZoneFiles, "game_decrypted.sqlite3");
        if (File.Exists(dbSrc) && (!File.Exists(db) || new FileInfo(db).Length != new FileInfo(dbSrc).Length))
        { Directory.CreateDirectory(Path.GetDirectoryName(db)!); File.Copy(dbSrc, db, true); }
        WriteDedicatedCfg();
    }

    /// <summary>
    /// game\config\dedicated.cfg come da guida: il database va indicato qui, perché da riga di comando
    /// arriva troppo tardi e il motore apre il compact.sqlite3 del pak (a cui mancano tabelle).
    /// </summary>
    void WriteDedicatedCfg()
    {
        var cfg = Path.Combine(ZoneClient, "game", "config", "dedicated.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        const string marker = "-- DaProd";
        var lines = File.Exists(cfg) ? File.ReadAllLines(cfg).TakeWhile(l => l != marker).ToList() : [];
        lines.AddRange([
            marker,
            "sys_dedicated_server = 1",
            "world_serveraddr = \"127.0.0.1\"",
            "world_serverport = 1240",
            "db_location = \"game/db/game_decrypted.sqlite3\"",
            "locale = \"en_us\"",
            "cl_account_id = 1",
            "auth_serveraddr = 127.0.0.1",
            $"auth_serverport = {s.LoginPort}"
        ]);
        File.WriteAllLines(cfg, lines);
    }

    static void CopyDir(string from, string to)
    {
        foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
        {
            var dst = Path.Combine(to, Path.GetRelativePath(from, f));
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(f, dst, true);
        }
    }

    public string ConnString(string db = "") =>
        $"Server=127.0.0.1;Port={s.MySqlPort};User ID=root;Database={db};SslMode=None;AllowUserVariables=true";

    public bool IsRunning(string name) => _procs.TryGetValue(name, out var p) && !p.HasExited;

    // ---------- Setup ----------

    public async Task FirstSetupAsync()
    {
        if (!Directory.Exists(MySqlData))
        {
            log("mysql", "Inizializzo il database MySQL (prima volta)...");
            await RunToEndAsync(Path.Combine(s.MySqlBinDir, "mysqld.exe"),
                $"--initialize-insecure --datadir=\"{MySqlData}\" --console", "mysql");
        }
        await StartMySqlAsync();

        await using (var c = new MySqlConnection(ConnString()))
        {
            await c.OpenAsync();
            foreach (var db in new[] { "aaemu_login", "aaemu_game" })
            {
                var exists = await new MySqlCommand($"SHOW DATABASES LIKE '{db}'", c).ExecuteScalarAsync() != null;
                if (exists) { log("mysql", $"{db} già presente, salto."); continue; }
                await new MySqlCommand($"CREATE DATABASE `{db}` CHARACTER SET utf8mb4", c).ExecuteNonQueryAsync();
                log("mysql", $"Importo {db}.sql ...");
                await RunToEndAsync(Path.Combine(s.MySqlBinDir, "mysql.exe"),
                    $"-uroot -h127.0.0.1 -P{s.MySqlPort} {db} -e \"source {Path.Combine(SqlDir, db + ".sql").Replace('\\', '/')}\"", "mysql");
            }
        }
        WriteConfigs();
        log("panel", "Setup completato.");
    }

    public void WriteConfigs()
    {
        var db = new JsonObject
        {
            ["Host"] = "127.0.0.1", ["Port"] = s.MySqlPort.ToString(), ["User"] = "root",
            ["Password"] = "", ["SslMode"] = "None"
        };
        JsonNode Db(string name) { var o = db.DeepClone(); o["Database"] = name; return o; }

        var login = new JsonObject
        {
            ["AutoAccount"] = s.AutoAccount,
            ["Network"] = new JsonObject { ["Host"] = "*", ["Port"] = s.LoginPort },
            ["Connections"] = new JsonObject { ["MySQLProvider"] = Db("aaemu_login") },
            ["GameServers"] = new JsonArray(new JsonObject
            {
                // Con Tailscale il launcher inoltra le porte su 127.0.0.1, quindi il client deve andare a localhost
                ["ID"] = 1, ["Name"] = s.ServerName, ["Host"] = s.UseTailscale ? "127.0.0.1" : s.PublicIp, ["Port"] = s.GamePort
            })
        };
        var game = new JsonObject
        {
            ["Network"] = new JsonObject { ["Host"] = "*", ["Port"] = s.GamePort },
            ["LoginNetwork"] = new JsonObject { ["Host"] = "127.0.0.1", ["Port"] = "1234" },
            ["Connections"] = new JsonObject { ["MySQLProvider"] = Db("aaemu_game") },
            ["DebugInfo"] = false,
        };
        if (!string.IsNullOrWhiteSpace(s.GamePakDir))
            game["ClientData"] = new JsonObject { ["Sources"] = new JsonArray(s.GamePakDir) };

        // World = logica di gioco (carica AAEmu.Game da GameContentRoot) + avvia lo Zone Host che carica le mappe
        var world = new JsonObject
        {
            ["GameContentRoot"] = GameDir,
            ["ZoneGameDataRoot"] = Path.Combine(ZoneClient, "game"),
            ["PublicNetwork"] = new JsonObject { ["Host"] = s.UseTailscale ? "127.0.0.1" : s.PublicIp, ["Port"] = s.GamePort },
            ["ZoneHost"] = new JsonObject
            {
                ["Enabled"] = true,
                ["Executable"] = Path.Combine(Bin64, "AAEmu.ZoneHost.exe"),
                ["WorkingDirectory"] = Bin64,
                ["NativeDll"] = Path.Combine(Bin64, "x2game-dev_dedicate.dll"),
                ["RuntimeLogRoot"] = Path.Combine(ServerDir, "logs", "zone")
            }
        };

        var o = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(LoginDir, "Config.Local.json"), login.ToJsonString(o));
        File.WriteAllText(Path.Combine(GameDir, "Config.Local.json"), game.ToJsonString(o));
        if (Directory.Exists(WorldDir)) File.WriteAllText(Path.Combine(WorldDir, "Config.Local.json"), world.ToJsonString(o));
        foreach (var d in new[] { LoginDir, GameDir, WorldDir }) QuietLogs(d);
        log("panel", "Configurazioni scritte per Login, Game e World.");
    }

    // ---------- Processi ----------

    public async Task StartMySqlAsync()
    {
        if (IsRunning("mysql")) return;
        Start("mysql", Path.Combine(s.MySqlBinDir, "mysqld.exe"),
            $"--datadir=\"{MySqlData}\" --port={s.MySqlPort} --bind-address=127.0.0.1 --console", s.MySqlBinDir);
        for (var i = 0; i < 30; i++)
        {
            try { await using var c = new MySqlConnection(ConnString()); await c.OpenAsync(); return; }
            catch { await Task.Delay(1000); }
        }
        throw new Exception("MySQL non risponde dopo 30 secondi.");
    }

    public async Task StartAllAsync()
    {
        await StartMySqlAsync();
        WriteConfigs();
        await PrepareZoneHostAsync();
        if (!IsRunning("login")) Start("login", Path.Combine(LoginDir, "AAEmu.Login.exe"), "", LoginDir);
        await Task.Delay(2000);
        // "game" = AAEmu.World (contiene la logica di gioco)
        if (!IsRunning("game")) Start("game", Path.Combine(WorldDir, "AAEmu.World.exe"), "", WorldDir);
        _ = Task.Run(async () =>
        {
            try { await StartZonesWhenWorldReady(); }
            catch (Exception ex) { log("zone", "Avvio zone fallito: " + ex); }
        });
    }

    /// <summary>
    /// Le mappe del mondo aperto le carica AAEmu.ZoneHost (lo Zone Manager della guida):
    /// appena il World ascolta sulla 1240 avvio un host per ogni zona in Impostazioni > Zones.
    /// </summary>
    async Task StartZonesWhenWorldReady()
    {
        for (var i = 0; i < 300 && !PortListening(1240); i++) await Task.Delay(1000);
        if (!IsRunning("game")) return;
        var exe = Path.Combine(Bin64, "AAEmu.ZoneHost.exe");
        var zones = s.Zones.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        for (var i = 0; i < zones.Length; i++)
        {
            var z = zones[i];
            if (IsRunning("zone:" + z)) continue;
            var logDir = Path.Combine(ServerDir, "logs", "zone", z);
            Directory.CreateDirectory(logDir);
            var args = $"-dedicated +world_ip 127.0.0.1 +world_port 1240 +world_serveraddr 127.0.0.1 +world_serverport 1240 " +
                       $"+zone {z} +sv_map {z} +instance 0 +sv_port {65000 + i} +db_location game/db/game_decrypted.sqlite3 " +
                       "+e_render 0 +r_Driver Null +npc_move_skip_standing 0 +npc_move_skip_disabledAI 0 +npc_movement_skip 0 +ai_systemupdate 1";
            Start("zone:" + z, exe, args, Bin64, new()
            {
                ["AAEMU_ZONE_DLL"] = Path.Combine(Bin64, "x2game-dev_dedicate.dll"),
                ["AAEMU_ZONE_SAVE_DIR"] = logDir,
                ["AAEMU_ZONE_LOG_NAME"] = z
            });
            _ = HideWindowsAsync(Proc("zone:" + z));
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool ShowWindow(IntPtr h, int cmd);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool EnumWindows(EnumProc cb, IntPtr l);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    delegate bool EnumProc(IntPtr h, IntPtr l);

    /// <summary>Lo Zone Host apre finestre (console e "Gweonid"): per 3 minuti le nascondo appena compaiono.</summary>
    static async Task HideWindowsAsync(Process? p)
    {
        if (p == null) return;
        for (var i = 0; i < 180 && !p.HasExited; i++)
        {
            EnumWindows((h, _) =>
            {
                GetWindowThreadProcessId(h, out var pid);
                if (pid == p.Id && IsWindowVisible(h)) ShowWindow(h, 0);
                return true;
            }, IntPtr.Zero);
            await Task.Delay(1000);
        }
    }

    public IEnumerable<string> ZoneNames => _procs.Keys.Where(k => k.StartsWith("zone:")).ToList();

    static bool PortListening(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    /// <summary>Log a livello INFO: con TRACE il World scrive migliaia di righe al secondo e rallenta molto.</summary>
    static void QuietLogs(string dir)
    {
        var f = Path.Combine(dir, "NLog.config");
        if (!File.Exists(f)) return;
        var t = File.ReadAllText(f);
        var q = System.Text.RegularExpressions.Regex.Replace(t, "minlevel=\"(Trace|Debug)\"", "minlevel=\"Info\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (q != t) File.WriteAllText(f, q);
    }

    public void StopAll()
    {
        foreach (var n in ZoneNames.Concat(["game", "login", "mysql"])) Stop(n);
        foreach (var z in Process.GetProcessesByName("AAEmu.ZoneHost")) try { z.Kill(); } catch { }
    }

    public void Stop(string name)
    {
        if (!_procs.TryGetValue(name, out var p)) return;
        _stopping.Add(name);
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { }
        _procs.Remove(name);
        _stopping.Remove(name);
        log(name, "Fermato.");
    }

    void Start(string name, string exe, string args, string cwd, Dictionary<string, string>? env = null)
    {
        if (!File.Exists(exe)) { log(name, $"File mancante: {exe}"); return; }
        var psi = new ProcessStartInfo(exe, args)
        {
            // lo Zone Host si crea la sua console (AllocConsole): con CreateNoWindow fallisce, quindi finestra nascosta
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = !name.StartsWith("zone:"), WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true,
            // lo Zone Host è un programma nativo: con l'input rediretto si chiude subito
            RedirectStandardInput = !name.StartsWith("zone:")
        };
        foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        // TRACE/DEBUG non vanno a schermo: intasano il pannello
        void Out(string? l) { if (l != null && !l.Contains("[TRACE]") && !l.Contains("[DEBUG]")) log(name, l); }
        p.OutputDataReceived += (_, e) => Out(e.Data);
        p.ErrorDataReceived += (_, e) => Out(e.Data);
        p.Exited += (_, _) =>
        {
            if (_stopping.Contains(name)) return;
            Crashes++;
            log(name, $"ARRESTO IMPREVISTO (codice {SafeExit(p)}). Controlla i log in server\\bin\\...\\Logs");
        };
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        _procs[name] = p;
        log(name, "Avviato.");
    }

    static string SafeExit(Process p) { try { return p.ExitCode.ToString(); } catch { return "?"; } }

    /// <summary>Invia un comando alla console del processo (es. comandi admin del Game server).</summary>
    public void SendConsole(string name, string line)
    {
        if (_procs.TryGetValue(name, out var p) && !p.HasExited) p.StandardInput.WriteLine(line);
        else log(name, "Processo non attivo, comando ignorato.");
    }

    async Task RunToEndAsync(string exe, string args, string tag)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        p.OutputDataReceived += (_, e) => { if (e.Data != null) log(tag, e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data != null) log(tag, e.Data); };
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await p.WaitForExitAsync();
    }

    // ---------- Account ----------

    public static string HashPassword(string pwd) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pwd)));

    public async Task CreateAccountAsync(string user, string pwd)
    {
        await using var c = new MySqlConnection(ConnString("aaemu_login"));
        await c.OpenAsync();
        var cmd = new MySqlCommand("INSERT INTO users (username,password,email,last_login,last_ip,created_at,updated_at) VALUES (@u,@p,'',0,'',0,0)", c);
        cmd.Parameters.AddWithValue("@u", user); cmd.Parameters.AddWithValue("@p", HashPassword(pwd));
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task SetPasswordAsync(long id, string pwd)
    {
        await using var c = new MySqlConnection(ConnString("aaemu_login"));
        await c.OpenAsync();
        var cmd = new MySqlCommand("UPDATE users SET password=@p WHERE id=@i", c);
        cmd.Parameters.AddWithValue("@p", HashPassword(pwd)); cmd.Parameters.AddWithValue("@i", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task DeleteAccountAsync(long id)
    {
        await using var c = new MySqlConnection(ConnString("aaemu_login"));
        await c.OpenAsync();
        var cmd = new MySqlCommand("DELETE FROM users WHERE id=@i", c);
        cmd.Parameters.AddWithValue("@i", id);
        await cmd.ExecuteNonQueryAsync();
    }

    public async Task<System.Data.DataTable> QueryAsync(string db, string sql)
    {
        await using var c = new MySqlConnection(ConnString(db));
        await c.OpenAsync();
        var t = new System.Data.DataTable();
        using var r = await new MySqlCommand(sql, c).ExecuteReaderAsync();
        t.Load(r);
        return t;
    }

    public async Task<int> ExecAsync(string db, string sql)
    {
        await using var c = new MySqlConnection(ConnString(db));
        await c.OpenAsync();
        return await new MySqlCommand(sql, c).ExecuteNonQueryAsync();
    }
}
