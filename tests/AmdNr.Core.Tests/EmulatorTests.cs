using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class EmulatorTests
{
    private static string Folder(string tag, params string[] executables)
    {
        var dir = Fixture.Temp($"emu-{tag}");
        foreach (var name in executables)
            File.WriteAllBytes(Path.Combine(dir, name), Fixture.Pe(x64: true));
        return dir;
    }

    [Fact]
    public void EachKnownEmulatorIsFoundByEveryNameItShipsUnder()
    {
        foreach (var emulator in Emulators.Known)
        {
            foreach (var exe in emulator.Executables)
            {
                var dir = Folder($"{emulator.Id}-{exe}", exe);
                var found = Emulators.Identify(dir);
                Assert.True(found is not null, $"{exe} was not recognised as anything");
                Assert.Equal(emulator.Id, found!.Id);
                Assert.Equal(Path.Combine(dir, exe), Emulators.ExecutableIn(dir, found));
            }
        }
    }

    [Fact]
    public void AFolderWithNoEmulatorInItIsNotOne()
    {
        Assert.Null(Emulators.Identify(Folder("plain", "Game.exe")));
        Assert.Null(Emulators.Identify(Fixture.Temp("emu-empty")));
        Assert.Null(Emulators.Identify(Path.Combine(Fixture.Temp("emu-missing"), "nope")));
    }

    /// <summary>The bug this guards: an emulator's executable links every renderer it can be set to,
    /// so the route read from the import table was whichever came first -- and the PCSX2 and RPCS3
    /// routes were being overwritten with D3D11 the moment detection ran.
    ///
    /// The executables here import exactly what the real ones do, which is what makes this a
    /// reproduction and not just an assertion: without the emulator branch, PCSX2 reads as a plain
    /// D3D11 game and RPCS3 as a plain Vulkan one.</summary>
    [Fact]
    public void AnEmulatorKeepsItsOwnRouteInsteadOfTheOneItsImportsSuggest()
    {
        // The same bytes under two names. A plain game with these imports is a D3D11 game, and that
        // is exactly the answer PCSX2 used to get.
        var everyRenderer = Fixture.PeWithImports(true, ["d3d11.dll", "d3d12.dll", "vulkan-1.dll", "opengl32.dll"]);

        var plain = Fixture.Temp("emu-plain-imports");
        File.WriteAllBytes(Path.Combine(plain, "Game.exe"), everyRenderer);
        var blind = GraphicsDetector.Detect(plain);
        Assert.Null(blind.Emulator);
        // Whatever the import table picks here, it is a plain game route and never the emulator's.
        Assert.NotNull(blind.Preset);
        Assert.NotEqual(Preset.Pcsx2, blind.Preset);

        var pcsx2 = Fixture.Temp("emu-pcsx2-imports");
        File.WriteAllBytes(Path.Combine(pcsx2, "pcsx2-qt.exe"), everyRenderer);

        var detection = GraphicsDetector.Detect(pcsx2);
        Assert.Equal("pcsx2", detection.Emulator?.Id);
        Assert.Equal(Preset.Pcsx2, detection.Preset);
        Assert.Equal(Preset.Pcsx2, GameScanner.GuessPreset(pcsx2));

        var rpcs3 = Fixture.Temp("emu-rpcs3-imports");
        File.WriteAllBytes(Path.Combine(rpcs3, "rpcs3.exe"),
            Fixture.PeWithImports(true, ["vulkan-1.dll", "opengl32.dll"]));
        Assert.Equal(Preset.Rpcs3, GraphicsDetector.Detect(rpcs3).Preset);
        Assert.Equal(Preset.Rpcs3, GameScanner.GuessPreset(rpcs3));
    }

    /// <summary>An emulator with no preset of its own still gets the route its best renderer maps
    /// to, rather than falling back to the generic D3D11 guess.</summary>
    [Fact]
    public void AnEmulatorWithoutItsOwnPresetStillRoutesByItsBestRenderer()
    {
        Assert.Equal(Preset.Dx11, GraphicsDetector.Detect(Folder("dolphin", "Dolphin.exe")).Preset);
        Assert.Equal(Preset.Dx12, GraphicsDetector.Detect(Folder("xenia", "xenia.exe")).Preset);
        Assert.Equal(Preset.Vulkan, GraphicsDetector.Detect(Folder("cemu", "Cemu.exe")).Preset);

        // xemu renders with OpenGL and nothing else, which used to be a dead end and is now the
        // OpenGL route.
        Assert.Equal(Preset.OpenGL, GraphicsDetector.Detect(Folder("xemu", "xemu.exe")).Preset);
    }

    /// <summary>PCSX2 alone ships under five names. Warning that pcsx2-qt.exe is missing next to a
    /// working pcsx2x64-avx2.exe is the kind of noise that teaches people to ignore warnings.</summary>
    [Fact]
    public void ThePreflightAcceptsAnyBuildNameOfTheSameEmulator()
    {
        var (src, pins) = Fixture.Payloads("emu-preflight");
        foreach (var exe in Emulators.ById("pcsx2")!.Executables)
        {
            var dir = Folder($"pcsx2-preflight-{exe}", exe);
            var report = Work.Preflight(dir, src, Preset.Pcsx2, pins);
            Assert.True(Fixture.HasAny(report, "this is the right folder"), report.ToLog(exe));
            Assert.False(Fixture.HasAny(report, "is not in this folder"), report.ToLog(exe));
        }
    }

    [Fact]
    public void EveryEmulatorInTheTableIsUsableAsWritten()
    {
        foreach (var e in Emulators.Known)
        {
            Assert.NotEmpty(e.Executables);
            Assert.NotEmpty(e.Renderers);
            Assert.Contains(e.Best, e.Renderers);
            Assert.False(string.IsNullOrWhiteSpace(e.Setting), $"{e.Id} has no guidance");
            Assert.Equal(e.Id, e.Id.ToLowerInvariant());
            // A route of its own has to be one the engine actually knows about as an emulator route.
            if (e.Route is { } route) Assert.True(route is Preset.Pcsx2 or Preset.Rpcs3);
        }
        Assert.Equal(Emulators.Known.Length, Emulators.Known.Select(e => e.Id).Distinct().Count());
    }
}
