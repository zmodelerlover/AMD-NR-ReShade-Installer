using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>Crimson Desert had ReShade.asi loaded by Ultimate ASI Loader as version.dll and winmm.dll, and the D3D12
/// route put ReShade 6.8.0 in as dxgi.dll beside it without a word: two ReShades in one process.</summary>
public class AsiReShadeTests
{
    [Fact]
    public void AReShadeLoadedAsAnAsiIsASecondReShade()
    {
        var game = Fixture.Temp("asi-reshade");
        var (src, pins) = Fixture.Payloads("asi-reshade");
        File.WriteAllBytes(Path.Combine(game, "CrimsonDesert.exe"), Fixture.PeWithImports(true, ["d3d12.dll"]));
        Directory.CreateDirectory(Path.Combine(game, "scripts"));
        File.WriteAllBytes(Path.Combine(game, "scripts", "ReShade.asi"), Fixture.Pe(true));

        // No loader: it loads nothing yet, which is said and does not stop the install.
        var alone = Work.Preflight(game, src, Preset.Dx12, pins);
        Assert.True(Fixture.HasAny(alone, "No ASI loader was found"), alone.ToLog("alone"));
        Assert.False(Fixture.HasAny(alone, "through the ASI loader"), alone.ToLog("alone"));

        // A loader that looks for *.asi: refused, like a second ReShade proxy.
        File.WriteAllBytes(Path.Combine(game, "version.dll"), [.. Fixture.Pe(true), .. Encoding.Unicode.GetBytes("*.asi")]);
        var loaded = Work.Preflight(game, src, Preset.Dx12, pins);
        Assert.True(loaded.Failed && Fixture.HasAny(loaded, "through the ASI loader version.dll"), loaded.ToLog("loaded"));
        var install = Work.Install(game, src, Preset.Dx12, pins);
        Assert.True(install.Failed, install.ToLog("install"));
        Assert.False(File.Exists(Path.Combine(game, Work.AddonName)), "nothing goes in");
    }
}
