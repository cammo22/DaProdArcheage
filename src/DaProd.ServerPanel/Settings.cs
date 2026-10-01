using System.ComponentModel;
using System.Security.Cryptography;
using System.Text.Json;

namespace DaProd.ServerPanel;

/// <summary>Impostazioni del pannello, salvate in data\panel.json accanto all'exe.</summary>
public sealed class PanelSettings
{
    const string Rete = "1. Rete e accesso", Server = "2. Server", Mondo = "3. Mondo", Sicurezza = "4. Sicurezza e backup", Avanzate = "5. Avanzate";

    // ---- Rete ----
    [Category(Rete), Description("Nome mostrato nel launcher e nell'elenco server del gioco.")]
    public string ServerName { get; set; } = "DaProd ArcheAge";
    [Category(Rete), Description("Rete privata attiva: gli amici entrano nella tua rete senza installare niente e il tuo IP si imposta da solo.")]
    public bool UseTailscale { get; set; } = true;
    [Category(Rete), TypeConverter(typeof(VpnEngineConverter)), Description("Motore della rete privata: NetBird (chiave senza scadenza) oppure Tailscale.")]
    public string VpnEngine { get; set; } = "";
    [Category(Rete), Description("IP che i giocatori usano per raggiungerti. Con la rete privata si imposta da solo.")]
    public string PublicIp { get; set; } = "127.0.0.1";
    [Category(Rete), Description("NetBird: chiave setup (Reusable, senza scadenza) da app.netbird.io > Setup Keys. Finisce dentro il launcher degli amici.")]
    public string NetBirdSetupKey { get; set; } = "";
    [Category(Rete), Description("Tailscale: chiave (Reusable) da login.tailscale.com/admin/settings/keys. Dura al massimo 90 giorni.")]
    public string TailscaleAuthKey { get; set; } = "";
    [Category(Rete)] public int LoginPort { get; set; } = 1237;
    [Category(Rete)] public int GamePort { get; set; } = 1239;
    [Category(Rete), Description("Porta usata dal launcher per stato, download e registrazione.")]
    public int LauncherApiPort { get; set; } = 8080;

    // ---- Server ----
    [Category(Server), Description("Avvia da solo il server all'apertura del pannello.")]
    public bool AutoStart { get; set; } = true;
    [Category(Server), Description("Riavvia da solo i servizi (Login, World, zone) se si fermano.")]
    public bool Watchdog { get; set; } = true;
    [Category(Server), Description("Cartella del gioco condivisa con i launcher.")]
    public string ClientDir { get; set; } = Path.Combine(AppContext.BaseDirectory, "gioco");
    [Category(Server), Description("Zone da caricare, separate da virgola. Formato nome oppure nome:istanza. Ogni zona usa circa 1 GB di RAM.")]
    public string Zones { get; set; } = ZoneCatalog.AllWorld;
    [Category(Server), Description("Zone dinamiche: quelle non 'sempre attive' si caricano quando un giocatore ci entra e si scaricano dopo un po' che sono vuote (risparmia RAM e CPU).")]
    public bool DynamicZones { get; set; } = true;
    [Category(Server), Description("Zone sempre caricate (separate da virgola). Le altre sono dinamiche.")]
    public string AlwaysOnZones { get; set; } = "w_solzreed_1,w_solzreed_2,w_solzreed_3,w_gweonid_forest_1,w_gweonid_forest_2,w_gweonid_forest_3,w_marianople_1,w_marianople_2";
    [Category(Server), Description("Minuti di zona vuota prima di scaricarla.")]
    public int ZoneIdleMinutes { get; set; } = 10;
    [Category(Server)] public string MySqlBinDir { get; set; } = "";
    [Category(Server)] public int MySqlPort { get; set; } = 3306;

    // ---- Mondo ----
    [Category(Mondo), Description("Testo mostrato nella sezione Notizie del launcher.")]
    public string News { get; set; } = "Benvenuti sul server DaProd ArcheAge!";
    [Category(Mondo), Description("Permette ai giocatori di creare l'account dal launcher.")]
    public bool AllowRegistration { get; set; } = true;
    [Category(Mondo), Description("Manutenzione: Login e World spenti, il launcher mostra il messaggio.")]
    public bool Maintenance { get; set; }
    [Category(Mondo)] public string MaintenanceMessage { get; set; } = "Server in manutenzione, torniamo presto!";

