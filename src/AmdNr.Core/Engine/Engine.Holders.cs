// Which programs hold a file open, through Windows' Restart Manager: the same question Explorer asks
// before it says "the action can't be completed because the file is open in ...".

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AmdNr.Core;

public static partial class Engine
{
    /// <summary>"BatmanAK.exe (PID 1234)" for every process holding one of <paramref name="paths"/>, or nothing
    /// when Windows cannot say.</summary>
    public static IReadOnlyList<string> HoldersOf(IEnumerable<string> paths)
    {
        var files = paths.Where(File.Exists).ToArray();
        if (files.Length == 0 || RmStartSession(out var session, 0, Guid.NewGuid().ToString("N")) != 0) return [];
        try
        {
            if (RmRegisterResources(session, (uint)files.Length, files, 0, null, 0, null) != 0) return [];
            uint needed = 0, count = 0;
            var result = RmGetList(session, out needed, ref count, null, out _);
            if (result != ErrorMoreData || needed == 0) return [];
            var found = new RmProcessInfo[needed];
            count = needed;
            if (RmGetList(session, out needed, ref count, found, out _) != 0) return [];
            return found.Take((int)count).Select(p => Named(p.Process.ProcessId, p.AppName)).Distinct().ToList();
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return [];
        }
        finally
        {
            RmEndSession(session);
        }
    }

    /// <summary>What an install says when files it has to replace are open: the programs holding them when
    /// Windows can name them, since a game that crashed can stay running with no window.</summary>
    public static string OpenElsewhere(string dir, IReadOnlyList<string> held)
    {
        var names = $"{string.Join(", ", held)} {(held.Count == 1 ? "is" : "are")}";
        var who = HoldersOf(held.Select(n => Path.Combine(dir, n)));
        return who.Count == 0
            ? $"{names} open by another program. The game or emulator is almost certainly still running -- close it and this line goes away."
            : $"{names} open in {string.Join(", ", who)}. A game that crashed or froze can stay running with no window: "
              + "end it in Task Manager (Details tab), or restart the PC, and this line goes away.";
    }

    private static string Named(int pid, string appName)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return $"{process.ProcessName}.exe (PID {pid})";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        {
            return $"{appName} (PID {pid})";
        }
    }

    private const int ErrorMoreData = 234;

    [StructLayout(LayoutKind.Sequential)]
    private struct RmUniqueProcess
    {
        public int ProcessId;
        public System.Runtime.InteropServices.ComTypes.FILETIME StartTime;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct RmProcessInfo
    {
        public RmUniqueProcess Process;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string AppName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string ServiceShortName;
        public int ApplicationType;
        public uint AppStatus;
        public uint TSSessionId;
        [MarshalAs(UnmanagedType.Bool)] public bool Restartable;
    }

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmStartSession(out uint session, int flags, string key);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmEndSession(uint session);

    [DllImport("rstrtmgr.dll", CharSet = CharSet.Unicode)]
    private static extern int RmRegisterResources(uint session, uint files, string[] names, uint applications,
        RmUniqueProcess[]? processes, uint services, string[]? serviceNames);

    [DllImport("rstrtmgr.dll")]
    private static extern int RmGetList(uint session, out uint needed, ref uint count, [In, Out] RmProcessInfo[]? info,
        out uint rebootReasons);
}
