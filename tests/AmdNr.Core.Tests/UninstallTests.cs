using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class UninstallTests
{
    /// <summary>The x64 route has always swept by name when there is no manifest. The x86 route
    /// reported "No install manifest" as an error for the same situation -- a folder nothing was
    /// installed into, or one an older build wrote -- which read as a failure.</summary>
    [Fact]
    public void TheX86RouteSweepsByNameWhenThereIsNoManifestInsteadOfFailing()
    {
        var clean = Fixture.Temp("x86-nothing-here");
        var nothing = Work.Uninstall(clean, Preset.X86Dx11);
        Assert.False(nothing.Failed, nothing.ToLog("x86 clean"));
        Assert.True(Fixture.HasAny(nothing, "Nothing of ours"), nothing.ToLog("x86 clean"));

        var legacy = Fixture.Temp("x86-legacy");
        foreach (var name in new[] { Work.Addon32Name, Work.Host64Name, Work.RuntimeName, Work.WeightsName })
            File.WriteAllText(Path.Combine(legacy, name), "from an older installer");
        Assert.False(File.Exists(Path.Combine(legacy, Route.X86.ManifestFileName())));

        var report = Work.Uninstall(legacy, Preset.X86Dx11);
        Assert.False(report.Failed, report.ToLog("x86 legacy"));
        foreach (var name in new[] { Work.Addon32Name, Work.Host64Name, Work.RuntimeName, Work.WeightsName })
            Assert.False(File.Exists(Path.Combine(legacy, name)), $"{name} survived an x86 legacy uninstall");
        Assert.False(GameScanner.IsInstalled(legacy));
    }

    [Fact]
    public void AnInstallFromBeforeTheManifestExistedCanStillBeUninstalled()
    {
        var game = Fixture.Temp("legacy");
        foreach (var name in new[] { Work.AddonName, Work.RuntimeName, Work.WeightsName })
            File.WriteAllText(Path.Combine(game, name), "from an older installer");
        File.WriteAllText(Path.Combine(game, "amd-nr.ini"), "[amd-nr]\r\nStartOn=1\r\n");
        Directory.CreateDirectory(Path.Combine(game, "dlss5-runtime"));
        Assert.False(File.Exists(Path.Combine(game, Route.X64.ManifestFileName())));

        var report = Work.Uninstall(game, Preset.Dx11);
        Assert.False(report.Failed, report.ToLog("legacy"));
        foreach (var name in new[] { Work.AddonName, Work.RuntimeName, Work.WeightsName })
            Assert.False(File.Exists(Path.Combine(game, name)), $"{name} survived a legacy uninstall");
        Assert.False(Directory.Exists(Path.Combine(game, "dlss5-runtime")));
        Assert.True(File.Exists(Path.Combine(game, "amd-nr.ini")), "the ini is still the user's");
    }

    [Fact]
    public void TheTwoRoutesDoNotMistakeEachOtherForTheSameInstall()
    {
        var game = Fixture.Temp("both-routes");
        var (src, pins) = Fixture.Payloads("both-routes");
        Assert.False(Work.Install(game, src, Preset.Dx11, pins).Failed);

        // An x86 install into the same folder must journal separately rather than collide.
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["amd-nr.addon32"] = Fixture.Pe(false),
        };
        Transaction.Apply(game, "D3D11", Route.X86, files, []);

        Assert.True(File.Exists(Path.Combine(game, Route.X64.ManifestFileName())));
        Assert.True(File.Exists(Path.Combine(game, Engine.ManifestName)));
    }

    [Fact]
    public void UninstallTakesBackWhatInstallPutThereAndKeepsTheIni()
    {
        var game = Fixture.Temp("round-trip");
        var (src, pins) = Fixture.Payloads("round-trip");
        Assert.False(Work.Install(game, src, Preset.Dx11, pins).Failed);
        Assert.True(File.Exists(Path.Combine(game, Work.AddonName)));

        File.WriteAllText(Path.Combine(game, "amd-nr.ini"), "[amd-nr]\r\nScale=0.75\r\n");
        Directory.CreateDirectory(Path.Combine(game, "dlss5-runtime"));

        var report = Work.Uninstall(game, Preset.Dx11);
        Assert.False(report.Failed, report.ToLog("uninstall"));
        Assert.False(File.Exists(Path.Combine(game, Work.AddonName)));
        Assert.False(Directory.Exists(Path.Combine(game, "dlss5-runtime")));
        Assert.True(File.Exists(Path.Combine(game, "amd-nr.ini")), "the ini is the user's, not ours");

        // What the window reads to draw the badge. Removing the files was never the broken half:
        // this is, because uninstall keeps the manifest to hold the preserved ini entries.
        Assert.False(GameScanner.IsInstalled(game),
            "the folder must stop reporting itself installed once uninstall has run");
    }

    /// <summary>What uninstall keeps -- the configuration, in a manifest that still names the preset
    /// it came from -- is not an install, and must not stop another one. It did: a folder that had
    /// OptiScaler taken out refused every ReShade route with "Uninstall previous preset before
    /// changing API", with nothing left to uninstall and no Uninstall button to press.</summary>
    [Fact]
    public void WhatUninstallKeepsDoesNotStopAnotherRouteOrApiFromInstalling()
    {
        var game = Fixture.Temp("route-after-route");
        File.WriteAllBytes(Path.Combine(game, "game.exe"), Fixture.Pe(true));
        var (optiSrc, optiPins) = OptiScalerRouteTests.Payloads("route-after-route");
        Assert.False(Work.Install(game, optiSrc, Preset.OptiScaler, optiPins).Failed);
        Assert.False(Work.Uninstall(game, Preset.OptiScaler).Failed);
        Assert.True(File.Exists(Path.Combine(game, Work.OptiScalerIni)), "OptiScaler.ini is kept as configuration");

        var (src, pins) = Fixture.Payloads("route-after-route");
        var dx11 = Work.Install(game, src, Preset.Dx11, pins);
        Assert.False(dx11.Failed, dx11.ToLog("ReShade after OptiScaler"));
        Assert.Equal(RouteFamily.ReShade, GameScanner.InstalledAs(game));

        // Inside one route too: D3D11 taken out, D3D12 put in.
        Assert.False(Work.Uninstall(game, Preset.Dx11).Failed);
        var dx12 = Work.Install(game, src, Preset.Dx12, pins);
        Assert.False(dx12.Failed, dx12.ToLog("D3D12 after D3D11"));

        // A live install is still not changed under itself.
        Assert.True(Work.Install(game, src, Preset.Dx11, pins).Failed);
    }

    /// <summary>The other way a folder stays installed after an uninstall, and the one that brought
    /// the complaint back: a file that no longer hashes to what the install wrote belongs to whoever
    /// changed it, so the transaction keeps it -- and the badge, which is only "is one of our files
    /// in here", goes on saying installed. Keeping it is right; saying "Removed" over the top of it
    /// was not, and there has to be a way to take it out when that is what was meant.</summary>
    [Fact]
    public void AFileChangedAfterTheInstallIsKeptUntilTheUninstallIsForced()
    {
        var game = Fixture.Temp("modified");
        var (src, pins) = Fixture.Payloads("modified");
        Assert.False(Work.Install(game, src, Preset.Dx11, pins).Failed);

        // What actually happened on this machine: the add-on in the game folder was replaced by
        // hand with a locally built one, so its hash stopped being the manifest's.
        var addon = Path.Combine(game, Work.AddonName);
        File.WriteAllBytes(addon, [.. File.ReadAllBytes(addon), 0x00, 0x99]);
        File.WriteAllText(Path.Combine(game, "amd-nr.ini"), "[amd-nr]\r\nScale=0.75\r\n");

        var kept = Work.Uninstall(game, Preset.Dx11);
        Assert.False(kept.Failed, kept.ToLog("uninstall"));
        Assert.True(File.Exists(addon), "a file somebody else changed is not deleted behind their back");
        Assert.True(GameScanner.IsInstalled(game), "so the folder is still installed, and has to keep saying so");

        var forced = Work.Uninstall(game, Preset.Dx11, force: true);
        Assert.False(forced.Failed, forced.ToLog("forced"));
        Assert.False(File.Exists(addon));
        Assert.False(GameScanner.IsInstalled(game), "forcing it is what makes the badge go out");
        Assert.True(File.Exists(Path.Combine(game, "amd-nr.ini")),
            "forcing takes back our files, never the tuning: that has its own switch");
    }

    /// <summary>The one that cost a real folder. A DLL the running game still has open cannot be
    /// deleted; the uninstall used to swallow that, log REMOVED, and drop the entry -- so the file
    /// stayed, the report said it had gone, and every later uninstall found an empty manifest and
    /// nothing to do. It has to say so, keep owning the file, and finish once the game is closed.
    /// </summary>
    [Fact]
    public void AFileTheGameStillHasOpenIsKeptRatherThanReportedRemoved()
    {
        var game = Fixture.Temp("in-use");
        var (src, pins) = Fixture.Payloads("in-use");
        Assert.False(Work.Install(game, src, Preset.Dx11, pins).Failed);
        var addon = Path.Combine(game, Work.AddonName);

        // FileShare.Read and not None: a DLL mapped into a running game can still be read, which
        // is what lets the uninstall hash it and decide it is ours. It just cannot be deleted.
        Report held;
        using (var open = new FileStream(addon, FileMode.Open, FileAccess.Read, FileShare.Read))
            held = Work.Uninstall(game, Preset.Dx11);

        Assert.False(held.Failed, held.ToLog("in use"));
        Assert.True(File.Exists(addon), "it could not be deleted, so it is still there");
        Assert.False(Fixture.HasAny(held, $"removed {Work.AddonName}"),
            "a delete that did not happen must never be reported as one");
        Assert.True(Fixture.HasAny(held, Transaction.StillOpen), held.ToLog("in use"));
        Assert.True(GameScanner.IsInstalled(game));

        // Game closed. The entry is still in the manifest, so this finishes what the first one could
        // not -- which is exactly what dropping the entry used to make impossible.
        var after = Work.Uninstall(game, Preset.Dx11);
        Assert.False(after.Failed, after.ToLog("after"));
        Assert.False(File.Exists(addon));
        Assert.False(GameScanner.IsInstalled(game));
    }

    [Fact]
    public void UninstallOnAnUnrelatedFolderSaysSoRatherThanFailing()
    {
        var report = Work.Uninstall(Fixture.Temp("unrelated"), Preset.Dx11);
        Assert.False(report.Failed);
        Assert.True(Fixture.HasAny(report, "Nothing of ours"), report.ToLog("unrelated"));
    }

    [Fact]
    public void UninstallWithNoFolderSaysWhichFieldIsEmpty()
    {
        var report = Work.Uninstall("", Preset.Dx11);
        Assert.True(report.Failed);
        Assert.True(Fixture.HasErr(report, "No game folder given"));
    }
}
