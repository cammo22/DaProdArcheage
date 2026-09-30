using System.IO.Compression;
using System.Text.Json;

namespace DaProd.ServerPanel;

/// <summary>PacchettoAmici.zip: launcher + Tailscale portatile + server.json già configurato (indirizzo e chiave).</summary>
public static class FriendPackage
{
    public static string Create(PanelSettings s)
    {
        var launcher = Path.Combine(PanelSettings.Root, "launcher", "DaProdLauncher.exe");
        if (!File.Exists(launcher)) throw new Exception("launcher\\DaProdLauncher.exe non trovato: ricompila con build.ps1.");
        var dir = Path.Combine(PanelSettings.Root, "PacchettoAmici");
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
        Directory.CreateDirectory(dir);
        File.Copy(launcher, Path.Combine(dir, "DaProdLauncher.exe"), true);

        // Tailscale portatile (BSD-3): gli amici non installano niente e non usano email
        var tsSrc = Path.GetDirectoryName(PanelSettings.TailscaleExe)!;
        if (s.UseTailscale && File.Exists(Path.Combine(tsSrc, "tailscaled.exe")))
        {
            Directory.CreateDirectory(Path.Combine(dir, "tailscale"));
            foreach (var f in new[] { "tailscale.exe", "tailscaled.exe" }) File.Copy(Path.Combine(tsSrc, f), Path.Combine(dir, "tailscale", f), true);
        }
        File.WriteAllText(Path.Combine(dir, "server.json"), JsonSerializer.Serialize(new
        {
            api = $"http://{s.PublicIp}:{s.LauncherApiPort}",
            tailscaleAuthKey = s.UseTailscale ? s.TailscaleAuthKey : ""
        }, new JsonSerializerOptions { WriteIndented = true }));
        var zip = dir + ".zip";
        if (File.Exists(zip)) File.Delete(zip);
        ZipFile.CreateFromDirectory(dir, zip);
        return zip;
    }
}