    // ---- Sicurezza e backup ----
    [Category(Sicurezza), Description("Ogni quante ore fare un backup automatico dei database (0 = mai).")]
    public int BackupEveryHours { get; set; } = 6;
    [Category(Sicurezza), Description("Quanti backup tenere.")]
    public int KeepBackups { get; set; } = 12;
    [Category(Sicurezza), Description("Segreto con cui il pannello firma gli accessi al gioco. Non condividerlo.")]
    public string TokenSecret { get; set; } = "";

    // ---- Avanzate ----
    [Category(Avanzate), Description("Parametri con cui il launcher avvia il client (modalità launcher del gioco).")]
    public string LaunchArgs { get; set; } = "-StrUserName {user} -strUserToken {token} -serverId 1 -sIp {ip} -sPort {port} -gameId 1";
    [Category(Avanzate), Description("owner/nome della repository GitHub per gli aggiornamenti del pannello.")]
    public string GitHubRepo { get; set; } = "cammo22/DaProdArcheage";
    [Category(Avanzate), Description("Token GitHub di sola lettura (serve se la repo è privata).")]
    public string GitHubToken { get; set; } = "";
    [Browsable(false)] public bool AutoAccount { get; set; }
    [Browsable(false)] public bool Tuned { get; set; }
    /// <summary>PC con poca RAM (meno di 24 GB): poche zone sempre accese, scarico più rapido, caricamenti uno o due alla volta.</summary>
    public static bool LowRam => GC.GetGCMemoryInfo().TotalAvailableMemoryBytes < 24L * 1024 * 1024 * 1024;
    [Browsable(false)] public string GamePakDir { get; set; } = "";
    [Browsable(false)] public string ClientDownloadUrl { get; set; } = "";

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    public static string Root => AppContext.BaseDirectory;
    public static string DataDir => Path.Combine(Root, "data");
    static string FilePath => Path.Combine(DataDir, "panel.json");
    public const string TailscaleExe = @"C:\Program Files\Tailscale\tailscale.exe";
    public const string NetBirdExe = @"C:\Program Files\NetBird\netbird.exe";
    public bool IsNetBird => VpnEngine.Equals("NetBird", StringComparison.OrdinalIgnoreCase);
    /// <summary>Chiave di rete del motore scelto (vuota se la rete privata è spenta).</summary>
    public string VpnKey => !UseTailscale ? "" : IsNetBird ? NetBirdSetupKey : TailscaleAuthKey;

    public static PanelSettings Load()
    {
        PanelSettings s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { s = new(); }
        // migrazioni da versioni precedenti
        if (s.LaunchArgs.Contains("{pwhash}") || s.LaunchArgs.Contains("auth_serveraddr")) s.LaunchArgs = new PanelSettings().LaunchArgs;
        // tutte le zone del mondo (mare, isole, zone chiuse come Diamond Shores...): sono dinamiche, quindi non pesano finché nessuno ci va
        if (string.IsNullOrWhiteSpace(s.Zones) || !s.Zones.Contains("o_shining_shore_1")) s.Zones = ZoneCatalog.AllWorld;
        if (!s.Tuned)
        {
            // primo avvio su questo PC: profilo in base alla RAM (si può cambiare a mano in Impostazioni)
            s.Tuned = true;
            if (LowRam) { s.AlwaysOnZones = "w_solzreed_1,w_solzreed_2,w_gweonid_forest_1"; s.ZoneIdleMinutes = 4; }
        }
        if (string.IsNullOrEmpty(s.VpnEngine)) s.VpnEngine = string.IsNullOrEmpty(s.TailscaleAuthKey) ? "NetBird" : "Tailscale";
        if (string.IsNullOrEmpty(s.TokenSecret)) s.TokenSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        return s;
    }

    /// <summary>Cartella dei binari MySQL: quella inclusa nel pacchetto dati, altrimenti quella di sistema.</summary>
    public string ResolveMySqlBin()
    {
        foreach (var d in new[] { MySqlBinDir, Path.Combine(Root, "server", "mysql", "bin"), @"C:\Program Files\MySQL\MySQL Server 8.4\bin", @"C:\Program Files\MySQL\MySQL Server 8.0\bin" })
            if (!string.IsNullOrWhiteSpace(d) && File.Exists(Path.Combine(d, "mysqld.exe"))) return d;
        return Path.Combine(Root, "server", "mysql", "bin");
    }

