using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>What the library says about a folder it already lists: which route is installed in it,
/// and whether the game is still there at all.</summary>
public class LibraryStateTests
{
    [Fact]
    public void TheRouteThatWroteTheFolderIsTheOneNamed()
    {
        var reshade = Fixture.Temp("as-reshade");
        File.WriteAllBytes(Path.Combine(reshade, "game.exe"), Fixture.Pe(true));
        Assert.Null(GameScanner.InstalledAs(reshade));
        var (src, pins) = Fixture.Payloads("as-reshade");
        Assert.False(Work.Install(reshade, src, Preset.Dx11, pins).Failed);
        Assert.Equal(RouteFamily.ReShade, GameScanner.InstalledAs(reshade));

        var opti = Fixture.Temp("as-opti");
        File.WriteAllBytes(Path.Combine(opti, "game.exe"), Fixture.Pe(true));
        var (optiSrc, optiPins) = OptiScalerRouteTests.Payloads("as-opti");
        Assert.False(Work.Install(opti, optiSrc, Preset.OptiScaler, optiPins).Failed);
        Assert.Equal(RouteFamily.OptiScaler, GameScanner.InstalledAs(opti));

        // Uninstall keeps the manifest for the configuration it preserves, and that is not an install.
        Assert.False(Work.Uninstall(opti, Preset.OptiScaler).Failed);
        Assert.Null(GameScanner.InstalledAs(opti));
    }

    /// <summary>The runtime and the weights deleted by hand, OptiScaler's other files still there: the
    /// install refuses another route until this one is uninstalled, so the sheet has to call it installed
    /// and offer the way out.</summary>
    [Fact]
    public void AnInstallMissingItsRuntimeIsStillOneWhileItsOtherFilesAreThere()
    {
        var game = Fixture.Temp("as-half-deleted");
        File.WriteAllBytes(Path.Combine(game, "game.exe"), Fixture.Pe(true));
        var (optiSrc, optiPins) = OptiScalerRouteTests.Payloads("as-half-deleted");
        Assert.False(Work.Install(game, optiSrc, Preset.OptiScaler, optiPins).Failed);
        foreach (var name in Work.InstalledMarkers)
            File.Delete(Path.Combine(game, name));
        Assert.False(GameScanner.IsInstalled(game));

        Assert.Equal(RouteFamily.OptiScaler, GameScanner.InstalledAs(game));
        var (src, pins) = Fixture.Payloads("as-half-deleted-reshade");
        Assert.True(Fixture.HasAny(Work.Install(game, src, Preset.Dx12, pins), "Uninstall previous preset"));

        var removed = Work.Uninstall(game, Preset.OptiScaler);
        Assert.False(removed.Failed, removed.ToLog("uninstall"));
        Assert.Null(GameScanner.InstalledAs(game));
        var reshade = Work.Install(game, src, Preset.Dx12, pins);
        Assert.False(reshade.Failed, reshade.ToLog("reshade"));
        Assert.Equal(RouteFamily.ReShade, GameScanner.InstalledAs(game));
    }

    /// <summary>No manifest: an install from before it existed, or an OptiScaler another setup put
    /// there. The files still say which one it is.</summary>
    [Fact]
    public void WithoutAManifestTheFilesSayWhichRouteItIs()
    {
        var legacy = Fixture.Temp("as-legacy");
        File.WriteAllText(Path.Combine(legacy, Work.AddonName), "an older install");
        File.WriteAllText(Path.Combine(legacy, Work.RuntimeName), "an older install");
        Assert.Equal(RouteFamily.ReShade, GameScanner.InstalledAs(legacy));

        var foreign = Fixture.Temp("as-foreign-opti");
        foreach (var pass in new[] { "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll" })
            File.WriteAllText(Path.Combine(foreign, pass), "their runtime");
        File.WriteAllText(Path.Combine(foreign, Work.OptiScalerIni), "[DlssNr]");
        Assert.Equal(RouteFamily.OptiScaler, GameScanner.InstalledAs(foreign));
    }

