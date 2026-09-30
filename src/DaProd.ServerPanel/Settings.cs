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
    [Category(Rete), Description("Se attivo, l'IP viene preso da solo da Tailscale (gli amici entrano nella tua rete privata).")]
    public bool UseTailscale { get; set; } = true;
    [Category(Rete), Description("IP che i giocatori usano per raggiungerti. Con Tailscale si imposta da solo.")]
    public string PublicIp { get; set; } = "127.0.0.1";
    [Category(Rete), Description("Chiave Tailscale (Reusable + Ephemeral) da login.tailscale.com/admin/settings/keys: finisce nel Pacchetto amici.")]
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
    public string Zones { get; set; } = ZoneCatalog.AllOpenWorld;
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
    [Browsable(false)] public string GamePakDir { get; set; } = "";
    [Browsable(false)] public string ClientDownloadUrl { get; set; } = "";

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    public static string Root => AppContext.BaseDirectory;
    public static string DataDir => Path.Combine(Root, "data");
    static string FilePath => Path.Combine(DataDir, "panel.json");
    public const string TailscaleExe = @"C:\Program Files\Tailscale\tailscale.exe";

    public static PanelSettings Load()
    {
        PanelSettings s;
        try { s = File.Exists(FilePath) ? JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(FilePath)) ?? new() : new(); }
        catch { s = new(); }
        // migrazioni da versioni precedenti
        if (s.LaunchArgs.Contains("{pwhash}") || s.LaunchArgs.Contains("auth_serveraddr")) s.LaunchArgs = new PanelSettings().LaunchArgs;
        if (string.IsNullOrWhiteSpace(s.Zones) || s.Zones.Split(',').Length < 5) s.Zones = ZoneCatalog.AllOpenWorld;
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
        if (UseTailscale && File.Exists(TailscaleExe))
        {
            try
            {
                var p = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(TailscaleExe, "ip -4")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
                var ip = p.StandardOutput.ReadLine()?.Trim();
                p.WaitForExit(5000);
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

/// <summary>Elenco delle zone del mondo (nome mappa ↔ id), da data\zone_map_names.txt.</summary>
public static class ZoneCatalog
{
    public const string AllOpenWorld =
        "w_gweonid_forest_1,w_solzreed_3,w_marianople_1,w_garangdol_plains_1,w_solzreed_1,w_white_forest_1,w_lilyut_meadow_1,w_the_carcass_1,w_cross_plains_1," +
        "w_two_crowns_1,w_cradle_of_genesis_1,w_golden_plains_1,w_bronze_rock_1,w_hell_swamp_1,w_long_sand_1,w_solzreed_2,w_gweonid_forest_2,w_gweonid_forest_3," +
        "w_marianople_2,w_garangdol_plains_2,w_two_crowns_2,w_bronze_rock_2,w_bronze_rock_3,w_lilyut_meadow_2,w_golden_plains_2,w_golden_plains_3,w_white_forest_2," +
        "w_long_sand_2,w_hell_swamp_2,w_cross_plains_2,w_mirror_kingdom_1,w_hanuimaru_1,w_the_carcass_2,w_cradle_of_genesis_2,w_hanuimaru_2,w_hanuimaru_3,arche_mall:1";

    static Dictionary<string, uint>? _ids;
    static readonly Dictionary<string, int> Groups = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gruppo di regione (es. le 3 parti di Solzreed): si caricano insieme.</summary>
    public static int GroupOf(string name) { IdOf(name); return Groups.GetValueOrDefault(name); }

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
                    if (p.Length >= 2 && uint.TryParse(p[0], out var id)) _ids[p[1]] = id;
                    if (p.Length >= 4 && int.TryParse(p[3], out var g)) Groups[p[1]] = g;
                }
            }
        }
        return _ids.GetValueOrDefault(name);
    }
}
