using System.Diagnostics;
using System.Runtime.InteropServices;

namespace DaProd.ServerPanel;

/// <summary>
/// Job object di Windows con "kill on close": se il pannello si chiude o crasha, Windows chiude da solo
/// tutti i processi figli (MySQL, Login, World, zone). Niente più server "orfani" che restano accesi.
/// </summary>
public sealed class JobObject : IDisposable
{
    readonly IntPtr _h;

    public JobObject()
    {
        _h = CreateJobObject(IntPtr.Zero, null);
        var info = new ExtendedLimit { BasicLimitInformation = new BasicLimit { LimitFlags = 0x2000 } }; // KILL_ON_JOB_CLOSE
        var len = Marshal.SizeOf<ExtendedLimit>();
        var ptr = Marshal.AllocHGlobal(len);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(_h, 9, ptr, (uint)len);
        }
        finally { Marshal.FreeHGlobal(ptr); }
    }

    public void Add(Process p) { try { AssignProcessToJobObject(_h, p.Handle); } catch { } }
    public void Dispose() => CloseHandle(_h);

    /// <summary>Chiude i processi rimasti aperti da una sessione precedente (solo quelli avviati dalla nostra cartella).</summary>
    public static int KillStale(string root)
    {
        var n = 0;
        foreach (var name in new[] { "AAEmu.ZoneHost", "AAEmu.World", "AAEmu.Game", "AAEmu.Login", "mysqld" })
            foreach (var p in Process.GetProcessesByName(name))
                try
                {
                    var path = p.MainModule?.FileName ?? "";
                    var cmd = name == "mysqld" ? (path + " " + CommandLine(p.Id)) : path;
                    if (cmd.Contains(root, StringComparison.OrdinalIgnoreCase)) { p.Kill(true); p.WaitForExit(4000); n++; }
                }
                catch { }
        return n;
    }

    static string CommandLine(int pid)
    {
        try
        {
            using var s = new System.Management.ManagementObjectSearcher($"SELECT CommandLine FROM Win32_Process WHERE ProcessId={pid}");
            foreach (System.Management.ManagementObject o in s.Get()) return o["CommandLine"]?.ToString() ?? "";
        }
        catch { }
        return "";
    }

    [StructLayout(LayoutKind.Sequential)] struct BasicLimit
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags; public UIntPtr MinWs, MaxWs;
        public uint ActiveProcessLimit; public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
    }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters { public ulong a, b, c, d, e, f; }
    [StructLayout(LayoutKind.Sequential)] struct ExtendedLimit
    {
        public BasicLimit BasicLimitInformation; public IoCounters Io; public UIntPtr ProcessMemory, JobMemory, PeakProcess, PeakJob;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObject(IntPtr a, string? n);
    [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr h, int c, IntPtr i, uint l);
    [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr j, IntPtr p);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);
}
