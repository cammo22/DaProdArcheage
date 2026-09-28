using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace DaProd.ServerPanel;

public enum HealthState { Spento, Avvio, Operativo, Problema }

public sealed record ServiceHealth(string Name, HealthState State, string Detail);

public sealed record HealthReport(HealthState Overall, string Headline, string Advice, List<ServiceHealth> Services,
    double CpuPc, double RamPc, double DiskFreeGb, int ErrorsLastMinute);

/// <summary>
/// Controlla ogni pochi secondi servizi e PC e dice in parole semplici cosa sta succedendo:
/// spento, in caricamento, operativo o problema (e se il problema è il server o il PC).
/// </summary>
public sealed class HealthMonitor(PanelSettings s, ServerManager srv)
{
    readonly Queue<DateTime> _errors = new();
    readonly Dictionary<string, (TimeSpan cpu, DateTime at)> _cpuPrev = new();
    readonly Dictionary<string, int> _hotCount = new();
    int _crashesSeen;
    DateTime _lastCrash = DateTime.MinValue;
    long _idlePrev, _totalPrev;

    /// <summary>Da chiamare per ogni riga di log: conta errori e fatal.</summary>
    public void OnLog(string line)
    {
        if (!line.Contains("[ERROR]") && !line.Contains("[FATAL]") && !line.Contains("ARRESTO IMPREVISTO")) return;
        lock (_errors) _errors.Enqueue(DateTime.Now);
    }

    public HealthReport Check()
    {
        if (srv.Crashes != _crashesSeen) { _crashesSeen = srv.Crashes; _lastCrash = DateTime.Now; }
        int errs;
        lock (_errors)
        {
            while (_errors.Count > 0 && DateTime.Now - _errors.Peek() > TimeSpan.FromMinutes(1)) _errors.Dequeue();
            errs = _errors.Count;
        }

        var listening = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        var established = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpConnections()
            .Where(c => c.State == TcpState.Established).Select(c => c.LocalEndPoint.Port).ToHashSet();

        var services = new List<ServiceHealth>
        {
            Service("MySQL", "mysql", listening.Contains(s.MySqlPort), 15),
            Service("Login", "login", listening.Contains(s.LoginPort), 30),
            Service("World", "game", listening.Contains(s.GamePort), 300),
        };
        var zones = Process.GetProcessesByName("AAEmu.ZoneHost");
        var zoneUp = established.Contains(1240);
        services.Add(!srv.IsRunning("game") ? new("Zone", HealthState.Spento, "spento")
            : zones.Length == 0 ? new("Zone", HealthState.Avvio, "in attesa del World")
            : zoneUp ? new("Zone", HealthState.Operativo, $"{zones.Length} zona/e caricate")
            : new("Zone", HealthState.Avvio, "carica la mappa..."));

        var cpu = CpuPercent();
        var ram = RamPercent();
        var disk = DiskFreeGb();

        HealthState overall;
        string head, advice;
        var bad = services.FirstOrDefault(x => x.State == HealthState.Problema);
        var pcStressed = cpu > 95 || ram > 92 || disk < 5;
        if (DateTime.Now - _lastCrash < TimeSpan.FromMinutes(3) && services.All(x => x.State != HealthState.Operativo || x.Name == "MySQL"))
        { overall = HealthState.Problema; head = "PROBLEMA DEL SERVER: un servizio si è arrestato"; advice = "Guarda il log qui sotto (righe ARRESTO IMPREVISTO / FATAL) e premi Riavvia."; }
        else if (bad != null)
        { overall = HealthState.Problema; head = $"PROBLEMA DEL SERVER: {bad.Name} - {bad.Detail}"; advice = "Il problema è nel server, non nel tuo PC. Prova Riavvia; se torna, mandami il log."; }
        else if (pcStressed)
        { overall = HealthState.Problema; head = "IL TUO PC È SOTTO SFORZO"; advice = $"CPU {cpu:F0}%  RAM {ram:F0}%  disco libero {disk:F0} GB. Chiudi programmi pesanti o libera spazio: il server rallenta per questo."; }
        else if (errs > 30)
        { overall = HealthState.Problema; head = $"MOLTI ERRORI NEL SERVER ({errs} al minuto)"; advice = "Il server gira ma segnala errori: se il gioco si comporta male mandami il log."; }
        else if (services.All(x => x.State == HealthState.Operativo))
        { overall = HealthState.Operativo; head = "SERVER OPERATIVO"; advice = "Tutto acceso e funzionante: i giocatori possono entrare."; }
        else if (services.All(x => x.State == HealthState.Spento))
        { overall = HealthState.Spento; head = s.Maintenance ? "MANUTENZIONE" : "SERVER SPENTO"; advice = s.Maintenance ? "Login e World spenti per manutenzione." : "Premi Avvia server."; }
        else
        {
            overall = HealthState.Avvio;
            var loading = services.First(x => x.State != HealthState.Operativo);
            head = $"IN CARICAMENTO: {loading.Name} ({loading.Detail})";
            advice = "Normale all'avvio: il World e le mappe possono metterci alcuni minuti.";
        }
        return new HealthReport(overall, head, advice, services, cpu, ram, disk, errs);
    }

