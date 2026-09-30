using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MySqlConnector;

namespace DaProd.ServerPanel;

/// <summary>Una zona del mondo (un processo AAEmu.ZoneHost).</summary>
public sealed class ZoneInfo
{
    public required string Name { get; init; }
    public int Instance { get; init; }
    public uint ZoneId { get; init; }
    public int Port { get; init; }
    public string Key => Instance > 0 ? $"{Name}:{Instance}" : Name;
    public Process? Proc;
    public bool Loaded;
    public int Restarts;
    public bool GaveUp;
    /// <summary>Deve essere caricata adesso (sempre attiva, oppure richiesta da un giocatore).</summary>
    public bool Wanted;
    public bool AlwaysOn;
    public int Group;
    public DateTime? EmptySince;
    public DateTime? Started;
    public readonly List<DateTime> RestartLog = [];
    public bool Running => Proc is { HasExited: false };
    /// <summary>Avvio già richiesto e non ancora partito: evita due processi per la stessa zona.</summary>
    public bool Starting;
}

/// <summary>
/// Avvia e sorveglia MySQL, Login, World e le zone. Se un servizio si ferma lo riavvia da solo,
/// e con il job object nessun processo resta aperto se il pannello si chiude.
/// </summary>
public sealed partial class ServerManager(PanelSettings s, Action<string, string> log) : IDisposable
{
    readonly Dictionary<string, Process> _procs = new();
    readonly Dictionary<string, ZoneInfo> _zones = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _stopping = [];
    readonly Dictionary<string, List<DateTime>> _restartLog = new();
    readonly object _lock = new();
    readonly JobObject _job = new();
    CancellationTokenSource? _watch;
    bool _wanted;
    DateTime _serverStart = DateTime.Now;

    public static string ServerDir => Path.Combine(PanelSettings.Root, "server");
    static string MySqlData => Path.Combine(ServerDir, "mysql-data");
    static string LoginDir => Path.Combine(ServerDir, "bin", "login");
    static string GameDir => Path.Combine(ServerDir, "bin", "game");
    static string WorldDir => Path.Combine(ServerDir, "bin", "world");
    static string SqlDir => Path.Combine(ServerDir, "sql");
    static string ZoneFiles => Path.Combine(ServerDir, "zonehost");
    /// <summary>Copia del client solo per il server di zona: Bin64 + game_pak estratto. Il client in gioco\ resta intatto.</summary>
    public static string ZoneClient => Path.Combine(ServerDir, "zoneclient");
    static string Bin64 => Path.Combine(ZoneClient, "Bin64");
    public static string BackupDir => Path.Combine(ServerDir, "backups");

    /// <summary>Cosa sta facendo di lungo (estrazione mappe, backup...): mostrato nella scheda di stato.</summary>
    public string Activity { get; set; } = "";
    public int Crashes { get; private set; }
    public bool Wanted => _wanted;

    public bool IsRunning(string name) => _procs.TryGetValue(name, out var p) && !p.HasExited;
    public Process? Proc(string name) => _procs.TryGetValue(name, out var p) && !p.HasExited ? p : null;
    public IReadOnlyList<ZoneInfo> Zones { get { lock (_lock) return _zones.Values.OrderBy(z => z.Key).ToList(); } }
    public int ZonesTotal { get { lock (_lock) return _zones.Count(z => z.Value.Wanted && !z.Value.GaveUp); } }
    public int ZonesLoaded { get { lock (_lock) return _zones.Count(z => z.Value.Wanted && z.Value.Loaded && z.Value.Running); } }
    public int ZonesFailed { get { lock (_lock) return _zones.Count(z => z.Value.GaveUp); } }

    string MySqlBin => s.ResolveMySqlBin();
    public string ConnString(string db = "") =>
        $"Server=127.0.0.1;Port={s.MySqlPort};User ID=root;Database={db};SslMode=None;AllowUserVariables=true;Default Command Timeout=120";

    // =====================================================================
    // Preparazione (prima volta)
    // =====================================================================

