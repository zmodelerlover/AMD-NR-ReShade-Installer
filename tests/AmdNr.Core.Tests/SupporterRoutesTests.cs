using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>The 32-bit bridge with danielblnc's supporter build, and with a payload whose pins are not the
/// engine's own. GTA IV is the case: the bridge installer checked the runtime it had just downloaded against
/// the engine's constant rather than the payload's pin, so every 32-bit install failed with "SHA256
/// mismatch" once the pin moved -- with the supporter build chosen or not. Stand-ins only.</summary>
public class SupporterRoutesTests
{
    /// <summary>A 32-bit game, the bridge release the fixture builds, and pins taken from that release -- none
    /// of which are the engine's constants.</summary>
    private static (string Exe, string Release, PayloadPins Pins) Bridge(string tag, UserRuntime build)
    {
        var game = Fixture.Temp(tag);
        var exe = Path.Combine(game, "GTAIV.exe");
        File.WriteAllBytes(exe, Fixture.Pe(false));
        var app = UninstallInvariantTests.X86Release(tag).Installer;
        Assert.NotEqual(Engine.RuntimeSha, app.RuntimeSha);
        return (exe, app.Release, new PayloadPins
        {
            AddonSha = "", AddonSize = 0,
            RuntimeSha = app.RuntimeSha, WeightsSha = app.WeightsSha, ReShade32Sha = app.ReShadeSha, D3d8To9Sha = app.D3d8To9Sha,
            BridgeVersion = "0.7.0", UserRuntimes = [build],
        });
    }

    /// <summary>The payload as the out-of-date check reads it: the bridge at <paramref name="bridge"/>, the
    /// download's runtime pinned, the installed bridge frontend, and the build listed.</summary>
    private static PayloadManifest Payload(PayloadPins pins, string bridge, UserRuntime build, string addon32)
    {
        var change = build.Changes.Single();
        return PayloadManifest.Parse($$"""
            {
              "schema": 1,
              "components": {
                "bridge": { "version": "{{bridge}}", "files": [ { "name": "amd-nr.addon32", "size": 1, "sha256": "{{addon32}}" } ] },
                "runtime": { "version": "1", "files": [ { "name": "dlssnr_amd_pass1.dll", "size": 1, "sha256": "{{pins.RuntimeSha}}" } ] }
              },
              "user_runtimes": [
                { "runtime": "{{build.Runtime}}", "name": "{{build.Name}}", "addon_since": "{{build.AddonSince}}",
                  "original_sha256": "{{build.OriginalSha256}}", "original_size": {{build.OriginalSize}},
                  "patched_sha256": "{{build.PatchedSha256}}",
                  "changes": [ { "patch": "{{change.Patch}}", "offset": "{{change.Offset}}", "before": "{{change.Before}}", "after": "{{change.After}}" } ] }
              ]
            }
            """);
    }

    private static string Pass1(string exe) => Engine.HashFile(Path.Combine(Path.GetDirectoryName(exe)!, Work.RuntimeName));

    [Fact]
    public void TheBridgeChecksThePayloadsPinsAndNotTheEnginesOwn()
    {
        var (exe, release, pins) = Bridge("sr-pins", UserRuntimeTests.Build(UserRuntimeTests.Dll(31)));
        foreach (var preset in new[] { Preset.X86Dx9, Preset.X86Dx11, Preset.X86Dx8 })
        {
            var report = Work.Install(exe, release, preset, pins);
            Assert.False(report.Failed, report.ToLog(preset.ToString()));
            Assert.Equal(pins.RuntimeSha, Pass1(exe));
            Assert.False(Work.Uninstall(exe, preset).Failed);
        }
    }

