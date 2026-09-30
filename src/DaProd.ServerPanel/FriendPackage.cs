using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DaProd.ServerPanel;

/// <summary>
/// Il launcher per gli amici: UN SOLO exe. È una copia di launcher\DaProdLauncher.exe con in coda indirizzo del server,
/// chiave di rete e Tailscale portatile (formato in Trailer.cs del launcher). Si rifà da solo se cambiano IP, porta o chiave,
/// e il launcher degli amici lo scarica da qui quando si aggiorna: così anche una chiave rinnovata arriva a tutti.
/// </summary>
public static class FriendLauncher
{
    static readonly object Gate = new();
    static string Dir => Path.Combine(PanelSettings.Root, "launcher");
    /// <summary>Il launcher più recente: build.ps1 copia in ".new.exe" se DaProdLauncher.exe è in uso (lo apre l'host stesso).</summary>
    public static string BasePath
    {
        get
        {
            string a = Path.Combine(Dir, "DaProdLauncher.exe"), b = Path.Combine(Dir, "DaProdLauncher.new.exe");
            if (!File.Exists(b)) return a;
            if (!File.Exists(a)) return b;
            return File.GetLastWriteTimeUtc(b) > File.GetLastWriteTimeUtc(a) ? b : a;
        }
    }
    public static string OutPath => Path.Combine(Dir, "DaProdLauncher-Amici.exe");

    /// <summary>Restituisce il percorso dell'exe personalizzato, ricostruendolo solo se serve.</summary>
    public static string EnsureBuilt(PanelSettings s)
    {
        lock (Gate)
        {
            if (!File.Exists(BasePath)) throw new Exception("launcher\\DaProdLauncher.exe non trovato: ricompila con build.ps1.");
            var cfg = JsonSerializer.SerializeToUtf8Bytes(new
            {
                api = $"http://{s.PublicIp}:{s.LauncherApiPort}",
                vpn = s.IsNetBird ? "netbird" : "tailscale",
                key = s.VpnKey
            });
            var dir = Path.GetDirectoryName(PanelSettings.TailscaleExe)!;
            var zip = !s.UseTailscale ? [] : ToolsZip(s.IsNetBird ? [PanelSettings.NetBirdExe]
                : [PanelSettings.TailscaleExe, Path.Combine(dir, "tailscaled.exe")]);
            var bi = new FileInfo(BasePath);
            var sig = Convert.ToHexString(SHA256.HashData([.. cfg, .. BitConverter.GetBytes(bi.Length), .. BitConverter.GetBytes(bi.LastWriteTimeUtc.Ticks), .. BitConverter.GetBytes(zip.Length)]));
            var sigFile = OutPath + ".sig";
            if (File.Exists(OutPath) && File.Exists(sigFile) && File.ReadAllText(sigFile) == sig) return OutPath;

            var tmp = OutPath + ".tmp";
            using (var o = File.Create(tmp))
            {
                using (var b = File.OpenRead(BasePath)) b.CopyTo(o);
                o.Write(zip); o.Write(cfg);
                o.Write(BitConverter.GetBytes(cfg.Length)); o.Write(BitConverter.GetBytes(zip.Length));
                o.Write(Encoding.ASCII.GetBytes("DAPRODL1"));
            }
            File.Move(tmp, OutPath, true);
            File.WriteAllText(sigFile, sig);
            return OutPath;
        }
    }

    /// <summary>Zip con i programmi della rete (NetBird: BSD-3, Tailscale: BSD-3), presi dall'installazione di questo PC.</summary>
    static byte[] ToolsZip(string[] files)
    {
        if (files.Any(f => !File.Exists(f))) return [];
        using var ms = new MemoryStream();
        using (var z = new ZipArchive(ms, ZipArchiveMode.Create, true))
            foreach (var f in files) z.CreateEntryFromFile(f, Path.GetFileName(f), CompressionLevel.Optimal);
        return ms.ToArray();
    }
}