    /// <summary>Prepara la copia del client per le zone: Bin64, game_pak estratto, ZoneHost, database, dedicated.cfg.</summary>
    public async Task PrepareZoneClientAsync()
    {
        var clientBin = Path.Combine(s.ClientDir, "Bin64");
        if (!Directory.Exists(clientBin)) { log("zone", $"Client non trovato in {s.ClientDir}"); return; }
        if (!Directory.Exists(Bin64))
        {
            Activity = "Copio i file del client per le mappe...";
            await Task.Run(() => CopyDir(clientBin, Bin64));
        }
        var pak = Path.Combine(s.ClientDir, "game_pak");
        var extractor = Path.Combine(ServerDir, "tools", "PakExtract", "PakExtract.exe");
        var marker = Path.Combine(ZoneClient, "game", ".estratto");
        if (!File.Exists(marker) && File.Exists(pak) && File.Exists(extractor))
        {
            log("zone", "Estraggo game_pak per le mappe: 20-40 minuti, una volta sola (riprende se interrotto).");
            Activity = "Preparo le mappe (0%)...";
            await RunToEndAsync(extractor, $"\"{pak}\" \"{ZoneClient}\"", "mappe", line =>
            {
                var m = Regex.Match(line, @"PROGRESS \d+/\d+ (\d+)%");
                if (m.Success) Activity = $"Preparo le mappe ({m.Groups[1].Value}%)...";
            });
            Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
            File.WriteAllText(marker, DateTime.Now.ToString("s"));
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
        // il database del mondo serve anche a Game e World come compact.sqlite3 (come da guida)
        foreach (var d in new[] { GameDir, WorldDir })
        {
            var dst = Path.Combine(d, "Data", "compact.sqlite3");
            if (File.Exists(dbSrc) && (!File.Exists(dst) || new FileInfo(dst).Length != new FileInfo(dbSrc).Length))
            { Directory.CreateDirectory(Path.GetDirectoryName(dst)!); File.Copy(dbSrc, dst, true); }
        }
        WriteDedicatedCfg();
        Activity = "";
    }

    /// <summary>game\config\dedicated.cfg come da guida (il database va indicato qui, da riga di comando arriva troppo tardi).</summary>
    void WriteDedicatedCfg()
    {
        var cfg = Path.Combine(ZoneClient, "game", "config", "dedicated.cfg");
        Directory.CreateDirectory(Path.GetDirectoryName(cfg)!);
        const string marker = "-- DaProd";
        var lines = File.Exists(cfg) ? File.ReadAllLines(cfg).TakeWhile(l => l != marker).ToList() : [];
        lines.AddRange([marker, "sys_dedicated_server = 1", "world_serveraddr = \"127.0.0.1\"", "world_serverport = 1240",
            "db_location = \"game/db/game_decrypted.sqlite3\"", "locale = \"en_us\"", "cl_account_id = 1",
            "auth_serveraddr = 127.0.0.1", $"auth_serverport = {s.LoginPort}"]);
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

    /// <summary>Crea il database MySQL e importa le tabelle di Login e Game, se mancano.</summary>
    public async Task FirstSetupAsync()
    {
        var mysqld = Path.Combine(MySqlBin, "mysqld.exe");
        if (!File.Exists(mysqld)) throw new Exception("MySQL non trovato. Serve il pacchetto dati oppure MySQL Server 8.4 installato.");
        if (!Directory.Exists(MySqlData))
        {
            Activity = "Creo il database (prima volta)...";
            log("mysql", "Inizializzo MySQL (prima volta)...");
            await RunToEndAsync(mysqld, $"--initialize-insecure --basedir=\"{Path.GetDirectoryName(MySqlBin)}\" --datadir=\"{MySqlData}\" --console", "mysql");
        }
        await StartMySqlAsync();

        await using (var c = new MySqlConnection(ConnString()))
        {
            await c.OpenAsync();
            foreach (var db in new[] { "aaemu_login", "aaemu_game" })
            {
                var exists = await new MySqlCommand($"SHOW DATABASES LIKE '{db}'", c).ExecuteScalarAsync() != null;
                if (exists) continue;
                Activity = $"Importo le tabelle di {db}...";
                await new MySqlCommand($"CREATE DATABASE `{db}` CHARACTER SET utf8mb4", c).ExecuteNonQueryAsync();
                await RunToEndAsync(Path.Combine(MySqlBin, "mysql.exe"),
                    $"-uroot -h127.0.0.1 -P{s.MySqlPort} {db} -e \"source {Path.Combine(SqlDir, db + ".sql").Replace('\\', '/')}\"", "mysql");
            }
        }
        await EnsureCommandTableAsync();
        WriteConfigs();
        Activity = "";
        log("panel", "Setup del database completato.");
    }

    // =====================================================================
    // Configurazioni (Login, Game, World)
    // =====================================================================

    static string RatesFile => Path.Combine(PanelSettings.DataDir, "rates.json");

    /// <summary>Rate e opzioni del mondo (sezione "World" della configurazione di AAEmu).</summary>
    public Dictionary<string, string> LoadRates()
    {
        var d = new Dictionary<string, string>
        {
            ["ExpRate"] = "100", ["LootRate"] = "1", ["GoldLootMultiplier"] = "100", ["HonorRate"] = "1", ["PvpHonorRate"] = "1",
            ["VocationRate"] = "1", ["GrowthRate"] = "1", ["ActabilityRate"] = "1", ["PlayerLevelCap"] = "55", ["AutoSaveInterval"] = "5", ["MOTD"] = ""
        };
        try
        {
            if (File.Exists(RatesFile))
                foreach (var (k, v) in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(RatesFile)) ?? [])
                    d[k] = v;
        }
        catch { }
        return d;
    }

    public void SaveRates(Dictionary<string, string> rates)
    {
        Directory.CreateDirectory(PanelSettings.DataDir);
        File.WriteAllText(RatesFile, JsonSerializer.Serialize(rates, new JsonSerializerOptions { WriteIndented = true }));
    }

    public void WriteConfigs()
    {
        JsonNode Db(string name) => new JsonObject
        {
            ["Host"] = "127.0.0.1", ["Port"] = s.MySqlPort.ToString(), ["User"] = "root", ["Password"] = "", ["SslMode"] = "None", ["Database"] = name
        };
        var host = s.UseTailscale ? "127.0.0.1" : s.PublicIp; // con Tailscale il launcher inoltra le porte su localhost

        var login = new JsonObject
        {
            ["AutoAccount"] = s.AutoAccount,
            ["Network"] = new JsonObject { ["Host"] = "*", ["Port"] = s.LoginPort },
            ["Connections"] = new JsonObject { ["MySQLProvider"] = Db("aaemu_login") },
            ["GameServers"] = new JsonArray(new JsonObject { ["ID"] = 1, ["Name"] = s.ServerName, ["Host"] = host, ["Port"] = s.GamePort })
        };

        var world = new JsonObject();
        foreach (var (k, v) in LoadRates())
        {
            if (k == "MOTD") world[k] = v;
            else if (double.TryParse(v.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n)) world[k] = n;
        }
        var game = new JsonObject
        {
            ["Network"] = new JsonObject { ["Host"] = "*", ["Port"] = s.GamePort },
            ["LoginNetwork"] = new JsonObject { ["Host"] = "127.0.0.1", ["Port"] = "1234" },
            ["Connections"] = new JsonObject { ["MySQLProvider"] = Db("aaemu_game") },
            ["DebugInfo"] = false,
            ["World"] = world
        };
        var pak = Path.Combine(s.ClientDir, "game_pak");
        if (File.Exists(pak)) game["ClientData"] = new JsonObject { ["Sources"] = new JsonArray(pak) };

        // World = logica di gioco (carica AAEmu.Game da GameContentRoot); le zone le avvia il pannello
        var worldCfg = new JsonObject
        {
            ["GameContentRoot"] = GameDir,
            ["ZoneGameDataRoot"] = Path.Combine(ZoneClient, "game"),
            ["PublicNetwork"] = new JsonObject { ["Host"] = host, ["Port"] = s.GamePort },
            ["ZoneHost"] = new JsonObject
            {
                ["Enabled"] = true,
                ["Executable"] = Path.Combine(Bin64, "AAEmu.ZoneHost.exe"),
                ["WorkingDirectory"] = Bin64,
                ["NativeDll"] = Path.Combine(Bin64, "x2game-dev_dedicate.dll"),
                ["RuntimeLogRoot"] = Path.Combine(ServerDir, "logs", "zone")
            }
        };

        ConfigOverrides.Apply(game, Path.Combine(GameDir, "Configurations"));
        if (!File.Exists(Tweaks.FilePath)) Tweaks.Save(Tweaks.Load());
        var o = new JsonSerializerOptions { WriteIndented = true };
        File.WriteAllText(Path.Combine(LoginDir, "Config.Local.json"), login.ToJsonString(o));
        File.WriteAllText(Path.Combine(GameDir, "Config.Local.json"), game.ToJsonString(o));
        if (Directory.Exists(WorldDir)) File.WriteAllText(Path.Combine(WorldDir, "Config.Local.json"), worldCfg.ToJsonString(o));
        foreach (var d in new[] { LoginDir, GameDir, WorldDir }) QuietLogs(d);
    }

    /// <summary>Log a livello INFO: con TRACE il World scrive migliaia di righe al secondo e rallenta molto.</summary>
    static void QuietLogs(string dir)
    {
        var f = Path.Combine(dir, "NLog.config");
        if (!File.Exists(f)) return;
        var t = File.ReadAllText(f);
        var q = Regex.Replace(t, "minlevel=\"(Trace|Debug)\"", "minlevel=\"Info\"", RegexOptions.IgnoreCase);
        if (q != t) File.WriteAllText(f, q);
    }

    // =====================================================================
    // Avvio / arresto
    // =====================================================================

    public async Task StartMySqlAsync()
    {
        if (IsRunning("mysql")) return;
        var basedir = Path.GetDirectoryName(MySqlBin)!;
        Start("mysql", Path.Combine(MySqlBin, "mysqld.exe"),
            $"--basedir=\"{basedir}\" --datadir=\"{MySqlData}\" --port={s.MySqlPort} --bind-address=127.0.0.1 --console", MySqlBin);
        for (var i = 0; i < 60; i++)
        {
            try { await using var c = new MySqlConnection(ConnString()); await c.OpenAsync(); return; }
            catch { await Task.Delay(1000); }
        }
        throw new Exception("MySQL non risponde dopo 60 secondi.");
    }

    void StartLogin() =>
        Start("login", Path.Combine(LoginDir, "AAEmu.Login.exe"), "", LoginDir, new() { ["DAPROD_TOKEN_SECRET"] = s.TokenSecret });

    void StartWorld() => Start("game", Path.Combine(WorldDir, "AAEmu.World.exe"), "", WorldDir, new() { ["DAPROD_TWEAKS"] = Tweaks.FilePath });

    public async Task StartAllAsync()
    {
        if (s.Maintenance) { log("panel", "Manutenzione attiva: Login e World restano spenti."); return; }
        _wanted = true;
        _serverStart = DateTime.Now;
        var killed = await Task.Run(() => JobObject.KillStale(PanelSettings.Root));
        if (killed > 0) log("panel", $"Chiusi {killed} processi rimasti aperti da una sessione precedente.");
        await FirstSetupAsync();
        try { await EnsureShopAsync(); } catch (Exception ex) { log("panel", "Shop: " + ex.Message); }
        await PrepareZoneClientAsync();
        _ = Task.Run(() => { try { if (!File.Exists(ClientDb.Output(s)) && File.Exists(ClientDb.Source)) log("panel", ClientDb.Build(s)); } catch (Exception ex) { log("panel", "Dati client: " + ex.Message); } });
        WriteConfigs();
        RegisterZones();
        if (!IsRunning("login")) StartLogin();
        await Task.Delay(2000);
        if (!IsRunning("game")) StartWorld();
        _ = Task.Run(StartZonesWhenWorldReady);
        StartWatchdog();
    }

    public void StopAll()
    {
        _wanted = false;
        _watch?.Cancel();
        foreach (var z in Zones) StopZoneProc(z);
        Stop("game"); Stop("login");
        StopMySql();
        foreach (var z in Process.GetProcessesByName("AAEmu.ZoneHost")) try { if (z.MainModule?.FileName?.StartsWith(ServerDir, StringComparison.OrdinalIgnoreCase) == true) z.Kill(); } catch { }
    }

    public async Task RestartAsync()
    {
        StopAll();
        await Task.Delay(2500);
        await StartAllAsync();
    }

    void StopMySql()
    {
        if (!IsRunning("mysql")) return;
        try
        {
            var a = Process.Start(new ProcessStartInfo(Path.Combine(MySqlBin, "mysqladmin.exe"), $"-uroot -h127.0.0.1 -P{s.MySqlPort} shutdown")
            { UseShellExecute = false, CreateNoWindow = true });
            a?.WaitForExit(20000);
        }
        catch { }
        Stop("mysql");
    }

    public void Stop(string name)
    {
        if (!_procs.TryGetValue(name, out var p)) return;
        lock (_stopping) _stopping.Add(name);
        try { if (!p.HasExited) { p.Kill(true); p.WaitForExit(5000); } } catch { }
        _procs.Remove(name);
        lock (_stopping) _stopping.Remove(name);
        log(name, "Fermato.");
    }

    Process? Start(string name, string exe, string args, string cwd, Dictionary<string, string>? env = null)
    {
        if (!File.Exists(exe)) { log(name, $"File mancante: {exe}"); return null; }
                var psi = new ProcessStartInfo(exe, args)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            RedirectStandardInput = true
        };
        foreach (var (k, v) in env ?? []) psi.Environment[k] = v;
        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        void Out(string? l)
        {
            if (l == null || l.Contains("[TRACE]") || l.Contains("[DEBUG]")) return;
            if (name == "game") ParseWorldLine(l);
            log(name, l);
        }
        p.OutputDataReceived += (_, e) => Out(e.Data);
        p.ErrorDataReceived += (_, e) => Out(e.Data);
        p.Exited += (_, _) => OnExited(name, p);
        p.Start(); p.BeginOutputReadLine(); p.BeginErrorReadLine();
        _job.Add(p);
        _procs[name] = p;
        log(name, "Avviato.");
        return p;
    }

