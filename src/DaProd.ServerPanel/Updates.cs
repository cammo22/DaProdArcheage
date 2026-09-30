using System.Diagnostics;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DaProd.ServerPanel;

/// <summary>Aggiornamento del pannello dall'ultima release GitHub (DaProdServer.zip: pannello + server, senza dati del gioco).</summary>
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
            if (a.GetProperty("name").GetString() == "DaProdServer.zip") return (ver, a.GetProperty("url").GetString()!);
        throw new Exception($"La release {tag} non contiene DaProdServer.zip.");
    }

    /// <summary>Scarica, estrae e sostituisce i file dopo la chiusura del pannello. Dati, gioco e database non vengono toccati.</summary>
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
        await Task.Run(() => System.IO.Compression.ZipFile.ExtractToDirectory(zip, files));
        var root = PanelSettings.Root.TrimEnd('\\');
        var script = Path.Combine(tmp, "apply.cmd");
        File.WriteAllText(script,
            "@echo off\r\ntimeout /t 3 /nobreak >nul\r\n" +
            $"robocopy \"{files}\" \"{root}\" /E /XD data gioco zoneclient mysql-data mysql logs backups /XF Config.Local.json >nul\r\n" +
            $"start \"\" \"{Path.Combine(root, "DaProdServer.exe")}\"\r\n");
        Process.Start(new ProcessStartInfo("cmd.exe", $"/c \"{script}\"") { WindowStyle = ProcessWindowStyle.Hidden, CreateNoWindow = true });
    }
}

/// <summary>
/// Pacchetto dati (DaProdGameData.7z): tutto ciò che NON sta su GitHub, cioè il client del gioco, il database del mondo,
/// lo Zone Host e MySQL portatile. È l'unica cosa che il pannello chiede su un PC nuovo.
/// </summary>
public static class GameDataPack
{
    public const string FileName = "DaProdGameData.7z";
    static string Root => PanelSettings.Root;
    public static string SevenZip => Path.Combine(Root, "server", "tools", "7z", "7z.exe");

    public static bool Missing(PanelSettings s) =>
        !File.Exists(Path.Combine(s.ClientDir, "game_pak")) || !File.Exists(Path.Combine(s.ClientDir, "Bin64", "archeage.exe")) ||
        !File.Exists(Path.Combine(Root, "server", "zonehost", "game_decrypted.sqlite3"));

    /// <summary>Cerca il pacchetto accanto al pannello, sul Desktop, in Download e Documenti.</summary>
    public static string? Find()
    {
        var dirs = new[] { Root, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
        return dirs.Select(d => Path.Combine(d, FileName)).FirstOrDefault(File.Exists);
    }

    /// <summary>Copia MySQL (bin, share, plugin) in server\mysql: così il pacchetto funziona su PC senza MySQL installato.</summary>
    static void EnsureBundledMySql(PanelSettings s)
    {
        var dst = Path.Combine(Root, "server", "mysql");
        if (File.Exists(Path.Combine(dst, "bin", "mysqld.exe"))) return;
        var bin = s.ResolveMySqlBin(); var baseDir = Path.GetDirectoryName(bin)!;
        if (!File.Exists(Path.Combine(bin, "mysqld.exe"))) throw new Exception("MySQL non trovato: installa MySQL Server 8.4 e riprova.");
        Directory.CreateDirectory(Path.Combine(dst, "bin"));
        foreach (var f in Directory.GetFiles(bin))
        {
            var n = Path.GetFileName(f).ToLowerInvariant();
            if (n.EndsWith(".dll") || n is "mysqld.exe" or "mysql.exe" or "mysqladmin.exe" or "mysqldump.exe") File.Copy(f, Path.Combine(dst, "bin", Path.GetFileName(f)), true);
        }
        foreach (var (from, to) in new[] { (Path.Combine(baseDir, "share"), Path.Combine(dst, "share")), (Path.Combine(baseDir, "lib", "plugin"), Path.Combine(dst, "lib", "plugin")) })
            if (Directory.Exists(from))
                foreach (var f in Directory.GetFiles(from, "*", SearchOption.AllDirectories))
                {
                    var d = Path.Combine(to, Path.GetRelativePath(from, f));
                    Directory.CreateDirectory(Path.GetDirectoryName(d)!);
                    File.Copy(f, d, true);
                }
    }

    static async Task Run7z(string args, string cwd, Action<int> pct)
    {
        if (!File.Exists(SevenZip)) throw new Exception("7-Zip non trovato in server\\tools\\7z.");
        var p = Process.Start(new ProcessStartInfo(SevenZip, args)
        { WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        var err = p.StandardError.ReadToEndAsync();
        var sb = new System.Text.StringBuilder();
        var buf = new char[512]; int n;
        while ((n = await p.StandardOutput.ReadAsync(buf, 0, buf.Length)) > 0)
        {
            sb.Append(buf, 0, n);
            var m = Regex.Matches(sb.ToString(), @"(\d{1,3})%");
            if (m.Count > 0) pct(int.Parse(m[^1].Groups[1].Value));
            if (sb.Length > 2000) sb.Remove(0, sb.Length - 200);
        }
        await p.WaitForExitAsync();
        if (p.ExitCode > 1) throw new Exception("7-Zip: " + (await err));
    }

    /// <summary>Crea il pacchetto dati (compressione LZMA2: il gioco passa da 68 GB a circa 20-25 GB).</summary>
    public static async Task<string> CreateAsync(PanelSettings s, string destDir, Action<string> status)
    {
        status("Preparo MySQL portatile...");
        await Task.Run(() => EnsureBundledMySql(s));
        var dest = Path.Combine(destDir, FileName);
        if (File.Exists(dest)) File.Delete(dest);
        await Run7z($"a -t7z -mx=3 -mmt=on -bsp1 -xr!*.part -x!gioco\\game -x!gioco\\*.log \"{dest}\" gioco server\\zonehost server\\mysql", Root, p => status($"Creo il pacchetto dati... {p}%"));
        status("Pacchetto dati creato.");
        return dest;
    }

    public static async Task InstallAsync(string pack, Action<string> status)
    {
        await Run7z($"x -y -bsp1 \"-o{Root}\" \"{pack}\"", Root, p => status($"Estraggo i dati del gioco... {p}%"));
        status("Dati del gioco installati.");
    }
}
