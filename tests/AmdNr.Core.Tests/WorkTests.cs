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

    /// <summary>ReShade never loads an add-on named in DisabledAddons, and says so nowhere -- it
    /// looks exactly like a broken install. Both routes prepare the ini through this one function,
    /// because the 32-bit one only docked the panel and left the add-on switched off.</summary>
    [Fact]
    public void PreparingReShadeIniReEnablesThisAddOnUnderEitherName()
    {
        const string ini = """
            [ADDON]
            DisabledAddons=SomeoneElse.addon32,dlss5-neural.addon32,amd-nr.addon32
            """;
        var ready = Work.ReadyReShadeIni(ini);
        Assert.Contains("SomeoneElse.addon32", ready, StringComparison.Ordinal);
        Assert.DoesNotContain("amd-nr.addon32", ready, StringComparison.Ordinal);
        Assert.DoesNotContain("dlss5-neural.addon32", ready, StringComparison.Ordinal);
        // And the rest of what a first run needs, which is the other half this route was missing.
        Assert.Contains("TutorialProgress=4", ready, StringComparison.Ordinal);
        Assert.Contains("[Window][AMD Neural Rendering]", ready, StringComparison.Ordinal);
    }

    /// <summary>ReShade loads every add-on in the folder, so one left under the name it used before
    /// v0.6.5 is a second add-on on the same present. Both routes sweep, and they sweep through this
    /// one function -- the 32-bit route went without it for a release, which is exactly the shape of
    /// bug that costs a user an evening.</summary>
    [Fact]
    public void TheSweepRemovesAnAddOnLeftUnderTheNameItUsedBeforeTheRename()
    {
        var game = Fixture.Temp("sweep-legacy");
        foreach (var name in new[] { "dlss5-neural.addon32", "dlss5-neural-host64.exe",
                                     "dlss5-neural.addon64", "dlssnr_amd_pass2.dll" })
            File.WriteAllText(Path.Combine(game, name), "an older install left this here");
        // The settings are not swept: the add-on copies them to amd-nr.ini and leaves the original.
        File.WriteAllText(Path.Combine(game, "dlss5-neural.ini"), "[dlss5]");

        var warnings = new List<string>();
        var removed = Work.SweepDead(game, warnings.Add);

        Assert.Empty(warnings);
        Assert.Contains("dlss5-neural.addon32", removed);
        Assert.Contains("dlss5-neural-host64.exe", removed);
        Assert.Contains("dlss5-neural.addon64", removed);
        Assert.Contains("dlssnr_amd_pass2.dll", removed);
        Assert.Empty(Directory.GetFiles(game, "*.addon32"));
        Assert.True(File.Exists(Path.Combine(game, "dlss5-neural.ini")), "the old settings are the user's");
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

    /// <summary>Without a payload manifest the add-on's size is unknown, and the pins carry a zero
    /// there. Comparing a real file against that zero reported the right file as the wrong build.</summary>
    [Fact]
    public void AnUnknownExpectedSizeIsNotReadAsAWrongBuild()
    {
        var game = Fixture.Temp("unknown-size-game");
        var (src, pins) = Fixture.Payloads("unknown-size");

        var blind = new PayloadPins
        {
            AddonSha = new string('0', 64),
            AddonSize = 0,
            RuntimeSha = pins.RuntimeSha, RuntimeSize = pins.RuntimeSize,
            WeightsSha = pins.WeightsSha, WeightsSize = pins.WeightsSize,
        };

        var report = Work.Preflight(game, src, Preset.Dx11, blind);
        Assert.False(Fixture.HasAny(report, "That is a different build"), report.ToLog("blind pins"));
        Assert.False(Fixture.HasErr(report, Work.AddonName), report.ToLog("blind pins"));
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
        Assert.True(Fixture.HasAny(report, "an older install left behind"), report.ToLog("sweep"));
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
}
