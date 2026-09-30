using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DaProd.Launcher;

/// <summary>
/// Rete privata "portatile": nessuna installazione, nessun amministratore, nessun account per gli amici.
/// Il motore (NetBird o Tailscale) gira in modalità userspace con un proxy SOCKS5 locale; il launcher inoltra
/// le porte del gioco da 127.0.0.1 al server attraverso quel proxy, così il client si collega a localhost.
/// </summary>
public abstract class VpnTunnel : IDisposable
{
    readonly List<TcpListener> _listeners = [];
    public abstract int SocksPort { get; }
    public IWebProxy Proxy => new WebProxy($"socks5://127.0.0.1:{SocksPort}");
    public abstract Task StartAsync(string key, Action<string> status);

    /// <summary>Crea il tunnel per il motore scelto ("netbird" o "tailscale"); null se i suoi file non ci sono.</summary>
    public static VpnTunnel? Create(string engine) =>
        engine.Equals("netbird", StringComparison.OrdinalIgnoreCase) ? (NetBirdTunnel.Available ? new NetBirdTunnel() : null)
        : TailscaleTunnel.Available ? new TailscaleTunnel() : null;

    /// <summary>True se l'IP è di questo PC (chi ospita il server non ha bisogno del tunnel).</summary>
    public static bool IsLocal(string ip) =>
        ip is "127.0.0.1" or "localhost" ||
        NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.ToString() == ip);

    /// <summary>Apre 127.0.0.1:porta → serverIp:porta attraverso il proxy SOCKS5 del motore.</summary>
    public void Forward(string serverIp, params int[] ports)
    {
        foreach (var port in ports.Distinct())
        {
            if (_listeners.Any(l => ((IPEndPoint)l.LocalEndpoint).Port == port)) continue;
            var l = new TcpListener(IPAddress.Loopback, port);
            l.Start();
            _listeners.Add(l);
            _ = Task.Run(async () =>
            {
                while (true)
                {
                    TcpClient c;
                    try { c = await l.AcceptTcpClientAsync(); } catch { return; }
                    _ = Pipe(c, serverIp, port);
                }
            });
        }
    }

    async Task Pipe(TcpClient client, string ip, int port)
    {
        using var _ = client;
        using var up = new TcpClient();
        try
        {
            client.NoDelay = true; up.NoDelay = true;
            await up.ConnectAsync(IPAddress.Loopback, SocksPort);
            var s = up.GetStream();
            await s.WriteAsync(new byte[] { 5, 1, 0 });
            var buf = new byte[10];
            await s.ReadExactlyAsync(buf.AsMemory(0, 2));
            var req = new List<byte> { 5, 1, 0, 1 };
            req.AddRange(IPAddress.Parse(ip).GetAddressBytes());
            req.Add((byte)(port >> 8)); req.Add((byte)port);
            await s.WriteAsync(req.ToArray());
            await s.ReadExactlyAsync(buf.AsMemory(0, 10));
            if (buf[1] != 0) return;
            var c = client.GetStream();
            await Task.WhenAny(c.CopyToAsync(s), s.CopyToAsync(c));
        }
        catch { }
    }

    protected static string? SafePath(Process p) { try { return p.MainModule?.FileName; } catch { return null; } }

    /// <summary>Esegue un programma a riga di comando e ne restituisce codice di uscita e testo.</summary>
    protected static Task<(int code, string output)> Run(string exe, string args, int timeoutMs = 30000) => Task.Run(() =>
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
            var o = p.StandardOutput.ReadToEndAsync(); var e = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, "timeout"); }
            return (p.ExitCode, o.Result + e.Result);
        }
        catch (Exception ex) { return (-2, ex.Message); }
    });

    public virtual void Dispose() { foreach (var l in _listeners) try { l.Stop(); } catch { } }
}

/// <summary>Tailscale portatile (tailscaled in userspace-networking con SOCKS5).</summary>
public sealed class TailscaleTunnel : VpnTunnel
{
    const int Socks = 1055;
    const string Pipe = @"\\.\pipe\daprod-tailscaled";
    /// <summary>Tailscale estratto dall'exe; in mancanza, la cartella "tailscale" accanto al launcher (vecchio pacchetto).</summary>
    static string BinDir => File.Exists(Path.Combine(Trailer.VpnDir, "tailscaled.exe")) ? Trailer.VpnDir : Path.Combine(AppContext.BaseDirectory, "tailscale");
    static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaProdArcheage", "tailscale");
    Process? _daemon;

    public static bool Available => File.Exists(Path.Combine(BinDir, "tailscaled.exe")) && File.Exists(Path.Combine(BinDir, "tailscale.exe"));
    public override int SocksPort => Socks;

