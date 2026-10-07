using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>What a folder can have become between two installs, on every route, with the download and with
/// danielblnc's supporter build: its record rewritten in another layout (Need for Speed), cut short or
/// garbage, an interrupted install, a file of ours replaced by hand, the backups deleted. Each used to end
/// in a red line of its own and an install that could not be run again; now the install clears it and goes
/// in, and the uninstall after it is clean. Stand-ins only.</summary>
public class RecoveryTests
{
    private sealed record Case(string Name, string Dir, string ManifestName, string Ours, Func<Report> Install, Func<Report> Uninstall);

    private static IEnumerable<Case> Cases()
    {
        var build = UserRuntimeTests.Build(UserRuntimeTests.Dll(41));
        var own = Path.Combine(Fixture.Temp("rc-own"), "version.dll");
        File.WriteAllBytes(own, UserRuntimeTests.Dll(41));

        foreach (var preset in new[] { Preset.Dx11, Preset.Dx12, Preset.Vulkan, Preset.OpenGL, Preset.Pcsx2 })
        foreach (var supporter in new[] { false, true })
        {
            var game = Fixture.Temp($"rc-{preset}-{supporter}");
            File.WriteAllBytes(Path.Combine(game, "game.exe"), Fixture.Pe(true));
            var (src, p) = UninstallInvariantTests.ReShadePayloads($"rc-{preset}-{supporter}");
            var pins = new PayloadPins
            {
                AddonSha = p.AddonSha, AddonSize = p.AddonSize, RuntimeSha = p.RuntimeSha, RuntimeSize = p.RuntimeSize,
                WeightsSha = p.WeightsSha, WeightsSize = p.WeightsSize, ReShade64Sha = p.ReShade64Sha,
                ShaderSha = p.ShaderSha, ShaderSize = p.ShaderSize, AddonVersion = "0.7.0", UserRuntimes = [build],
            };
            yield return new Case($"{preset}{(supporter ? " supporter" : "")}", game, Engine.ManifestNameX64, Work.AddonName,
                () => Work.Install(game, src, preset, pins, ownRuntime: supporter ? own : null),
                () => Work.Uninstall(game, preset, pinned: [pins.ReShade64Sha]));
        }

        foreach (var preset in new[] { Preset.X86Dx9, Preset.X86Dx11 })
        foreach (var supporter in new[] { false, true })
        {
            var game = Fixture.Temp($"rc-{preset}-{supporter}");
            var exe = Path.Combine(game, "Game.exe");
            File.WriteAllBytes(exe, Fixture.Pe(false));
            var (app, pinned) = UninstallInvariantTests.X86Release($"rc-{preset}-{supporter}");
            var pins = new PayloadPins
            {
                AddonSha = "", AddonSize = 0, RuntimeSha = app.RuntimeSha, WeightsSha = app.WeightsSha,
                ReShade32Sha = app.ReShadeSha, D3d8To9Sha = app.D3d8To9Sha, BridgeVersion = "0.7.0", UserRuntimes = [build],
            };
            yield return new Case($"{preset}{(supporter ? " supporter" : "")}", game, Engine.ManifestName, Work.Addon32Name,
                () => Work.Install(exe, app.Release, preset, pins, ownRuntime: supporter ? own : null),
                () => Work.Uninstall(exe, preset, pinned: pinned));
        }

        var opti = Fixture.Temp("rc-opti");
        var (optiSrc, optiPins) = OptiScalerRouteTests.Payloads("rc-opti");
        yield return new Case("OptiScaler", opti, Engine.ManifestNameX64, "dlssnr_amd_pass2.dll",
            () => Work.Install(opti, optiSrc, Preset.OptiScaler, optiPins),
            () => Work.Uninstall(opti, Preset.OptiScaler));
    }

    private static void Clean(Report report, string what)
    {
        Assert.False(report.Failed, report.ToLog(what));
        Assert.DoesNotContain(report.Lines, l => l.Level == Level.Err);
    }

