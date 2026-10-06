namespace AmdNr.Core.Tests;

/// <summary>danielblnc's version picked per game: the payload's releases.runtime list, what each add-on and
/// OptiScaler version runs, and a pick that holds through a version applied after it.</summary>
public class RuntimeChoiceTests
{
    private const string Patched043 = "f3d9f2e53b775e4870917572f1f87a28c73068a4dc97252d6fb52360ddf8597a";
    private const string Raw043 = "d1e320862a8763ac39e7ce194536d4b6c55ba61bae9e8a92753cec32df67a457";
    private const string Raw060 = "195c4a891b6eac4c1cb7671e10ff62bbbe2b17f1dfae1344dc5a6714e4775721";

    private static PayloadManifest Shipped() =>
        PayloadManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "payload.json")));

    [Fact]
    public void TheShippedListOffersFrom043ToTheNewestNewestFirst()
    {
        Assert.Equal(["0.6.0", "0.5.1", "0.5.0", "0.4.3"], Shipped().RuntimeVersions().Select(r => r.Version));
    }

    [Fact]
    public void APickHoldsOnBothRoutesWhicheverVersionIsAppliedAfterIt()
    {
        var shipped = Shipped();
        foreach (var picked in new[]
                 {
                     shipped.Newest(PayloadManifest.OptiScalerComponent).WithRuntime("0.4.3"),
                     shipped.WithRuntime("0.4.3").Newest(PayloadManifest.OptiScalerComponent),
                 })
        {
            var pins = picked.Pins();
            Assert.True(pins.RuntimeChosen);
            Assert.Equal(Raw043, pins.OptiRuntimeSha);
            Assert.Equal("0.4.3", pins.OptiRuntimeVersion);
            Assert.Equal(Patched043, Engine.Lower(pins.RuntimeSha));
        }

        // No pick, or one the payload does not list, is the version's own runtime.
        var plain = shipped.Newest(PayloadManifest.OptiScalerComponent).Pins();
        Assert.False(plain.RuntimeChosen);
        Assert.Equal(Raw060, plain.OptiRuntimeSha);
        Assert.Equal(Raw060, shipped.Newest(PayloadManifest.OptiScalerComponent).WithRuntime("0.3.9").Pins().OptiRuntimeSha);
    }

    [Fact]
    public void OnlyWhatTheVersionChosenRunsIsOffered()
    {
        var versions = Shipped().RuntimeVersions().ToDictionary(r => r.Version);
        Assert.False(Work.RuntimeRunsOn(versions["0.6.0"], optiScaler: true, "0.4.6-amd-nr"));
        Assert.True(Work.RuntimeRunsOn(versions["0.6.0"], optiScaler: true, "0.4.7-amd-nr"));
        Assert.True(Work.RuntimeRunsOn(versions["0.4.3"], optiScaler: true, "0.4.9-amd-nr"));
        Assert.False(Work.RuntimeRunsOn(versions["0.6.0"], optiScaler: false, "0.7.5"));
        Assert.True(Work.RuntimeRunsOn(versions["0.6.0"], optiScaler: false, "0.7.6"));
        Assert.False(Work.RuntimeRunsOn(versions["0.4.3"], optiScaler: false, "0.6.9"));
        Assert.True(Work.RuntimeRunsOn(versions["0.4.3"], optiScaler: false, "0.7.9"));
    }

    /// <summary>RX 9000: every version, 0.4.3 the default and the recommended one, the rest unstable and async.
    /// Any other card: the newest only, no tag, never async.</summary>
    [Fact]
    public void AnRx9000GetsEveryVersionWith043FirstAndOtherCardsTheNewestOnly()
    {
        var runs = Shipped().RuntimeVersions();
        Assert.Equal(["0.6.0", "0.5.1", "0.5.0", "0.4.3"], Work.RuntimeOffer(runs, rdna4: true).Select(r => r.Version));
        Assert.Equal(["0.6.0"], Work.RuntimeOffer(runs, rdna4: false).Select(r => r.Version));

        Assert.Equal("0.4.3", Work.RuntimePick(Work.RuntimeOffer(runs, true), null, rdna4: true));
        Assert.Equal("0.5.1", Work.RuntimePick(Work.RuntimeOffer(runs, true), "0.5.1", rdna4: true));
        Assert.Null(Work.RuntimePick(Work.RuntimeOffer(runs, false), null, rdna4: false));
        Assert.Null(Work.RuntimePick(Work.RuntimeOffer(runs, false), "0.4.3", rdna4: false));
        // An add-on too old for 0.4.3 keeps its own pin.
        Assert.Null(Work.RuntimeDefault(runs.Where(r => r.Version != "0.4.3").ToList(), rdna4: true));

        Assert.Equal(Work.RuntimeBadge.Recommended, Work.BadgeFor("0.4.3", rdna4: true));
        Assert.Equal(Work.RuntimeBadge.Unstable, Work.BadgeFor("0.6.0", rdna4: true));
        Assert.Equal(Work.RuntimeBadge.None, Work.BadgeFor("0.6.0", rdna4: false));
    }

    /// <summary>A folder on the newest runtime is out of date once its game is pinned to an older one, so the
    /// update puts the pick in; without a pick it is current.</summary>
    [Fact]
    public void AFolderOnTheNewestRuntimeIsOutOfDateForAGamePinnedToAnOlderOne()
    {
        var shipped = Shipped();
        var dir = Fixture.Temp("runtime-choice-opti");
        var manifest = new Manifest(Preset.OptiScaler.ManifestPreset(), Route.X64);
        foreach (var pass in new[] { "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll" })
            manifest.Entries.Add(new Entry { Name = pass, Hash = Raw060, Owned = true });
        Manifest.WriteAtomic(dir, manifest);
        Assert.False(Work.PayloadMovedOn(dir, shipped));
        Assert.True(Work.PayloadMovedOn(dir, shipped.WithRuntime("0.4.3")));
    }
}
