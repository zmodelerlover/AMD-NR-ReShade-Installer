using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using AmdNr.Core;

namespace AmdNr.App;

internal static class Program
{
    /// <summary>What an update starts the new executable with: it waits for the old one to let go,
    /// where a second start by hand gives up at once and brings the first one forward.</summary>
    public const string AfterUpdate = "--after-update";

    [STAThread]
    public static void Main(string[] args)
    {
        // One copy per data folder. Two write the same games.json -- the last to save wins, and the
        // other's games are gone -- and the same cache and game folders, each with its own idea of
        // what is busy. Double-clicking twice is all it takes; the second one brings the first to
        // the front instead.
        var name = @"Local\AmdNrInstaller-"
                   + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(AppPaths.Root.ToUpperInvariant())))[..16];
        using var single = new Mutex(false, name);
        bool mine;
        try { mine = single.WaitOne(args.Contains(AfterUpdate) ? TimeSpan.FromSeconds(20) : TimeSpan.Zero); }
        catch (AbandonedMutexException) { mine = true; } // the other one ended without letting go
        if (!mine)
        {
            ShowRunning();
            return;
        }
        try { BuildAvaloniaApp().StartWithClassicDesktopLifetime(args); }
        finally { single.ReleaseMutex(); }
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();

    /// <summary>The copy already running, to the front, and out of the taskbar if it was minimised.
    /// Found by process name, so one renamed "(1).exe" is not found, and this just exits.</summary>
    private static void ShowRunning()
    {
        using var me = Process.GetCurrentProcess();
        foreach (var other in Process.GetProcessesByName(me.ProcessName))
        {
            using (other)
            {
                if (other.Id == me.Id || other.MainWindowHandle == IntPtr.Zero) continue;
                if (IsIconic(other.MainWindowHandle)) ShowWindow(other.MainWindowHandle, 9); // SW_RESTORE
                SetForegroundWindow(other.MainWindowHandle);
            }
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);
}
