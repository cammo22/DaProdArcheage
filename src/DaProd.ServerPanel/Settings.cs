using System.Text.Json;

namespace DaProd.ServerPanel;

/// <summary>Impostazioni del pannello, salvate in data\panel.json accanto all'exe.</summary>
public sealed class PanelSettings
{
    public string ServerName { get; set; } = "DaProd ArcheAge";
    public string PublicIp { get; set; } = "127.0.0.1";
    public string MySqlBinDir { get; set; } = @"C:\Program Files\MySQL\MySQL Server 8.4\bin";
    public int MySqlPort { get; set; } = 3306;
    public string GamePakDir { get; set; } = "";
    /// <summary>Cartella del client estratto: viene servita ai launcher e usata per trovare game_pak.</summary>
    public string ClientDir { get; set; } = @"Z:\Archeage\Client";
    /// <summary>Se attivo, PublicIp viene preso automaticamente da "tailscale ip -4".</summary>
    public bool UseTailscale { get; set; } = true;
    /// <summary>Auth key Tailscale (riutilizzabile, ephemeral) inclusa nel pacchetto per gli amici.</summary>
    public string TailscaleAuthKey { get; set; } = "";
    public int LoginPort { get; set; } = 1237;
    public int GamePort { get; set; } = 1239;
    public int LauncherApiPort { get; set; } = 8080;
    public bool AutoAccount { get; set; } = false;
    /// <summary>Permette agli utenti di creare l'account dal launcher.</summary>
    public bool AllowRegistration { get; set; } = true;
    /// <summary>All'apertura del pannello: setup (se serve) e avvio automatico del server.</summary>
    public bool AutoStart { get; set; } = true;
    public string ClientDownloadUrl { get; set; } = "";
    public string LaunchArgs { get; set; } = "-t +auth_ip {ip} -auth_port {port} -uid {user} -token {token} -lang en_us";
    public string News { get; set; } = "Benvenuti sul server DaProd ArcheAge!";

    static readonly JsonSerializerOptions Opts = new() { WriteIndented = true };
    public static string Root => AppContext.BaseDirectory;
    public static string DataDir => Path.Combine(Root, "data");
    static string FilePath => Path.Combine(DataDir, "panel.json");

    public static PanelSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<PanelSettings>(File.ReadAllText(FilePath)) ?? new(); }
        catch { }
        return new();
    }

    public const string TailscaleExe = @"C:\Program Files\Tailscale\tailscale.exe";

    /// <summary>Riempie in automatico IP Tailscale e cartella game_pak.</summary>
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
        if (string.IsNullOrWhiteSpace(GamePakDir) && Directory.Exists(ClientDir))
        {
            var pak = Directory.EnumerateFileSystemEntries(ClientDir, "game_pak", SearchOption.AllDirectories).FirstOrDefault();
            if (pak != null) GamePakDir = pak;
        }
        Save();
    }

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
    }
}
