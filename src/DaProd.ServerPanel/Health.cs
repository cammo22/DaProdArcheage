using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace DaProd.ServerPanel;

public enum HealthState { Spento, Avvio, Operativo, Problema }

public sealed record ServiceHealth(string Name, HealthState State, string Detail);

public sealed record HealthReport(HealthState Overall, string Headline, string Advice, List<ServiceHealth> Services,
    double CpuPc, double RamPc, double DiskFreeGb, int ErrorsLastMinute, bool Ready, int ZonesLoaded, int ZonesTotal, int Players);

/// <summary>Controlla servizi e PC e dice in parole semplici cosa sta succedendo (server o PC?).</summary>
public sealed class HealthMonitor(PanelSettings s, ServerManager srv)
{
    readonly Queue<DateTime> _errors = new();
    readonly Dictionary<string, (TimeSpan cpu, DateTime at)> _cpuPrev = new();
    readonly Dictionary<string, int> _hot = new();
    int _crashesSeen;
    DateTime _lastCrash = DateTime.MinValue;
    long _idlePrev, _totalPrev;
    readonly Queue<double> _cpuHist = new();
    static readonly Regex Noise = new("GameData|RandomMerchant|Sailing activity|Reopen box|UccGameData|SkillManager|ZoneCoord", RegexOptions.Compiled);

    /// <summary>Conta gli errori veri (ignora gli avvisi di qualità dei dati che escono a ogni avvio).</summary>
    public void OnLog(string line)
    {
        if (!line.Contains("[ERROR]") && !line.Contains("[FATAL]") && !line.Contains("ARRESTO IMPREVISTO")) return;
        if (Noise.IsMatch(line)) return;
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

        var ip = IPGlobalProperties.GetIPGlobalProperties();
        var listening = ip.GetActiveTcpListeners().Select(e => e.Port).ToHashSet();
        var players = ip.GetActiveTcpConnections().Where(c => c.State == TcpState.Established && c.LocalEndPoint.Port == s.GamePort)
            .Select(c => c.RemoteEndPoint.Address.ToString()).Distinct().Count();

        var loaded = srv.ZonesLoaded; var total = srv.ZonesTotal;
        var services = new List<ServiceHealth>
        {
            Service("MySQL", "mysql", listening.Contains(s.MySqlPort), 30),
            Service("Login", "login", listening.Contains(s.LoginPort), 40),
            Service("World", "game", listening.Contains(s.GamePort), 300),
        };
        var worldStart = srv.Proc("game")?.StartTime;
        services.Add(!srv.IsRunning("game") ? new("Zone", HealthState.Spento, "spento")
            : total == 0 ? new("Zone", HealthState.Avvio, "in attesa")
            : loaded >= total ? new("Zone", HealthState.Operativo, $"{loaded}/{total} caricate")
            : worldStart != null && DateTime.Now - worldStart > TimeSpan.FromMinutes(20)
                ? new("Zone", HealthState.Problema, $"{loaded}/{total}: ne mancano {total - loaded}")
                : new("Zone", HealthState.Avvio, $"{loaded}/{total} caricate"));

        var cpu = Smooth(CpuPercent()); var ram = RamPercent(); var disk = DiskFreeGb();
        var pcBad = cpu > 97 || ram > 92 || disk < 5;
        var bad = services.FirstOrDefault(x => x.State == HealthState.Problema);
        var allOn = services.All(x => x.State == HealthState.Operativo);
        var ready = allOn && srv.Activity == "";

        HealthState overall; string head, advice;
        if (srv.Activity != "")
        { overall = HealthState.Avvio; head = srv.Activity; advice = "Operazione in corso: non chiudere il pannello."; }
        else if (DateTime.Now - _lastCrash < TimeSpan.FromMinutes(2) && !allOn && srv.Wanted)
        { overall = HealthState.Problema; head = "Un servizio si è arrestato"; advice = s.Watchdog ? "Lo riavvio da solo: se succede di continuo guarda il log." : "Premi Riavvia e guarda il log."; }
        else if (bad != null)
        { overall = HealthState.Problema; head = $"PROBLEMA DEL SERVER: {bad.Name} - {bad.Detail}"; advice = "Il problema è nel server, non nel tuo PC. Prova Riavvia; se torna guarda il log."; }
        else if (pcBad)
        { overall = HealthState.Problema; head = "IL TUO PC È SOTTO SFORZO"; advice = $"CPU {cpu:F0}%  RAM {ram:F0}%  disco libero {disk:F0} GB: il server rallenta per questo."; }
        else if (errs > 60)
        { overall = HealthState.Problema; head = $"MOLTI ERRORI NEL SERVER ({errs} al minuto)"; advice = "Il server gira ma segnala errori: guarda il log."; }
        else if (allOn)
        { overall = HealthState.Operativo; head = "SERVER OPERATIVO"; advice = $"Tutto acceso: {loaded} zone, {players} giocatori collegati."; }
        else if (services.All(x => x.State == HealthState.Spento))
        { overall = HealthState.Spento; head = s.Maintenance ? "MANUTENZIONE" : "SERVER SPENTO"; advice = s.Maintenance ? "Login e World spenti per manutenzione." : "Premi Avvia server."; }
        else
        {
            overall = HealthState.Avvio;
            var l = services.First(x => x.State != HealthState.Operativo);
            head = $"IN CARICAMENTO: {l.Name} ({l.Detail})"; advice = "Normale all'avvio: le mappe ci mettono qualche minuto.";
        }
        return new(overall, head, advice, services, cpu, ram, disk, errs, ready, loaded, total, players);
    }