    /// <summary>danielblnc's setup: his runtime as version.dll, and the weights it builds beside it.
    /// Neither is an install of ours, and an uninstall after ours leaves his weights to his runtime.</summary>
    [Fact]
    public void TheAuthorsOwnSetupIsNotAnInstallOfOurs()
    {
        var game = Fixture.Temp("as-author");
        File.WriteAllBytes(Path.Combine(game, "game.exe"), Fixture.Pe(true));
        File.WriteAllText(Path.Combine(game, "version.dll"), "his runtime");
        File.WriteAllText(Path.Combine(game, Work.WeightsName), "his weights");
        Assert.False(GameScanner.IsInstalled(game));
        Assert.Null(GameScanner.InstalledAs(game));

        var (src, pins) = OptiScalerRouteTests.Payloads("as-author");
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins).Failed);
        Assert.Equal(RouteFamily.OptiScaler, GameScanner.InstalledAs(game));

        var report = Work.Uninstall(game, Preset.OptiScaler);
        Assert.False(report.Failed, report.ToLog("author"));
        Assert.True(File.Exists(Path.Combine(game, Work.WeightsName)), report.ToLog("author"));
        Assert.Equal("his weights", File.ReadAllText(Path.Combine(game, Work.WeightsName)));
        Assert.Null(GameScanner.InstalledAs(game));
    }

    [Fact]
    public void AGameThatIsStillThereIsHere()
    {
        var game = Fixture.Temp("presence-here");
        File.WriteAllBytes(Path.Combine(game, "game.exe"), Fixture.Pe(true));
        Assert.Equal(Presence.Here, GameScanner.PresenceOf(game));
    }

    /// <summary>A launcher that uninstalls a game takes what it installed and leaves the rest -- which
    /// is exactly what this app and ReShade put there. That folder is a game that has gone.</summary>
    [Fact]
    public void AFolderHoldingOnlyWhatWeLeftBehindIsAGameThatHasGone()
    {
        var game = Fixture.Temp("presence-uninstalled");
        var exe = Path.Combine(game, "game.exe");
        File.WriteAllBytes(exe, Fixture.Pe(true));
        var (src, pins) = Fixture.Payloads("presence-uninstalled");
        Assert.False(Work.Install(game, src, Preset.Dx11, pins).Failed);
        File.WriteAllText(Path.Combine(game, "dxgi.log"), "ReShade's own log");
        Directory.CreateDirectory(Path.Combine(game, "reshade-shaders", "Shaders"));
        Assert.Equal(Presence.Here, GameScanner.PresenceOf(game));

        File.Delete(exe);
        Assert.Equal(Presence.Gone, GameScanner.PresenceOf(game));

        Directory.Delete(game, recursive: true);
        Assert.Equal(Presence.Gone, GameScanner.PresenceOf(game));
    }

    [Fact]
    public void AnEmptyFolderIsAGameThatHasGone() =>
        Assert.Equal(Presence.Gone, GameScanner.PresenceOf(Fixture.Temp("presence-empty")));

    /// <summary>A game on a drive that is not plugged in is not a game that was uninstalled: taking it
    /// out of the list would lose the route somebody chose for it the day the drive comes back.</summary>
    [Fact]
    public void AGameOnADriveThatIsNotThereIsUnreachableNotGone()
    {
        var missing = "ZYXWVUTSRQPONMLKJ".Select(c => $"{c}:\\").FirstOrDefault(d => !Directory.Exists(d));
        Assert.NotNull(missing);
        Assert.Equal(Presence.Unreachable, GameScanner.PresenceOf(Path.Combine(missing!, "Games", "Something")));
    }

    /// <summary>The drive letter is there and belongs to another disk now: the external SSD is
    /// unplugged and a USB stick took E:. None of the game's path is on it. Taking that for "every
    /// game on E: was uninstalled" dropped all of them, with every route chosen for them.</summary>
    [Fact]
    public void AGameWhoseLibraryIsNotThereEitherIsUnreachableNotGone()
    {
        var root = Fixture.Temp("presence-library");
        var common = Path.Combine(root, "SteamLibrary", "steamapps", "common");
        Directory.CreateDirectory(common);
        // Uninstalled: its library is still there, its own folder is not.
        Assert.Equal(Presence.Gone, GameScanner.PresenceOf(Path.Combine(common, "Some Game")));
        // Another disk under the same letter: nothing on the way to the game is there.
        Assert.Equal(Presence.Unreachable,
            GameScanner.PresenceOf(Path.Combine(root, "OtherLibrary", "steamapps", "common", "Some Game")));
    }
}