    void OnExited(string name, Process p)
    {
        bool expected; lock (_stopping) expected = _stopping.Contains(name) || !_wanted;
        if (name.StartsWith("zone:"))
        {
            lock (_lock) { if (_zones.TryGetValue(name[5..], out var z)) z.Loaded = false; }
            if (expected) return;
        }
        else if (expected) return;
        Crashes++;
        string code; try { code = p.ExitCode.ToString(); } catch { code = "?"; }
        log(name, $"ARRESTO IMPREVISTO (codice {code}).{(s.Watchdog ? " Lo riavvio da solo." : "")}");
    }

    // =====================================================================
    // Zone
    // =====================================================================

    void RegisterZones()
    {
        lock (_lock)
        {
            var wanted = s.Zones.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(x =>
            {
                var p = x.Split(':');
                return (name: p[0], inst: p.Length > 1 && int.TryParse(p[1], out var n) ? n : 0);
            }).ToList();
            var always = s.AlwaysOnZones.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var (name, inst) in wanted)
            {
                var z = new ZoneInfo { Name = name, Instance = inst, ZoneId = ZoneCatalog.IdOf(name), Port = 60100 + _zones.Count, Group = ZoneCatalog.GroupOf(name) };
                if (!_zones.TryGetValue(z.Key, out var ex)) _zones[z.Key] = ex = z;
                ex.AlwaysOn = !s.DynamicZones || always.Contains(name);
                if (!ex.Running) { ex.Wanted = ex.AlwaysOn; ex.Started = null; ex.GaveUp = false; ex.Restarts = 0; ex.EmptySince = null; ex.Loaded = false; }
            }
            lock (_players) _players.Clear();
            foreach (var k in _zones.Keys.Where(k => !wanted.Any(w => (w.inst > 0 ? $"{w.name}:{w.inst}" : w.name).Equals(k, StringComparison.OrdinalIgnoreCase))).ToList())
                if (!_zones[k].Running) _zones.Remove(k);
        }
    }

    async Task StartZonesWhenWorldReady()
    {
        for (var i = 0; i < 300 && _wanted && !PortListening(1240); i++) await Task.Delay(1000);
        if (!IsRunning("game")) return;
        foreach (var z in Zones.Where(z => z.Wanted))
        {
            if (!_wanted || !IsRunning("game")) return;
            if (z.Running || z.GaveUp) continue;
            await StartZoneSafeAsync(z);
            await Task.Delay(1500);
        }
    }

    /// <summary>Avvia una zona aspettando che non ce ne siano più di 6 in caricamento: niente picchi di CPU e RAM.</summary>
    async Task StartZoneSafeAsync(ZoneInfo z)
    {
        try
        {
            for (var i = 0; i < 180 && Zones.Count(x => x.Running && !x.Loaded) >= 6; i++) await Task.Delay(1000);
            if (!_wanted || !IsRunning("game") || z.Running || !z.Wanted) return;
            StartZoneProc(z);
        }
        finally { z.Starting = false; }
    }

    // ---- zone dinamiche: il World scrive DAPROD_ZONE_ENTER / LEAVE / NEED, qui le leggo
    readonly Dictionary<string, HashSet<uint>> _players = new();
    static readonly Regex ZoneEvRx = new(@"DAPROD_ZONE_(ENTER|LEAVE|NEED) zone=(\d+) inst=(\d+)(?: bc=(\d+))?", RegexOptions.Compiled);

    public int PlayersIn(ZoneInfo z) { lock (_players) return _players.GetValueOrDefault($"{z.ZoneId}:{z.Instance}")?.Count ?? 0; }
    int GroupPlayers(ZoneInfo z)
    {
        var zs = z.Group > 0 ? Zones.Where(x => x.Group == z.Group).ToList() : [z];
        return zs.Sum(PlayersIn);
    }

    void OnZoneEvent(string kind, uint zone, uint inst, uint bc)
    {
        var key = $"{zone}:{inst}";
        if (kind == "NEED") { RequestZone(zone, (int)inst); return; }
        lock (_players)
        {
            foreach (var set in _players.Values) set.Remove(bc);            // un giocatore è in una zona sola
            if (kind == "ENTER") { if (!_players.TryGetValue(key, out var set)) _players[key] = set = []; set.Add(bc); }
        }
        if (kind == "ENTER") RequestZone(zone, (int)inst); // prepara anche le zone vicine della stessa regione
    }

    /// <summary>Carica la zona (e le altre della sua regione) se non lo è già.</summary>
    void RequestZone(uint zoneId, int inst)
    {
        List<ZoneInfo> toStart = [];
        lock (_lock)
        {
            var z = _zones.Values.FirstOrDefault(x => x.ZoneId == zoneId && x.Instance == inst) ?? _zones.Values.FirstOrDefault(x => x.ZoneId == zoneId);
            if (z == null) return;
            // prima la zona richiesta, poi la sua regione, poi le regioni confinanti (il giocatore ci può camminare dentro)
            var near = z.Group > 0 ? ZoneCatalog.NeighboursOf(z.Group) : [];
            foreach (var x in _zones.Values
                         .Where(x => x == z || (z.Group > 0 && (x.Group == z.Group || near.Contains(x.Group))))
                         .OrderBy(x => x == z ? 0 : x.Group == z.Group ? 1 : 2))
            {
                x.EmptySince = null;
                if (x.Running || x.Starting) continue;
                if (!x.Wanted) { x.Wanted = true; x.GaveUp = false; x.RestartLog.Clear(); }
                x.Starting = true;
                toStart.Add(x);
            }
        }
        if (toStart.Count == 0) return;
        // avvio in fila (la zona richiesta per prima, al massimo 6 in caricamento insieme)
        _ = Task.Run(async () =>
        {
            foreach (var x in toStart)
            {
                log("zone", $"Carico {x.Key} (serve a un giocatore o è vicina).");
                await StartZoneSafeAsync(x);
                await Task.Delay(300);
            }
        });
    }

    /// <summary>Scarica le zone dinamiche rimaste vuote (con la loro regione) per più di ZoneIdleMinutes.</summary>
    void UnloadIdleZones()
    {
        if (!s.DynamicZones) return;
        // regioni "in uso": quelle con giocatori e le loro confinanti (restano caricate finché servono)
        var busy = new HashSet<int>();
        foreach (var z in Zones.Where(z => z.Group > 0 && PlayersIn(z) > 0))
        {
            busy.Add(z.Group);
            foreach (var n in ZoneCatalog.NeighboursOf(z.Group)) busy.Add(n);
        }
        foreach (var z in Zones.Where(z => z.Wanted && !z.AlwaysOn && z.Running))
        {
            if (z.Started != null && DateTime.Now - z.Started < TimeSpan.FromMinutes(3)) continue;
            if (GroupPlayers(z) > 0 || (z.Group > 0 && busy.Contains(z.Group))) { z.EmptySince = null; continue; }
            z.EmptySince ??= DateTime.Now;
            if (DateTime.Now - z.EmptySince < TimeSpan.FromMinutes(Math.Max(1, s.ZoneIdleMinutes))) continue;
            log("zone", $"Scarico {z.Key}: vuota da {s.ZoneIdleMinutes} minuti.");
            z.Wanted = false; z.EmptySince = null;
            StopZoneProc(z);
        }
    }

    /// <summary>Carica a mano una zona dormiente.</summary>
    public void LoadZone(string key)
    {
        ZoneInfo? z; lock (_lock) _zones.TryGetValue(key, out z);
        if (z == null) return;
        z.Wanted = true; z.GaveUp = false; z.RestartLog.Clear();
        if (z.Running || z.Starting) return;
        z.Starting = true;
        _ = Task.Run(() => StartZoneSafeAsync(z));
    }

    void StartZoneProc(ZoneInfo z)
    {
        var exe = Path.Combine(Bin64, "AAEmu.ZoneHost.exe");
        var logDir = Path.Combine(ServerDir, "logs", "zone", z.Key.Replace(':', '_'));
        Directory.CreateDirectory(logDir);
        var args = $"-dedicated +world_ip 127.0.0.1 +world_port 1240 +world_serveraddr 127.0.0.1 +world_serverport 1240 " +
                   $"+zone {z.Name} +sv_map {z.Name} +instance {z.Instance} +sv_port {z.Port} +db_location game/db/game_decrypted.sqlite3 " +
                   "+e_render 0 +r_Driver Null +npc_move_skip_standing 0 +npc_move_skip_disabledAI 0 +npc_movement_skip 0 +ai_systemupdate 1";
        z.Loaded = false;
        z.Started = DateTime.Now;
        var name = "zone:" + z.Key;
        if (!File.Exists(exe)) { log("zone", $"File mancante: {exe}"); return; }
        // desktop nascosto: le finestre dello Zone Host non compaiono mai. L'output non passa dal pannello, ha i suoi log in logs\zone.
        z.Proc = HiddenDesktop.Start(exe, args, Bin64, new()
        {
            ["AAEMU_ZONE_DLL"] = Path.Combine(Bin64, "x2game-dev_dedicate.dll"),
            ["AAEMU_ZONE_SAVE_DIR"] = logDir,
            ["AAEMU_ZONE_LOG_NAME"] = z.Key.Replace(':', '_')
        });
        if (z.Proc == null) { log("zone", $"Zona {z.Key}: avvio fallito."); return; }
        _job.Add(z.Proc);
        var proc = z.Proc;
        proc.EnableRaisingEvents = true;
        proc.Exited += (_, _) => OnExited(name, proc);
    }

    void StopZoneProc(ZoneInfo z)
    {
        if (z.Proc == null) return;
        var key = "zone:" + z.Key;
        lock (_stopping) _stopping.Add(key);
        try { if (!z.Proc.HasExited) { z.Proc.Kill(true); z.Proc.WaitForExit(4000); } } catch { }
        z.Loaded = false;
        lock (_stopping) _stopping.Remove(key);
    }

    /// <summary>Riavvia una sola zona: gli altri giocatori restano collegati.</summary>
    public async Task RestartZoneAsync(string key)
    {
        ZoneInfo? z; lock (_lock) _zones.TryGetValue(key, out z);
        if (z == null) return;
        StopZoneProc(z); z.GaveUp = false; z.Wanted = true; z.RestartLog.Clear();
        await Task.Delay(1500);
        StartZoneProc(z);
    }

    public void StopZone(string key)
    {
        ZoneInfo? z; lock (_lock) _zones.TryGetValue(key, out z);
        if (z == null) return;
        z.Wanted = false; z.EmptySince = null; // scaricata a mano: torna dormiente (si ricarica se serve a un giocatore)
        StopZoneProc(z);
        log("zone", $"Zona {key} fermata.");
    }

    static readonly Regex ZoneLoadedRx = new(@"ZoneLoaded zoneId=(\d+) instanceId=(\d+)", RegexOptions.Compiled);
    static readonly Regex ZoneLostRx = new(@"(Zone lost|Zone disconnect).*zoneId=(\d+) instanceId=(\d+)", RegexOptions.Compiled);

    void ParseWorldLine(string l)
    {
        Match m;
        if ((m = ZoneEvRx.Match(l)).Success) OnZoneEvent(m.Groups[1].Value, uint.Parse(m.Groups[2].Value), uint.Parse(m.Groups[3].Value), m.Groups[4].Success ? uint.Parse(m.Groups[4].Value) : 0);
        else if ((m = ZoneLoadedRx.Match(l)).Success) SetLoaded(uint.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), true);
        else if ((m = ZoneLostRx.Match(l)).Success) SetLoaded(uint.Parse(m.Groups[2].Value), int.Parse(m.Groups[3].Value), false);
    }

    void SetLoaded(uint zoneId, int inst, bool loaded)
    {
        lock (_lock)
            foreach (var z in _zones.Values.Where(z => z.ZoneId == zoneId && z.Instance == inst)) z.Loaded = loaded;
    }

    static bool PortListening(int port) =>
        System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);

    // =====================================================================
    // Controllo automatico
    // =====================================================================

    void StartWatchdog()
    {
        _watch?.Cancel();
        _watch = new CancellationTokenSource();
        var ct = _watch.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(6000, ct); await WatchOnceAsync(); }
                catch (OperationCanceledException) { return; }
                catch (Exception ex) { log("controllo", ex.Message); }
            }
        }, ct);
    }

    bool CanRestart(string key)
    {
        if (!_restartLog.TryGetValue(key, out var l)) _restartLog[key] = l = [];
        l.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(10));
        if (l.Count >= 5) return false;
        l.Add(DateTime.Now);
        return true;
    }

    async Task WatchOnceAsync()
    {
        await MaybeBackupAsync();
        if (!_wanted || !s.Watchdog || s.Maintenance) return;
        if (!IsRunning("mysql") && !IsRunning("game") && !IsRunning("login")) return; // spento di proposito
        if (!IsRunning("mysql")) { if (CanRestart("mysql")) { log("controllo", "MySQL fermo: lo riavvio."); await StartMySqlAsync(); } return; }
        if (!IsRunning("login") && CanRestart("login")) { log("controllo", "Login fermo: lo riavvio."); StartLogin(); }
        if (!IsRunning("game"))
        {
            if (!CanRestart("game")) { log("controllo", "World fermo troppe volte: non lo riavvio più. Controlla i log."); return; }
            log("controllo", "World fermo: lo riavvio con le sue zone.");
            foreach (var z in Zones) StopZoneProc(z);
            StartWorld();
            _ = Task.Run(StartZonesWhenWorldReady);
            return;
        }
        UnloadIdleZones();
        foreach (var z in Zones)
        {
            // le zone mai avviate le avvia l'avvio scaglionato: il controllo interviene solo su quelle cadute
            if (z.Running || z.GaveUp || !z.Wanted || z.Started == null || !PortListening(1240)) continue;
            if (DateTime.Now - z.Started < TimeSpan.FromSeconds(20)) continue;
            z.RestartLog.RemoveAll(t => DateTime.Now - t > TimeSpan.FromMinutes(10));
            if (z.RestartLog.Count >= 4) { z.GaveUp = true; log("controllo", $"Zona {z.Key}: si ferma di continuo, la escludo."); continue; }
            z.RestartLog.Add(DateTime.Now); z.Restarts++;
            log("controllo", $"Zona {z.Key} ferma: la riavvio.");
            await StartZoneSafeAsync(z);
            await Task.Delay(1500);
        }
    }

    /// <summary>Invia un comando alla console del processo, se la accetta.</summary>
    public void SendConsole(string name, string line)
    {
        if (_procs.TryGetValue(name, out var p) && !p.HasExited) p.StandardInput.WriteLine(line);
        else log(name, "Processo non attivo, comando ignorato.");
    }

    async Task RunToEndAsync(string exe, string args, string tag, Action<string>? onLine = null)
    {
        var p = Process.Start(new ProcessStartInfo(exe, args)
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        _job.Add(p);
        void Out(string? l) { if (l == null) return; onLine?.Invoke(l); if (!l.StartsWith("PROGRESS")) log(tag, l); }
        p.OutputDataReceived += (_, e) => Out(e.Data);
        p.ErrorDataReceived += (_, e) => Out(e.Data);
        p.BeginOutputReadLine(); p.BeginErrorReadLine();
        await p.WaitForExitAsync();
    }

    // =====================================================================
    // Backup
    // =====================================================================

    /// <summary>Backup automatico ogni BackupEveryHours ore (solo con MySQL acceso).</summary>
    async Task MaybeBackupAsync()
    {
        if (s.BackupEveryHours <= 0 || !IsRunning("mysql") || !_wanted) return;
        var last = Backups().FirstOrDefault()?.LastWriteTime ?? DateTime.MinValue;
        if (DateTime.Now - last < TimeSpan.FromHours(s.BackupEveryHours)) return;
        if (DateTime.Now - _serverStart < TimeSpan.FromMinutes(5)) return; // non durante il caricamento
        try { await BackupNowAsync(); } catch (Exception ex) { log("backup", "Backup automatico fallito: " + ex.Message); }
    }

    public IEnumerable<FileInfo> Backups() =>
        Directory.Exists(BackupDir) ? new DirectoryInfo(BackupDir).GetFiles("*.sql.gz").OrderByDescending(f => f.LastWriteTime) : [];

    public async Task<string> BackupNowAsync()
    {
        if (!IsRunning("mysql")) throw new Exception("MySQL non è acceso.");
        Directory.CreateDirectory(BackupDir);
        var file = Path.Combine(BackupDir, $"daprod_{DateTime.Now:yyyyMMdd_HHmm}.sql.gz");
        var prev = Activity; Activity = "Backup dei database...";
        try
        {
            var p = Process.Start(new ProcessStartInfo(Path.Combine(MySqlBin, "mysqldump.exe"),
                $"-uroot -h127.0.0.1 -P{s.MySqlPort} --single-transaction --routines --databases aaemu_login aaemu_game")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            await using (var fs = File.Create(file))
            await using (var gz = new GZipStream(fs, CompressionLevel.Fastest))
                await p.StandardOutput.BaseStream.CopyToAsync(gz);
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) { File.Delete(file); throw new Exception("mysqldump: " + await p.StandardError.ReadToEndAsync()); }
        }
        finally { Activity = prev; }
        foreach (var old in Backups().Skip(Math.Max(1, s.KeepBackups))) try { old.Delete(); } catch { }
        log("backup", $"Creato {Path.GetFileName(file)} ({new FileInfo(file).Length / 1048576} MB).");
        return file;
    }

    /// <summary>Ripristina un backup. Ferma il server, sostituisce i database e riavvia.</summary>
    public async Task RestoreAsync(string file)
    {
        var wasWanted = _wanted;
        StopAll();
        await Task.Delay(1500);
        await StartMySqlAsync();
        Activity = "Ripristino il backup...";
        try
        {
            var p = Process.Start(new ProcessStartInfo(Path.Combine(MySqlBin, "mysql.exe"), $"-uroot -h127.0.0.1 -P{s.MySqlPort}")
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardError = true })!;
            await using (var fs = File.OpenRead(file))
            await using (var gz = new GZipStream(fs, CompressionMode.Decompress))
                await gz.CopyToAsync(p.StandardInput.BaseStream);
            p.StandardInput.Close();
            await p.WaitForExitAsync();
            if (p.ExitCode != 0) throw new Exception("Ripristino fallito: " + await p.StandardError.ReadToEndAsync());
        }
        finally { Activity = ""; }
        log("backup", "Backup ripristinato.");
        StopMySql();
        if (wasWanted) await StartAllAsync();
    }

    // =====================================================================
    // Account e database
    // =====================================================================

    public static string HashPassword(string pwd) => Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(pwd)));

    /// <summary>Token di accesso per il client: "{scadenza}.{HMAC(utente|scadenza)}", verificato dal Login.</summary>
    public string MakeToken(string user, int hours = 12)
    {
        var exp = DateTimeOffset.UtcNow.AddHours(hours).ToUnixTimeSeconds();
        using var h = new HMACSHA256(Encoding.UTF8.GetBytes(s.TokenSecret));
        return $"{exp}.{Convert.ToHexString(h.ComputeHash(Encoding.UTF8.GetBytes($"{user}|{exp}"))).ToLowerInvariant()}";
    }

    /// <summary>Tabella dei comandi in tempo reale: il World la legge ogni 2 secondi (vedi DaProdCommands.cs nella patch di AAEmu).</summary>
    public Task<int> EnsureCommandTableAsync() =>
        ExecAsync("aaemu_game", "CREATE TABLE IF NOT EXISTS daprod_commands (id BIGINT AUTO_INCREMENT PRIMARY KEY, kind VARCHAR(20) NOT NULL, " +
            "char_id INT UNSIGNED NOT NULL, amount BIGINT NOT NULL DEFAULT 0, done TINYINT NOT NULL DEFAULT 0, result VARCHAR(60) NULL, created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP)");

    /// <summary>Manda un comando al World e ne aspetta l'esito (null = nessuna risposta entro 12 secondi).</summary>
    async Task<string?> RunCommandAsync(string kind, long charId, long amount)
    {
        await EnsureCommandTableAsync();
        await ExecAsync("aaemu_game", "INSERT INTO daprod_commands (kind, char_id, amount) VALUES (@k, @i, @a)", ("@k", kind), ("@i", charId), ("@a", amount));
        var id = Convert.ToInt64((await QueryAsync("aaemu_game", "SELECT MAX(id) FROM daprod_commands")).Rows[0][0]);
        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(1000);
            var t = await QueryAsync("aaemu_game", "SELECT done, result FROM daprod_commands WHERE id=@i", ("@i", id));
            if (t.Rows.Count == 1 && Convert.ToInt32(t.Rows[0]["done"]) == 1) return t.Rows[0]["result"] as string ?? "";
        }
        return null;
    }

    const string NoAnswer = "Il World non ha risposto entro 12 secondi (si sta ancora avviando?). Il comando resta in coda e verrà eseguito appena possibile.";

    /// <summary>Aggiunge oro (in rame) a un personaggio: subito in gioco se è online, altrimenti nel database. Restituisce un messaggio per l'utente.</summary>
    public async Task<string> GiveGoldAsync(long charId, long copper)
    {
        if (!IsRunning("game"))
        {
            var n = await ExecAsync("aaemu_game", "UPDATE characters SET money=GREATEST(0, money+@a) WHERE id=@i", ("@a", copper), ("@i", charId));
            return n > 0 ? "Server spento: oro aggiunto nel database, lo vedrai al prossimo accesso." : "Personaggio non trovato.";
        }
        var r = await RunCommandAsync("gold", charId, copper);
        return r == null ? NoAnswer : r == "online" ? "Oro consegnato in gioco." : r == "offline" ? "Personaggio non collegato: oro aggiunto, lo vedrai all'accesso." : "Esito: " + r;
    }

    /// <summary>Riempie al massimo il labor del personaggio (in tempo reale se è in gioco).</summary>
    public async Task<string> FillLaborAsync(long charId)
    {
        if (!IsRunning("game"))
        {
            var n = await ExecAsync("aaemu_game", "UPDATE accounts SET labor=GREATEST(labor, 5000) WHERE account_id=(SELECT account_id FROM characters WHERE id=@i)", ("@i", charId));
            return n > 0 ? "Server spento: labor riempito nel database." : "Personaggio non trovato.";
        }
        var r = await RunCommandAsync("labor", charId, 0);
        return r == null ? NoAnswer : r == "online" ? "Labor riempito al massimo in gioco." : r == "offline" ? "Personaggio non collegato: labor dell'account riempito, lo vedrai all'accesso." : "Esito: " + r;
    }

    /// <summary>Aggiunge punti al pass in corso del personaggio (deve essere in gioco).</summary>
    public async Task<string> GivePassPointsAsync(long charId, long points)
    {
        if (!IsRunning("game")) return "Server spento.";
        var r = await RunCommandAsync("passpoints", charId, points);
        return r == null ? NoAnswer : r == "online" ? "Punti pass consegnati." : r == "nessun pass in corso" ? "Il personaggio non ha nessun pass in corso: deve avviarne uno dal gioco (finestra ArchePass)." : "Esito: " + r;
    }

    // =====================================================================
    // Shop (Arche Mall)
    // =====================================================================

    /// <summary>Se lo shop è vuoto carica quello predefinito; poi, se "shopFree" è attivo, azzera prezzi e limiti.</summary>
    async Task EnsureShopAsync()
    {
        var n = Convert.ToInt64((await QueryAsync("aaemu_game", "SELECT COUNT(*) FROM ics_skus WHERE shop_id >= 3000000")).Rows[0][0]);
        if (n == 0 && File.Exists(Path.Combine(SqlDir, "shop-full.sql"))) await ImportShopAsync(false); // shop vuoto o solo quello predefinito
        if (Tweaks.On("shopFree")) await ApplyShopFreeAsync(false);
    }

    /// <summary>Carica lo shop predefinito (esempio ufficiale di AAEmu) con i prezzi originali; poi applica "tutto gratis" se attivo.</summary>
    public async Task<string> ImportShopAsync(bool reloadInGame = true)
    {
        var file = Path.Combine(SqlDir, "examples", "example-ics-default-en.sql");
        if (!File.Exists(file)) return "File dello shop non trovato: " + file;
        await RunToEndAsync(Path.Combine(MySqlBin, "mysql.exe"),
            $"-uroot -h127.0.0.1 -P{s.MySqlPort} aaemu_game --default-character-set=utf8mb4 -e \"source {file.Replace('\\', '/')}\"", "mysql");
        var full = Path.Combine(SqlDir, "shop-full.sql"); // tutti gli oggetti del gioco divisi per scheda (tools\genshop.py)
        if (File.Exists(full))
            await RunToEndAsync(Path.Combine(MySqlBin, "mysql.exe"),
                $"-uroot -h127.0.0.1 -P{s.MySqlPort} aaemu_game --default-character-set=utf8mb4 -e \"source {full.Replace('\\', '/')}\"", "mysql");
        if (Tweaks.On("shopFree")) await ApplyShopFreeAsync(false);
        if (reloadInGame) await ReloadShopInGameAsync();
        return "Shop caricato.";
    }

    /// <summary>Prezzi a zero e nessun limite di acquisto, livello o disponibilità.</summary>
    public async Task<string> ApplyShopFreeAsync(bool reloadInGame = true)
    {
        await ExecAsync("aaemu_game", "UPDATE ics_skus SET price=0, discount_price=0");
        await ExecAsync("aaemu_game", "UPDATE ics_shop_items SET limited_type=0, limited_stock_max=0, level_min=0, level_max=0, buy_restrict_type=0, buy_restrict_id=0, is_hidden=0, remaining=-1");
        if (reloadInGame) await ReloadShopInGameAsync();
        return "Shop gratis e senza limiti.";
    }

    /// <summary>Fa rileggere lo shop al World in esecuzione (i giocatori riaprono la finestra).</summary>
    public async Task ReloadShopInGameAsync()
    {
        if (IsRunning("game")) await RunCommandAsync("shop_reload", 0, 0);
    }

    public async Task CreateAccountAsync(string user, string pwd) =>
        await ExecAsync("aaemu_login", "INSERT INTO users (username,password,email,last_login,last_ip,created_at,updated_at) VALUES (@u,@p,'',0,'',0,0)",
            ("@u", user), ("@p", HashPassword(pwd)));

    public async Task SetPasswordAsync(long id, string pwd) =>
        await ExecAsync("aaemu_login", "UPDATE users SET password=@p WHERE id=@i", ("@p", HashPassword(pwd)), ("@i", id));

    public async Task DeleteAccountAsync(long id) =>
        await ExecAsync("aaemu_login", "DELETE FROM users WHERE id=@i", ("@i", id));

    public async Task<bool> CheckPasswordAsync(string user, string pwd)
    {
        var t = await QueryAsync("aaemu_login", "SELECT password, banned FROM users WHERE username=@u", ("@u", user));
        return t.Rows.Count == 1 && Convert.ToInt32(t.Rows[0]["banned"]) == 0 && (string)t.Rows[0]["password"] == HashPassword(pwd);
    }

    public async Task<System.Data.DataTable> QueryAsync(string db, string sql, params (string, object)[] p)
    {
        await using var c = new MySqlConnection(ConnString(db));
        await c.OpenAsync();
        var cmd = new MySqlCommand(sql, c);
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
        var t = new System.Data.DataTable();
        using var r = await cmd.ExecuteReaderAsync();
        t.Load(r);
        return t;
    }

    public async Task<int> ExecAsync(string db, string sql, params (string, object)[] p)
    {
        await using var c = new MySqlConnection(ConnString(db));
        await c.OpenAsync();
        var cmd = new MySqlCommand(sql, c);
        foreach (var (k, v) in p) cmd.Parameters.AddWithValue(k, v);
        return await cmd.ExecuteNonQueryAsync();
    }

    public void Dispose() { _watch?.Cancel(); _job.Dispose(); }
}
