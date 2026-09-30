using System.Collections;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace DaProd.ServerPanel;

/// <summary>
/// Avvia i processi in un desktop virtuale nascosto: le finestre dello Zone Host (console, popup "Gweonid")
/// esistono lì dentro e non compaiono mai sullo schermo, niente scatti né lampeggi.
/// </summary>
public static class HiddenDesktop
{
    const string DesktopName = "DaProdZones";
    static IntPtr _desk;

    public static Process? Start(string exe, string args, string cwd, Dictionary<string, string> env, uint extraFlags = 0)
    {
        if (_desk == IntPtr.Zero) _desk = CreateDesktopW(DesktopName, null, null, 0, 0x10000000, IntPtr.Zero);

        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) vars[(string)e.Key] = (string?)e.Value ?? "";
        foreach (var (k, v) in env) vars[k] = v;
        var block = new StringBuilder();
        foreach (var (k, v) in vars) block.Append(k).Append('=').Append(v).Append('\0');
        block.Append('\0');
        var envPtr = Marshal.StringToHGlobalUni(block.ToString());
        try
        {
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>(), lpDesktop = "WinSta0\\" + DesktopName, dwFlags = 1 /* STARTF_USESHOWWINDOW */, wShowWindow = 0 /* SW_HIDE: anche la console nasce nascosta */ };
            var cmd = new StringBuilder($"\"{exe}\" {args}");
            if (!CreateProcessW(exe, cmd, IntPtr.Zero, IntPtr.Zero, false, 0x400 | 0x8 | extraFlags /* UNICODE_ENVIRONMENT + DETACHED_PROCESS: nessuna console ereditata */, envPtr, cwd, ref si, out var pi))
                return null;
            CloseHandle(pi.hThread); CloseHandle(pi.hProcess);
            return Process.GetProcessById(pi.dwProcessId);
        }
        finally { Marshal.FreeHGlobal(envPtr); }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb; public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2; public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }
    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION { public IntPtr hProcess, hThread; public int dwProcessId, dwThreadId; }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateDesktopW(string name, string? dev, string? dm, int flags, uint access, IntPtr sa);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool CreateProcessW(string app, StringBuilder cmd, IntPtr pa, IntPtr ta, bool inherit, uint flags, IntPtr env, string cwd, ref STARTUPINFO si, out PROCESS_INFORMATION pi);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
