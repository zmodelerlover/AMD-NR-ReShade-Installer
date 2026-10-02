using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>danielblnc's builds a person supplies (payload.json's user_runtimes): recognised by hash in a
/// version.dll, a patched copy or his setup, patched for the add-on only over the bytes each change expects,
/// kept by hash, and never taken on trust. Every build here is a synthetic stand-in: no real runtime or
/// setup is in this repository, and none may be.</summary>
public class UserRuntimeTests
{
    private const int At = 0x300;
    private static readonly byte[] After = [0x31, 0xc0, 0x90, 0x90, 0x90, 0x90];

    /// <summary>A PE32+ DLL: headers, then one section of random bytes to the end of the file.</summary>
    internal static byte[] Dll(int seed)
    {
        var b = new byte[0x200 + 0x4000];
        new Random(seed).NextBytes(b.AsSpan(0x200));
        Header(b, dll: true, rawSize: b.Length - 0x200);
        return b;
    }

    private static void Header(byte[] b, bool dll, int rawSize)
    {
        b[0] = (byte)'M';
        b[1] = (byte)'Z';
        BitConverter.GetBytes(0x80).CopyTo(b, 0x3c);
        "PE\0\0"u8.CopyTo(b.AsSpan(0x80));
        BitConverter.GetBytes(Engine.MachineX64).CopyTo(b, 0x84);
        BitConverter.GetBytes((ushort)1).CopyTo(b, 0x86);
        BitConverter.GetBytes((ushort)240).CopyTo(b, 0x94);
        BitConverter.GetBytes((ushort)(dll ? 0x2022 : 0x0022)).CopyTo(b, 0x96);
        BitConverter.GetBytes((ushort)0x20b).CopyTo(b, 0x98);
        BitConverter.GetBytes(rawSize).CopyTo(b, 0x188 + 16);
        BitConverter.GetBytes(0x200).CopyTo(b, 0x188 + 20);
    }

    /// <summary>His setup's shape from v0.3.3 on: an executable whose .rdata holds the DLL as a byte array,
    /// after two strings, with more of the setup after it -- and an "MZ" that is not a header.</summary>
    internal static byte[] Setup(byte[] dll)
    {
        var before = "dlssnr_on_amd_weights.bin\0nvngx_dlssnr.dll\0MZ, but not a header\0"u8.ToArray();
        var b = new byte[0x200 + before.Length + dll.Length + 1234];
        Header(b, dll: false, rawSize: b.Length - 0x200);
        before.CopyTo(b, 0x200);
        dll.CopyTo(b, 0x200 + before.Length);
        new Random(7).NextBytes(b.AsSpan(0x200 + before.Length + dll.Length));
        return b;
    }

    internal static UserRuntime Build(byte[] original, bool complete = true, string? patchedSha = null, string? before = null)
    {
        var patched = (byte[])original.Clone();
        After.CopyTo(patched, At);
        return new UserRuntime
        {
            Runtime = "DLSS-NR-on-AMD stand-in",
            Name = "9.9.9",
            AddonSince = "0.7.0",
            OriginalSha256 = Engine.Sha(original),
            OriginalSize = (ulong)original.Length,
            PatchedSha256 = complete ? patchedSha ?? Engine.Sha(patched) : "",
            Changes = complete
                ? [new RuntimeChange
                  {
                      Patch = "setup-thread", Offset = $"0x{At:x}",
                      Before = before ?? Convert.ToHexStringLower(original.AsSpan(At, After.Length)),
                      After = Convert.ToHexStringLower(After),
                  }]
                : [],
        };
    }

    /// <summary>The fixture's add-on payload, pinned for an add-on at <paramref name="addon"/> with this build listed.</summary>
    internal static (string Src, PayloadPins Pins) Payloads(string tag, UserRuntime build, string addon = "0.7.0")
    {
        var (src, pins) = Fixture.Payloads(tag);
        return (src, new PayloadPins
        {
            AddonSha = pins.AddonSha, AddonSize = pins.AddonSize,
            RuntimeSha = pins.RuntimeSha, RuntimeSize = pins.RuntimeSize,
            WeightsSha = pins.WeightsSha, WeightsSize = pins.WeightsSize,
            AddonVersion = addon, BridgeVersion = addon, UserRuntimes = [build],
        });
    }

