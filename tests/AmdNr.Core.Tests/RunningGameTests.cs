using System.Diagnostics;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>Cyberpunk 2077 left running failed six installs and five rollbacks with its files open: a game running
/// from the folder is now a pre-flight error, refused before the transaction, and names the process.</summary>
public class RunningGameTests
{
    [Fact]
    public void AGameRunningFromTheFolderStopsInstallAndUninstall()
    {
        var game = Fixture.Temp("running-game");
        var (src, pins) = Fixture.Payloads("running-game");
        var exe = Path.Combine(game, "Game.exe");
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), exe);
        using var process = Process.Start(new ProcessStartInfo(exe, "-n 60 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        try
        {
            var found = Work.RunningFrom(game, Preset.Dx11).Single(p => p.Pid == process.Id);
            // The start time is what tells the game from a later process Windows gives the same id.
            Assert.True(Engine.SameProcess(process, found.Started));
            Assert.False(Engine.SameProcess(process, found.Started.AddMinutes(-5)));
            var preflight = Work.Preflight(game, src, Preset.Dx11, pins);
            Assert.True(preflight.Failed && Fixture.HasAny(preflight, $"(PID {process.Id}) is running"), preflight.ToLog("preflight"));
            var install = Work.Install(game, src, Preset.Dx11, pins);
            Assert.True(install.Failed, install.ToLog("install"));
            Assert.False(File.Exists(Path.Combine(game, Work.AddonName)), "nothing goes in");
        }
        finally
        {
            process.Kill();
            process.WaitForExit();
        }
        Assert.Empty(Work.RunningFrom(game, Preset.Dx11));
    }

    /// <summary>This app is never "the game": run from inside the game folder, or holding a file there itself, it would
    /// refuse every install and offer to end itself.</summary>
    [Fact]
    public void ThisProcessIsNeverOneHoldingTheFolder()
    {
        var file = Path.Combine(Fixture.Temp("running-self"), "held.exe");
        File.WriteAllText(file, "x");
        using var held = new FileStream(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.DoesNotContain(Engine.ProcessesHolding([file]), p => p.Pid == Environment.ProcessId);
    }

    /// <summary>A log the game still writes is not a file of ours left installed: it goes with the next uninstall.</summary>
    [Fact]
    public void ALogTheGameHoldsOpenIsAWarningOnUninstall()
    {
        var game = Fixture.Temp("running-log");
        var (src, pins) = Fixture.Payloads("running-log");
        File.WriteAllBytes(Path.Combine(game, "Game.exe"), Fixture.PeWithImports(true, ["d3d11.dll"]));
        Assert.False(Work.Install(game, src, Preset.Dx11, pins).Failed);
        var log = Path.Combine(game, "amd-nr.log");
        File.WriteAllText(log, "frame 1 processed\n");
        using (new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = Work.Uninstall(game, Preset.Dx11);
            Assert.False(report.Failed, report.ToLog("uninstall"));
            Assert.True(Fixture.HasAny(report, "amd-nr.log is still open"), report.ToLog("uninstall"));
        }
    }
}
