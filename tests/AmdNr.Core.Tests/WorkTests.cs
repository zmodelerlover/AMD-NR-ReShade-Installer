using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class WorkTests
{
    [Fact]
    public void InstallWritesEveryPayloadAndJournalsAManifest()
    {
        var game = Fixture.Temp("x64-install");
        var (src, pins) = Fixture.Payloads("x64-install");

        var report = Work.Install(game, src, Preset.Dx12, pins);
        Assert.False(report.Failed, report.ToLog("install"));

        foreach (var name in new[] { Work.AddonName, Work.RuntimeName, Work.WeightsName })
            Assert.True(File.Exists(Path.Combine(game, name)), $"{name} was not installed");

        var manifest = Path.Combine(game, Route.X64.ManifestFileName());
        Assert.True(File.Exists(manifest), "the x64 route must journal what it did");
        Assert.NotEqual(Engine.ManifestName, Route.X64.ManifestFileName());

        var m = Manifest.Decode(File.ReadAllText(manifest));
        Assert.Equal("D3D12", m.Preset);
        Assert.Equal(Route.X64, m.Route);
        Assert.Contains(m.Entries, e => e.Name == Work.AddonName && e.Owned);
    }

    [Fact]
    public void ARuntimeWithTheWrongHashIsRefusedAndNotCopied()
    {
        var game = Fixture.Temp("bad-hash-game");
        var (src, pins) = Fixture.Payloads("bad-hash");
        File.WriteAllText(Path.Combine(src, Work.RuntimeName), "not the runtime");

        var report = Work.Install(game, src, Preset.Dx11, pins);
        Assert.True(report.Failed);
        Assert.True(Fixture.HasErr(report, "does not match the expected SHA-256"), report.ToLog("hash"));
        Assert.False(File.Exists(Path.Combine(game, Work.RuntimeName)), "a rejected file must not be copied");
        Assert.False(File.Exists(Path.Combine(game, Work.AddonName)),
            "nothing may be written when a payload is refused");
        Assert.False(File.Exists(Path.Combine(game, Route.X64.ManifestFileName())));
    }

    [Fact]
    public void AMissingPayloadFileIsNamed()
    {
        var game = Fixture.Temp("missing-game");
        var (src, pins) = Fixture.Payloads("missing");
        File.Delete(Path.Combine(src, Work.WeightsName));

        var report = Work.Install(game, src, Preset.Dx11, pins);
        Assert.True(report.Failed);
        Assert.True(Fixture.HasErr(report, Work.WeightsName), report.ToLog("missing"));
    }

    [Fact]
    public void TheOldPerPassFilesAreSwept()
    {
        var game = Fixture.Temp("sweep");
        var (src, pins) = Fixture.Payloads("sweep");
        for (var n = 2; n <= 10; n++)
            File.WriteAllText(Path.Combine(game, $"dlssnr_amd_pass{n}.dll"), "x");

        var report = Work.Install(game, src, Preset.Dx11, pins);
        Assert.True(Fixture.HasAny(report, "old per-pass layout"), report.ToLog("sweep"));
        for (var n = 2; n <= 10; n++)
            Assert.False(File.Exists(Path.Combine(game, $"dlssnr_amd_pass{n}.dll")), $"pass{n} survived");
    }

    /// <summary>One unpacked release folder has to serve both routes, or the download stops being
    /// one download. The bridge package keeps its payloads in files\; a folder someone unzipped the
    /// runtime into by itself keeps them loose.</summary>
    [Fact]
    public void ThePayloadsAreFoundInEitherShapeOfFolder()
    {
        var root = Fixture.Temp("payload-dir");

        var loose = Path.Combine(root, "loose");
        Directory.CreateDirectory(loose);
        File.WriteAllText(Path.Combine(loose, Work.RuntimeName), "stand-in");
        Assert.Equal(loose, Work.PayloadDir(loose));

        var release = Path.Combine(root, "release");
        Directory.CreateDirectory(Path.Combine(release, "files"));
        File.WriteAllText(Path.Combine(release, "files", Work.RuntimeName), "stand-in");
        Assert.Equal(Path.Combine(release, "files"), Work.PayloadDir(release));

        var empty = Path.Combine(root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Equal(empty, Work.PayloadDir(empty));
    }

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

    [Fact]
    public void TheOfferedPresetsFollowTheDetectedWidth()
    {
        Assert.Equal(Presets.All.Length, Presets.Offered(Detected.Unknown).Count);

        var x64 = Presets.Offered(Detected.On(Route.X64, string.Empty));
        Assert.Contains(Preset.Pcsx2, x64);
        Assert.Contains(Preset.Vulkan, x64);
        Assert.DoesNotContain(x64, p => p.Route() == Route.X86);

        var x86 = Presets.Offered(Detected.On(Route.X86, string.Empty));
        Assert.Equal([Preset.X86Dx11, Preset.X86Dx9, Preset.X86Dx8], x86);
        Assert.DoesNotContain(x86, p => p.Route() == Route.X64);
    }

    [Fact]
    public void TheBridgeRouteWantsTheExecutableAndSaysWhy()
    {
        var game = Fixture.Temp("x86-folder");
        var (src, pins) = Fixture.Payloads("x86-folder");
        File.WriteAllBytes(Path.Combine(game, "a.exe"), Fixture.Pe(false));
        File.WriteAllBytes(Path.Combine(game, "b.exe"), Fixture.Pe(false));

        var report = Work.Install(game, src, Preset.X86Dx9, pins);
        Assert.True(report.Failed);
        Assert.True(Fixture.HasErr(report, "More than one 32-bit executable"), report.ToLog("ambiguous"));

        // One candidate is unambiguous, so the folder is enough and the release folder is what is
        // missing next.
        File.Delete(Path.Combine(game, "b.exe"));
        var second = Work.Install(game, src, Preset.X86Dx9, pins);
        Assert.True(second.Failed);
        Assert.True(Fixture.HasErr(second, "payload.sha256"), second.ToLog("no release"));
    }

    [Fact]
    public void A64BitTargetIsRefusedByTheBridgeRouteBeforeAnythingIsWritten()
    {
        var game = Fixture.Temp("x86-wrong-width");
        var exe = Path.Combine(game, "game64.exe");
        File.WriteAllBytes(exe, Fixture.Pe(true));
        var release = Fixture.Temp("x86-wrong-width-release");
        File.WriteAllText(Path.Combine(release, "payload.sha256"), "");

        var report = Work.Install(exe, release, Preset.X86Dx11, Fixture.Payloads("unused").Pins);
        Assert.True(report.Failed);
        Assert.True(Fixture.HasErr(report, "PE32/x86"), report.ToLog("width"));
        Assert.False(File.Exists(Path.Combine(game, Engine.ManifestName)));
    }

    [Fact]
    public void AnAddOnAlreadyInTheFolderIsBackedUpBeforeBeingReplaced()
    {
        var game = Fixture.Temp("x64-backup");
        var (src, pins) = Fixture.Payloads("x64-backup");
        // Somebody else's file under our name: it must be recoverable, not overwritten silently.
        File.WriteAllText(Path.Combine(game, Work.AddonName), "a different add-on");

        var report = Work.Install(game, src, Preset.Dx11, pins);
        Assert.False(report.Failed, report.ToLog("backup"));
        Assert.Equal(pins.AddonSha, Engine.HashFile(Path.Combine(game, Work.AddonName)));

        var backups = Path.Combine(game, Engine.BackupDir);
        Assert.True(Directory.Exists(backups), "the displaced file must be kept");
        Assert.Contains(Fixture.Walk(backups), p => File.ReadAllText(p) == "a different add-on");

        // And uninstall puts it back rather than deleting what was not ours to delete.
        var removed = Work.Uninstall(game, Preset.Dx11);
        Assert.False(removed.Failed, removed.ToLog("restore"));
        Assert.Equal("a different add-on", File.ReadAllText(Path.Combine(game, Work.AddonName)));
    }

    [Fact]
    public void TheX64RouteRefusesToInstallOverAFileTheGameIsHolding()
    {
        var game = Fixture.Temp("x64-locked");
        var (src, pins) = Fixture.Payloads("x64-locked");
        var held = Path.Combine(game, Work.AddonName);
        File.WriteAllText(held, "held open by the running game");

        using (var _ = File.Open(held, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var report = Work.Install(game, src, Preset.Dx11, pins);
            Assert.True(report.Failed);
            Assert.True(Fixture.HasErr(report, "still running") || Fixture.HasErr(report, "open by another program"),
                report.ToLog("locked"));
            Assert.False(File.Exists(Path.Combine(game, Route.X64.ManifestFileName())));
        }

        Assert.Equal("held open by the running game", File.ReadAllText(held));
    }

    [Fact]
    public void AnInstallFromBeforeTheManifestExistedCanStillBeUninstalled()
    {
        var game = Fixture.Temp("legacy");
        foreach (var name in new[] { Work.AddonName, Work.RuntimeName, Work.WeightsName })
            File.WriteAllText(Path.Combine(game, name), "from an older installer");
        File.WriteAllText(Path.Combine(game, "dlss5-neural.ini"), "[dlss5]\r\nStartOn=1\r\n");
        Directory.CreateDirectory(Path.Combine(game, "dlss5-runtime"));
        Assert.False(File.Exists(Path.Combine(game, Route.X64.ManifestFileName())));

        var report = Work.Uninstall(game, Preset.Dx11);
        Assert.False(report.Failed, report.ToLog("legacy"));
        foreach (var name in new[] { Work.AddonName, Work.RuntimeName, Work.WeightsName })
            Assert.False(File.Exists(Path.Combine(game, name)), $"{name} survived a legacy uninstall");
        Assert.False(Directory.Exists(Path.Combine(game, "dlss5-runtime")));
        Assert.True(File.Exists(Path.Combine(game, "dlss5-neural.ini")), "the ini is still the user's");
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
            ["dlss5-neural.addon32"] = Fixture.Pe(false),
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

        File.WriteAllText(Path.Combine(game, "dlss5-neural.ini"), "[dlss5]\r\nScale=0.75\r\n");
        Directory.CreateDirectory(Path.Combine(game, "dlss5-runtime"));

        var report = Work.Uninstall(game, Preset.Dx11);
        Assert.False(report.Failed, report.ToLog("uninstall"));
        Assert.False(File.Exists(Path.Combine(game, Work.AddonName)));
        Assert.False(Directory.Exists(Path.Combine(game, "dlss5-runtime")));
        Assert.True(File.Exists(Path.Combine(game, "dlss5-neural.ini")), "the ini is the user's, not ours");
    }

    [Fact]
    public void UninstallOnAnUnrelatedFolderSaysSoRatherThanFailing()
    {
        var report = Work.Uninstall(Fixture.Temp("unrelated"), Preset.Dx11);
        Assert.False(report.Failed);
        Assert.True(Fixture.HasAny(report, "Nothing of ours"), report.ToLog("unrelated"));
    }

    [Fact]
    public void AQuotedPathIsAcceptedBecauseWindowsCopiesThemThatWay()
    {
        var game = Fixture.Temp("quoted");
        var (src, pins) = Fixture.Payloads("quoted");

        var report = Work.Install($"\"{game}\"", $"\"{src}\"", Preset.Dx11, pins);
        Assert.False(report.Failed, report.ToLog("quoted"));
        Assert.True(File.Exists(Path.Combine(game, Work.AddonName)));
    }

    [Fact]
    public void APathThatIsNotAFolderIsReportedNotIgnored()
    {
        var (src, pins) = Fixture.Payloads("not-a-folder");
        var report = Work.Install(Path.Combine(Fixture.Temp("nf"), "nothing-here"), src, Preset.Dx11, pins);
        Assert.True(report.Failed);
        Assert.True(Fixture.HasErr(report, "is not a folder"), report.ToLog("not a folder"));
    }

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

    [Fact]
    public void PreflightFindsTheDisabledAddonsLine()
    {
        var game = Fixture.Temp("preflight-ini");
        var (src, pins) = Fixture.Payloads("preflight-ini");
        File.WriteAllBytes(Path.Combine(game, "d3d11.dll"), Fixture.Pe(true));
        File.WriteAllText(Path.Combine(game, "ReShade.ini"),
            "[ADDON]\nDisabledAddons=dlss5 neural@dlss5-neural.addon64\n");

        var report = Work.Preflight(game, src, Preset.Dx11, pins);
        Assert.True(report.Failed);
        Assert.True(Fixture.HasErr(report, "DisabledAddons"), report.ToLog("ini"));

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
            Assert.True(Fixture.HasErr(report, "still running"), report.ToLog("locked"));
        }

        var after = Work.Preflight(game, src, Preset.Dx11, pins);
        Assert.False(Fixture.HasErr(after, "still running"), "closing it should clear the line");
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
        File.WriteAllBytes(Path.Combine(src, "files", "dlss5-neural.addon32"), Fixture.Pe(false));
        File.WriteAllBytes(Path.Combine(src, "files", "dlss5-neural-host64.exe"), Fixture.Pe(true));
        File.WriteAllBytes(Path.Combine(src, "files", "dxgi.dll"), Fixture.Pe(false));

        var report = Work.Preflight(Path.Combine(game, "old.exe"), src, Preset.X86Dx9, pins);
        Assert.False(report.Failed, report.ToLog("x86 preflight"));
        Assert.False(Fixture.HasAny(report, Work.AddonName), report.ToLog("x86 preflight"));
        Assert.True(Fixture.HasAny(report, "pinned 32-bit ReShade"), report.ToLog("x86 preflight"));

        File.Delete(Path.Combine(src, "files", "dlss5-neural-host64.exe"));
        var missing = Work.Preflight(Path.Combine(game, "old.exe"), src, Preset.X86Dx9, pins);
        Assert.True(Fixture.HasErr(missing, "dlss5-neural-host64.exe"), missing.ToLog("x86 missing"));
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

    [Fact]
    public void UninstallWithNoFolderSaysWhichFieldIsEmpty()
    {
        var report = Work.Uninstall("", Preset.Dx11);
        Assert.True(report.Failed);
        Assert.True(Fixture.HasErr(report, "No game folder given"));
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
            Assert.True(Fixture.HasAny(report, "global layer"), $"{p.Label()} said the wrong thing");
            Assert.False(Fixture.HasAny(report, "No ReShade proxy DLL found"), p.Label());
        }

        foreach (var p in new[] { Preset.Pcsx2, Preset.Dx11, Preset.Dx12 })
            Assert.True(Fixture.HasAny(Work.Preflight(dir, src, p, pins), "No ReShade proxy DLL found"), p.Label());

        File.WriteAllBytes(Path.Combine(dir, "d3d11.dll"), Fixture.Pe(true));
        Assert.True(Fixture.HasAny(Work.Preflight(dir, src, Preset.Dx11, pins), "ReShade found"));
    }

    // -- The real payloads -------------------------------------------------------------------------

    /// <summary>End to end against the genuine runtime and weights. Set AMDNR_TEST_PAYLOAD_DIR to a
    /// folder holding dlssnr_amd_pass1.dll, dlssnr_on_amd_weights.bin and dlss5-neural.addon64;
    /// skipped otherwise, because those are 141 MB and not in any repository.</summary>
    [Fact]
    public void ARealPayloadRoundTrip()
    {
        var payloads = Environment.GetEnvironmentVariable("AMDNR_TEST_PAYLOAD_DIR");
        if (string.IsNullOrWhiteSpace(payloads) || !Directory.Exists(payloads)) return;

        var dir = Work.PayloadDir(payloads);
        var addon = Path.Combine(dir, Work.AddonName);
        if (!File.Exists(addon)) return;

        var pins = new PayloadPins
        {
            AddonSha = Engine.HashFile(addon),
            AddonSize = Engine.SizeOf(addon)!.Value,
        };

        var game = Fixture.Temp("real");
        var report = Work.Install(game, payloads, Preset.Dx11, pins);
        Assert.False(report.Failed, report.ToLog("real install"));
        Assert.Equal(Engine.RuntimeSha, Engine.HashFile(Path.Combine(game, Work.RuntimeName)));
        Assert.Equal(Engine.WeightsSha, Engine.HashFile(Path.Combine(game, Work.WeightsName)));

        // Running it twice is what a person does when they are not sure it worked.
        var manifestBefore = File.ReadAllBytes(Path.Combine(game, Route.X64.ManifestFileName()));
        var again = Work.Install(game, payloads, Preset.Dx11, pins);
        Assert.False(again.Failed, again.ToLog("real reinstall"));
        Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(game, Route.X64.ManifestFileName())));

        var removed = Work.Uninstall(game, Preset.Dx11);
        Assert.False(removed.Failed, removed.ToLog("real uninstall"));
        Assert.False(File.Exists(Path.Combine(game, Work.WeightsName)));
    }
}