    [Fact]
    public void EveryRouteInstallsOverWhateverItsFolderBecame()
    {
        foreach (var c in Cases())
        {
            var manifest = Path.Combine(c.Dir, c.ManifestName);
            Clean(c.Install(), $"{c.Name}: first install");
            var written = File.ReadAllText(manifest);

            // Another layout: read as it is, nothing to clear.
            File.WriteAllText(manifest, (char)0xFEFF + System.Text.Json.JsonSerializer.Serialize(
                System.Text.Json.JsonDocument.Parse(written).RootElement).ReplaceLineEndings("\r\n"));
            var relaid = c.Install();
            Clean(relaid, $"{c.Name}: over a reformatted record");
            Assert.False(Fixture.HasAny(relaid, Work.ClearsTheWay), relaid.ToLog($"{c.Name}: nothing was in the way"));

            // Cut short: cleared, set aside, and a fresh record written.
            File.WriteAllText(manifest, written[..(written.Length / 2)]);
            var cut = c.Install();
            Clean(cut, $"{c.Name}: over a record cut short");
            Assert.True(Fixture.HasAny(cut, Work.ClearsTheWay), cut.ToLog($"{c.Name}: cut short"));
            Assert.True(File.Exists(manifest + ".unreadable"), $"{c.Name}: the unreadable record is kept aside");
            Assert.Equal("installed", Manifest.Decode(File.ReadAllText(manifest)).State);

            // An install cut off in the middle.
            var m = Manifest.Decode(File.ReadAllText(manifest));
            m.State = "installing";
            Manifest.WriteAtomic(c.Dir, m);
            Clean(c.Install(), $"{c.Name}: over an interrupted install");
            Assert.Equal("installed", Manifest.Decode(File.ReadAllText(manifest)).State);

            // A file of ours replaced by hand, then the backups deleted.
            var ours = Path.Combine(c.Dir, c.Ours);
            var shipped = File.ReadAllBytes(ours);
            File.WriteAllText(ours, "a build copied in by hand");
            Clean(c.Install(), $"{c.Name}: over a file replaced by hand");
            Assert.Equal(shipped, File.ReadAllBytes(ours));
            Directory.Delete(Path.Combine(c.Dir, Engine.BackupDir), recursive: true);

            Clean(c.Uninstall(), $"{c.Name}: uninstall with the backups gone");
            Assert.False(GameScanner.IsInstalled(c.Dir), $"{c.Name}: nothing of ours is left installed");
        }
    }

    /// <summary>Garbage where the record was, and nothing else: the uninstall takes everything of ours by
    /// name, sets the record aside, and says so as a warning rather than failing.</summary>
    [Fact]
    public void AnUnreadableRecordIsSetAsideByUninstall()
    {
        foreach (var c in Cases())
        {
            Clean(c.Install(), $"{c.Name}: install");
            var manifest = Path.Combine(c.Dir, c.ManifestName);
            File.WriteAllText(manifest, "not a manifest");
            var removed = c.Uninstall();
            Clean(removed, $"{c.Name}: uninstall over garbage");
            Assert.False(File.Exists(manifest), $"{c.Name}: the garbage no longer stands in the way");
            Assert.True(File.Exists(manifest + ".unreadable"), $"{c.Name}: and is kept aside");
            Assert.False(GameScanner.IsInstalled(c.Dir), $"{c.Name}: nothing of ours is left installed");
            Clean(c.Install(), $"{c.Name}: install again");
        }
    }

    /// <summary>"... and this line goes away.. Nothing was left half-written": a refusal that is a sentence already
    /// gets no second period.</summary>
    [Fact]
    public void ARolledBackInstallSaysItsReasonWithOnePeriod()
    {
        const string tail = ". Nothing was left half-written: the install rolled itself back.";
        Assert.Equal("Locked" + tail, Work.RolledBack(new InstallException("Locked")));
        Assert.Equal("close it and this line goes away" + tail, Work.RolledBack(new InstallException("close it and this line goes away.")));
    }
}
