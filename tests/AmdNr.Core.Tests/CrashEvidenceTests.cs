using System.Text;

namespace AmdNr.Core.Tests;

public class CrashEvidenceTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "amdnr-crash-" + Guid.NewGuid().ToString("N"));
    private readonly CrashEvidence.Roots roots;
    private readonly string game;

    public CrashEvidenceTests()
    {
        roots = new(Dir("local"), Dir("locallow"), Dir("temp"), Dir("programdata"));
        game = Dir("game");
    }

    public void Dispose() => Directory.Delete(root, true);

    private string Dir(string rel) => Directory.CreateDirectory(Path.Combine(root, rel)).FullName;

    private static string Put(string path, string text = "x", int bytes = 0, Encoding? encoding = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (bytes > 0) File.WriteAllBytes(path, new byte[bytes]);
        else File.WriteAllText(path, text, encoding ?? new UTF8Encoding(false));
        return path;
    }

    private (List<CrashFile> Files, string Index) Collect(params string[] exes) =>
        CrashEvidence.Collect([game], exes, roots, DateTime.Now);

    [Fact]
    public void UnityPlayerLogAndCrashFolderAreFoundThroughAppInfo()
    {
        Put(Path.Combine(game, "VaM_Data", "app.info"), "Meshed VR\nVaM\n");
        Put(Path.Combine(roots.LocalLow, "Meshed VR", "VaM", "Player.log"), "log");
        Put(Path.Combine(roots.LocalLow, "Meshed VR", "VaM", "Player-prev.log"), "prev");
        Put(Path.Combine(roots.Temp, "Meshed VR", "VaM", "Crashes", "Crash_2026-10-15_1", "crash.dmp"), bytes: 100);
        Put(Path.Combine(roots.Temp, "Meshed VR", "VaM", "Crashes", "Crash_2026-10-15_1", "error.log"), "err");

        var (files, index) = Collect("VaM.exe");
        var names = files.Select(f => f.Name).ToList();
        Assert.Contains("unity/Player.log", names);
        Assert.Contains("unity/Player-prev.log", names);
        Assert.Contains("unity/Crash_2026-10-15_1/crash.dmp", names);
        Assert.Contains("unity/Crash_2026-10-15_1/error.log", names);
        Assert.True(files.Single(f => f.Name.EndsWith("crash.dmp")).Whole);
        Assert.False(files.Single(f => f.Name == "unity/Player.log").Whole);
        Assert.Contains("Meshed VR / VaM", index);
    }

    [Fact]
    public void UnityNamesLoseWhatAFolderCannotHoldAndOldLogsAreTaken()
    {
        Put(Path.Combine(game, "Cities_Data", "app.info"), "Colossal Order\nCities: Skylines II\n");
        Put(Path.Combine(game, "Cities_Data", "output_log.txt"), "old");
        Put(Path.Combine(roots.LocalLow, "Colossal Order", "Cities Skylines II", "Player.log"), "log");

        var names = Collect("Cities.exe").Files.Select(f => f.Name).ToList();
        Assert.Contains("unity/Player.log", names);
        Assert.Contains("unity/Cities_Data/output_log.txt", names);
    }

    [Fact]
    public void WerReportIsTakenOnlyForTheGamesProcess()
    {
        var wer = Path.Combine(roots.LocalAppData, "Microsoft", "Windows", "WER", "ReportArchive");
        Put(Path.Combine(wer, "AppCrash_VaM.exe_abc", "Report.wer"), "Version=1\r\nAppPath=E:\\Games\\VaM\\VaM.exe\r\n",
            encoding: Encoding.Unicode);
        Put(Path.Combine(wer, "AppCrash_VaM.exe_abc", "memory.hdmp"), bytes: 10);
        Put(Path.Combine(wer, "AppCrash_other.exe_def", "Report.wer"), "AppPath=C:\\other.exe\r\n", encoding: Encoding.Unicode);

        var names = Collect("VaM.exe").Files.Select(f => f.Name).ToList();
        Assert.Contains("wer/AppCrash_VaM.exe_abc/Report.wer", names);
        Assert.DoesNotContain(names, n => n.Contains("other.exe"));
    }

    [Fact]
    public void LocalDumpsMatchTheExecutableAndGameFolderDumpsAreTaken()
    {
        Put(Path.Combine(roots.LocalAppData, "CrashDumps", "VaM.exe.1234.dmp"), bytes: 10);
        Put(Path.Combine(roots.LocalAppData, "CrashDumps", "explorer.exe.99.dmp"), bytes: 10);
        Put(Path.Combine(game, "crash_2026.mdmp"), bytes: 10);

        var names = Collect("E:\\Games\\VaM\\VaM.exe").Files.Select(f => f.Name).ToList();
        Assert.Contains("dumps/VaM.exe.1234.dmp", names);
        Assert.Contains("dumps/crash_2026.mdmp", names);
        Assert.DoesNotContain("dumps/explorer.exe.99.dmp", names);
    }

    [Fact]
    public void WithoutAKnownExecutableTheFoldersOwnStandIn()
    {
        Put(Path.Combine(game, "Game.exe"), bytes: 1);
        Put(Path.Combine(game, "UnityCrashHandler64.exe"), bytes: 1);
        Put(Path.Combine(roots.LocalAppData, "CrashDumps", "Game.exe.1.dmp"), bytes: 10);

        var (files, index) = Collect();
        Assert.Contains(files, f => f.Name == "dumps/Game.exe.1.dmp");
        Assert.DoesNotContain("UnityCrashHandler64", index.Split('\n')[0]);
    }

    [Fact]
    public void DumpsOverTheBudgetAreNamedNotTaken()
    {
        var old = Put(Path.Combine(game, "old.dmp"), bytes: (int)CrashEvidence.DumpBudget - 10);
        File.SetLastWriteTime(old, DateTime.Now.AddHours(-2));
        Put(Path.Combine(game, "new.dmp"), bytes: 100);

        var (files, index) = Collect("Game.exe");
        Assert.Contains(files, f => f.Name == "dumps/new.dmp");
        Assert.DoesNotContain(files, f => f.Name == "dumps/old.dmp");
        Assert.Contains("old.dmp", index);
        Assert.Contains("over the size budget", index);
    }

    [Fact]
    public void OldCrashesAreSomeOtherSessions()
    {
        var dump = Put(Path.Combine(game, "stale.dmp"), bytes: 10);
        File.SetLastWriteTime(dump, DateTime.Now - CrashEvidence.MaxAge - TimeSpan.FromDays(1));

        var (files, index) = Collect("Game.exe");
        Assert.Empty(files);
        Assert.Contains("No Player.log", index);
    }
}