    public override async Task StartAsync(string authKey, Action<string> status)
    {
        Directory.CreateDirectory(StateDir);
        foreach (var old in Process.GetProcessesByName("tailscaled").Where(p => SafePath(p)?.StartsWith(BinDir, StringComparison.OrdinalIgnoreCase) == true))
            try { old.Kill(); } catch { }

        status("Avvio della rete privata (Tailscale portatile)...");
        _daemon = Process.Start(new ProcessStartInfo(Path.Combine(BinDir, "tailscaled.exe"),
            $"--tun=userspace-networking --socks5-server=127.0.0.1:{Socks} --statedir=\"{StateDir}\" --socket={Pipe} --port=0 --no-logs-no-support")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        _daemon!.OutputDataReceived += (_, _) => { }; _daemon.ErrorDataReceived += (_, _) => { };
        _daemon.BeginOutputReadLine(); _daemon.BeginErrorReadLine();
        await Task.Delay(2000);

        status("Accesso alla rete del server...");
        var host = "daprod-" + new string(Environment.MachineName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        var cli = Path.Combine(BinDir, "tailscale.exe");
        for (var i = 0; i < 5; i++)
        {
            if ((await Run(cli, $"--socket={Pipe} up --auth-key={authKey} --hostname={host} --accept-dns=false --timeout=60s", 90000)).code == 0) break;
            await Task.Delay(2000);
        }
        for (var i = 0; i < 30 && (await Run(cli, $"--socket={Pipe} status")).code != 0; i++) await Task.Delay(1000);
    }

    public override void Dispose() { base.Dispose(); try { if (_daemon is { HasExited: false }) _daemon.Kill(true); } catch { } }
}

/// <summary>
/// NetBird portatile: "netbird service run" in modalità netstack (NB_USE_NETSTACK_MODE) espone un proxy SOCKS5 senza driver
/// e senza amministratore. La chiave "setup" può non scadere mai: si crea una volta e basta.
/// </summary>
public sealed class NetBirdTunnel : VpnTunnel
{
    const int Socks = 1056;
    const string Addr = "tcp://127.0.0.1:41999";
    static string Exe => Path.Combine(Trailer.VpnDir, "netbird.exe");
    static readonly string Dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaProdArcheage", "netbird");
    Process? _daemon;

    public static bool Available => File.Exists(Exe);
    public override int SocksPort => Socks;

    public override async Task StartAsync(string setupKey, Action<string> status)
    {
        Directory.CreateDirectory(Dir);
        foreach (var old in Process.GetProcessesByName("netbird").Where(p => SafePath(p)?.StartsWith(Trailer.VpnDir, StringComparison.OrdinalIgnoreCase) == true))
            try { old.Kill(); } catch { }

        status("Avvio della rete privata (NetBird)...");
        var psi = new ProcessStartInfo(Exe, $"service run --daemon-addr {Addr} --config \"{Dir}\\config.json\" --log-file \"{Dir}\\client.log\"")
        { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Dir };
        psi.Environment["NB_USE_NETSTACK_MODE"] = "true";
        psi.Environment["NB_SOCKS5_LISTENER_PORT"] = Socks.ToString();
        psi.Environment["NB_STATE_DIR"] = Dir;
        psi.Environment["NB_DISABLE_DNS"] = "true";
        psi.Environment["NB_ENABLE_NETSTACK_LOCAL_FORWARDING"] = "true";
        _daemon = Process.Start(psi);

        var alive = false;
        for (var i = 0; i < 30 && !alive; i++) { alive = (await Run(Exe, $"status --daemon-addr {Addr}", 8000)).code == 0; if (!alive) await Task.Delay(1000); }
        if (!alive) throw new Exception("La rete privata (NetBird) non parte. Log: " + Path.Combine(Dir, "client.log"));

        status("Accesso alla rete del server...");
        // "netbird up" con una chiave sbagliata riprova all'infinito: leggo il log per dare subito un errore chiaro
        var logPath = Path.Combine(Dir, "client.log");
        var logStart = File.Exists(logPath) ? new FileInfo(logPath).Length : 0;
        var upP = Process.Start(new ProcessStartInfo(Exe, $"up --daemon-addr {Addr} --setup-key {setupKey}")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        _ = upP.StandardOutput.ReadToEndAsync(); _ = upP.StandardError.ReadToEndAsync();
        for (var i = 0; i < 90 && !upP.HasExited; i++)
        {
            await Task.Delay(1000);
            if (LogHas(logPath, logStart, "setup key is invalid") || LogHas(logPath, logStart, "setup key is expired") || LogHas(logPath, logStart, "setup key was revoked"))
            {
                try { upP.Kill(true); } catch { }
                throw new Exception("La chiave di rete non è valida, è scaduta o è stata revocata: il proprietario del server deve crearne una nuova.");
            }
        }
        if (!upP.HasExited) { try { upP.Kill(true); } catch { } throw new Exception("La rete privata non risponde: controlla la connessione a internet."); }
        for (var i = 0; i < 40; i++)
        {
            var st = await Run(Exe, $"status --daemon-addr {Addr}", 8000);
            if (st.output.Contains("Management: Connected") && st.output.Contains("Signal: Connected")) return;
            await Task.Delay(1000);
        }
    }

    static bool LogHas(string path, long from, string text)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length <= from) return false;
            fs.Seek(from, SeekOrigin.Begin);
            return new StreamReader(fs).ReadToEnd().Contains(text, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    public override void Dispose() { base.Dispose(); try { if (_daemon is { HasExited: false }) _daemon.Kill(true); } catch { } }
}
