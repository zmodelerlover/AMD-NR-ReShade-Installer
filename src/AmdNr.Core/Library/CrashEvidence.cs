// What a game leaves outside its own folder when it crashes, for the problem report: a Unity player's
// Player.log and crash folders, the Windows Error Reporting record of the crash, and any minidump. The
// VaM report could not go anywhere without the Player.log, which lives under AppData\LocalLow under the
// company and product names the player keeps in <exe>_Data\app.info.

using System.Text;

namespace AmdNr.Core;

/// <param name="File">The file to take.</param>
/// <param name="Name">Its name in the zip, under crashes/.</param>
/// <param name="Whole">A dump is useless cut, so it goes in whole or not at all; a log keeps its tail.</param>
public sealed record CrashFile(FileInfo File, string Name, bool Whole);

public static class CrashEvidence
{
    /// <summary>The folders this reads, so a test can point them anywhere.</summary>
    public sealed record Roots(string LocalAppData, string LocalLow, string Temp, string ProgramData)
    {
        public static Roots Machine()
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            return new(local, Path.Combine(Path.GetDirectoryName(local)!, "LocalLow"), Path.GetTempPath(),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData));
        }
    }

    /// <summary>Crashes older than this are some other session's.</summary>
    public static readonly TimeSpan MaxAge = TimeSpan.FromDays(14);

    /// <summary>Dumps above this go unsent and are named in the index instead: a free Discord account
    /// uploads 10 MB, and a dump compresses to about a third.</summary>
    public const long DumpBudget = 24L * 1024 * 1024;

    private const int PerSource = 3;

    private static readonly string[] DumpExtensions = [".dmp", ".mdmp"];

    // Helpers every Unity build or launcher ships beside the game; a crash of theirs is not the game's.
    private static readonly string[] NotTheGame = ["UnityCrashHandler64.exe", "UnityCrashHandler32.exe",
        "unins000.exe", "CrashReportClient.exe", "vc_redist.x64.exe", "vc_redist.x86.exe"];

    /// <summary>The files to take and an index saying what was found, where, and what was left out.</summary>
    /// <param name="folders">The install folder and the game's root; either may be missing.</param>
    /// <param name="executables">The game's executables as detected or chosen, names or paths; when none is
    /// known, every executable at the top of the folders stands in.</param>
    public static (List<CrashFile> Files, string Index) Collect(IEnumerable<string?> folders,
        IEnumerable<string?> executables, Roots roots, DateTime now)
    {
        var dirs = folders.OfType<string>().Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var exes = executables.OfType<string>().Where(e => e.Length > 0).Select(Path.GetFileName).OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (exes.Count == 0)
            exes = dirs.SelectMany(d => Safe(() => new DirectoryInfo(d).GetFiles("*.exe")))
                .Select(f => f.Name).Where(n => !NotTheGame.Contains(n, StringComparer.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        var files = new List<CrashFile>();
        var index = new StringBuilder();
        index.AppendLine($"executables     {(exes.Count == 0 ? "-" : string.Join(", ", exes))}");
        var since = now - MaxAge;

        // Unity before 2018 kept its log in the data folder instead.
        foreach (var data in dirs.SelectMany(UnityData))
        {
            var old = Path.Combine(data.FullName, "output_log.txt");
            if (File.Exists(old)) files.Add(new(new FileInfo(old), $"unity/{data.Name}/output_log.txt", false));
        }

        foreach (var (company, product) in dirs.SelectMany(UnityNames).Distinct())
        {
            var data = Path.Combine(roots.LocalLow, company, product);
            index.AppendLine($"unity           {company} / {product}  ({data})");
            foreach (var name in new[] { "Player.log", "Player-prev.log" })
                if (File.Exists(Path.Combine(data, name)))
                    files.Add(new(new FileInfo(Path.Combine(data, name)), $"unity/{name}", false));
            // The crash handler's folders: crash.dmp, error.log and a copy of the Player.log of that run.
            foreach (var crash in Newest(Path.Combine(roots.Temp, company, product, "Crashes"), since))
                foreach (var f in Safe(() => crash.GetFiles()))
                    files.Add(new(f, $"unity/{crash.Name}/{f.Name}", IsDump(f)));
        }

        // Windows Error Reporting: one folder per crash, its Report.wer naming the process and the faulting module.
        foreach (var wer in new[] { roots.LocalAppData, roots.ProgramData })
            foreach (var queue in new[] { "ReportArchive", "ReportQueue" })
            {
                var taken = 0;
                foreach (var report in Newest(Path.Combine(wer, "Microsoft", "Windows", "WER", queue), since, int.MaxValue))
                {
                    if (taken == PerSource) break;
                    var record = Path.Combine(report.FullName, "Report.wer");
                    if (!File.Exists(record) || !Names(ReadText(record), exes)) continue;
                    taken++;
                    foreach (var f in Safe(() => report.GetFiles()))
                        files.Add(new(f, $"wer/{report.Name}/{f.Name}", IsDump(f)));
                }
            }

        // LocalDumps, when someone turned it on, and whatever the game or a mod dumped beside itself.
        var dumps = Safe(() => new DirectoryInfo(Path.Combine(roots.LocalAppData, "CrashDumps")).GetFiles())
            .Where(f => IsDump(f) && exes.Any(e => f.Name.StartsWith(e + ".", StringComparison.OrdinalIgnoreCase)))
            .Concat(dirs.SelectMany(d => Safe(() => new DirectoryInfo(d).GetFiles()).Where(IsDump)))
            .Where(f => f.LastWriteTime >= since)
            .DistinctBy(f => f.FullName, StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(f => f.LastWriteTimeUtc).Take(PerSource);
        foreach (var f in dumps)
            files.Add(new(f, $"dumps/{f.Name}", true));

        // Newest dumps first until the budget is spent; the rest are named so they can be asked for.
        var spent = 0L;
        foreach (var dump in files.Where(f => f.Whole).OrderByDescending(f => f.File.LastWriteTimeUtc).ToList())
        {
            if (spent + dump.File.Length <= DumpBudget) { spent += dump.File.Length; continue; }
            files.Remove(dump);
            index.AppendLine($"left out         {dump.File.FullName}  ({dump.File.Length:N0} bytes, over the size budget)");
        }

        index.AppendLine();
        if (files.Count == 0) index.AppendLine("No Player.log, crash report or dump from the last 14 days.");
        foreach (var f in files)
            index.AppendLine($"{f.Name,-60} {f.File.LastWriteTime:yyyy-MM-dd HH:mm}  {f.File.FullName}");
        return (files, index.ToString());
    }

    /// <summary>Company and product from each &lt;exe&gt;_Data\app.info: the two lines Unity names
    /// AppData\LocalLow\&lt;company&gt;\&lt;product&gt; after, without the characters a folder cannot
    /// hold ("Cities: Skylines II" lives in "Cities Skylines II").</summary>
    private static IEnumerable<(string, string)> UnityNames(string dir)
    {
        foreach (var data in UnityData(dir))
        {
            var lines = ReadText(Path.Combine(data.FullName, "app.info"))
                .Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                .Select(l => string.Concat(l.Where(c => !Path.GetInvalidFileNameChars().Contains(c))).Trim())
                .ToArray();
            if (lines.Length >= 2 && lines[0].Length > 0 && lines[1].Length > 0)
                yield return (lines[0], lines[1]);
        }
    }

    private static IEnumerable<DirectoryInfo> UnityData(string dir) =>
        Safe(() => new DirectoryInfo(dir).GetDirectories("*_Data"))
            .Where(d => File.Exists(Path.Combine(d.FullName, "app.info")));

    /// <summary>Whether a Report.wer is about one of these processes: its AppPath or its app name names it.</summary>
    private static bool Names(string report, List<string> exes) =>
        report.Split('\n').Any(l =>
            (l.StartsWith("AppPath=", StringComparison.OrdinalIgnoreCase)
             || l.StartsWith("NsAppName=", StringComparison.OrdinalIgnoreCase)
             || l.StartsWith("AppName=", StringComparison.OrdinalIgnoreCase))
            && exes.Any(e => Path.GetFileName(l[(l.IndexOf('=') + 1)..].Trim()).Equals(e, StringComparison.OrdinalIgnoreCase)));

    private static IEnumerable<DirectoryInfo> Newest(string dir, DateTime since, int count = PerSource) =>
        Safe(() => new DirectoryInfo(dir).GetDirectories())
            .Where(d => d.LastWriteTime >= since)
            .OrderByDescending(d => d.LastWriteTimeUtc).Take(count);

    private static bool IsDump(FileInfo f) => DumpExtensions.Contains(f.Extension, StringComparer.OrdinalIgnoreCase);

    /// <summary>Report.wer is UTF-16; app.info is UTF-8. The BOM, or its absence, says which.</summary>
    private static string ReadText(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return "";
        }
    }

    private static T[] Safe<T>(Func<T[]> read)
    {
        try { return read(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }
}