    /// <summary>Picked from his setup, then kept and preselected on an update, then back to the download: the
    /// bridge writes, records and judges the patched build, and uninstall takes it.</summary>
    [Fact]
    public void TheBridgeTakesTheSupporterBuildFromAFileAndFromTheKeptCopy()
    {
        var dll = UserRuntimeTests.Dll(32);
        var build = UserRuntimeTests.Build(dll);
        var (exe, release, pins) = Bridge("sr-supporter", build);
        var setup = Path.Combine(Fixture.Temp("sr-setup"), "dlssnr_on_amd_setup.exe");
        File.WriteAllBytes(setup, UserRuntimeTests.Setup(dll));

        Assert.Single(Work.OfferedRuntimes(pins, Preset.X86Dx9));
        var check = Work.Preflight(exe, release, Preset.X86Dx9, pins, ownRuntime: setup);
        Assert.True(Fixture.HasAny(check, "is checked and patched for the add-on"), check.ToLog("preflight"));

        var picked = Work.Install(exe, release, Preset.X86Dx9, pins, ownRuntime: setup);
        Assert.False(picked.Failed, picked.ToLog("from the setup"));
        Assert.Equal(build.PatchedSha256, Pass1(exe));
        var dir = Path.GetDirectoryName(exe)!;
        var recorded = Manifest.Decode(File.ReadAllText(Path.Combine(dir, Engine.ManifestName)));
        Assert.Equal(build.PatchedSha256, recorded.Entries.Single(e => e.Name == Work.RuntimeName).Hash);
        Assert.Same(build, Work.UserRuntimeIn(dir, [build]));
        var addon32 = recorded.Entries.Single(e => e.Name == Work.Addon32Name).Hash;
        Assert.False(Work.PayloadMovedOn(dir, Payload(pins, "0.7.0", build, addon32)));
        Assert.True(Work.PayloadMovedOn(dir, Payload(pins, "0.6.9", build, addon32)));

        // The update: the copy kept from that pick, as the sheet preselects it.
        UserRuntime.Keep(setup, [build]);
        var update = Work.Install(exe, release, Preset.X86Dx9, pins, ownRuntime: build.Kept());
        Assert.False(update.Failed, update.ToLog("kept copy"));
        Assert.Equal(build.PatchedSha256, Pass1(exe));

        var download = Work.Install(exe, release, Preset.X86Dx9, pins);
        Assert.False(download.Failed, download.ToLog("download again"));
        Assert.Equal(pins.RuntimeSha, Pass1(exe));

        Assert.False(Work.Install(exe, release, Preset.X86Dx9, pins, ownRuntime: build.Kept()).Failed);
        var removed = Work.Uninstall(exe, Preset.X86Dx9);
        Assert.False(removed.Failed, removed.ToLog("uninstall"));
        Assert.False(File.Exists(Path.Combine(dir, Work.RuntimeName)));
    }

    /// <summary>An OptiScaler folder whose three passes are danielblnc's 0.6.0, supplied or taken from his version.dll,
    /// is not out of date against the shipped payload's 0.6.0; one on 0.5.1, 0.5.0 or 0.4.1 is.</summary>
    [Fact]
    public void OptiScalerOnTheSupporterBuildIsNotOutOfDate()
    {
        var shipped = PayloadManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "payload.json")));
        foreach (var (sha, outdated) in new[]
                 {
                     ("195c4a891b6eac4c1cb7671e10ff62bbbe2b17f1dfae1344dc5a6714e4775721", false),
                     ("493b4a3b80a21f7255109172ab7bb01ba08d35f2941718f441768f1abfc48acd", true),
                     ("cddfb09e019347957bf7b96c95c0e900e8d3062dfaed697a8a96b0a039aec31a", true),
                     ("823063eb4c76b1334fd1800c41798873ae61d4016af0406f1f0b9dce57b1d376", true),
                 })
        {
            var dir = Fixture.Temp("sr-opti-current");
            var manifest = new Manifest(Preset.OptiScaler.ManifestPreset(), Route.X64);
            foreach (var pass in new[] { "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll" })
                manifest.Entries.Add(new Entry { Name = pass, Hash = sha, Owned = true });
            Manifest.WriteAtomic(dir, manifest);
            Assert.Equal(outdated, Work.PayloadMovedOn(dir, shipped));
        }
    }

    /// <summary>The pins a payload carries for ReShade and the 32-bit extras are the ones installs check.</summary>
    [Fact]
    public void ThePinsAreThePayloads()
    {
        var shipped = PayloadManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "payload.json"))).Pins();
        Assert.Equal((Engine.ReShade64Sha, Engine.ReShadeSha, Engine.D3d8To9Sha),
            (shipped.ReShade64Sha, shipped.ReShade32Sha, shipped.D3d8To9Sha));
        var moved = PayloadManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "payload.json"))
            .Replace(Engine.D3d8To9Sha, new string('d', 64))).Pins();
        Assert.Equal(new string('d', 64), moved.D3d8To9Sha);
    }
}
