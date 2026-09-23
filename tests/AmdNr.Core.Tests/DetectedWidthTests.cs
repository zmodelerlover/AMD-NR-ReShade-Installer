using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class DetectedWidthTests
{
    [Fact]
    public void AnExecutableNamesItsOwnWidth()
    {
        var dir = Fixture.Temp("detect-exe");
        var x86 = Path.Combine(dir, "old-game.exe");
        var x64 = Path.Combine(dir, "new-game.exe");
        File.WriteAllBytes(x86, Fixture.Pe(false));
        File.WriteAllBytes(x64, Fixture.Pe(true));

        var d = Work.Detect(x86);
        Assert.Equal(Route.X86, d.Route);
        Assert.Contains("old-game.exe", d.Line!, StringComparison.Ordinal);

        Assert.Equal(Route.X64, Work.Detect(x64).Route);
        Assert.Null(Work.Detect("").Route);
    }

    [Fact]
    public void AFolderIsReadThroughTheExecutablesInIt()
    {
        var dir = Fixture.Temp("detect-folder");
        File.WriteAllBytes(Path.Combine(dir, "game.exe"), Fixture.Pe(true));
        File.WriteAllText(Path.Combine(dir, "readme.txt"), "not an executable");
        Assert.Equal(Route.X64, Work.Detect(dir).Route);

        // A 32-bit launcher beside a 64-bit game is ordinary, and guessing between them would be
        // worse than saying so.
        File.WriteAllBytes(Path.Combine(dir, "launcher.exe"), Fixture.Pe(false));
        var mixed = Work.Detect(dir);
        Assert.Null(mixed.Route);
        Assert.True(mixed.IsMixed);
        Assert.Contains("launcher.exe", mixed.Line!, StringComparison.Ordinal);
    }

    [Fact]
    public void AFolderWithNothingToReadStaysUnknown()
    {
        var dir = Fixture.Temp("detect-empty");
        Assert.Null(Work.Detect(dir).Route);
        File.WriteAllText(Path.Combine(dir, "notes.txt"), "x");
        var d = Work.Detect(dir);
        Assert.Null(d.Route);
        Assert.False(d.IsMixed);
    }

    /// <summary>The detected width leads the list and nothing is dropped from it. It used to be a
    /// filter, which is a dead end exactly when the detection is wrong: BeamNG.drive keeps a 32-bit
    /// launcher in the root and the game in Bin64, was read as 32-bit, and then offered the three
    /// routes that cannot work on it and no others.</summary>
    [Fact]
    public void TheOfferedPresetsLeadWithTheDetectedWidthAndHideNothing()
    {
        Assert.Equal(Presets.All.Length, Presets.Offered(Detected.Unknown).Count);

        var x64 = Presets.Offered(Detected.On(Route.X64, string.Empty));
        Assert.Contains(Preset.Pcsx2, x64);
        Assert.Contains(Preset.Vulkan, x64);
        Assert.Equal(Presets.All.Length, x64.Count);
        Assert.All(x64.Take(5), p => Assert.Equal(Route.X64, p.Route()));

        var x86 = Presets.Offered(Detected.On(Route.X86, string.Empty));
        Assert.Equal([Preset.X86Dx11, Preset.X86Dx9, Preset.X86Dx8], x86.Take(3));
        Assert.Equal(Presets.All.Length, x86.Count);
        Assert.Contains(Preset.Dx12, x86);

        // Every preset appears exactly once, whichever width led.
        foreach (var offered in new[] { x64, x86, Presets.Offered(Detected.Unknown) })
            Assert.Equal(Presets.All.Order(), offered.Order());
    }

    /// <summary>A 32-bit launcher in the root with the real game in Bin64 -- BeamNG.drive's layout,
    /// reported as "shows as 32 bit only". The width comes off whichever executable is picked, so
    /// picking the launcher made every route offered afterwards the wrong architecture.</summary>
    [Fact]
    public void ALauncherInTheRootDoesNotMakeASixtyFourBitGameThirtyTwoBit()
    {
        var game = Fixture.Temp("BeamNG.drive");
        File.WriteAllBytes(Path.Combine(game, "BeamNG.drive.exe"), Fixture.Pe(x64: false));
        var bin64 = Directory.CreateDirectory(Path.Combine(game, "Bin64")).FullName;
        var real = Path.Combine(bin64, "BeamNG.drive.x64.exe");
        File.WriteAllBytes(real, Fixture.PeWithImports(x64: true, ["d3d11.dll"]));

        var found = GraphicsDetector.Detect(game, "BeamNG.drive");
        Assert.Equal(real, found.Executable);
        Assert.Equal(Route.X64, found.Width);

        // A 64-bit binary in x64\ that is not the game is not taken instead: a crash handler beside
        // a genuinely 32-bit game would otherwise turn it into a 64-bit one.
        var other = Fixture.Temp("small-game");
        File.WriteAllBytes(Path.Combine(other, "small-game.exe"), Fixture.Pe(x64: false));
        Directory.CreateDirectory(Path.Combine(other, "x64"));
        File.WriteAllBytes(Path.Combine(other, "x64", "reporter.exe"), Fixture.Pe(x64: true));
        Assert.Equal(Route.X86, GraphicsDetector.Detect(other, "small-game").Width);

        // And the person can point at whichever file they like, which is the way back when this is
        // still wrong -- it is read instead of the folder being searched at all.
        var launcher = Path.Combine(game, "BeamNG.drive.exe");
        Assert.Equal(Route.X86, GraphicsDetector.Detect(game, "BeamNG.drive", launcher).Width);
    }
}
