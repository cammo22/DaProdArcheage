using System.Diagnostics;

namespace DaProd.Launcher;

static class Program
{
    /// <summary>Solo per provare l'aspetto: niente rete e niente blocco "già aperto".</summary>
    public static bool TestMode;

    [STAThread]
    static void Main(string[] args)
    {
        TestMode = args.Contains("--test");
        using var mutex = new Mutex(true, TestMode ? "DaProdLauncherTest" : "DaProdLauncher", out var first);
        if (!first) { MessageBox.Show("Il launcher è già aperto.", "Ponticheage"); return; }
        ApplicationConfiguration.Initialize();
        // mai finestre di "eccezione non gestita": gli errori finiscono nella barra di stato
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "Ponticheage Launcher");

        // splash: si vede mentre il launcher si prepara (--nosplash la salta)
        SplashForm? splash = null;
        if (!args.Contains("--nosplash")) { try { splash = new SplashForm(); splash.Show(); splash.Step(0, 2400); } catch { splash = null; } }
        var main = new LauncherForm();
        if (splash != null)
        {
            const int total = 2400; var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < total) { splash.Step(sw.ElapsedMilliseconds, total); Application.DoEvents(); Thread.Sleep(12); }
            splash.Close(); splash.Dispose();
        }
        Application.Run(main);
    }
}