    /// <summary>Riempie in automatico IP Tailscale.</summary>
    public void AutoDetect()
    {
        if (UseTailscale)
        {
            try
            {
                string? ip = null;
                if (IsNetBird && File.Exists(NetBirdExe))
                {
                    // "NetBird IP: 100.92.1.5/16"
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(NetBirdExe, "status")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
                    var txt = p.StandardOutput.ReadToEnd(); p.WaitForExit(8000);
                    var m = System.Text.RegularExpressions.Regex.Match(txt, @"NetBird IP:\s*(\d+\.\d+\.\d+\.\d+)");
                    if (m.Success) ip = m.Groups[1].Value;
                }
                else if (!IsNetBird && File.Exists(TailscaleExe))
                {
                    var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TailscaleExe, "ip -4")
                    { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
                    ip = p.StandardOutput.ReadLine()?.Trim();
                    p.WaitForExit(5000);
                }
                if (!string.IsNullOrEmpty(ip)) PublicIp = ip;
            }
            catch { }
        }
        Save();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
    }
}

/// <summary>Menu a tendina per la scelta del motore di rete.</summary>
public sealed class VpnEngineConverter : StringConverter
{
    public override bool GetStandardValuesSupported(ITypeDescriptorContext? c) => true;
    public override bool GetStandardValuesExclusive(ITypeDescriptorContext? c) => true;
    public override StandardValuesCollection GetStandardValues(ITypeDescriptorContext? c) => new(new[] { "NetBird", "Tailscale" });
}

/// <summary>Elenco delle zone del mondo (nome mappa ↔ id), da Resources\zone_map_names.txt.</summary>
public static class ZoneCatalog
{
    /// <summary>Tutte le zone del mondo aperto (colonna 5 di zone_map_names.txt) più l'Arche Mall.</summary>
    public static string AllWorld
    {
        get
        {
            using var st = typeof(ZoneCatalog).Assembly.GetManifestResourceStream("zone_map_names.txt");
            if (st == null) return "w_solzreed_1,w_solzreed_2,w_solzreed_3,arche_mall:1";
            using var rd = new StreamReader(st);
            var names = rd.ReadToEnd().Split('\n').Select(x => x.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != '#')
                .Select(l => l.Split('\t')).Where(p => p.Length >= 5 && p[4] == "1").Select(p => p[1]).ToList();
            names.Add("arche_mall:1");
            return string.Join(",", names);
        }
    }

    static Dictionary<string, uint>? _ids;
    static readonly Dictionary<string, int> Groups = new(StringComparer.OrdinalIgnoreCase);

    static Dictionary<int, int[]>? _neighbours;

    /// <summary>Regioni confinanti (rettangoli di zone_groups che si toccano): si precaricano prima che il giocatore ci arrivi.</summary>
    public static int[] NeighboursOf(int group)
    {
        if (_neighbours == null)
        {
            var d = new Dictionary<int, int[]>();
            using var st = typeof(ZoneCatalog).Assembly.GetManifestResourceStream("zone_neighbors.txt");
            if (st != null)
            {
                using var rd = new StreamReader(st);
                foreach (var l in rd.ReadToEnd().Split('\n').Select(x => x.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != '#'))
                {
                    var p = l.Split('\t');
                    if (p.Length == 2 && int.TryParse(p[0], out var g))
                        d[g] = p[1].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
                }
            }
            _neighbours = d;
        }
        return _neighbours.GetValueOrDefault(group) ?? [];
    }

    /// <summary>Gruppo di regione (es. le 3 parti di Solzreed): si caricano insieme.</summary>
    public static int GroupOf(string name) { IdOf(name); return Groups.GetValueOrDefault(name); }

    /// <summary>Nome della zona dal suo id (null se sconosciuta).</summary>
    public static string? NameOf(uint id)
    {
        IdOf("");
        return _ids!.Where(kv => kv.Value == id).Select(kv => kv.Key).FirstOrDefault();
    }

    public static uint IdOf(string name)
    {
        if (_ids == null)
        {
            _ids = new(StringComparer.OrdinalIgnoreCase);
            using var st = typeof(ZoneCatalog).Assembly.GetManifestResourceStream("zone_map_names.txt");
            if (st != null)
            {
                using var rd = new StreamReader(st);
                foreach (var l in rd.ReadToEnd().Split('\n').Select(x => x.TrimEnd('\r')).Where(l => l.Length > 0 && l[0] != '#'))
                {
                    var p = l.Split('\t');
                    if (p.Length >= 2 && uint.TryParse(p[0], out var id)) _ids[p[1].Trim()] = id;
                    if (p.Length >= 4 && int.TryParse(p[3], out var g)) Groups[p[1].Trim()] = g;
                }
            }
        }
        return _ids.GetValueOrDefault(name);
    }
}
