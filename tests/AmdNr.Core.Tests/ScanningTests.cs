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
    public void AFolderIsCalledInstalledOnceTheAddOnOrAManifestIsInIt()
    {
        var folder = Fixture.Temp("installed-state");
        Assert.False(GameScanner.IsInstalled(folder));

        File.WriteAllText(Path.Combine(folder, Work.AddonName), "x");
        Assert.True(GameScanner.IsInstalled(folder));
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
