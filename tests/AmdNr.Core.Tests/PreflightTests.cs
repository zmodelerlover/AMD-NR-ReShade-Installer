using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class PreflightTests
{
    // -- Pre-flight ------------------------------------------------------------------------------

    [Fact]
    public void PreflightAsksForThePayloadsFirstAndThenTheFolder()
    {
        var pins = Fixture.Payloads("preflight-empty").Pins;
        var empty = Work.Preflight("", "", Preset.Dx11, pins);
        Assert.True(Fixture.HasAny(empty, "Waiting for the payload folder"), empty.ToLog("empty"));
        Assert.False(empty.Failed, "an empty form is not an error");

        var half = Work.Preflight(Fixture.Temp("preflight-half"), "", Preset.Dx11, pins);
        Assert.True(Fixture.HasAny(half, "Waiting for the payload folder"), half.ToLog("half"));
    }

    [Fact]
    public void PreflightNamesAWrongSizedRuntimeWithoutHashingIt()
    {
        var game = Fixture.Temp("preflight-size-game");
        var (src, pins) = Fixture.Payloads("preflight-size");
        File.WriteAllText(Path.Combine(src, Work.RuntimeName), "a different build entirely");

        var report = Work.Preflight(game, src, Preset.Dx11, pins);
        Assert.True(report.Failed);
        Assert.True(Fixture.HasErr(report, "different build"), report.ToLog("size"));
    }

    [Fact]
    public void PreflightSpotsAMissingFileInThePayloadFolder()
    {
        var game = Fixture.Temp("preflight-missing-game");
        var (src, pins) = Fixture.Payloads("preflight-missing");
        File.Delete(Path.Combine(src, Work.WeightsName));

        var report = Work.Preflight(game, src, Preset.Dx11, pins);
        Assert.True(Fixture.HasErr(report, "dlssnr_on_amd_weights.bin is not in that folder"),
            report.ToLog("missing"));
    }

    /// <summary>Need for Speed (2016): NFS16.exe imports d3d12.dll and renders D3D11, and the D3D11 route was
    /// told its renderer was not in this build. D3D11 and D3D12 come through the same DXGI; a different API
    /// altogether -- Half-Life's opengl32 under a D3D9 route -- is still said.</summary>
    [Fact]
    public void ALinkedD3D12IsNoEvidenceAgainstD3D11()
    {
        var (src, pins) = Fixture.Payloads("reachable");
        foreach (var (imports, preset, warned) in new[]
                 {
                     ("d3d12.dll", Preset.Dx11, false), ("d3d11.dll", Preset.Dx12, false),
                     ("opengl32.dll", Preset.Dx11, true), ("opengl32.dll", Preset.X86Dx9, true),
                 })
        {
            var game = Fixture.Temp("reachable");
            var x86 = preset.Route() == Route.X86;
            File.WriteAllBytes(Path.Combine(game, "Game.exe"), Fixture.PeWithImports(!x86, ["KERNEL32.dll", imports]));
            var report = Work.Preflight(game, src, preset, pins);
            Assert.True(warned == Fixture.HasAny(report, "This route needs"), report.ToLog($"{imports} under {preset}"));
        }
    }

    [Fact]
    public void PreflightFindsTheDisabledAddonsLine()
    {
        var game = Fixture.Temp("preflight-ini");
        var (src, pins) = Fixture.Payloads("preflight-ini");
        File.WriteAllBytes(Path.Combine(game, "d3d11.dll"), Fixture.Pe(true));
        File.WriteAllText(Path.Combine(game, "ReShade.ini"),
            "[ADDON]\nDisabledAddons=dlss5 neural@amd-nr.addon64\n");

        // Said, not refused: the install takes it off the list.
        var report = Work.Preflight(game, src, Preset.Dx11, pins);
        Assert.False(Fixture.HasErr(report, "DisabledAddons"), report.ToLog("ini"));
        Assert.True(Fixture.HasAny(report, "takes it off that list"), report.ToLog("ini"));
        var installed = Work.Install(game, src, Preset.Dx11, pins);
        Assert.False(installed.Failed, installed.ToLog("install"));
        Assert.DoesNotContain("amd-nr.addon64", Engine.GetIni(File.ReadAllText(Path.Combine(game, "ReShade.ini")), "ADDON", "DisabledAddons"),
            StringComparison.Ordinal);
        Work.Uninstall(game, Preset.Dx11, removeConfig: true);

        File.WriteAllText(Path.Combine(game, "ReShade.ini"), "[ADDON]\nDisabledAddons=SomethingElse.addon64\n");
        var clean = Work.Preflight(game, src, Preset.Dx11, pins);
        Assert.False(clean.Failed, clean.ToLog("ini clean"));
        Assert.True(Fixture.HasAny(clean, "disables other add-ons"));
    }

    [Fact]
    public void PreflightReportsAFileAnotherProgramIsHoldingOpen()
    {
        var game = Fixture.Temp("preflight-locked");
        var (src, pins) = Fixture.Payloads("preflight-locked");
        var held = Path.Combine(game, Work.AddonName);
        File.WriteAllText(held, "x");

        using (var _ = File.Open(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = Work.Preflight(game, src, Preset.Dx11, pins);
            Assert.True(report.Failed);
            Assert.True(Fixture.HasErr(report, "this line goes away"), report.ToLog("locked"));
        }

        var after = Work.Preflight(game, src, Preset.Dx11, pins);
        Assert.False(Fixture.HasErr(after, "this line goes away"), "closing it should clear the line");
    }

    /// <summary>The 32-bit route never installs the 64-bit add-on, so a payload folder without it is
    /// the correct shape there -- and the pinned ReShade it carries means "no ReShade in the game
    /// folder" is the state before installing, not a problem.</summary>
    [Fact]
    public void PreflightOnTheBridgeRouteAsksForTheBridgeFilesNotTheX64AddOn()
    {
        var game = Fixture.Temp("preflight-x86-game");
        File.WriteAllBytes(Path.Combine(game, "old.exe"), Fixture.Pe(false));

        var (src, pins) = Fixture.Payloads("preflight-x86");
        File.Delete(Path.Combine(src, Work.AddonName));
        Directory.CreateDirectory(Path.Combine(src, "files"));
        File.WriteAllText(Path.Combine(src, "payload.sha256"), "");
        File.WriteAllBytes(Path.Combine(src, "files", "amd-nr.addon32"), Fixture.Pe(false));
        File.WriteAllBytes(Path.Combine(src, "files", "amd-nr-host64.exe"), Fixture.Pe(true));
        File.WriteAllBytes(Path.Combine(src, "files", "dxgi.dll"), Fixture.Pe(false));

        var report = Work.Preflight(Path.Combine(game, "old.exe"), src, Preset.X86Dx9, pins);
        Assert.False(report.Failed, report.ToLog("x86 preflight"));
        Assert.False(Fixture.HasAny(report, Work.AddonName), report.ToLog("x86 preflight"));
        Assert.True(Fixture.HasAny(report, "pinned 32-bit ReShade"), report.ToLog("x86 preflight"));

        File.Delete(Path.Combine(src, "files", "amd-nr-host64.exe"));
        var missing = Work.Preflight(Path.Combine(game, "old.exe"), src, Preset.X86Dx9, pins);
        Assert.True(Fixture.HasErr(missing, "amd-nr-host64.exe"), missing.ToLog("x86 missing"));
    }

    [Fact]
    public void PreflightIsQuietWhenThereIsGenuinelyNothingWrong()
    {
        var game = Fixture.Temp("preflight-clean");
        var (src, pins) = Fixture.Payloads("preflight-clean");
        File.WriteAllBytes(Path.Combine(game, "d3d11.dll"), Fixture.Pe(true));

        var report = Work.Preflight(game, src, Preset.Dx11, pins);
        Assert.False(report.Failed, report.ToLog("clean"));
        Assert.True(Fixture.HasAny(report, "Nothing in the way"));
    }

    /// <summary>OptiScaler, DXVK and SpecialK all install as dxgi.dll. A DLL that says in its own
    /// version resource that it is something else must not be reported as ReShade.</summary>
    [Fact]
    public void AProxyThatIsNotReShadeIsNamedAsWhatItIs()
    {
        var game = Fixture.Temp("not-reshade");
        var (src, pins) = Fixture.Payloads("not-reshade");
        // Windows' own dxgi.dll: a real version resource that says Microsoft, not ReShade.
        File.Copy(Path.Combine(Environment.SystemDirectory, "dxgi.dll"), Path.Combine(game, "dxgi.dll"));

        var report = Work.Preflight(game, src, Preset.Dx11, pins);
        Assert.False(Fixture.HasAny(report, "ReShade found"), report.ToLog("not reshade"));
        Assert.True(Fixture.HasAny(report, "not ReShade"), report.ToLog("not reshade"));
    }

    // -- The prose -------------------------------------------------------------------------------

    /// <summary>These notes are concatenated across source lines, which is easy to get wrong -- a
    /// missing space joins two words, a doubled one shows up as a gap in the middle of a sentence.</summary>
    [Fact]
    public void NoPresetNoteCarriesTheJoinsOfItsOwnSource()
    {
        foreach (var p in Presets.All)
        {
            var note = p.Note();
            Assert.DoesNotContain("  ", note, StringComparison.Ordinal);
            Assert.DoesNotContain("\n", note, StringComparison.Ordinal);
            Assert.True(note.Length > 40, $"{p} has no note worth showing");
            Assert.False(string.IsNullOrWhiteSpace(p.Label()));
            Assert.Contains("folder", p.FolderLabel(), StringComparison.Ordinal);
        }

        var labels = Presets.All.Select(p => p.Label()).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(Presets.All.Length, labels.Count);
    }

    [Fact]
    public void TheVulkanPresetsExpectALayerAndTheD3DOnesExpectAProxyDll()
    {
        var dir = Fixture.Temp("reshade-shape");
        var (src, pins) = Fixture.Payloads("reshade-shape");

        foreach (var p in new[] { Preset.Rpcs3, Preset.Vulkan })
        {
            var report = Work.Preflight(dir, src, p, pins);
            Assert.True(Fixture.HasAny(report, "Vulkan layer"), $"{p.Label()} said the wrong thing");
            Assert.False(Fixture.HasAny(report, "No ReShade proxy DLL found"), p.Label());
        }

        foreach (var p in new[] { Preset.Pcsx2, Preset.Dx11, Preset.Dx12 })
            Assert.True(Fixture.HasAny(Work.Preflight(dir, src, p, pins), "No ReShade proxy DLL found"), p.Label());

        File.WriteAllBytes(Path.Combine(dir, "d3d11.dll"), Fixture.Pe(true));
        Assert.True(Fixture.HasAny(Work.Preflight(dir, src, Preset.Dx11, pins), "ReShade found"));
    }
}
