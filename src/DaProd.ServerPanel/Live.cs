using System.Data;
using System.Diagnostics;
using System.IO.Compression;
using System.Net.NetworkInformation;
using System.Text.Json;

namespace DaProd.ServerPanel;

/// <summary>Stato in tempo reale: connessioni ai server e dispositivi Tailscale.</summary>
public static class Live
{
    static IEnumerable<TcpConnectionInformation> Conns(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .Where(c => c.LocalEndPoint.Port == port && c.State == TcpState.Established);

    public static object Snapshot(PanelSettings s, ServerManager srv, HealthReport? h) => new
    {
        online = srv.IsRunning("login") && srv.IsRunning("game"),
        // "ready" = tutto operativo (anche le mappe): solo allora il launcher fa giocare
        ready = h?.Overall == HealthState.Operativo,
        status = h?.Headline ?? "In avvio",
        players = Conns(s.GamePort).Select(c => c.RemoteEndPoint.Address.ToString()).Distinct().Count(),
        time = DateTime.Now.ToString("HH:mm:ss")
    };

    public static DataTable Rows(PanelSettings s)
    {
        var names = TailscalePeers();
        var t = new DataTable();
        t.Columns.Add("Chi"); t.Columns.Add("IP"); t.Columns.Add("Stato");
        foreach (var (port, what) in new[] { (s.GamePort, "In gioco"), (s.LoginPort, "Al login"), (s.LauncherApiPort, "Launcher / download") })
            foreach (var ip in Conns(port).Select(c => c.RemoteEndPoint.Address.ToString()).Distinct())
                t.Rows.Add(names.GetValueOrDefault(ip, ip), ip, what);
        foreach (var (ip, name) in names)
            t.Rows.Add(name, ip, "Online (Tailscale)");
        return t;
    }

    /// <summary>ip -> nome dei dispositivi Tailscale online.</summary>
    static Dictionary<string, string> TailscalePeers()
    {
        var map = new Dictionary<string, string>();
        if (!File.Exists(PanelSettings.TailscaleExe)) return map;
        try
        {
            var p = Process.Start(new ProcessStartInfo(PanelSettings.TailscaleExe, "status")
            { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            string? line;
            while ((line = p.StandardOutput.ReadLine()) != null)
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && parts[0].StartsWith("100.") && !line.Contains("offline"))
                    map[parts[0]] = parts[1];
            }
            p.WaitForExit(3000);
        }
        catch { }
        return map;
    }
}

/// <summary>Crea dist\PacchettoAmici.zip: launcher + server.json già configurato.</summary>
public static class FriendPackage
{
    public static void Create(PanelSettings s, Action<string, string> log)
    {
        var launcher = Path.Combine(PanelSettings.Root, "launcher", "DaProdLauncher.exe");
        if (!File.Exists(launcher)) { MessageBox.Show("launcher\\DaProdLauncher.exe non trovato: esegui build.ps1."); return; }
        if (s.UseTailscale && string.IsNullOrWhiteSpace(s.TailscaleAuthKey))
            MessageBox.Show("Nessuna TailscaleAuthKey impostata: gli amici dovranno entrare nella tua rete Tailscale a mano.\n" +
                            "Creala su login.tailscale.com → Settings → Keys (Reusable + Ephemeral) e incollala nelle Impostazioni.");

        var dir = Path.Combine(PanelSettings.Root, "PacchettoAmici");
        Directory.CreateDirectory(dir);
        File.Copy(launcher, Path.Combine(dir, "DaProdLauncher.exe"), true);
        // Tailscale portatile (open source, BSD-3): gli amici non installano nulla e non usano email
        var tsSrc = Path.GetDirectoryName(PanelSettings.TailscaleExe)!;
        if (s.UseTailscale && File.Exists(Path.Combine(tsSrc, "tailscaled.exe")))
        {
            Directory.CreateDirectory(Path.Combine(dir, "tailscale"));
            foreach (var f in new[] { "tailscale.exe", "tailscaled.exe" })
                File.Copy(Path.Combine(tsSrc, f), Path.Combine(dir, "tailscale", f), true);
        }
        File.WriteAllText(Path.Combine(dir, "server.json"), JsonSerializer.Serialize(new
        {
            api = $"http://{s.PublicIp}:{s.LauncherApiPort}",
            tailscaleAuthKey = s.UseTailscale ? s.TailscaleAuthKey : ""
        }, new JsonSerializerOptions { WriteIndented = true }));
        var zip = dir + ".zip";
        File.Delete(zip);
        ZipFile.CreateFromDirectory(dir, zip);
        log("panel", $"Pacchetto creato: {zip} — mandalo ai tuoi amici.");
        Process.Start("explorer.exe", $"/select,\"{zip}\"");
    }
}