    ServiceHealth Service(string label, string key, bool portOpen, int maxStartSeconds)
    {
        var p = srv.Proc(key);
        if (p == null) return new(label, HealthState.Spento, "spento");
        var up = DateTime.Now - p.StartTime;
        // uso CPU del processo: se resta sopra il 90% di tutti i core per >20s è un picco anomalo
        var cpu = ProcessCpu(key, p);
        _hotCount[key] = cpu > 90 ? _hotCount.GetValueOrDefault(key) + 1 : 0;
        var memGb = SafeMem(p) / 1073741824.0;
        if (!portOpen)
            return up.TotalSeconds > maxStartSeconds
                ? new(label, HealthState.Problema, $"acceso da {up.TotalSeconds:F0}s ma non risponde")
                : new(label, HealthState.Avvio, $"avvio {up.TotalSeconds:F0}s");
        if (_hotCount[key] > 10) return new(label, HealthState.Problema, $"CPU al {cpu:F0}% da troppo tempo");
        return new(label, HealthState.Operativo, $"ok - {memGb:F1} GB RAM");
    }

    double ProcessCpu(string key, Process p)
    {
        try
        {
            p.Refresh();
            var now = DateTime.Now; var total = p.TotalProcessorTime;
            var pct = 0.0;
            if (_cpuPrev.TryGetValue(key, out var prev))
                pct = (total - prev.cpu).TotalMilliseconds / Math.Max(1, (now - prev.at).TotalMilliseconds) / Environment.ProcessorCount * 100;
            _cpuPrev[key] = (total, now);
            return pct;
        }
        catch { return 0; }
    }

    static long SafeMem(Process p) { try { p.Refresh(); return p.WorkingSet64; } catch { return 0; } }

    double DiskFreeGb() { try { return new DriveInfo(Path.GetPathRoot(PanelSettings.Root)!).AvailableFreeSpace / 1073741824.0; } catch { return 999; } }

    // ---- CPU e RAM di sistema (Win32) ----
    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    struct MemStatus { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemStatus m);

    double CpuPercent()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user)) return 0;
        var total = kernel + user;
        var pct = _totalPrev == 0 ? 0 : 100.0 * (1 - (double)(idle - _idlePrev) / Math.Max(1, total - _totalPrev));
        _idlePrev = idle; _totalPrev = total;
        return Math.Clamp(pct, 0, 100);
    }

    static double RamPercent()
    {
        var m = new MemStatus { Length = (uint)Marshal.SizeOf<MemStatus>() };
        return GlobalMemoryStatusEx(ref m) ? m.Load : 0;
    }
}
