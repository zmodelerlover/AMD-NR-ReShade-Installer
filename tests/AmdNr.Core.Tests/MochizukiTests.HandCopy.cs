using System.Text;
using AmdNr.Core;
namespace AmdNr.Core.Tests;

// Mochizuki copied into a game folder by hand before this app installed it (MochizukiTests.cs).
public partial class MochizukiTests
{
    /// <summary>A game folder with mochizuki 0.4.0 copied in by hand, as OptiScaler's own notes say to.</summary>
    private static string HandCopied(string label)
    {
        var game = Game(label);
        foreach (var (path, destination) in MochizukiPaths.Zip(Destinations).Append((ModelPath, ModelPath)))
        {
            var full = Path.Combine(game, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Content(path, path == ModelPath ? "model" : "0.4.0"));
        }
        return game;
    }

    [Fact]
    public void OverAHandCopyThePrewarmListTheRuntimeRewroteDoesNotStopTheNextInstall()
    {
        var game = HandCopied("hand");
        var (src, pins) = Staged("hand");
        var first = Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true);
        Assert.False(first.Failed, first.ToLog("install"));
        var list = Path.Combine(game, "dlssnr-amd", "prewarm", "manifest.txt");
        var manifest = Path.Combine(game, Route.X64.ManifestFileName());
        Assert.Contains(Manifest.Decode(File.ReadAllText(manifest)).Entries, e => e.Name == "dlssnr-amd/prewarm/manifest.txt" && !e.Owned);

        // The runtime makes the list again for this driver: the same build keeps it...
        File.WriteAllText(list, "mochizuki-prewarm 1\nrewritten for this driver\n");
        var again = Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true);
        Assert.False(again.Failed, again.ToLog("reinstall"));
        Assert.Equal("mochizuki-prewarm 1\nrewritten for this driver\n", File.ReadAllText(list));

        // ...and a newer one replaces it, and from then on the list is this app's.
        var (newer, newerPins) = Staged("hand-newer", "0.4.1");
        var update = Work.Install(game, newer, Preset.OptiScaler, newerPins, mochizuki: true);
        Assert.False(update.Failed, update.ToLog("update"));
        Assert.Equal(Content("dlssnr-amd.prewarm/manifest.txt", "0.4.1"), File.ReadAllText(list));
        Assert.Contains(Manifest.Decode(File.ReadAllText(manifest)).Entries,
            e => e.Name == "dlssnr-amd/prewarm/manifest.txt" && e.Owned && e.Backup.Length == 0);

        // Uninstall puts the hand copy back where it was replaced; the list, which the runtime makes
        // again on its first start, goes.
        var gone = Work.Uninstall(game, Preset.OptiScaler, removeConfig: true);
        Assert.False(gone.Failed, gone.ToLog("uninstall"));
        Assert.Equal(Content("MochizukiNrRuntime.dll", "0.4.0"), File.ReadAllText(Path.Combine(game, "MochizukiNrRuntime.dll")));
        Assert.Equal(Content("dlssnr-amd.shaders/g_attn.spv", "0.4.0"),
            File.ReadAllText(Path.Combine(game, "dlssnr-amd", "shaders", "g_attn.spv")));
        Assert.False(File.Exists(list));
    }

    [Fact]
    public void UntickedOverAHandCopyTheCopyStaysAndOnlyTheRecordGoes()
    {
        var game = HandCopied("hand-untick");
        var (src, pins) = Staged("hand-untick");
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true).Failed);

        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install unticked"));
        Assert.True(Fixture.HasAny(report, "this app did not put it there"), report.ToLog("install unticked"));
        foreach (var (path, destination) in MochizukiPaths.Zip(Destinations))
            Assert.Equal(Content(path, "0.4.0"), File.ReadAllText(Path.Combine(game, destination)));
        var m = Manifest.Decode(File.ReadAllText(Path.Combine(game, Route.X64.ManifestFileName())));
        Assert.DoesNotContain(m.Entries, e => e.Name.StartsWith("dlssnr-amd", StringComparison.Ordinal) || e.Name == "MochizukiNrRuntime.dll");
        Assert.False(Work.HasMochizuki(game));
    }
}
