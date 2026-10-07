using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class EngineTests
{
    [Fact]
    public void Sha256KnownVector() =>
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            Engine.Sha("abc"u8));

    [Fact]
    public void PeMachineIsReadAndMalformedImagesRefused()
    {
        Assert.Equal(Engine.MachineX86, Engine.Machine(Fixture.Pe(false)));
        Assert.Equal(Engine.MachineX64, Engine.Machine(Fixture.Pe(true)));
        Assert.Throws<InstallException>(() => Engine.Machine("not a pe"u8));
        Assert.Throws<InstallException>(() => Engine.Machine(Fixture.Pe(false).AsSpan(0, 100)));
    }

    [Fact]
    public void D3d8SidecarMarkerDetectedInBothEncodings()
    {
        Assert.True(Engine.AdvertisesD3d8Sidecar("prefix D3D8R.DLL suffix"u8));
        var wide = "d3d8R.dll".SelectMany(c => new[] { (byte)c, (byte)0 }).ToArray();
        Assert.True(Engine.AdvertisesD3d8Sidecar(wide));
        // An unknown wrapper is never assumed chainable: replacing it would break the game.
        Assert.False(Engine.AdvertisesD3d8Sidecar("ordinary d3d8.dll wrapper"u8));
    }

    [Fact]
    public void IniEditsPreserveEveryOtherByte()
    {
        const string before = "[INPUT]\nKeyOverlay=36,0,0,0\n";
        var after = Engine.SetIni(before, "OVERLAY", "Window", "x");
        Assert.Equal("36,0,0,0", Engine.GetIni(after, "INPUT", "KeyOverlay"));
        Assert.Equal("x", Engine.GetIni(after, "OVERLAY", "Window"));

        // Rewriting an existing key replaces that line and nothing else.
        var again = Engine.SetIni(after, "INPUT", "KeyOverlay", "9,0,0,0");
        Assert.Equal("9,0,0,0", Engine.GetIni(again, "INPUT", "KeyOverlay"));
        Assert.Equal("x", Engine.GetIni(again, "OVERLAY", "Window"));
    }

    [Fact]
    public void AnIniWrittenWithAByteOrderMarkIsStillReadable()
    {
        // ReShade writes one. Without this the first section is invisible and every key in it reads
        // as absent, which fails silently: nothing errors, the value is just never found.
        const string withBom = "\uFEFF[INSTALL]\r\nBasePath=bin\r\n";
        Assert.Equal("bin", Engine.GetIni(withBom, "INSTALL", "BasePath"));

        var edited = Engine.SetIni(withBom, "INSTALL", "BasePath", "other");
        Assert.StartsWith("\uFEFF", edited, StringComparison.Ordinal);
        Assert.Equal("other", Engine.GetIni(edited, "INSTALL", "BasePath"));
    }

    [Theory]
    [InlineData(1280u, 720u)]
    [InlineData(2560u, 1440u)]
    public void FreshDockingFollowsTheSuppliedViewport(uint w, uint h)
    {
        var s = Engine.FirstDock("[INPUT]\nKeyOverlay=36,0,0,0\n", w, h);
        Assert.Contains($"Size={w},,{h}", s, StringComparison.Ordinal);
        Assert.Equal("36,0,0,0", Engine.GetIni(s, "INPUT", "KeyOverlay"));
    }

    [Fact]
    public void ASavedPanelLayoutIsNeverRedocked()
    {
        const string saved = "[OVERLAY]\nWindow=[Window][AMD Neural Rendering],Collapsed=0\n";
        Assert.Equal(saved, Engine.FirstDock(saved, 1920, 1080));
    }

    [Fact]
    public void AnExistingLayoutWithoutADockedHomeIsLeftAlone()
    {
        const string other = "[OVERLAY]\nWindow=[Window][###something],Collapsed=0\n";
        Assert.Equal(other, Engine.FirstDock(other, 1920, 1080));
    }

    [Fact]
    public void AnExistingHomeDockIdIsReused()
    {
        const string home = "[OVERLAY]\nWindow=[Window][###home],Collapsed=0,DockId=0x0000ABCD,,0\n";
        var s = Engine.FirstDock(home, 1920, 1080);
        Assert.Contains("[Window][AMD Neural Rendering],Collapsed=0,DockId=0x0000ABCD", s,
            StringComparison.Ordinal);
        Assert.DoesNotContain("DockSpace", s, StringComparison.Ordinal);
    }

    [Fact]
    public void TheX86PanelIsDockedUnderItsOwnTitle()
    {
        const string home = "[OVERLAY]\nWindow=[Window][###home],Collapsed=0,DockId=0x0000ABCD,,0\n";
        var s = Engine.FirstDock(home, 1920, 1080, Engine.PanelTitle32);
        Assert.Contains("[Window][AMD Neural Rendering (32-bit)],Collapsed=0,DockId=0x0000ABCD", s,
            StringComparison.Ordinal);
        Assert.Equal(s, Engine.FirstDock(s, 1920, 1080, Engine.PanelTitle32));
    }

    [Fact]
    public void AFreshInstallWritesTheX86TuningDefaults()
    {
        var ini = Engine.FreshIni();
        Assert.Equal("0.25", Engine.GetIni(ini, "amd-nr", "ColourStrength"));
        Assert.Equal("1.0", Engine.GetIni(ini, "amd-nr", "Scale"));
        Assert.Equal("1", Engine.GetIni(ini, "amd-nr", "Passes"));
        Assert.Contains("\r\n", ini, StringComparison.Ordinal);
    }

    // -- Manifest, the compatibility surface ---------------------------------------------------

    /// <summary>The exact bytes installer-x86 writes. If Encode drifts, every install already on
    /// disk stops being readable.</summary>
    private static string CapturedManifest() =>
        "{\n\"schema\":1,\n\"preset\":\"D3D8\",\n\"state\":\"installed\",\n"
        + "\"bridge_protocol\":3,\n\"dgVoodoo\":\"none\",\n"
        + "\"ReShade\":\"6.8.0.2156 Full Add-on Support\",\n\"files\":[\n"
        + "{\"name\":\"d3d8R.dll\",\"sha256\":\"ab6bf7a9a9f4b3e66a75ca038d8d10289c88acbfe8d52c3b5a8a9a259cb26cd5\","
        + "\"backup\":\".amd-nr-x86bridge-backups/17894153360915702/d3d8R.dll\","
        + "\"backup_sha256\":\"ee9b4916304592a31f0882f339bcbeac7133439a297fbd5274e503c0147d209e\","
        + "\"owned\":true,\"configuration\":false},\n"
        + "{\"name\":\"d3d9.dll\",\"sha256\":\"da430e0a9c6eecefa0d1b27d05e16c426fb5d04e808b194d914eaac4b31bc0f8\","
        + "\"backup\":\"\",\"backup_sha256\":\"\",\"owned\":false,\"configuration\":false}\n"
        + "]\n}\n";

    [Fact]
    public void ManifestWrittenByTheCppInstallerIsStillReadable()
    {
        var text = CapturedManifest();
        var m = Manifest.Decode(text);
        Assert.Equal("D3D8", m.Preset);
        Assert.Equal("installed", m.State);
        Assert.Equal(2, m.Entries.Count);
        Assert.True(m.Entries[0].Owned);
        Assert.False(m.Entries[1].Owned);
        Assert.Equal(64, m.Entries[0].BackupHash.Length);
        // The round trip is the contract: re-encoding must reproduce the file byte for byte.
        Assert.Equal(text, Manifest.Encode(m));
    }

    /// <summary>The Rust installer recorded the old tuning file as configuration, and its manifests
    /// are still read: one of them has to decode, and keep the file as the person's.</summary>
    [Fact]
    public void TheRustInstallersTuningFileIsConfiguration()
    {
        var text = CapturedManifest().Replace("\"name\":\"d3d9.dll\"", "\"name\":\"dlss5-neural.ini\"")
            .Replace("\"owned\":false,\"configuration\":false", "\"owned\":false,\"configuration\":true");
        Assert.Contains(Manifest.Decode(text).Entries, e => e.Name == "dlss5-neural.ini" && e.Configuration);
    }

    [Fact]
    public void AHandEditedManifestIsRejected()
    {
        // A name outside Allowed is what stops a forged manifest from deleting arbitrary files.
        Assert.Throws<InstallException>(() => Manifest.Decode(CapturedManifest().Replace("d3d9.dll", "evil.dll")));
        // Two rows for one file.
        Assert.Throws<InstallException>(() => Manifest.Decode(CapturedManifest().Replace("d3d8R.dll", "d3d9.dll")));
        // The configuration flag has to agree with the filename.
        Assert.Throws<InstallException>(() => Manifest.Decode(CapturedManifest()
            .Replace("\"owned\":false,\"configuration\":false", "\"owned\":false,\"configuration\":true")));
        // Not JSON any more, or a field of the wrong kind.
        Assert.Throws<InstallException>(() => Manifest.Decode(CapturedManifest()[..^10]));
        Assert.Throws<InstallException>(() => Manifest.Decode(CapturedManifest().Replace("\"owned\":true", "\"owned\":\"yes\"")));
        Assert.Throws<InstallException>(() => Manifest.Decode(CapturedManifest().Replace("\"schema\":1", "\"schema\":2")));
    }

    /// <summary>The same record in another layout is the same record. Need for Speed had its manifest
    /// rewritten compact, with no trailing newline, and every install and uninstall there failed with
    /// "Modified or unsupported install manifest" -- for a file that said exactly what it always had.</summary>
    [Fact]
    public void AManifestInAnotherLayoutStillReads()
    {
        var captured = Manifest.Decode(CapturedManifest());
        var compact = System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(CapturedManifest()).RootElement, new System.Text.Json.JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() });
        var pretty = (char)0xFEFF + System.Text.Json.JsonSerializer.Serialize(System.Text.Json.JsonDocument.Parse(CapturedManifest()).RootElement,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() }).ReplaceLineEndings("\r\n");
        foreach (var text in new[] { compact, pretty, CapturedManifest().Replace("\"schema\":1,", "\"schema\": 1,") })
        {
            var m = Manifest.Decode(text);
            Assert.Equal(captured, m);
            // Written back in the one layout the older installers read.
            Assert.Equal(CapturedManifest(), Manifest.Encode(m));
        }
    }

    /// <summary>Need for Speed's record, word for word as it was found on 28/09: compact, no newline at the
    /// end. Its install and its uninstall both failed on it.</summary>
    [Fact]
    public void TheRecordNeedForSpeedWasLeftWithReads()
    {
        const string nfs = """
            {"schema":1,"preset":"D3D11","state":"installed","route":"x64","bridge_protocol":3,"dgVoodoo":"none","ReShade":"6.8.0.2156 Full Add-on Support","files":[
            {"name":"ReShade.ini","sha256":"633b85a3e5d14f8dc713540c7579bf897ee3d87c86e93ba47e61f08901d8845d","backup":".amd-nr-x86bridge-backups/1790221974521910300/ReShade.ini","backup_sha256":"2ce66fb2fccfc0c78f7cc6a7ab67cb95b3a825a90d827826d4dcfac464e7fc1e","owned":true,"configuration":true},
            {"name":"amd-nr.addon64","sha256":"e526f9d7ed0ed3e20494131c6c13013c24788fe6f18f04f6818b1fffae746caa","backup":"","backup_sha256":"","owned":true,"configuration":false},
            {"name":"dxgi.dll","sha256":"0cee63f9c9f13f3ac909c5b4903f4dbb4b719a7ab3b4f13b0deaf83c814b94f7","backup":"","backup_sha256":"","owned":true,"configuration":false},
            {"name":"reshade-shaders/Shaders/AMD_Neural_Feed.fx","sha256":"9f53875221c130903bdd205d15011a924ab0f1c0c85b17191e4f39b140e7848e","backup":"","backup_sha256":"","owned":true,"configuration":false}]}
            """;
        var m = Manifest.Decode(nfs.ReplaceLineEndings("\n"));
        Assert.Equal(("D3D11", Route.X64, 4), (m.Preset, m.Route, m.Entries.Count));
        Assert.Equal(".amd-nr-x86bridge-backups/1790221974521910300/ReShade.ini", m.Entries[0].Backup);
        Assert.True(m.Entries[0].Configuration && m.Entries.All(e => e.Owned));
    }

    /// <summary>Owned and the recorded hash are data, and editing either still decodes. This is the
    /// edge of what the manifest check proves; the blast radius is bounded by Allowed and by uninstall
    /// re-hashing the target before acting.</summary>
    [Fact]
    public void TheManifestCheckDoesNotConstrainOwnedOrTheRecordedHash()
    {
        var flipped = CapturedManifest()
            .Replace("\"owned\":false,\"configuration\":false", "\"owned\":true,\"configuration\":false");
        Assert.Equal("D3D8", Manifest.Decode(flipped).Preset);
    }

    [Fact]
    public void ALegacyDgVoodooManifestStaysUninstallable()
    {
        var legacy = CapturedManifest().Replace("\"dgVoodoo\":\"none\"", "\"dgVoodoo\":\"2.87.4\"");
        Assert.Equal("D3D8", Manifest.Decode(legacy).Preset);

        var d3d9 = CapturedManifest()
            .Replace("\"preset\":\"D3D8\"", "\"preset\":\"D3D9\"")
            .Replace("\"dgVoodoo\":\"none\"", "\"dgVoodoo\":\"2.87.4\"");
        Assert.Equal("D3D9", Manifest.Decode(d3d9).Preset);
    }

    [Fact]
    public void ABackupPathOutsideTheBackupDirectoryIsRefused()
    {
        var escape = CapturedManifest().Replace(
            ".amd-nr-x86bridge-backups/17894153360915702/d3d8R.dll", "../../elsewhere/d3d8R.dll");
        Assert.Throws<InstallException>(() => Manifest.Decode(escape));
    }

    [Fact]
    public void AnX86ManifestIsStillByteIdenticalNowThatRoutesExist()
    {
        // The route marker must not leak into x86 output, or every existing install breaks.
        var text = CapturedManifest();
        var m = Manifest.Decode(text);
        Assert.Equal(Route.X86, m.Route);
        Assert.Equal(text, Manifest.Encode(m));
        Assert.DoesNotContain("\"route\"", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AnX64ManifestRoundTripsAndNamesItsRoute()
    {
        var m = new Manifest("Vulkan", Route.X64);
        m.Entries.Add(new Entry { Name = "amd-nr.addon64", Hash = new string('a', 64), Owned = true });

        var text = Manifest.Encode(m);
        Assert.Contains("\"route\":\"x64\",", text, StringComparison.Ordinal);
        Assert.Equal(m, Manifest.Decode(text));
        // x64 presets are only valid on the x64 route, and vice versa.
        Assert.Throws<InstallException>(() =>
            Manifest.Decode(text.Replace("\"preset\":\"Vulkan\"", "\"preset\":\"D3D8\"")));
    }

    [Fact]
    public void TheTwoRoutesWriteDifferentManifestFiles()
    {
        Assert.Equal(Engine.ManifestName, Route.X86.ManifestFileName());
        Assert.Equal(Engine.ManifestNameX64, Route.X64.ManifestFileName());
        Assert.NotEqual(Route.X86.ManifestFileName(), Route.X64.ManifestFileName());
    }

    [Fact]
    public void AtomicManifestRoundTripsThroughTheFilesystem()
    {
        var root = Fixture.Temp("atomic");
        var m = new Manifest("D3D9", Route.X86);
        m.Entries.Add(new Entry { Name = "d3d9.dll", Hash = Engine.ReShadeSha, Owned = true });

        Manifest.WriteAtomic(root, m);
        var text = File.ReadAllText(Path.Combine(root, Engine.ManifestName));
        Assert.Equal(m, Manifest.Decode(text));
        Assert.False(File.Exists(Path.Combine(root, $"{Engine.ManifestName}.tmp")),
            "the temporary journal file must not survive the commit");
    }

    // -- Paths ---------------------------------------------------------------------------------

    [Fact]
    public void ReShadeBasePathIsFollowedIntoTheGameDirectoryAndNoFurther()
    {
        var root = Fixture.Temp("basepath");
        Directory.CreateDirectory(Path.Combine(root, "bin"));
        var exe = Path.Combine(root, "game.exe");
        File.WriteAllBytes(exe, Fixture.Pe(false));

        File.WriteAllText(Path.Combine(root, "ReShade.ini"), "[INSTALL]\nBasePath=bin\n");
        Assert.Equal(Engine.WeaklyCanonical(Path.Combine(root, "bin")), Engine.InstallDirectory(exe));

        File.WriteAllText(Path.Combine(root, "ReShade.ini"), "[INSTALL]\nBasePath=..\n");
        Assert.Throws<InstallException>(() => Engine.InstallDirectory(exe));

        File.Delete(Path.Combine(root, "ReShade.ini"));
        Assert.Equal(Engine.WeaklyCanonical(root), Engine.InstallDirectory(exe));
    }

    /// <summary>C:\game must not be taken to contain C:\gameX. The Rust compared Path components;
    /// this compares with the separator, because a plain string prefix would say yes.</summary>
    [Fact]
    public void ASiblingFolderWithTheSamePrefixIsNotInsideTheGameDirectory()
    {
        var root = Fixture.Temp("prefix");
        var game = Path.Combine(root, "game");
        var sibling = Path.Combine(root, "gameX");
        Directory.CreateDirectory(game);
        Directory.CreateDirectory(sibling);
        File.WriteAllBytes(Path.Combine(game, "game.exe"), Fixture.Pe(false));
        File.WriteAllText(Path.Combine(game, "ReShade.ini"), $"[INSTALL]\nBasePath={sibling}\n");

        Assert.Throws<InstallException>(() => Engine.InstallDirectory(Path.Combine(game, "game.exe")));
    }

    // -- Guard ---------------------------------------------------------------------------------

    [Fact]
    public void AFileAnotherProgramIsHoldingStopsTheTransactionBeforeItStarts()
    {
        var root = Fixture.Temp("guard-locked");
        var target = Path.Combine(root, "amd-nr.addon64");
        File.WriteAllText(target, "in use");

        using (var _ = File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
            {
                ["amd-nr.addon64"] = "the new one"u8.ToArray(),
            };
            var log = new List<string>();
            var error = Assert.Throws<InstallException>(() =>
                Transaction.Apply(root, "D3D11", Route.X64, files, log));
            Assert.Contains("this line goes away", error.Message, StringComparison.Ordinal);

            // The journal must not exist: the guard runs before anything is recorded or written.
            Assert.False(File.Exists(Path.Combine(root, Route.X64.ManifestFileName())));
            Assert.False(Directory.Exists(Path.Combine(root, Engine.BackupDir)));
        }

        Assert.Equal("in use", File.ReadAllText(target));
    }

    [Fact]
    public void AFolderThatIsNotThereIsNamedRatherThanFailingMidway()
    {
        var root = Path.Combine(Fixture.Temp("guard-missing"), "no-such-subfolder");
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["amd-nr.addon64"] = "x"u8.ToArray(),
        };
        var error = Assert.Throws<InstallException>(() =>
            Transaction.Apply(root, "D3D11", Route.X64, files, []));
        Assert.Contains("is not a folder", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheGuardCountsTheBackupAsWellAsTheReplacement()
    {
        var root = Fixture.Temp("guard-space");
        // A file that will be displaced: the transaction needs room for the new bytes and for the
        // copy of the old ones, which is what the old check did not account for.
        File.WriteAllBytes(Path.Combine(root, "amd-nr.addon64"), new byte[2048]);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["amd-nr.addon64"] = Enumerable.Repeat((byte)1, 4096).ToArray(),
        };

        Transaction.Apply(root, "D3D11", Route.X64, files, []);
        Assert.Equal(4096, new FileInfo(Path.Combine(root, "amd-nr.addon64")).Length);
    }

    [Fact]
    public void ADisplacedFileGoesToTheBackupAndUninstallPutsItBack()
    {
        var root = Fixture.Temp("displace");
        var author = "the author's own runtime"u8.ToArray();
        File.WriteAllBytes(Path.Combine(root, "version.dll"), author);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["dlssnr_amd_pass1.dll"] = author,
        };

        var log = new List<string>();
        Transaction.Apply(root, "OptiScaler", Route.X64, files, log, displace: ["version.dll"]);
        Assert.False(File.Exists(Path.Combine(root, "version.dll")));
        Assert.Contains("MOVED to the backup: version.dll", log);
        var entry = Manifest.Decode(Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(root, Route.X64.ManifestFileName()))))
            .Entries.Single(e => e.Name == "version.dll");
        Assert.True(entry.Owned);
        Assert.Equal(Engine.Sha(author), entry.BackupHash);

        // Put back by hand after the install, then installed over again: the newer one is what
        // uninstall restores, and the first backup is spent.
        var again = "the author's newer runtime"u8.ToArray();
        File.WriteAllBytes(Path.Combine(root, "version.dll"), again);
        Transaction.Apply(root, "OptiScaler", Route.X64, files, [], displace: ["version.dll"]);
        Assert.False(File.Exists(Path.Combine(root, entry.Backup)));

        Transaction.Uninstall(root, Route.X64, false, log);
        Assert.Equal(again, File.ReadAllBytes(Path.Combine(root, "version.dll")));
        Assert.False(File.Exists(Path.Combine(root, "dlssnr_amd_pass1.dll")));
    }

    [Fact]
    public void ApplyRefusesAFilenameItDoesNotManage()
    {
        var root = Fixture.Temp("unmanaged");
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["something-else.dll"] = "x"u8.ToArray(),
        };
        var error = Assert.Throws<InstallException>(() =>
            Transaction.Apply(root, "D3D11", Route.X64, files, []));
        Assert.Contains("unmanaged filename", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void UninstallWithoutAManifestRefuses()
    {
        var root = Fixture.Temp("nomanifest");
        var error = Assert.Throws<InstallException>(() =>
            Transaction.Uninstall(root, Route.X86, false, []));
        Assert.Equal("No install manifest", error.Message);
    }

    // -- X86 route -----------------------------------------------------------------------------

    [Fact]
    public void PlanRefusesAnX64TargetAndAnUnsupportedPreset()
    {
        var root = Fixture.Temp("refuse");
        var x64 = Path.Combine(root, "x64.exe");
        File.WriteAllBytes(x64, Fixture.Pe(true));
        var app = new X86Installer(Path.Combine(root, "release"));

        Assert.Contains("PE32/x86",
            Assert.Throws<InstallException>(() => app.Plan(x64, "D3D11")).Message, StringComparison.Ordinal);

        var x86 = Path.Combine(root, "x86.exe");
        File.WriteAllBytes(x86, Fixture.Pe(false));
        Assert.Equal("Unsupported x86 preset",
            Assert.Throws<InstallException>(() => app.Plan(x86, "D3D12")).Message);
    }

    [Fact]
    public void TheBridgeChecksumListIsReadByNameNotByPosition()
    {
        var sums = $"{new string('a', 64)}  amd-nr.addon32\n{new string('b', 64)}  amd-nr-host64.exe\n";
        Assert.Equal(new string('b', 64), X86Installer.BridgeSum(sums, "amd-nr-host64.exe"));
        Assert.Throws<InstallException>(() => X86Installer.BridgeSum(sums, "not-in-the-list.dll"));
    }
    [Fact]
    public void ABackupWrittenBeforeTheRenameStillDecodes()
    {
        // A folder installed before v0.6.5 has its backups under .dlss5-x86bridge-backups, and a
        // manifest carried over to the new name still points at them -- the files really are
        // there. Requiring the current directory's prefix made the whole manifest undecodable, so
        // the install failed with "Unsafe backup entry" and rolled itself back. Reported from
        // GTA IV, where it meant the add-on could not be installed at all.
        var legacy = CapturedManifest().Replace(Engine.BackupDir, Engine.LegacyBackupDir,
            StringComparison.Ordinal);
        var m = Manifest.Decode(legacy);
        Assert.StartsWith(Engine.LegacyBackupDir, m.Entries[0].Backup, StringComparison.Ordinal);

        // Anywhere else is still refused: that check is what stops a tampered manifest pointing a
        // backup at a path outside the folder.
        Assert.Throws<InstallException>(() => Manifest.Decode(
            CapturedManifest().Replace(Engine.BackupDir, "..", StringComparison.Ordinal)));
        Assert.Throws<InstallException>(() => Manifest.Decode(
            CapturedManifest().Replace(Engine.BackupDir, "elsewhere", StringComparison.Ordinal)));
    }

    /// <summary>A junction inside a game folder still stops a write -- it can lead anywhere -- now
    /// that a reparse point is only refused when it is a link, and not for OneDrive's flag. Free
    /// space is asked of the folder, so one that is not there yet still has an answer.</summary>
    [Fact]
    public void ALinkStillStopsAWriteAndFreeSpaceIsAskedOfTheFolder()
    {
        var game = Fixture.Temp("junction");
        var elsewhere = Fixture.Temp("junction-target");
        var link = Path.Combine(game, "linked");
        using (var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
                   "cmd.exe", $"/c mklink /J \"{link}\" \"{elsewhere}\"") { CreateNoWindow = true, UseShellExecute = false }))
            mklink!.WaitForExit();
        Assert.True(Directory.Exists(link));

        Assert.True(Engine.IsLink(new DirectoryInfo(link)));
        Assert.Throws<InstallException>(() => Engine.SafePath(Path.Combine(link, "dxgi.dll")));
        Engine.SafePath(Path.Combine(game, "dxgi.dll"));
        Assert.False(Engine.IsLink(new DirectoryInfo(game)));

        Assert.NotNull(Engine.FreeBytes(Path.Combine(game, "not there yet")));
    }
}
