using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace DaProd.ServerPanel;

/// <summary>
/// Aggiornamento del pannello dall'ultima release GitHub.
/// La release contiene DaProdServer.zip (pannello + server compilati, senza dati del gioco).
/// </summary>
public static class Updater
{
    public static Version Current => Assembly.GetExecutingAssembly().GetName().Version ?? new(0, 0);

    static HttpClient Client(PanelSettings s)
    {
        var h = new HttpClient();
        h.DefaultRequestHeaders.UserAgent.ParseAdd("DaProdArcheage");
        if (!string.IsNullOrWhiteSpace(s.GitHubToken)) h.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", s.GitHubToken);
        return h;
    }

    /// <summary>Ritorna (versione, url API dell'asset) se c'è una release più nuova, altrimenti null.</summary>
    public static async Task<(Version ver, string assetApiUrl)?> CheckAsync(PanelSettings s)
    {
        using var h = Client(s);
        var resp = await h.GetAsync($"https://api.github.com/repos/{s.GitHubRepo}/releases/latest");
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            throw new Exception("Nessuna release trovata. Se la repo è privata imposta GitHubToken nelle Impostazioni.");
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var tag = doc.RootElement.GetProperty("tag_name").GetString()!.TrimStart('v', 'V');
        if (!Version.TryParse(tag, out var ver) || ver <= Current) return null;
        foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            if (a.GetProperty("name").GetString() == "DaProdServer.zip")
                return (ver, a.GetProperty("url").GetString()!);
        throw new Exception($"La release {tag} non contiene DaProdServer.zip.");
    }

    /// <summary>Scarica, estrae in una cartella temporanea e sostituisce i file dopo la chiusura del pannello.</summary>
    public static async Task InstallAsync(PanelSettings s, string assetApiUrl, Action<string> status)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "DaProdUpdate");
        if (Directory.Exists(tmp)) Directory.Delete(tmp, true);
        Directory.CreateDirectory(tmp);
        var zip = Path.Combine(tmp, "update.zip");
        using (var h = Client(s))
        {
            h.DefaultRequestHeaders.Accept.ParseAdd("application/octet-stream");
            status("Scarico l'aggiornamento...");
            await File.WriteAllBytesAsync(zip, await h.GetByteArrayAsync(assetApiUrl));
        }
        status("Estraggo...");
        var files = Path.Combine(tmp, "files");
        await Task.Run(() => ZipFile.ExtractToDirectory(zip, files));

        // robocopy dopo che il pannello si è chiuso; data\, gioco\ e i database non vengono toccati
        var root = PanelSettings.Root.TrimEnd('\\');
        var script = Path.Combine(tmp, "apply.cmd");
        File.WriteAllText(script,
            "@echo off\r\n" +
            $"timeout /t 3 /nobreak >nul\r\n" +
            $"robocopy \"{files}\" \"{root}\" /E /XD data gioco mysql-data logs /XF compact.sqlite3 game_decrypted.sqlite3 Config.Local.json >nul\r\n" +
            $"start \"\" \"{Path.Combine(root, "DaProdServer.exe")}\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
    }
}

/// <summary>
/// Pacchetto dati: tutto ciò che NON è su GitHub (client del gioco, database del mondo, Zone Host).
/// È l'unica cosa che il pannello chiede alla prima apertura su un PC nuovo.
/// </summary>
public static class GameDataPack
{
    public const string FileName = "DaProdGameData.zip";

    static readonly (string zipPath, Func<PanelSettings, string> local)[] Items =
    [
        ("server/zonehost/AAEmu.ZoneHost.exe", _ => Path.Combine(PanelSettings.Root, "server", "zonehost", "AAEmu.ZoneHost.exe")),
        ("server/zonehost/x2game-dev_dedicate.dll", _ => Path.Combine(PanelSettings.Root, "server", "zonehost", "x2game-dev_dedicate.dll")),
        ("server/zonehost/game_decrypted.sqlite3", _ => Path.Combine(PanelSettings.Root, "server", "zonehost", "game_decrypted.sqlite3")),
    ];

    /// <summary>Mancano i dati del gioco su questo PC?</summary>
    public static bool Missing(PanelSettings s) =>
        !File.Exists(Path.Combine(s.ClientDir, "Bin64", "archeage.exe")) || Items.Any(i => !File.Exists(i.local(s)));

    public static async Task CreateAsync(PanelSettings s, Action<string> status)
    {
        var zip = Path.Combine(PanelSettings.Root, FileName);
        if (File.Exists(zip)) File.Delete(zip);
        await Task.Run(() =>
        {
            using var z = ZipFile.Open(zip, ZipArchiveMode.Create);
            foreach (var (zp, local) in Items)
            {
                status("Aggiungo " + zp);
                z.CreateEntryFromFile(local(s), zp, CompressionLevel.NoCompression);
            }
            var files = Directory.GetFiles(s.ClientDir, "*", SearchOption.AllDirectories);
            for (var i = 0; i < files.Length; i++)
            {
                var rel = Path.GetRelativePath(s.ClientDir, files[i]).Replace('\\', '/');
                // file del server di zona copiati nel client: si rigenerano da soli
                if (rel is "Bin64/AAEmu.ZoneHost.exe" or "Bin64/x2game-dev_dedicate.dll" || rel.StartsWith("game/db/")) continue;
                status($"Gioco {i + 1}/{files.Length}: {rel}");
                // nessuna compressione: il client è già compresso, così è molto più veloce
                z.CreateEntryFromFile(files[i], "gioco/" + rel, CompressionLevel.NoCompression);
            }
        });
        status("Pacchetto dati creato: " + zip);
        Process.Start("explorer.exe", $"/select,\"{zip}\"");
    }

    public static async Task InstallAsync(PanelSettings s, string zip, Action<string> status)
    {
        status("Estraggo i dati del gioco (può richiedere diversi minuti)...");
        await Task.Run(() =>
        {
            using var z = ZipFile.OpenRead(zip);
            var n = 0;
            foreach (var e in z.Entries)
            {
                if (string.IsNullOrEmpty(e.Name)) continue;
                var dest = e.FullName.StartsWith("gioco/")
                    ? Path.Combine(s.ClientDir, e.FullName["gioco/".Length..])
                    : Path.Combine(PanelSettings.Root, e.FullName);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                e.ExtractToFile(dest, true);
                if (++n % 20 == 0) status($"Estratti {n}/{z.Entries.Count} file...");
            }
        });
        // il database del mondo serve anche a Game e World come compact.sqlite3
        var db = Path.Combine(PanelSettings.Root, "server", "zonehost", "game_decrypted.sqlite3");
        foreach (var d in new[] { "game", "world" })
        {
            var dst = Path.Combine(PanelSettings.Root, "server", "bin", d, "Data", "compact.sqlite3");
            Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
            File.Copy(db, dst, true);
        }
        s.GamePakDir = ""; s.AutoDetect();
        status("Dati del gioco installati.");
    }
}
