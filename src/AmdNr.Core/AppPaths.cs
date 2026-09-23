// Where this app keeps everything it writes for itself: settings, the list of games, the download
// cache and the logs.

namespace AmdNr.Core;

public static class AppPaths
{
    /// <summary>%AppData%\AmdNrInstaller, created on first use -- or wherever AMDNR_HOME says.
    /// The override is what keeps the test suite out of the real cache, and it is also how a
    /// portable install would keep everything beside the executable.</summary>
    public static string Root { get; } = Create(
        Environment.GetEnvironmentVariable("AMDNR_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                    Environment.SpecialFolderOption.Create),
                "AmdNrInstaller"));

    public static string Cache => Create(Path.Combine(Root, "cache"));
    public static string Logs => Create(Path.Combine(Root, "logs"));
    public static string GamesFile => Path.Combine(Root, "games.json");
    public static string CrashLog => Path.Combine(Logs, "crash.log");

    private static string Create(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}
