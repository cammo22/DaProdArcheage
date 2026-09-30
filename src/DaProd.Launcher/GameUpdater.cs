using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace DaProd.Launcher;

/// <summary>Controlla e scarica il gioco: solo i file cambiati, con ripresa dei download interrotti e più file piccoli in parallelo.</summary>
public sealed class GameUpdater(Func<HttpClient> http, Func<string, string> api, string gameDir)
{
    public sealed record Plan(List<ManifestFile> Todo, long Bytes, bool FirstInstall, int TotalFiles);

    public long Done, Total;
    public string Current = "";
    const long BigFile = 256L << 20;

    /// <summary>Confronta il manifest del server con i file installati. In modalità "ripara" riscarica anche i file piccoli.</summary>
    public async Task<Plan> CheckAsync(bool repair = false)
    {
        var files = await http().GetFromJsonAsync<List<ManifestFile>>(api("/manifest.json")) ?? [];
        var installed = LauncherSettings.LoadInstalled();
        var todo = new List<ManifestFile>();
        foreach (var f in files)
        {
            var fi = new FileInfo(Path.Combine(gameDir, f.path.Replace('/', Path.DirectorySeparatorChar)));
            var ok = fi.Exists && fi.Length == f.size;
            if (ok && !installed.ContainsKey(f.path)) { installed[f.path] = f; continue; }   // già presente e identico
            if (ok && installed[f.path].time == f.time && !(repair && f.size <= (1L << 30))) continue;
            todo.Add(f);
        }
        LauncherSettings.SaveInstalled(installed);
        return new Plan(todo, todo.Sum(t => t.size), installed.Count == 0 || files.Count == todo.Count, files.Count);
    }

    public async Task DownloadAsync(Plan plan, CancellationToken ct)
    {
        Done = 0; Total = plan.Bytes;
        var installed = LauncherSettings.LoadInstalled();
        var saved = 0;
        void Record(ManifestFile f) { lock (installed) { installed[f.path] = f; if (++saved % 20 == 0) LauncherSettings.SaveInstalled(installed); } }

        // file grandi uno alla volta; piccoli in parallelo
        foreach (var f in plan.Todo.Where(t => t.size >= BigFile)) { await One(f, ct); Record(f); }
        await Parallel.ForEachAsync(plan.Todo.Where(t => t.size < BigFile), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (f, c) => { await One(f, c); Record(f); });
        lock (installed) LauncherSettings.SaveInstalled(installed);
    }

    async Task One(ManifestFile f, CancellationToken ct)
    {
        var dest = Path.Combine(gameDir, f.path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        var part = $"{dest}.{f.time}.part";      // il nome contiene la versione: un file cambiato non riprende da dati vecchi
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (have > f.size) { File.Delete(part); have = 0; }
        if (have == f.size) { File.Move(part, dest, true); Interlocked.Add(ref Done, f.size); return; }

        var url = api("/files/" + string.Join('/', f.path.Split('/').Select(Uri.EscapeDataString)));
        for (var attempt = 0; ; attempt++)
        {
            long counted = 0;
            try
            {
                have = File.Exists(part) ? new FileInfo(part).Length : 0;
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                if (have > 0) req.Headers.Range = new RangeHeaderValue(have, null);
                using var resp = await http().SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.OK && have > 0) have = 0;   // il server ha ignorato Range: ricomincio
                else resp.EnsureSuccessStatusCode();
                Current = f.path;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, true);
                if (have > 0) { Interlocked.Add(ref Done, have); counted += have; }
                var buf = new byte[1 << 20]; int n;
                while ((n = await src.ReadAsync(buf, ct)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n), ct); Interlocked.Add(ref Done, n); counted += n; }
                break;
            }
            catch (OperationCanceledException) { throw; }
            catch when (attempt < 4)   // rete instabile: attendo e riprendo da dove ero
            {
                Interlocked.Add(ref Done, -counted);
                await Task.Delay(2000 * (attempt + 1), ct);
            }
        }
        File.Move(part, dest, true);
    }
}
