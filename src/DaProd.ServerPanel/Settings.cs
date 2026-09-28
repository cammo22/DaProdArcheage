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
    public int LoginPort { get; set; } = 1237;
    public int GamePort { get; set; } = 1239;
    public int LauncherApiPort { get; set; } = 8080;
    public bool AutoAccount { get; set; } = true;
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

    public void Save()
    {
        Directory.CreateDirectory(DataDir);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Opts));
    }
}
