namespace DaProd.Launcher;

static class Program
{
    [STAThread]
    static void Main()
    {
        using var mutex = new Mutex(true, "DaProdLauncher", out var first);
        if (!first) { MessageBox.Show("Il launcher è già aperto.", "DaProd"); return; }
        ApplicationConfiguration.Initialize();
        // mai finestre di "eccezione non gestita": gli errori finiscono nella barra di stato
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => MessageBox.Show(e.Exception.Message, "DaProd Launcher");
        Application.Run(new LauncherForm());
    }
}
