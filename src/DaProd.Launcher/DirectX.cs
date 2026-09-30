using System.Diagnostics;

namespace DaProd.Launcher;

/// <summary>Il client usa DirectX 9 (d3dx9_43.dll), che Windows 10/11 non ha di serie: senza, il gioco dà "Error loading DLL: CryRenderD3D9.dll".</summary>
static class DirectX
{
    const string Url = "https://download.microsoft.com/download/8/4/A/84A35BF1-DAFE-4AE8-82AF-AD2AE20B6B14/directx_Jun2010_redist.exe";
    public const string ManualPage = "https://www.microsoft.com/download/details.aspx?id=8109";

    public static bool Installed =>
        File.Exists(Path.Combine(Environment.SystemDirectory, "d3dx9_43.dll")) && File.Exists(Path.Combine(Environment.SystemDirectory, "D3DCompiler_42.dll"));

    /// <summary>Scarica il runtime ufficiale Microsoft e lo installa in silenzio (Windows chiede il permesso di amministratore).</summary>
    public static async Task InstallAsync(Action<string> status)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "daprod_dx");
        Directory.CreateDirectory(tmp);
        var exe = Path.Combine(tmp, "dx.exe");
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) })
        using (var resp = await http.GetAsync(Url, HttpCompletionOption.ResponseHeadersRead))
        {
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? 0;
            await using var src = await resp.Content.ReadAsStreamAsync();
            await using var dst = File.Create(exe);
            var buf = new byte[1 << 16]; long done = 0; int n;
            while ((n = await src.ReadAsync(buf)) > 0) { await dst.WriteAsync(buf.AsMemory(0, n)); done += n; if (total > 0) status($"Scarico DirectX... {done * 100 / total}%"); }
        }
        status("Estraggo DirectX...");
        var ex = Process.Start(new ProcessStartInfo(exe, $"/Q /T:\"{tmp}\\x\"") { UseShellExecute = false, CreateNoWindow = true })!;
        await ex.WaitForExitAsync();
        status("Installo DirectX (conferma la richiesta di Windows)...");
        var setup = Process.Start(new ProcessStartInfo(Path.Combine(tmp, "x", "DXSETUP.exe"), "/silent") { UseShellExecute = true, Verb = "runas" })!;
        await setup.WaitForExitAsync();
        try { Directory.Delete(tmp, true); } catch { }
    }
}
