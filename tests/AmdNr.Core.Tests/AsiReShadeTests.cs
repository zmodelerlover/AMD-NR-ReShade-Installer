using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>Crimson Desert had ReShade.asi loaded by Ultimate ASI Loader as version.dll and winmm.dll, and the D3D12
/// route put ReShade 6.8.0 in as dxgi.dll beside it without a word: two ReShades in one process. Both are known by
/// their version resources, not by names or bytes: OptiScaler's dxgi.dll names .asi too.</summary>
public class AsiReShadeTests
{
    [Fact]
    public void AReShadeLoadedAsAnAsiIsASecondReShade()
    {
        var game = Fixture.Temp("asi-reshade");
        var (src, pins) = Fixture.Payloads("asi-reshade");
        File.WriteAllBytes(Path.Combine(game, "CrimsonDesert.exe"), Fixture.PeWithImports(true, ["d3d12.dll"]));
        Directory.CreateDirectory(Path.Combine(game, "scripts"));
        Versioned.Write(Path.Combine(game, "scripts", "ReShade.asi"), "ReShade", "6.7.3.2000");

        // No loader: it loads nothing yet, which is said and does not stop the install.
        var alone = Work.Preflight(game, src, Preset.Dx12, pins);
        Assert.True(Fixture.HasAny(alone, "No ASI loader was found"), alone.ToLog("alone"));
        Assert.False(Fixture.HasAny(alone, "through the ASI loader"), alone.ToLog("alone"));

        // Something that only names .asi in its bytes is no loader.
        File.WriteAllBytes(Path.Combine(game, "winmm.dll"), [.. Fixture.Pe(true), .. Encoding.Unicode.GetBytes("*.asi")]);
        Assert.False(Fixture.HasAny(Work.Preflight(game, src, Preset.Dx12, pins), "through the ASI loader"));

        // Ultimate ASI Loader: refused, like a second ReShade proxy.
        Versioned.Write(Path.Combine(game, "version.dll"), "Ultimate ASI Loader", "9.7.4");
        var loaded = Work.Preflight(game, src, Preset.Dx12, pins);
        Assert.True(loaded.Failed && Fixture.HasAny(loaded, "through the ASI loader version.dll"), loaded.ToLog("loaded"));
        var install = Work.Install(game, src, Preset.Dx12, pins);
        Assert.True(install.Failed, install.ToLog("install"));
        Assert.False(File.Exists(Path.Combine(game, Work.AddonName)), "nothing goes in");
    }

    [Fact]
    public void AnAsiNamedReShadeThatIsNotReShadeIsNotOne()
    {
        var game = Fixture.Temp("asi-named");
        var (src, pins) = Fixture.Payloads("asi-named");
        File.WriteAllBytes(Path.Combine(game, "Game.exe"), Fixture.PeWithImports(true, ["d3d12.dll"]));
        File.WriteAllBytes(Path.Combine(game, "ReShadeToggle.asi"), Fixture.Pe(true));
        Versioned.Write(Path.Combine(game, "dinput8.dll"), "Ultimate ASI Loader", "9.7.2");
        var report = Work.Preflight(game, src, Preset.Dx12, pins);
        Assert.False(Fixture.HasAny(report, "as an ASI plugin") || Fixture.HasAny(report, "through the ASI loader"), report.ToLog("named"));
    }
}
