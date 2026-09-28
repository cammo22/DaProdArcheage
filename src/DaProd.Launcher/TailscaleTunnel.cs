using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace DaProd.Launcher;

/// <summary>
/// Tailscale portatile: nessuna installazione, nessun admin, nessuna email.
/// Avvia tailscaled in "userspace-networking" con un proxy SOCKS5 locale e inoltra
/// le porte del gioco da 127.0.0.1 al server, così il client si collega a localhost.
/// </summary>
public sealed class TailscaleTunnel : IDisposable
{
    const int SocksPort = 1055;
    const string Pipe = @"\\.\pipe\daprod-tailscaled";
    static readonly string BinDir = Path.Combine(AppContext.BaseDirectory, "tailscale");
    static readonly string StateDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaProdArcheage", "tailscale");

    Process? _daemon;
    readonly List<TcpListener> _listeners = [];

    public static bool Available => File.Exists(Path.Combine(BinDir, "tailscaled.exe")) && File.Exists(Path.Combine(BinDir, "tailscale.exe"));
    public static IWebProxy Proxy => new WebProxy($"socks5://127.0.0.1:{SocksPort}");

    /// <summary>True se l'IP è di questo PC (chi ospita il server non ha bisogno del tunnel).</summary>
    public static bool IsLocal(string ip) =>
        ip is "127.0.0.1" or "localhost" ||
        NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.ToString() == ip);

    public async Task StartAsync(string authKey, Action<string> status)
    {
        Directory.CreateDirectory(StateDir);
        foreach (var old in Process.GetProcessesByName("tailscaled").Where(p => SafePath(p)?.StartsWith(BinDir, StringComparison.OrdinalIgnoreCase) == true))
            try { old.Kill(); } catch { }

        status("Avvio della rete privata (Tailscale portatile)...");
        _daemon = Process.Start(new ProcessStartInfo(Path.Combine(BinDir, "tailscaled.exe"),
            $"--tun=userspace-networking --socks5-server=127.0.0.1:{SocksPort} --statedir=\"{StateDir}\" --socket={Pipe} --port=0 --no-logs-no-support")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true });
        _daemon!.OutputDataReceived += (_, _) => { }; _daemon.ErrorDataReceived += (_, _) => { };
        _daemon.BeginOutputReadLine(); _daemon.BeginErrorReadLine();
        await Task.Delay(2000);

        status("Accesso alla rete del server...");
        var host = "daprod-" + new string(Environment.MachineName.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        for (var i = 0; i < 5; i++)
        {
            if (await Cli($"up --auth-key={authKey} --hostname={host} --accept-dns=false --timeout=60s") == 0) break;
            await Task.Delay(2000);
        }
        for (var i = 0; i < 30 && await Cli("status") != 0; i++) await Task.Delay(1000);
    }

    Task<int> Cli(string args) => Task.Run(() =>
    {
        var p = Process.Start(new ProcessStartInfo(Path.Combine(BinDir, "tailscale.exe"), $"--socket={Pipe} {args}")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        p.StandardOutput.ReadToEnd(); p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode;
    });

    /// <summary>Apre 127.0.0.1:porta → serverIp:porta attraverso il SOCKS5 di tailscaled.</summary>
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
                    _ = Pipe2(c, serverIp, port);
                }
            });
        }
    }

    static async Task Pipe2(TcpClient client, string ip, int port)
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

    static string? SafePath(Process p) { try { return p.MainModule?.FileName; } catch { return null; } }

    public void Dispose()
    {
        foreach (var l in _listeners) try { l.Stop(); } catch { }
        try { if (_daemon is { HasExited: false }) _daemon.Kill(true); } catch { }
    }
}
