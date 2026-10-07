using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>danielblnc 0.6.0 run inline (AmdAsync=auto) on an RX 9070 XT stalled the GPU for seconds until the driver
/// reset it. The sheet says so when that build is picked on OptiScaler, and the problem report flags it in the folder.</summary>
public class InstallStateTests
{
    private static string Folder(string tag, string backend, string async)
    {
        var dir = Fixture.Temp(tag);
        File.WriteAllText(Path.Combine(dir, Work.OptiScalerIni), $"[DlssNr]\nNrBackend = {backend}\nAmdAsync = {async}\n");
        return dir;
    }

    [Fact]
    public void AnUnstableRuntimeInlineOnAnRx9000IsFlagged()
    {
        var inline = Folder("state-inline", "daniel", "auto");
        Assert.Contains("unstable runtime inline", InstallState.OptiNr(inline, "0.6.0", rdna4: true));
        Assert.DoesNotContain("unstable", InstallState.OptiNr(inline, Work.Rdna4Recommended, rdna4: true));
        Assert.DoesNotContain("unstable", InstallState.OptiNr(inline, "0.6.0", rdna4: false));
        Assert.DoesNotContain("unstable", InstallState.OptiNr(Folder("state-async", "daniel", "true"), "0.6.0", rdna4: true));
        Assert.DoesNotContain("unstable", InstallState.OptiNr(Folder("state-lmxxf", "lmxxf", "auto"), "0.6.0", rdna4: true));
        Assert.Null(InstallState.OptiNr(Fixture.Temp("state-none"), "0.6.0", rdna4: true));

        var lines = InstallState.Lines(inline, null, true);
        Assert.Contains(lines, l => l.Contains("NrBackend=daniel, AmdAsync=auto (inline)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheSheetWarnsOfAnUnstableBuildOnlyOnAnRx9000()
    {
        Assert.Contains("DEVICE_HUNG", Work.UnstableOnRdna4("0.6.0", rdna4: true));
        Assert.Null(Work.UnstableOnRdna4(Work.Rdna4Recommended, rdna4: true));
        Assert.Null(Work.UnstableOnRdna4("0.6.0", rdna4: false));
        Assert.Null(Work.UnstableOnRdna4("", rdna4: true));
    }
}