    ServiceHealth Service(string label, string key, bool portOpen, int maxStartSeconds)
    {
        var p = srv.Proc(key);
        if (p == null) return new(label, HealthState.Spento, "spento");
        DateTime start; try { start = p.StartTime; } catch { return new(label, HealthState.Avvio, "avvio"); }
        var up = DateTime.Now - start;
        var cpu = ProcessCpu(key, p);
        _hot[key] = cpu > 90 ? _hot.GetValueOrDefault(key) + 1 : 0;
        if (!portOpen)
            return up.TotalSeconds > maxStartSeconds ? new(label, HealthState.Problema, $"acceso da {up.TotalSeconds:F0}s ma non risponde")
                : new(label, HealthState.Avvio, $"avvio {up.TotalSeconds:F0}s");
        if (_hot[key] > 20) return new(label, HealthState.Problema, $"CPU al {cpu:F0}% da troppo tempo");
        long mem; try { p.Refresh(); mem = p.WorkingSet64; } catch { mem = 0; }
        return new(label, HealthState.Operativo, $"{mem / 1073741824.0:F1} GB RAM");
    }

    double ProcessCpu(string key, Process p)
    {
        try
        {
            var now = DateTime.Now; var total = p.TotalProcessorTime; var pct = 0.0;
            if (_cpuPrev.TryGetValue(key, out var prev))
                pct = (total - prev.cpu).TotalMilliseconds / Math.Max(1, (now - prev.at).TotalMilliseconds) / Environment.ProcessorCount * 100;
            _cpuPrev[key] = (total, now);
            return pct;
        }
        catch { return 0; }
    }

    static double DiskFreeGb() { try { return new DriveInfo(Path.GetPathRoot(PanelSettings.Root)!).AvailableFreeSpace / 1073741824.0; } catch { return 999; } }

    [DllImport("kernel32.dll")] static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
    [StructLayout(LayoutKind.Sequential)]
    struct MemStatus { public uint Length, Load; public ulong TotalPhys, AvailPhys, TotalPage, AvailPage, TotalVirt, AvailVirt, AvailExt; }
    [DllImport("kernel32.dll")] static extern bool GlobalMemoryStatusEx(ref MemStatus m);

    /// <summary>Media degli ultimi 5 controlli (15 s): un picco di pochi secondi durante il caricamento non è un problema.</summary>
    double Smooth(double v)
    {
        _cpuHist.Enqueue(v);
        while (_cpuHist.Count > 5) _cpuHist.Dequeue();
        return _cpuHist.Average();
    }

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