    /// <summary>The same pins as a payload list, the way the app reads them.</summary>
    private static PayloadManifest Manifest(PayloadPins pins, string addon, UserRuntime build)
    {
        var change = build.Changes.Single();
        return PayloadManifest.Parse($$"""
            {
              "schema": 1,
              "components": {
                "addon": { "version": "{{addon}}", "files": [
                  { "name": "amd-nr.addon64", "size": {{pins.AddonSize}}, "sha256": "{{pins.AddonSha}}", "url": "https://example.invalid/a" } ] },
                "runtime": { "version": "1", "files": [
                  { "name": "dlssnr_amd_pass1.dll", "size": {{pins.RuntimeSize}}, "sha256": "{{pins.RuntimeSha}}", "url": "https://example.invalid/r" },
                  { "name": "dlssnr_on_amd_weights.bin", "size": {{pins.WeightsSize}}, "sha256": "{{pins.WeightsSha}}", "url": "https://example.invalid/w" } ] }
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

    private static string Sha(string dir, string name) => Engine.HashFile(Path.Combine(dir, name));

    [Fact]
    public void HisVersionDllIsTakenAndKept()
    {
        var dll = Dll(1);
        var build = Build(dll);
        var path = Path.Combine(Fixture.Temp("ur-dll"), "version.dll");
        File.WriteAllBytes(path, dll);

        var (found, original) = UserRuntime.Read(path, [build]);
        Assert.Same(build, found);
        Assert.Equal(dll, original);

        Assert.Null(build.Kept());
        Assert.Same(build, UserRuntime.Keep(path, [build]));
        Assert.Equal(dll, File.ReadAllBytes(build.Kept()!));
        Assert.StartsWith(AppPaths.Runtimes, build.Kept()!);
    }

    [Fact]
    public void HisSetupGivesUpTheBuildInsideIt()
    {
        var dll = Dll(2);
        var build = Build(dll);
        var setup = Setup(dll);
        Assert.Equal(dll, UserRuntime.Extract(setup));

        var path = Path.Combine(Fixture.Temp("ur-setup"), "dlssnr_on_amd_setup.exe");
        File.WriteAllBytes(path, setup);
        var (found, original) = UserRuntime.Read(path, [build]);
        Assert.Same(build, found);
        Assert.Equal(dll, original);

        // Two DLLs inside is a layout that moved, not one to guess between; a plain DLL is not a setup.
        Assert.Null(UserRuntime.Extract([.. setup, .. Dll(3)]));
        Assert.Null(UserRuntime.Extract(dll));
    }

    [Fact]
    public void AFileThatIsNotAListedBuildIsRefused()
    {
        var build = Build(Dll(4));
        var dir = Fixture.Temp("ur-wrong");
        var other = Path.Combine(dir, "version.dll");
        File.WriteAllBytes(other, Dll(5));
        var e = Assert.Throws<InstallException>(() => UserRuntime.Read(other, [build]));
        Assert.Contains("is neither danielblnc's runtime 9.9.9", e.Message);

        var setup = Path.Combine(dir, "dlssnr_on_amd_setup.exe");
        File.WriteAllBytes(setup, Setup(Dll(6)));
        e = Assert.Throws<InstallException>(() => UserRuntime.Read(setup, [build]));
        Assert.Contains("carries danielblnc's runtime with the SHA-256", e.Message);

        // And an install handed it writes nothing.
        var game = Fixture.Temp("ur-wrong-game");
        var (src, pins) = Payloads("ur-wrong", build);
        var report = Work.Install(game, src, Preset.Dx11, pins, ownRuntime: other);
        Assert.True(report.Failed);
        Assert.False(File.Exists(Path.Combine(game, Work.AddonName)));
    }

    [Fact]
    public void AChangeOverBytesItDoesNotExpectIsRefused()
    {
        var dll = Dll(8);
        var build = Build(dll, before: "000000000000");
        var e = Assert.Throws<InstallException>(() => build.Patched(dll));
        Assert.Contains("are not the ones the setup-thread change expects", e.Message);

        var game = Fixture.Temp("ur-before");
        var source = Path.Combine(Fixture.Temp("ur-before-src"), "version.dll");
        File.WriteAllBytes(source, dll);
        var (src, pins) = Payloads("ur-before", build);
        var report = Work.Install(game, src, Preset.Dx11, pins, ownRuntime: source);
        Assert.True(report.Failed, report.ToLog("before"));
        Assert.False(File.Exists(Path.Combine(game, Work.RuntimeName)));
    }

    [Fact]
    public void APatchThatDoesNotComeOutAtTheListedHashIsRefused()
    {
        var dll = Dll(9);
        var build = Build(dll, patchedSha: new string('a', 64));
        var game = Fixture.Temp("ur-patched");
        var source = Path.Combine(Fixture.Temp("ur-patched-src"), "version.dll");
        File.WriteAllBytes(source, dll);
        var (src, pins) = Payloads("ur-patched", build);
        var report = Work.Install(game, src, Preset.Dx11, pins, ownRuntime: source);
        Assert.True(report.Failed);
        Assert.True(Fixture.HasErr(report, "not to the " + new string('a', 64)), report.ToLog("patched"));
        Assert.False(File.Exists(Path.Combine(game, Work.AddonName)));
    }

    [Fact]
    public void TheKeptCopyIsReusedAndCheckedAgainEveryTime()
    {
        var dll = Dll(10);
        var build = Build(dll);
        var source = Path.Combine(Fixture.Temp("ur-kept"), "dlssnr_on_amd_setup.exe");
        File.WriteAllBytes(source, Setup(dll));
        UserRuntime.Keep(source, [build]);
        File.Delete(source);

        // Another game, with nothing of his in it, finds the kept copy and installs from it.
        var game = Fixture.Temp("ur-kept-game");
        var kept = Work.FindUserRuntime(build, game);
        Assert.Equal(build.Kept(), kept);
        var (src, pins) = Payloads("ur-kept", build);
        var report = Work.Install(game, src, Preset.Dx11, pins, ownRuntime: kept);
        Assert.False(report.Failed, report.ToLog("kept"));
        Assert.Equal(build.PatchedSha256, Sha(game, Work.RuntimeName));

        // A kept copy that no longer hashes to the build is not one: not found, and refused when handed over.
        var bytes = File.ReadAllBytes(kept!);
        bytes[^1] ^= 0xff;
        File.WriteAllBytes(kept!, bytes);
        Assert.Null(build.Kept());
        Assert.Null(Work.FindUserRuntime(build, Fixture.Temp("ur-kept-none")));
        var again = Work.Install(Fixture.Temp("ur-kept-again"), src, Preset.Dx11, pins, ownRuntime: kept);
        Assert.True(again.Failed, again.ToLog("tampered"));
    }

    /// <summary>His setup ran in the game: version.dll is his runtime and loader, his weights beside it. The
    /// add-on takes that build patched, version.dll goes to the backup, an update keeps the build with nothing
    /// kept on this machine, the folder is not out of date over it, and uninstall puts his files back.</summary>
    [Fact]
    public void AnInstallOnHisBuildKeepsItAcrossAnUpdate()
    {
        var dll = Dll(11);
        var build = Build(dll);
        var game = Fixture.Temp("ur-update");
        File.WriteAllBytes(Path.Combine(game, "version.dll"), dll);
        File.WriteAllText(Path.Combine(game, Work.WeightsName), "his weights");
        var (src, pins) = Payloads("ur-update", build);

        Assert.Same(build, Work.UserRuntimeIn(game, [build]));
        var check = Work.Preflight(game, src, Preset.Dx11, pins, ownRuntime: Path.Combine(game, "version.dll"));
        Assert.False(check.Failed, check.ToLog("preflight"));
        Assert.True(Fixture.HasAny(check, "moves it to the backup"), check.ToLog("preflight"));

        var first = Work.Install(game, src, Preset.Dx11, pins, ownRuntime: Path.Combine(game, "version.dll"));
        Assert.False(first.Failed, first.ToLog("first"));
        Assert.Equal(build.PatchedSha256, Sha(game, Work.RuntimeName));
        Assert.False(File.Exists(Path.Combine(game, "version.dll")));
        Assert.Same(build, Work.UserRuntimeIn(game, [build]));

        // Pinned to another runtime, the payload does not call it out of date while its add-on runs the build...
        Assert.False(Work.PayloadMovedOn(game, Manifest(pins, "0.7.0", build)));
        Assert.True(Work.PayloadMovedOn(game, Manifest(pins, "0.6.9", build)));

        // ...and the update reads it back out of the folder, since version.dll is in the backup.
        var source = Work.FindUserRuntime(build, game);
        Assert.NotNull(source);
        var update = Work.Install(game, src, Preset.Dx11, pins, ownRuntime: source);
        Assert.False(update.Failed, update.ToLog("update"));
        Assert.Equal(build.PatchedSha256, Sha(game, Work.RuntimeName));

        var removed = Work.Uninstall(game, Preset.Dx11);
        Assert.False(removed.Failed, removed.ToLog("uninstall"));
        Assert.Equal(dll, File.ReadAllBytes(Path.Combine(game, "version.dll")));
        Assert.Equal("his weights", File.ReadAllText(Path.Combine(game, Work.WeightsName)));
        Assert.False(File.Exists(Path.Combine(game, Work.RuntimeName)));
    }

    [Fact]
    public void AnAddOnThatDoesNotRunItGetsTheDownloadAndSaysSo()
    {
        var dll = Dll(12);
        var build = Build(dll);
        var source = Path.Combine(Fixture.Temp("ur-old-src"), "version.dll");
        File.WriteAllBytes(source, dll);
        var game = Fixture.Temp("ur-old");
        var (src, pins) = Payloads("ur-old", build, addon: "0.6.9");
        Assert.Empty(Work.OfferedRuntimes(pins, Preset.Dx11));

        var report = Work.Install(game, src, Preset.Dx11, pins, ownRuntime: source);
        Assert.False(report.Failed, report.ToLog("old add-on"));
        Assert.Equal(pins.RuntimeSha, Sha(game, Work.RuntimeName));
        Assert.Contains(report.Lines, l => l.Level == Level.Warn
            && l.Text.Contains("runtime 9.9.9, the build you supplied, is not used: add-on v0.6.9 does not run it"));
    }

    /// <summary>The build chosen for the game and no file of it found: the download goes in, and the report
    /// says the build is not used and why, both on an add-on that does not run it and on one that does.</summary>
    [Fact]
    public void AChosenBuildWithNoCopyIsNotLeftOutInSilence()
    {
        var build = Build(Dll(14));
        foreach (var (addon, why) in new[]
                 {
                     ("0.6.9", "add-on v0.6.9 does not run it (v0.7.0 and later do)"),
                     ("0.7.0", "no copy of it is kept on this machine, and none was supplied"),
                 })
        {
            var game = Fixture.Temp($"ur-unfound-{addon}");
            var (src, pins) = Payloads($"ur-unfound-{addon}", build, addon);
            var check = Work.Preflight(game, src, Preset.Dx11, pins, wantedRuntime: build);
            Assert.Contains(check.Lines, l => l.Level == Level.Warn && l.Text.Contains(why));

            var report = Work.Install(game, src, Preset.Dx11, pins, wantedRuntime: build);
            Assert.False(report.Failed, report.ToLog(addon));
            Assert.Equal(pins.RuntimeSha, Sha(game, Work.RuntimeName));
            Assert.Contains(report.Lines, l => l.Level == Level.Warn
                && l.Text == $"danielblnc's runtime 9.9.9, the build chosen for this game, is not used: {why}. "
                   + "The download's runtime goes in instead.");
        }
    }

    [Fact]
    public void AnEntryWithoutItsPatchIsNotOfferedOnTheReShadeRoutes()
    {
        var dll = Dll(13);
        var incomplete = Build(dll, complete: false);
        var (src, pins) = Payloads("ur-incomplete", incomplete);
        Assert.False(incomplete.Patchable);
        Assert.Empty(Work.OfferedRuntimes(pins, Preset.Dx11));
        Assert.Empty(Work.OfferedRuntimes(pins, Preset.X86Dx9));
        Assert.Single(Work.OfferedRuntimes(Payloads("ur-complete", Build(dll)).Pins, Preset.X86Dx9));

        // Handed to an install anyway, the download goes in and the report says why.
        var source = Path.Combine(Fixture.Temp("ur-incomplete-src"), "version.dll");
        File.WriteAllBytes(source, dll);
        var game = Fixture.Temp("ur-incomplete-game");
        var report = Work.Install(game, src, Preset.Dx11, pins, ownRuntime: source);
        Assert.False(report.Failed, report.ToLog("incomplete"));
        Assert.Equal(pins.RuntimeSha, Sha(game, Work.RuntimeName));
        Assert.True(Fixture.HasAny(report, "no patch for it yet"), report.ToLog("incomplete"));
    }

    /// <summary>The shipped list carries 0.6.0 alone now that 0.5.1 is public and the download: offered on the ReShade
    /// routes from add-on v0.7.6 and on OptiScaler from 0.4.7, which runs it as it is.</summary>
    [Fact]
    public void TheShippedListOffersTheSupporterBuildOnlyWhereItRuns()
    {
        var shipped = PayloadManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "payload.json")));
        var build = Assert.Single(shipped.Pins().UserRuntimes);
        Assert.Equal(("0.6.0", "195c4a891b6eac4c1cb7671e10ff62bbbe2b17f1dfae1344dc5a6714e4775721", 56_677_888UL, "0.7.6"),
            (build.Name, build.OriginalSha256, build.OriginalSize, build.AddonSince));
        Assert.Same(build, Assert.Single(Work.OfferedRuntimes(shipped.Pins(), Preset.Dx11)));

        var opti = shipped.Newest(PayloadManifest.OptiScalerComponent).Pins();
        Assert.Equal("0.4.9-amd-nr", opti.OptiScalerVersion);
        Assert.Same(build, Assert.Single(Work.OfferedRuntimes(opti, Preset.OptiScaler)));
        Assert.Empty(Work.OfferedRuntimes(shipped.With(shipped.Offered(PayloadManifest.OptiScalerComponent)
            .First(r => r.Version == "0.4.6-amd-nr")).Pins(), Preset.OptiScaler));
    }

    /// <summary>Against the real setup, when AMDNR_TEST_RUNTIME_SETUP points at one (skipped otherwise: it is
    /// danielblnc's, and a supporter build at that): the build inside is the one the shipped list names, and
    /// the first OptiScaler that runs it takes it as its runtime.</summary>
    [Fact]
    public void ARealSetupGivesUpTheBuildTheListNames()
    {
        var setup = Environment.GetEnvironmentVariable("AMDNR_TEST_RUNTIME_SETUP");
        if (string.IsNullOrWhiteSpace(setup) || !File.Exists(setup)) return;
        var shipped = PayloadManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "payload.json")))
            .Newest(PayloadManifest.OptiScalerComponent);
        var (build, original) = UserRuntime.Read(setup, shipped.Pins().UserRuntimes);
        Assert.Equal(build.OriginalSha256, Engine.Sha(original));
        // And the patch the list gives it makes the very file the add-on accepts (its kSha256 for this build).
        Assert.Equal(build.PatchedSha256, Engine.Sha(build.Patched(original)));

        var game = Fixture.Temp("ur-real");
        var (src, pins) = OptiScalerRouteTests.Payloads("ur-real");
        pins = new PayloadPins
        {
            AddonSha = pins.AddonSha, AddonSize = pins.AddonSize, WeightsSha = pins.WeightsSha, WeightsSize = pins.WeightsSize,
            OptiFiles = pins.OptiFiles, OptiRuntimeName = pins.OptiRuntimeName, OptiRuntimeSha = pins.OptiRuntimeSha,
            OptiRuntimeSize = pins.OptiRuntimeSize, OptiScalerVersion = $"{Work.OptiScalerSince(build.OriginalSha256)}-amd-nr",
            OptiRuntimeVersion = "0.4.2",
            UserRuntimes = shipped.Pins().UserRuntimes,
        };
        var report = Work.Install(game, src, Preset.OptiScaler, pins, ownRuntime: setup);
        Assert.False(report.Failed, report.ToLog("real"));
        Assert.True(Fixture.HasAny(report, "from your own file"), report.ToLog("real"));
        foreach (var pass in new[] { "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll" })
            Assert.Equal(build.OriginalSha256, Sha(game, pass));
        Assert.Same(build, Work.UserRuntimeIn(game, pins.UserRuntimes));
        Assert.False(Work.Uninstall(game, Preset.OptiScaler).Failed);

        // And the 32-bit bridge takes it patched, checked against the list's pins and not the engine's.
        var x86 = Fixture.Temp("ur-real-x86");
        var exe = Path.Combine(x86, "Game.exe");
        File.WriteAllBytes(exe, Fixture.Pe(false));
        var bridge = UninstallInvariantTests.X86Release("ur-real").Installer;
        var bridgePins = new PayloadPins
        {
            AddonSha = "", AddonSize = 0, RuntimeSha = bridge.RuntimeSha, WeightsSha = bridge.WeightsSha,
            ReShade32Sha = bridge.ReShadeSha, D3d8To9Sha = bridge.D3d8To9Sha, BridgeVersion = build.AddonSince,
            UserRuntimes = shipped.Pins().UserRuntimes,
        };
        var bridged = Work.Install(exe, bridge.Release, Preset.X86Dx9, bridgePins, ownRuntime: setup);
        Assert.False(bridged.Failed, bridged.ToLog("real x86"));
        Assert.Equal(build.PatchedSha256, Sha(x86, Work.RuntimeName));
        Assert.False(Work.Uninstall(exe, Preset.X86Dx9).Failed);
    }
}
