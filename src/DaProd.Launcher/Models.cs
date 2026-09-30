using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DaProd.Launcher;

public sealed record LiveInfo(bool online, bool ready, string? status, int players, int zonesLoaded, int zonesTotal, string? time);
public sealed record EventInfo(string name, string at);
public sealed record ServerInfo(string name, string ip, int port, int gamePort, int streamPort, string news, string launchArgs, LiveInfo? live,
    List<EventInfo>? events, bool maintenance, string? maintenanceMessage, bool registration, string? launcherVersion);
public sealed record ManifestFile(string path, long size, long time);
public sealed record AuthReply(bool ok, string? message, string? token);

/// <summary>Impostazioni del launcher (in %AppData%\DaProdLauncher). Se accanto all'exe c'è server.json, ha la precedenza.</summary>
public sealed class LauncherSettings
{
    public string ServerUrl { get; set; } = "";
    /// <summary>Motore della rete privata ("netbird" o "tailscale") e sua chiave.</summary>
    public string Vpn { get; set; } = "tailscale";
    public string VpnKey { get; set; } = "";
    public string GameDir { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaProdArcheage", "Game");
    public string Username { get; set; } = "";
    public bool Remember { get; set; }
    /// <summary>Password cifrata con DPAPI: si legge solo con questo utente Windows su questo PC.</summary>
    public string SavedPassword { get; set; } = "";

    static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DaProdLauncher");
    static string FilePath => Path.Combine(Dir, "launcher.json");
    static string InstalledPath => Path.Combine(Dir, "installed.json");

    public static LauncherSettings Load()
    {
        LauncherSettings s;
        try { s = JsonSerializer.Deserialize<LauncherSettings>(File.ReadAllText(FilePath)) ?? new(); } catch { s = new(); }
        // exe personalizzato dal pannello: indirizzo e chiave sono dentro il file
        if (Trailer.ReadConfig() is { } cfg) { s.ServerUrl = cfg.api; s.Vpn = cfg.vpn; s.VpnKey = cfg.key; }
        else try
        {
            var pkg = Path.Combine(AppContext.BaseDirectory, "server.json");
            if (File.Exists(pkg))
            {
                using var d = JsonDocument.Parse(File.ReadAllText(pkg));
                if (d.RootElement.TryGetProperty("api", out var a)) s.ServerUrl = a.GetString() ?? s.ServerUrl;
                if (d.RootElement.TryGetProperty("tailscaleAuthKey", out var k)) { s.VpnKey = k.GetString() ?? ""; s.Vpn = "tailscale"; }
            }
        }
        catch { }
        if (string.IsNullOrWhiteSpace(s.ServerUrl)) s.ServerUrl = "http://127.0.0.1:8080";
        return s;
    }

    public void Save() { Directory.CreateDirectory(Dir); File.WriteAllText(FilePath, JsonSerializer.Serialize(this)); }

    public string GetPassword()
    {
        try { return SavedPassword == "" ? "" : Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(SavedPassword), null, DataProtectionScope.CurrentUser)); }
        catch { return ""; }
    }

    public void SetPassword(string pwd) =>
        SavedPassword = pwd == "" ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(pwd), null, DataProtectionScope.CurrentUser));

    /// <summary>File installati (percorso → dimensione e data sul server): serve a scaricare solo le patch.</summary>
    public static Dictionary<string, ManifestFile> LoadInstalled()
    {
        try { return JsonSerializer.Deserialize<Dictionary<string, ManifestFile>>(File.ReadAllText(InstalledPath)) ?? []; } catch { return []; }
    }

    public static void SaveInstalled(Dictionary<string, ManifestFile> m) { Directory.CreateDirectory(Dir); File.WriteAllText(InstalledPath, JsonSerializer.Serialize(m)); }
}
