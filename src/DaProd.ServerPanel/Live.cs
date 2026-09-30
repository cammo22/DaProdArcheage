using System.Data;
using System.Diagnostics;
using System.Net.NetworkInformation;

namespace DaProd.ServerPanel;

/// <summary>Stato in tempo reale: connessioni, dispositivi Tailscale e giocatori.</summary>
public static class Live
{
    static (DateTime at, Dictionary<string, string> map) _peers;

    static IEnumerable<string> RemoteIps(int port) =>
        IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .Where(c => c.State == TcpState.Established && c.LocalEndPoint.Port == port)
            .Select(c => c.RemoteEndPoint.Address.ToString().Replace("::ffff:", "")).Distinct();

    public static object Snapshot(PanelSettings s, ServerManager srv, HealthReport? h) => new
    {
        online = srv.IsRunning("login") && srv.IsRunning("game"),
        // "ready" = tutto operativo, anche le mappe: solo allora il launcher fa giocare
        ready = h?.Ready == true,
        status = h?.Headline ?? "In avvio",
        players = h?.Players ?? RemoteIps(s.GamePort).Count(),
        zonesLoaded = h?.ZonesLoaded ?? 0,
        zonesTotal = h?.ZonesTotal ?? 0,
        time = DateTime.Now.ToString("HH:mm:ss")
    };

    /// <summary>Chi è collegato adesso: account (stimato dall'ultimo IP di login), dispositivo Tailscale e dove.</summary>
    public static async Task<DataTable> PlayersAsync(PanelSettings s, ServerManager srv)
    {
        var names = Peers();
        var t = new DataTable();
        foreach (var c in new[] { "Account", "IP", "Dispositivo", "Stato" }) t.Columns.Add(c);
        DataTable users;
        try { users = await srv.QueryAsync("aaemu_login", "SELECT username, last_ip FROM users WHERE last_ip<>'' ORDER BY last_login DESC"); }
        catch { users = new DataTable(); }
        string Who(string ip) => users.Rows.Cast<DataRow>().FirstOrDefault(r => ((string)r["last_ip"]).Replace("::ffff:", "") == ip)?["username"] as string ?? "?";
        var seen = new HashSet<string>();
        foreach (var (port, what) in new[] { (s.GamePort, "In gioco"), (s.LoginPort, "Al login"), (s.LauncherApiPort, "Launcher / download") })
            foreach (var ip in RemoteIps(port))
                if (seen.Add(ip + what)) t.Rows.Add(what == "In gioco" || what == "Al login" ? Who(ip) : "", ip, names.GetValueOrDefault(ip, ip is "127.0.0.1" or "::1" ? "Questo PC" : ""), what);
        return t;
    }

    static Dictionary<string, string> Peers()
    {
        if (DateTime.Now - _peers.at < TimeSpan.FromSeconds(15) && _peers.map != null) return _peers.map;
        var map = new Dictionary<string, string>();
        if (File.Exists(PanelSettings.TailscaleExe))
            try
            {
                var p = Process.Start(new ProcessStartInfo(PanelSettings.TailscaleExe, "status")
                { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
                string? line;
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && parts[0].StartsWith("100.") && !line.Contains("offline")) map[parts[0]] = parts[1];
                }
                p.WaitForExit(3000);
            }
            catch { }
        _peers = (DateTime.Now, map);
        return map;
    }
}
