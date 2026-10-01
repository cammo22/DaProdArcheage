using System.Reflection;

namespace DaProd.Launcher;

/// <summary>
/// Il mod dell'interfaccia (pulsanti nuovi nel gioco) è un "addon" del client: una cartella con toc.g e uno script Lua in
/// Documenti\ArcheAge\Addon\DaProdMod. Sta dentro il launcher e si rimette a posto a ogni avvio (e prima di entrare in gioco).
/// </summary>
static class AddonInstaller
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ArcheAge", "Addon", "DaProdMod");

    public static string Install()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            Copy("addon_toc.g", "toc.g");
            Copy("addon_main.lua", "main.lua");
            return Dir;
        }
        catch (Exception ex) { return "errore: " + ex.Message; }
    }

    static void Copy(string resource, string file)
    {
        using var st = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (st == null) return;
        using var f = File.Create(Path.Combine(Dir, file));
        st.CopyTo(f);
    }
}
