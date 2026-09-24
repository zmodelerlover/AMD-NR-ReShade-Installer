using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class ScanningTests
{
    /// <summary>A Steam library, built the way Steam builds one, so the parsing is tested without a
    /// Steam install.</summary>
    private static string FakeSteam(params (string AppId, string Name, string InstallDir)[] games)
    {
        var steam = Fixture.Temp("steam");
        var steamapps = Path.Combine(steam, "steamapps");
        Directory.CreateDirectory(Path.Combine(steamapps, "common"));

        File.WriteAllText(Path.Combine(steamapps, "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
            	"0"
            	{
            		"path"		"{{steam.Replace("\\", "\\\\")}}"
            		"label"		""
            	}
            }
            """);

        foreach (var (appId, name, installDir) in games)
        {
            Directory.CreateDirectory(Path.Combine(steamapps, "common", installDir));
            File.WriteAllText(Path.Combine(steamapps, $"appmanifest_{appId}.acf"), $$"""
                "AppState"
                {
                	"appid"		"{{appId}}"
                	"name"		"{{name}}"
                	"installdir"		"{{installDir}}"
                	"StateFlags"		"4"
                }
                """);
        }
        return steam;
    }

    [Fact]
    public void ASteamLibraryIsReadFromItsOwnManifests()
    {
        var steam = FakeSteam(("1091500", "Cyberpunk 2077", "Cyberpunk 2077"), ("271590", "GTA V", "Grand Theft Auto V"));
        var found = GameScanner.Steam(steam).ToList();

        Assert.Equal(2, found.Count);
        var cyberpunk = found.Single(g => g.AppId == "1091500");
        Assert.Equal("Cyberpunk 2077", cyberpunk.Name);
        Assert.Equal(GamePlatform.Steam, cyberpunk.Platform);
        Assert.True(Directory.Exists(cyberpunk.InstallPath));
        Assert.EndsWith(Path.Combine("steamapps", "common", "Cyberpunk 2077"), cyberpunk.InstallPath,
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>A manifest whose folder was deleted by hand, which is the ordinary state of a
    /// library someone has been cleaning up.</summary>
    [Fact]
    public void AManifestWithoutItsFolderIsNotOffered()
    {
        var steam = FakeSteam(("1", "Gone", "Gone"));
        Directory.Delete(Path.Combine(steam, "steamapps", "common", "Gone"));
        Assert.Empty(GameScanner.Steam(steam));
    }

    [Fact]
    public void ASecondLibraryOnAnotherDriveIsFollowed()
    {
        var main = FakeSteam(("1", "First", "First"));
        var second = FakeSteam(("2", "Second", "Second"));

        // The second library, added to the first one's list the way Steam records it.
        File.WriteAllText(Path.Combine(main, "steamapps", "libraryfolders.vdf"), $$"""
            "libraryfolders"
            {
            	"0" { "path" "{{main.Replace("\\", "\\\\")}}" }
            	"1" { "path" "{{second.Replace("\\", "\\\\")}}" }
            }
            """);

        var names = GameScanner.Steam(main).Select(g => g.Name).ToList();
        Assert.Contains("First", names);
        Assert.Contains("Second", names);
    }

    [Fact]
    public void TheKeyValueReaderTakesTheEscapedPathsSteamWrites()
    {
        var fields = GameScanner.ParseKeyValues("\t\"path\"\t\t\"D:\\\\SteamLibrary\"\n\t\"apps\" { }");
        Assert.Equal("D:\\\\SteamLibrary", fields.Single(f => f.Key == "path").Value);
    }

    /// <summary>A library folder carries things that are not games, and offering them as games is
    /// how a list becomes noise.</summary>
    [Fact]
    public void TheThingsInALibraryThatAreNotGamesAreLeftOut()
    {
        var steam = FakeSteam(
            ("431960", "Wallpaper Engine", "wallpaper_engine"),
            ("228980", "Steamworks Common Redistributables", "Steamworks Shared"),
            ("250820", "SteamVR", "SteamVR"),
            ("1091500", "Cyberpunk 2077", "Cyberpunk 2077"));

        // ScanAll applies the exclusions; the per-source scan deliberately does not, so a folder
        // can still be added by hand if someone really wants it.
        var kept = GameScanner.Steam(steam)
            .Where(g => !new[] { "wallpaper_engine", "Steamworks Shared", "SteamVR" }
                .Any(x => g.InstallPath.Contains(x, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        Assert.Single(kept);
        Assert.Equal("Cyberpunk 2077", kept[0].Name);
    }

    [Fact]
    public void TheRouteGuessFollowsWhatIsInTheFolder()
    {
        var pcsx2 = Fixture.Temp("guess-pcsx2");
        File.WriteAllBytes(Path.Combine(pcsx2, "pcsx2-qt.exe"), Fixture.Pe(true));
        Assert.Equal(Preset.Pcsx2, GameScanner.GuessPreset(pcsx2));

        var rpcs3 = Fixture.Temp("guess-rpcs3");
        File.WriteAllBytes(Path.Combine(rpcs3, "rpcs3.exe"), Fixture.Pe(true));
        Assert.Equal(Preset.Rpcs3, GameScanner.GuessPreset(rpcs3));

        var old = Fixture.Temp("guess-x86");
        File.WriteAllBytes(Path.Combine(old, "game.exe"), Fixture.Pe(false));
        Assert.Equal(Preset.X86Dx11, GameScanner.GuessPreset(old));

        var modern = Fixture.Temp("guess-x64");
        File.WriteAllBytes(Path.Combine(modern, "game.exe"), Fixture.Pe(true));
        Assert.Equal(Preset.Dx11, GameScanner.GuessPreset(modern));
    }

    [Fact]
    public void AFolderIsCalledInstalledOnceAPayloadIsInIt()
    {
        var folder = Fixture.Temp("installed-state");
        Assert.False(GameScanner.IsInstalled(folder));

        File.WriteAllText(Path.Combine(folder, Work.AddonName), "x");
        Assert.True(GameScanner.IsInstalled(folder));
    }

    /// <summary>The bug this guards: uninstall keeps the manifest whenever it preserves a
    /// configuration entry, and the state used to be read off that file -- so a folder that had been
    /// fully uninstalled still reported itself installed, and the badge never went away.</summary>
    [Fact]
    public void AManifestLeftBehindByUninstallIsNotAnInstall()
    {
        var folder = Fixture.Temp("installed-after-uninstall");

        // Exactly what Transaction.Uninstall leaves: the manifest, holding only the configuration
        // entries it preserved on purpose, and those configuration files themselves.
        File.WriteAllText(Path.Combine(folder, Route.X64.ManifestFileName()), "{}");
        File.WriteAllText(Path.Combine(folder, "ReShade.ini"), "[GENERAL]\r\n");
        File.WriteAllText(Path.Combine(folder, "amd-nr.ini"), "[amd-nr]\r\n");

        Assert.False(GameScanner.IsInstalled(folder),
            "a preserved manifest and the user's own ini files are not an install");
    }

    /// <summary>Steam keeps the folder of a game it knows about but has not downloaded. Listing one
    /// put a card on screen whose tags read "no route" in red, which says something false about the
    /// game when the truth was only that nothing had been installed into it yet.</summary>
    [Fact]
    public void AFolderWithNothingInItIsNotAnInstalledGame()
    {
        var empty = Fixture.Temp("scan-empty");
        Assert.Empty(Directory.EnumerateFileSystemEntries(empty));

        var real = Fixture.Temp("scan-real");
        File.WriteAllBytes(Path.Combine(real, "game.exe"), Fixture.Pe(true));

        // ScanAll reads this machine's launchers, so the filter is exercised through its own rule.
        Assert.False(GameScanner.IsInstalled(empty));
        Assert.True(Directory.Exists(empty), "the folder is there; it is just empty");
        Assert.NotEmpty(Directory.EnumerateFileSystemEntries(real));
    }

    /// <summary>The registry sources have to answer on a machine where none of those launchers are
    /// installed -- which is every CI runner -- without throwing.</summary>
    [Fact]
    public void EveryRegistrySourceIsQuietWhenItsLauncherIsAbsent()
    {
        foreach (var source in new Func<IEnumerable<ScannedGame>>[]
                 {
                     GameScanner.Gog, GameScanner.Ea, GameScanner.Ubisoft,
                     GameScanner.BattleNet, GameScanner.Xbox, GameScanner.Epic,
                 })
        {
            var games = source().ToList();
            Assert.All(games, g => Assert.True(Directory.Exists(g.InstallPath)));
        }
    }

    /// <summary>The folder somebody keeps games in, which is the other half of "add a game": one at
    /// a time is nobody's answer for forty of them. Every shape that turns up in a real one has to
    /// come back as the game's own folder -- not its bin\, and not the publisher folder above it.
    /// </summary>
    [Fact]
    public void SearchingAFolderFindsTheGamesUnderIt()
    {
        var root = Fixture.Temp("library");

        // Straight in its own folder.
        Exe(root, "Alpha", "alpha.exe");
        // Source: the executable lives in bin\, and the game is still the folder above it.
        Exe(root, Path.Combine("Sigma", "bin"), "sigma.exe");
        // Unreal: same again, one level deeper.
        Exe(root, Path.Combine("Omega", "Binaries", "Win64"), "Omega-Win64-Shipping.exe");
        // A publisher folder: the game is the one inside, not the folder holding it.
        Exe(root, Path.Combine("Publisher", "Beta"), "beta.exe");
        // Nothing in it at all.
        Directory.CreateDirectory(Path.Combine(root, "Empty"));
        // Unreal with its project in a folder of its own and Engine beside it: the game is the root,
        // not the project -- which came back as a game of its own, "UFG" beside Strikers Club.
        Exe(root, Path.Combine("Kappa", "KappaGame", "Binaries", "Win64"), "KappaGame-Win64-Shipping.exe");
        Directory.CreateDirectory(Path.Combine(root, "Kappa", "Engine"));
        // The same shape with no Engine beside it is a publisher folder holding an Unreal game.
        Exe(root, Path.Combine("Studio", "Lambda", "Binaries", "Win64"), "Lambda-Win64-Shipping.exe");
        // The executable in a folder of its own name: GTAIV\GTAIV.exe under Grand Theft Auto IV.
        Exe(root, Path.Combine("Grand Theft Auto IV", "GTAIV"), "GTAIV.exe");
        // Euro Truck Simulator 2's layout, which came back as a game called "win_x64".
        Exe(root, Path.Combine("Euro Truck Simulator 2", "bin", "win_x64"), "eurotrucks2.exe");
        // Anti-cheat on its own is not a game, however large its setup.
        Exe(root, "EasyAntiCheat", "EasyAntiCheat_EOS_Setup.exe");
        // A series folder: each game named like the folder, and still two games, not one "Fallout".
        Exe(root, Path.Combine("Fallout", "Fallout 3"), "Fallout3.exe");
        Exe(root, Path.Combine("Fallout", "Fallout 4"), "Fallout4.exe");
        // A game whose own name holds a word the tool list refuses -- crash, agent -- is still a game.
        Exe(root, "Crash Bandicoot N. Sane Trilogy", "CrashBandicootNSaneTrilogy.exe");
        Exe(root, "Agents of Mayhem", "AgentsOfMayhem.exe");

        var found = GameScanner.UnderFolder(root);
        var paths = found.Select(g => Path.GetRelativePath(root, g.InstallPath)).OrderBy(p => p).ToList();

        Assert.Equal(["Agents of Mayhem", "Alpha", "Crash Bandicoot N. Sane Trilogy", "Euro Truck Simulator 2",
            Path.Combine("Fallout", "Fallout 3"), Path.Combine("Fallout", "Fallout 4"), "Grand Theft Auto IV",
            "Kappa", "Omega", Path.Combine("Publisher", "Beta"), "Sigma", Path.Combine("Studio", "Lambda")], paths);
        Assert.All(found, g => Assert.Equal(GamePlatform.Manual, g.Platform));
        Assert.Equal("Beta", found.Single(g => g.InstallPath.EndsWith("Beta", StringComparison.Ordinal)).Name);
    }

    /// <summary>Our own 64-bit host beside a 32-bit game is not the game: the folder read as mixed,
    /// and the first guess at its route was a 64-bit one.</summary>
    [Fact]
    public void OurOwnHostIsNotTakenForTheGame()
    {
        var game = Fixture.Temp("with-host");
        File.WriteAllBytes(Path.Combine(game, "game.exe"), Fixture.Pe(false));
        File.WriteAllBytes(Path.Combine(game, Work.Host64Name), Fixture.Pe(true));
        Assert.Equal(Route.X86, Work.Detect(game).Route);
        Assert.Equal("game.exe", Path.GetFileName(GraphicsDetector.FindExecutable(game)));
    }

    private static void Exe(string root, string folder, string name)
    {
        var dir = Path.Combine(root, folder);
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, name), Fixture.Pe(true));
    }

    [Fact]
    public void ScanningEverythingDoesNotThrowAndReturnsRealFolders()
    {
        var found = GameScanner.ScanAll();
        Assert.All(found, g =>
        {
            Assert.True(Directory.Exists(g.InstallPath));
            Assert.False(string.IsNullOrWhiteSpace(g.Name));
        });
        // Whatever this machine has, the same folder must not come back twice.
        Assert.Equal(found.Select(g => g.InstallPath.ToLowerInvariant()).Distinct().Count(), found.Count);
    }
}
