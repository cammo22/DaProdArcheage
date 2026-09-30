using System.IO.Compression;
using System.Text.Json;

namespace DaProd.Launcher;

/// <summary>
/// Il launcher per gli amici è un unico exe: in coda al file il pannello aggiunge indirizzo del server, motore e chiave di rete
/// e i programmi del motore (NetBird o Tailscale). Formato in coda:
/// [zip programmi][json configurazione][int32 lunghezza json][int32 lunghezza zip]["DAPRODL1"].
/// </summary>
static class Trailer
{
    const string Magic = "DAPRODL1";
    static readonly string ExePath = Environment.ProcessPath ?? "";
    /// <summary>Dove vengono estratti i programmi della rete (mai nella cartella dell'exe, che può essere di sola lettura).</summary>
    public static string VpnDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DaProdArcheage", "vpn-bin");

    static bool Locate(FileStream fs, out long zipStart, out int jsonLen, out int zipLen)
    {
        zipStart = 0; jsonLen = zipLen = 0;
        if (fs.Length < 16) return false;
        var f = new byte[16];
        fs.Seek(-16, SeekOrigin.End); fs.ReadExactly(f);
        if (System.Text.Encoding.ASCII.GetString(f, 8, 8) != Magic) return false;
        jsonLen = BitConverter.ToInt32(f, 0); zipLen = BitConverter.ToInt32(f, 4);
        zipStart = fs.Length - 16 - jsonLen - zipLen;
        return jsonLen >= 0 && zipLen >= 0 && zipStart > 0;
    }

    /// <summary>Configurazione incorporata, se questo exe è stato personalizzato dal pannello.</summary>
    public static (string api, string vpn, string key)? ReadConfig()
    {
        try
        {
            using var fs = new FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!Locate(fs, out var zipStart, out var jl, out var zl)) return null;
            var json = new byte[jl];
            fs.Seek(zipStart + zl, SeekOrigin.Begin); fs.ReadExactly(json);
            using var d = JsonDocument.Parse(json);
            string Get(string n) => d.RootElement.TryGetProperty(n, out var v) ? v.GetString() ?? "" : "";
            var key = Get("key"); if (key == "") key = Get("tailscaleAuthKey");
            var vpn = Get("vpn"); if (vpn == "") vpn = "tailscale";
            return Get("api") == "" ? null : (Get("api"), vpn, key);
        }
        catch { return null; }
    }

    /// <summary>Estrae i programmi della rete dall'exe (una volta sola, o se il pannello ne ha incluso una versione diversa).</summary>
    public static void ExtractTools()
    {
        try
        {
            using var fs = new FileStream(ExePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (!Locate(fs, out var zipStart, out _, out var zl) || zl == 0) return;
            var zip = new byte[zl];
            fs.Seek(zipStart, SeekOrigin.Begin); fs.ReadExactly(zip);
            using var za = new ZipArchive(new MemoryStream(zip), ZipArchiveMode.Read);
            Directory.CreateDirectory(VpnDir);
            foreach (var e in za.Entries)
            {
                var dst = Path.Combine(VpnDir, e.Name);
                if (File.Exists(dst) && new FileInfo(dst).Length == e.Length) continue;
                e.ExtractToFile(dst, true);
            }
        }
        catch { }
    }
}
