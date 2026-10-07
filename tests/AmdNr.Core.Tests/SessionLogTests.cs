using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class SessionLogTests
{
    private static readonly DateTime When = new(2026, 9, 29, 21, 40, 0);

    [Fact]
    public void TheRuntimeLogIsReadFromItsLastStartAndCountsItsJobs()
    {
        var log = """
            dlssnr_amd v0.4.1 (build e0d070a0) loaded into Game.exe as version.dll from E:\G\; log x; settings y
            network job 900 done in 9 ms (6.4 ms network on the GPU, 2.6 ms waiting for the capture; history on, zero-copy)
            CRASH: exception 0xc0000005 at 0000 in x (thread 1, last job 900, mode inline)
            dlssnr_amd v0.4.3 (build 6fd1a1f6) loaded into Game.exe as version.dll from E:\G\; log x; settings y
            note: first-chance exception 0xc0000005 at 00000281F4C38CE8 outside any module (last job 1); raised and handled by the game itself (RE Engine does this throughout), not a crash
            network job 3350 done in 9 ms (6.4 ms network on the GPU, 2.6 ms waiting for the capture; history on, zero-copy)
            timing (avg of 200 jobs): worker wall 9.5 ms = queued 0.0 + inputs 0.0 + wait-for-capture 2.7 + network 6.5 + flag+sync 0.3; flag readback 0.17
            network job 3400 done in 9 ms (6.3 ms network on the GPU, 2.7 ms waiting for the capture; history on, zero-copy)
            """;
        var r = SessionLog.Runtime(log, When);
        // The crash was the session before; a handled first-chance exception is not one.
        Assert.Equal(SessionOutcome.Ran, r.Outcome);
        Assert.Equal(3400, r.Frames);
        Assert.Equal(6.5, r.NetworkMs);
        Assert.Equal("0.4.3", r.Runtime);
        Assert.Null(r.Line);
    }

    [Fact]
    public void ACrashOrARemovedDeviceIsACrashEvenAfterFramesRan()
    {
        var crash = SessionLog.Runtime("""
            dlssnr_amd v0.5.0 (build 1) loaded into Game.exe as version.dll from x
            network job 50 done in 9 ms (6.4 ms network on the GPU)
            CRASH: exception 0xc0000005 at 0000 in dxgi.dll (thread 1, last job 50, mode inline)
            """, When);
        Assert.Equal(SessionOutcome.Crashed, crash.Outcome);
        Assert.StartsWith("CRASH:", crash.Line);

        var removed = SessionLog.Runtime("""
            dlssnr_amd v0.5.0 (build 1) loaded into Game.exe as version.dll from x
            network job 50 done in 9 ms
            present failed (DEVICE REMOVED: the GPU faulted or reset earlier in this session)
            """, When);
        Assert.Equal(SessionOutcome.Crashed, removed.Outcome);
    }

    [Fact]
    public void ARuntimeThatNeverStartedSaysWhyAndOneThatWaitedSaysNothing()
    {
        var failed = SessionLog.Runtime("""
            dlssnr_amd v0.5.0 (build 1) loaded into Game.exe as version.dll from x
            env: HIP: no usable device (hipErrorNoDevice)
            setup failed; idle
            """, When);
        Assert.Equal(SessionOutcome.Failed, failed.Outcome);
        Assert.Equal("env: HIP: no usable device (hipErrorNoDevice)", failed.Line);

        var idle = SessionLog.Runtime("dlssnr_amd v0.5.0 (build 1) loaded into Game.exe as version.dll from x\n", When);
        Assert.Equal(SessionOutcome.NoFrames, idle.Outcome);
        Assert.Null(idle.Frames);
    }

    [Fact]
    public void TheAddOnCountsFramesAndSaysWhyItStoodDown()
    {
        var ran = SessionLog.Addon("""
            AMD Neural Rendering: uncatalogued target (runs the same as a listed one)
            engine ready.
            frame 1 processed (0 skipped)
            frame 3600 processed (0 skipped)
            swapchain going away (resize 0) after 3638 frames; draining and dropping everything sized to it.
            """, When);
        Assert.Equal(SessionOutcome.Ran, ran.Outcome);
        Assert.Equal(3600, ran.Frames);

        var off = SessionLog.Addon("""
            AMD Neural Rendering: uncatalogued target
            HIP: amdhip64_7.dll failed to load (error 126). AMD HIP 7 is required.
            """, When);
        Assert.Equal(SessionOutcome.Failed, off.Outcome);
        Assert.StartsWith("HIP: amdhip64_7.dll failed", off.Line);
    }

    [Fact]
    public void MochizukiRanWhenItsNetworkWasReady()
    {
        var r = SessionLog.Mochizuki("""
            21:56:26.889 [mochizuki] Vulkan device AMD Radeon RX 9070 XT, queue family 0
            21:56:30.305 [mochizuki] network ready at 1920x1080 in 3.4 s
            21:56:51.846 [mochizuki] replacing the network: network 1920x1080 9.36 ms (p95 9.53, average 9.40), 982 frames in the session
            """, When);
        Assert.Equal(SessionOutcome.Ran, r.Outcome);
        Assert.Equal(982, r.Frames);
        Assert.Equal("mochizuki", r.Runtime);
    }

    [Fact]
    public void MochizukiRefusingTheNetworkForWantOfVramFailed()
    {
        var r = SessionLog.Mochizuki("""
            21:56:26.889 [mochizuki] Vulkan device AMD Radeon RX 9060 XT, queue family 0
            21:56:27.105 [mochizuki] insufficient VRAM for 2 passes at 2560x1440
            """, When);
        Assert.Equal(SessionOutcome.Failed, r.Outcome);
        Assert.Contains("insufficient VRAM", r.Line);
    }

    [Fact]
    public void AReShadeThatLeftTheAddOnOutSaysWhy()
    {
        const string skipped = "WARN | Skipped loading add-on \"amd-nr.addon64\" because ReShade was built with limited add-on functionality.";
        Assert.Equal(skipped, SessionLog.NotLoadedWhy("INFO | Initializing\n" + skipped + "\n"));
        Assert.Contains("Another ReShade instance",
            SessionLog.NotLoadedWhy(@"ERROR | Another ReShade instance was already loaded from ""C:\x\dxgi.dll""!" + "\n"));
        Assert.Null(SessionLog.NotLoadedWhy("INFO | Searching for add-ons\n"));
    }

    [Fact]
    public void AFolderReadsItsNewestSessionAndAReShadeThatSkippedTheAddOn()
    {
        var dir = Directory.CreateTempSubdirectory("amdnr-session").FullName;
        try
        {
            Assert.Null(SessionLog.Read(dir));

            var addon = Path.Combine(dir, SessionLog.AddonLog);
            File.WriteAllText(addon, "engine ready.\nframe 120 processed (0 skipped)\n");
            File.SetLastWriteTime(addon, When);
            var runtime = Path.Combine(dir, SessionLog.RuntimeLog);
            File.WriteAllText(runtime, "dlssnr_amd v0.5.0 (build 1) loaded into G.exe\nnetwork job 110 done in 9 ms\n");
            File.SetLastWriteTime(runtime, When.AddSeconds(-5));

            var both = SessionLog.Read(dir)!;
            Assert.Equal(SessionOutcome.Ran, both.Outcome);
            Assert.Equal(120, both.Frames);
            Assert.Equal("0.5.0", both.Runtime);
            Assert.Equal(When, SessionLog.LastWrite([dir]));

            // A later start where ReShade never loaded the add-on: the logs above are the session before.
            File.WriteAllText(Path.Combine(dir, Work.AddonName), "");
            var reshade = Path.Combine(dir, SessionLog.ReShadeLog);
            File.WriteAllText(reshade, "INFO | Searching for add-ons (*.addon, *.addon64) in 'C:\\Games\\X\\bin' ...\n");
            File.SetLastWriteTime(reshade, When.AddHours(1));
            Assert.Equal(SessionOutcome.NotLoaded, SessionLog.Read(dir)!.Outcome);

            File.WriteAllText(reshade, "INFO | Registered add-on \"AMD Neural Rendering\" v0.0.0.0 using ReShade API version 20.\n");
            File.SetLastWriteTime(reshade, When.AddHours(1));
            Assert.Equal(SessionOutcome.Ran, SessionLog.Read(dir)!.Outcome);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}

public class ReleaseNotesTests
{
    [Fact]
    public void AnAssetAddressNamesItsReleaseAndMarkdownReadsAsText()
    {
        Assert.Equal(("MatheusFerreiraS", "neural-amd-opti", "v0.4.6-amd-nr"), ReleaseNotes.FromAssetUrl(
            "https://github.com/MatheusFerreiraS/neural-amd-opti/releases/download/v0.4.6-amd-nr/OptiScaler-0.4.6-amd-nr.zip"));
        Assert.Null(ReleaseNotes.FromAssetUrl("https://huggingface.co/datasets/x/y/resolve/main/a.zip"));

        var blocks = ReleaseNotes.Blocks(
            "### danielblnc's 0.5.1\r\n\r\n- **The add-on runs `0.5.1`**, see the [README](https://x/y).\r\n  carried on\r\n  - nested\r\n\r\nFull notes in CHANGELOG.");
        Assert.Equal([
            new ReleaseNotes.Block(ReleaseNotes.BlockKind.Heading, 0, "danielblnc's 0.5.1"),
            new ReleaseNotes.Block(ReleaseNotes.BlockKind.Bullet, 0, "**The add-on runs `0.5.1`**, see the README. carried on"),
            new ReleaseNotes.Block(ReleaseNotes.BlockKind.Bullet, 1, "nested"),
            new ReleaseNotes.Block(ReleaseNotes.BlockKind.Text, 0, "Full notes in CHANGELOG."),
        ], blocks);
    }
}

public class SettingsTransferTests
{
    [Fact]
    public void AReShadeExportCarriesTheWholeFilesAndGoesBackWithABackup()
    {
        var root = Directory.CreateTempSubdirectory("amdnr-transfer").FullName;
        try
        {
            var from = Directory.CreateDirectory(Path.Combine(root, "from")).FullName;
            var to = Directory.CreateDirectory(Path.Combine(root, "to")).FullName;
            var zip = Path.Combine(root, "x" + SettingsTransfer.Extension);
            Assert.False(SettingsTransfer.Export(from, RouteFamily.ReShade, "G", "0.7.0", zip));

            File.WriteAllText(Path.Combine(from, "amd-nr.ini"), "[NR]\nstructure=2.05\n");
            Assert.True(SettingsTransfer.Export(from, RouteFamily.ReShade, "G", "0.7.0", zip));
            Assert.Equal((RouteFamily.ReShade, "G"), SettingsTransfer.Describe(zip));

            File.WriteAllText(Path.Combine(to, "amd-nr.ini"), "[NR]\nstructure=1\n");
            var backup = Path.Combine(root, "backup");
            Assert.Equal(["amd-nr.ini"], SettingsTransfer.Import(zip, to, RouteFamily.ReShade, backup));
            Assert.Equal("[NR]\nstructure=2.05\n", File.ReadAllText(Path.Combine(to, "amd-nr.ini")));
            Assert.Equal("[NR]\nstructure=1\n", File.ReadAllText(Path.Combine(backup, "amd-nr.ini")));

            var wrong = Assert.Throws<SettingsTransfer.WrongRouteException>(() =>
                SettingsTransfer.Import(zip, to, RouteFamily.OptiScaler, backup));
            Assert.Equal(RouteFamily.ReShade, wrong.Route);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void OnlyTheForksSectionsOfOptiScalerIniTravelAndTheyAreSetKeyByKey()
    {
        var ini = "[FrameGen]\nEnabled = true\n\n[DlssNr]\n; the network\nEnabled = auto\nNrBackend = mochizuki\n\n[AmdLook]\nMix = 0.5\n\n[Log]\nLogLevel = 2\n";
        var part = SettingsTransfer.Sections(ini);
        Assert.Equal("[DlssNr]\nEnabled = auto\nNrBackend = mochizuki\n\n[AmdLook]\nMix = 0.5", part);

        var theirs = "[FrameGen]\nEnabled = false\n\n[DlssNr]\n; the network\nEnabled = true\nNrBackend = auto\n\n[AmdLook]\nMix = auto\n";
        var merged = SettingsTransfer.Merge(theirs, part);
        Assert.Contains("[FrameGen]\nEnabled = false", merged);
        Assert.Contains("; the network\nEnabled=auto\nNrBackend=mochizuki", merged);
        Assert.Equal("0.5", Engine.GetIni(merged, "AmdLook", "Mix"));
    }
}
