using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>The mochizuki runtime OptiScaler 0.4.0 carries: how the payload lists it, when it is
/// offered, where each file goes, the one ini key it sets, and how uninstall takes it back together
/// with what the runtime writes while a game runs.</summary>
public class MochizukiTests
{
    private const string RuntimeFile = "dlssnr_amd_runtime-0.3.1.dll";
    private const string ModelPath = "dlssnr-amd/dlssnr.bin";

    /// <summary>The package's ini, shaped like the real one: the key sits among others, with a comment.</summary>
    private const string Ini =
        "[DlssNr]\r\n; NR runtime: daniel, lmxxf or mochizuki. Restart after changing.\r\nEnabled=false\r\n"
        + "NrBackend=daniel\r\nMochizukiColourStrength=0\r\n\r\n[Upscalers]\r\nDx12Upscaler=auto\r\n";

    private static readonly string[] OptiPaths =
        ["OptiScaler.dll", "OptiScaler.ini", "OptiScaler/libxess.dll", "D3D12_OptiScaler/D3D12Core.dll", "LmxxfNrRuntime.dll"];

    /// <summary>Payload paths as the archive keeps them: the folders under dlssnr-amd spelled with dots,
    /// one name twice in two folders, as the real shaders have it, and the upstream licence beside them.</summary>
    private static readonly string[] MochizukiPaths =
    [
        "MochizukiNrRuntime.dll", "dlssnr-amd.prewarm/manifest.txt", "dlssnr-amd.shaders/g_attn.spv",
        "dlssnr-amd.shaders/shader-constants.txt", "dlssnr-amd.shaders.runtime/cascade_blur.spv",
        "dlssnr-amd.shaders.temporal/shader-constants.txt", "dlssnr-amd/LICENSE-DLSSNR-AMD.txt",
    ];

    private static readonly string[] Destinations =
    [
        "MochizukiNrRuntime.dll", "dlssnr-amd/prewarm/manifest.txt", "dlssnr-amd/shaders/g_attn.spv",
        "dlssnr-amd/shaders/shader-constants.txt", "dlssnr-amd/shaders/runtime/cascade_blur.spv",
        "dlssnr-amd/shaders/temporal/shader-constants.txt", "dlssnr-amd/LICENSE-DLSSNR-AMD.txt", ModelPath,
    ];

    private static string Content(string path, string tag) => path == "OptiScaler.ini" ? Ini : $"{tag} {path}";
    private static string Sha(string s) => Engine.Sha(Encoding.UTF8.GetBytes(s));

    private static string Pinned(IEnumerable<string> paths, string tag, string? sha = null) => string.Join(",", paths.Select(p =>
        $$"""{"name":"{{Path.GetFileName(p)}}","path":"{{p}}","size":{{Encoding.UTF8.GetByteCount(Content(p, tag))}},"sha256":"{{sha ?? Sha(Content(p, tag))}}"}"""));

    private static string Zip(string name, string? sha = null) =>
        $$"""[{"name":"{{name}}","size":1,"sha256":"{{sha ?? new string('3', 64)}}","url":"https://x/{{name}}"}]""";

    /// <summary>A payload list shaped like the one this release ships: 0.3.0 without mochizuki and 0.4.0
    /// with it. <paramref name="tag"/> is what every mochizuki file of 0.4.0 holds, so a second list with
    /// another tag pins another build; <paramref name="placeholder"/> leaves 0.4.0's archive unpinned.</summary>
    internal static PayloadManifest Payload(string tag = "0.4.0", bool model = true, bool placeholder = false) =>
        PayloadManifest.Parse($$$"""
        {"schema":1,"components":{
          "addon":{"version":"1","files":[{"name":"amd-nr.addon64","size":1,"sha256":"{{{new string('1', 64)}}}","url":"https://x/a"}]},
          "runtime":{"version":"1","files":[
            {"name":"dlssnr_amd_pass1.dll","size":1,"sha256":"{{{new string('2', 64)}}}","url":"https://x/r"},
            {"name":"dlssnr_on_amd_weights.bin","size":1,"sha256":"{{{Sha("stand-in weights")}}}","url":"https://x/w"}]},
          "optiscaler":{"version":"0.1.1-amd-nr","files":{{{Zip("o11.zip")}}},"extract":[{{{Pinned(OptiPaths.Take(2), "0.1.1")}}}]},
          "opti-runtime":{"version":"0.3.1","files":[{"name":"{{{RuntimeFile}}}","size":1,"sha256":"{{{Sha("stand-in 0.3.1 runtime")}}}","url":"https://x/p"}]}
        },"releases":{"optiscaler":[
          {"version":"0.4.0-amd-nr","components":{
            "optiscaler":{"version":"0.4.0-amd-nr","files":{{{Zip("o40.zip", placeholder ? PayloadManifest.PlaceholderSha : null)}}},"extract":[{{{Pinned(OptiPaths, "0.4.0")}}}]},
            "mochizuki":{"version":"0.4.0-amd-nr","files":{{{Zip("mochizuki-0.4.0-amd-nr.zip")}}},"extract":[{{{Pinned(MochizukiPaths, tag)}}}]}
            {{{(model ? $$""","mochizuki-model":{"version":"1","files":[{"name":"dlssnr.bin","path":"{{ModelPath}}","size":{{Encoding.UTF8.GetByteCount(Content(ModelPath, "model"))}},"sha256":"{{Sha(Content(ModelPath, "model"))}}","url":"https://x/m"}]}""" : "")}}}
          }},
          {"version":"0.3.0-amd-nr","components":{
            "optiscaler":{"version":"0.3.0-amd-nr","files":{{{Zip("o30.zip")}}},"extract":[{{{Pinned(OptiPaths, "0.3.0")}}}]}
          }}
        ]}}
        """);

    /// <summary>The staging folder PayloadCache.Stage would make for the newest version, with the bytes
    /// it pins, and the pins.</summary>
    private static (string Src, PayloadPins Pins) Staged(string label, string tag = "0.4.0")
    {
        var manifest = Payload(tag).Newest(PayloadManifest.OptiScalerComponent);
        var src = Fixture.Temp($"mochizuki-src-{label}");
        foreach (var name in new[] { PayloadManifest.OptiScalerComponent, PayloadManifest.MochizukiComponent, PayloadManifest.MochizukiModelComponent })
            foreach (var file in manifest.Component(name).Installed)
            {
                var full = Path.Combine(src, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, Content(file.RelativePath, name == PayloadManifest.MochizukiModelComponent ? "model"
                    : name == PayloadManifest.MochizukiComponent ? tag : "0.4.0"));
            }
        File.WriteAllText(Path.Combine(src, RuntimeFile), "stand-in 0.3.1 runtime");
        File.WriteAllText(Path.Combine(src, Work.WeightsName), "stand-in weights");
        return (src, manifest.Pins());
    }

    private static string Game(string label, bool foreignDxgi = true)
    {
        var game = Fixture.Temp($"mochizuki-game-{label}");
        File.WriteAllBytes(Path.Combine(game, "Game.exe"), Fixture.PeWithImports(true, ["d3d12.dll"]));
        if (foreignDxgi) File.WriteAllText(Path.Combine(game, "dxgi.dll"), "somebody else's dxgi");
        return game;
    }

    /// <summary>Every file under a folder with its bytes, for comparing a folder with what it was.</summary>
    private static SortedDictionary<string, string> Snapshot(string dir) =>
        new(Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories).ToDictionary(
            p => Path.GetRelativePath(dir, p), p => File.Exists(p) ? Engine.HashFile(p) : "<folder>"), StringComparer.Ordinal);

    [Fact]
    public void TheReleaseThatCarriesItIsOfferedAndPinsTheRuntimeOnlyWithItsModel()
    {
        var payload = Payload();
        var offered = payload.Offered(PayloadManifest.OptiScalerComponent);
        Assert.Equal(["0.4.0-amd-nr", "0.3.0-amd-nr", "0.1.1-amd-nr"], offered.Select(r => r.Version));

        var pins = payload.Newest(PayloadManifest.OptiScalerComponent).Pins();
        Assert.Equal(MochizukiPaths.Append(ModelPath).Order(StringComparer.Ordinal), pins.MochizukiFiles.Keys.Order(StringComparer.Ordinal));
        // Kept apart from OptiScaler's own files: those always go in, these only when asked for.
        Assert.DoesNotContain(pins.OptiFiles.Keys, k => k.Contains("dlssnr-amd", StringComparison.Ordinal));

        // An older version has none of it, and neither has one that carries the runtime but no model.
        Assert.Empty(payload.With(offered[1]).Pins().MochizukiFiles);
        Assert.Empty(Payload(model: false).Newest(PayloadManifest.OptiScalerComponent).Pins().MochizukiFiles);

        // Downloaded when an install asks for it, never in the first-run wizard or Download all.
        Assert.Contains(PayloadManifest.MochizukiComponent, PayloadManifest.OnDemand);
        Assert.Contains(PayloadManifest.MochizukiModelComponent, PayloadManifest.OnDemand);
        Assert.DoesNotContain(payload.Everyday, p => p.Key.StartsWith("mochizuki", StringComparison.Ordinal));
    }

    [Fact]
    public void AReleaseStillPinnedToPlaceholdersIsNotOffered()
    {
        var payload = Payload(placeholder: true);
        Assert.True(PayloadManifest.IsPlaceholder(payload.Releases!["optiscaler"][0]));
        Assert.Equal(["0.3.0-amd-nr", "0.1.1-amd-nr"], payload.Offered(PayloadManifest.OptiScalerComponent).Select(r => r.Version));
        Assert.Equal("0.3.0-amd-nr", payload.Newest(PayloadManifest.OptiScalerComponent).Component(PayloadManifest.OptiScalerComponent).Version);
        Assert.False(PayloadManifest.IsPlaceholder(payload.Releases["optiscaler"][1]));
    }

    [Fact]
    public void EachFileLandsInTheRuntimesFolderAndOnlyThereIsAllowed()
    {
        Assert.Equal(Destinations, MochizukiPaths.Append(ModelPath).Select(Work.MochizukiDestination));
        foreach (var destination in Destinations) Assert.True(Engine.IsAllowed(destination), destination);

        // Nothing deeper, nothing that climbs, no other folder, not the runtime under another name.
        Assert.False(Engine.IsAllowed("dlssnr-amd/shaders/runtime/deeper/x.spv"));
        Assert.False(Engine.IsAllowed("dlssnr-amd/../dxgi.dll"));
        Assert.False(Engine.IsAllowed("dlssnr-amd/shaders/.."));
        Assert.False(Engine.IsAllowed("dlssnr-amd/"));
        Assert.False(Engine.IsAllowed("dlssnr-amd"));
        Assert.False(Engine.IsAllowed("other/shaders/x.spv"));
        Assert.False(Engine.IsAllowed("mochizuki/dlssnr.bin"));
        Assert.True(Engine.IsAllowed("MochizukiNrRuntime.dll"));

        // The payload itself stays two levels deep, which is what every app already out there reads.
        Assert.Throws<InstallException>(() => PayloadManifest.Parse($$$"""
            {"schema":1,"components":{"optiscaler":{"version":"1","files":{{{Zip("o.zip")}}}}},
             "releases":{"optiscaler":[{"version":"1.0.0","components":{
               "optiscaler":{"version":"1.0.0","files":{{{Zip("o.zip")}}}},
               "mochizuki":{"version":"1","files":{{{Zip("m.zip")}}},"extract":[{"name":"x.spv","path":"dlssnr-amd/shaders/x.spv","size":1,"sha256":"{{{new string('4', 64)}}}"}]}
             } }]} }
            """));
    }

    [Fact]
    public void InstallPutsEveryFileInPlaceAndNamesItTheNrRuntime()
    {
        var game = Game("install");
        var (src, pins) = Staged("install");

        Assert.False(Work.Preflight(game, src, Preset.OptiScaler, pins, mochizuki: true).Failed);
        var report = Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.True(Fixture.HasAny(report, "mochizuki runtime went in"), report.ToLog("install"));

        foreach (var (path, destination) in MochizukiPaths.Zip(Destinations))
            Assert.Equal(Content(path, "0.4.0"), File.ReadAllText(Path.Combine(game, destination)));
        Assert.Equal(Content(ModelPath, "model"), File.ReadAllText(Path.Combine(game, ModelPath)));
        // OptiScaler, its runtime passes and weights went in as always.
        Assert.Equal("0.4.0 OptiScaler.dll", File.ReadAllText(Path.Combine(game, "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(game, "dlssnr_amd_pass3.dll")));

        // One key changed, every other byte of the package's ini as it was.
        Assert.Equal(Ini.Replace("NrBackend=daniel", "NrBackend=mochizuki"), File.ReadAllText(Path.Combine(game, Work.OptiScalerIni)));

        var m = Manifest.Decode(File.ReadAllText(Path.Combine(game, Route.X64.ManifestFileName())));
        foreach (var destination in Destinations)
            Assert.Contains(m.Entries, e => e.Name == destination && e.Owned && !e.Configuration);
        Assert.Contains(m.Entries, e => e.Name == Work.OptiScalerIni && e.Configuration);

        // The same install again changes nothing; a newer mochizuki build makes it out of date.
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true).Failed);
        Assert.False(Work.PayloadMovedOn(game, Payload()));
        Assert.True(Work.PayloadMovedOn(game, Payload("0.4.1")));
    }

    [Fact]
    public void LeftOffNothingOfItGoesInAndTheIniKeepsThePackagesRuntime()
    {
        var game = Game("off");
        var (src, pins) = Staged("off");

        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.False(File.Exists(Path.Combine(game, "MochizukiNrRuntime.dll")));
        Assert.False(Directory.Exists(Path.Combine(game, "dlssnr-amd")));
        Assert.Equal(Ini, File.ReadAllText(Path.Combine(game, Work.OptiScalerIni)));
        // A newer mochizuki does not make a folder without it out of date.
        Assert.False(Work.PayloadMovedOn(game, Payload("0.4.1")));
    }

    [Fact]
    public void AskedOfAVersionWithoutItNothingIsWritten()
    {
        var game = Game("without", foreignDxgi: false);
        var (src, _) = Staged("without");
        var older = Payload();
        var pins = older.With(older.Offered(PayloadManifest.OptiScalerComponent)[1]).Pins();

        Assert.True(Work.Preflight(game, src, Preset.OptiScaler, pins, mochizuki: true).Failed);
        var report = Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true);
        Assert.True(Fixture.HasErr(report, "no mochizuki runtime"), report.ToLog("install"));
        Assert.False(File.Exists(Path.Combine(game, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game, Route.X64.ManifestFileName())));
    }

    [Fact]
    public void AnIniThatIsThePersonsIsLeftAsItIsAndTheReportSaysWhereToPickIt()
    {
        // One that was here before this app installed anything.
        var game = Game("own-ini");
        File.WriteAllText(Path.Combine(game, Work.OptiScalerIni), "[DlssNr]\nNrBackend=lmxxf\n");
        var (src, pins) = Staged("own-ini");
        var report = Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.Equal("[DlssNr]\nNrBackend=lmxxf\n", File.ReadAllText(Path.Combine(game, Work.OptiScalerIni)));
        Assert.True(Fixture.HasAny(report, "Pick mochizuki under NR runtime"), report.ToLog("install"));
        Assert.True(File.Exists(Path.Combine(game, "MochizukiNrRuntime.dll")));

        // One this app installed, that OptiScaler has saved settings into since.
        var saved = Game("saved-ini");
        Assert.False(Work.Install(saved, src, Preset.OptiScaler, pins).Failed);
        var edited = Ini.Replace("Enabled=false", "Enabled=true");
        File.WriteAllText(Path.Combine(saved, Work.OptiScalerIni), edited);
        report = Work.Install(saved, src, Preset.OptiScaler, pins, mochizuki: true);
        Assert.False(report.Failed, report.ToLog("install over saved settings"));
        Assert.Equal(edited, File.ReadAllText(Path.Combine(saved, Work.OptiScalerIni)));
        Assert.True(Fixture.HasAny(report, "Pick mochizuki under NR runtime"), report.ToLog("install over saved settings"));
    }

    [Fact]
    public void ThePrewarmListTheRuntimeRewroteIsKeptForTheSameBuildAndReplacedByANewOne()
    {
        var game = Game("prewarm");
        var (src, pins) = Staged("prewarm");
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true).Failed);

        // The runtime makes the list again for this machine's driver.
        var list = Path.Combine(game, "dlssnr-amd", "prewarm", "manifest.txt");
        File.WriteAllText(list, "mochizuki-prewarm 1\nrewritten for this driver\n");

        var again = Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true);
        Assert.False(again.Failed, again.ToLog("reinstall"));
        Assert.Equal("mochizuki-prewarm 1\nrewritten for this driver\n", File.ReadAllText(list));
        Assert.True(Fixture.HasAny(again, "kept as the runtime rewrote it"), again.ToLog("reinstall"));

        // Another build brings its own list, made for its own shaders.
        var (newer, newerPins) = Staged("prewarm-newer", "0.4.1");
        var update = Work.Install(game, newer, Preset.OptiScaler, newerPins, mochizuki: true);
        Assert.False(update.Failed, update.ToLog("update"));
        Assert.Equal(Content("dlssnr-amd.prewarm/manifest.txt", "0.4.1"), File.ReadAllText(list));
        Assert.Equal(Content("dlssnr-amd.shaders.runtime/cascade_blur.spv", "0.4.1"),
            File.ReadAllText(Path.Combine(game, "dlssnr-amd", "shaders", "runtime", "cascade_blur.spv")));

        // Anything else of ours changed by hand is still refused, as before.
        File.WriteAllText(Path.Combine(game, "dlssnr-amd", "shaders", "g_attn.spv"), "edited by hand");
        Assert.True(Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true).Failed);
    }

    [Fact]
    public void UninstallTakesItAllBackWithWhatTheRuntimeWroteAndTheFolderIsAsItWas()
    {
        var game = Game("uninstall");
        var before = Snapshot(game);
        var (src, pins) = Staged("uninstall");
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true).Failed);

        // A game ran: the pipeline cache, a write cut short, the log, and the prewarm list made again.
        var data = Path.Combine(game, "dlssnr-amd");
        File.WriteAllText(Path.Combine(data, "pipeline.cache"), "compiled pipelines");
        File.WriteAllText(Path.Combine(data, "pipeline.cache.4242.3.tmp"), "half");
        File.WriteAllText(Path.Combine(data, "prewarm", "manifest.txt.4242.4.tmp"), "half");
        File.WriteAllText(Path.Combine(data, "prewarm", "manifest.txt"), "rewritten for this driver");
        File.WriteAllText(Path.Combine(game, "mochizuki_nr.log"), "[mochizuki] prewarm 32 pipelines");

        var gone = Work.Uninstall(game, Preset.OptiScaler);
        Assert.False(gone.Failed, gone.ToLog("uninstall"));
        Assert.False(Directory.Exists(data), gone.ToLog("uninstall"));
        Assert.False(File.Exists(Path.Combine(game, "MochizukiNrRuntime.dll")));
        Assert.False(File.Exists(Path.Combine(game, "mochizuki_nr.log")));
        Assert.Equal("somebody else's dxgi", File.ReadAllText(Path.Combine(game, "dxgi.dll")));
        // What is left is the settings, which are asked about; taken too, the folder is what it was.
        Assert.Equal([Work.OptiScalerIni], Work.KeptConfiguration(game, Preset.OptiScaler));
        Assert.False(Work.Uninstall(game, Preset.OptiScaler, removeConfig: true).Failed);
        Assert.Equal(before, Snapshot(game));
    }

    [Fact]
    public void WhatIsNotOursInItsFolderStaysAndSoDoesARuntimeTheGameHolds()
    {
        var game = Game("keep");
        var (src, pins) = Staged("keep");
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true).Failed);
        File.WriteAllText(Path.Combine(game, "dlssnr-amd", "shaders", "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(game, "dlssnr-amd", "pipeline.cache"), "compiled");

        using (File.Open(Path.Combine(game, "MochizukiNrRuntime.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var held = Work.Uninstall(game, Preset.OptiScaler);
            Assert.True(File.Exists(Path.Combine(game, "MochizukiNrRuntime.dll")), held.ToLog("held"));
            // The runtime is still here, so what it wrote is still its own.
            Assert.True(File.Exists(Path.Combine(game, "dlssnr-amd", "pipeline.cache")), held.ToLog("held"));
        }

        var report = Work.Uninstall(game, Preset.OptiScaler);
        Assert.False(File.Exists(Path.Combine(game, "MochizukiNrRuntime.dll")), report.ToLog("uninstall"));
        Assert.False(File.Exists(Path.Combine(game, "dlssnr-amd", "pipeline.cache")), report.ToLog("uninstall"));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(game, "dlssnr-amd", "shaders", "notes.txt")));
        Assert.False(File.Exists(Path.Combine(game, "dlssnr-amd", "shaders", "g_attn.spv")));
    }

    [Fact]
    public void UntickedTheNextInstallTakesItOutAndNothingOfItIsLeftToBeOutOfDate()
    {
        var game = Game("untick");
        var before = Snapshot(game);
        var (src, pins) = Staged("untick");
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true).Failed);
        var data = Path.Combine(game, "dlssnr-amd");
        File.WriteAllText(Path.Combine(data, "pipeline.cache"), "compiled pipelines");
        File.WriteAllText(Path.Combine(data, "prewarm", "manifest.txt"), "rewritten for this driver");
        Assert.True(Work.PayloadMovedOn(game, Payload("0.4.1")));

        // The game still has the runtime open: nothing is taken out, nothing is written.
        var (newer, newerPins) = Staged("untick-newer", "0.4.1");
        var recorded = File.ReadAllText(Path.Combine(game, Route.X64.ManifestFileName()));
        using (File.Open(Path.Combine(game, "MochizukiNrRuntime.dll"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var held = Work.Install(game, newer, Preset.OptiScaler, newerPins);
            Assert.True(Fixture.HasErr(held, "MochizukiNrRuntime.dll is open by another program"), held.ToLog("held"));
            Assert.Equal(recorded, File.ReadAllText(Path.Combine(game, Route.X64.ManifestFileName())));
            Assert.Equal(Ini.Replace("NrBackend=daniel", "NrBackend=mochizuki"), File.ReadAllText(Path.Combine(game, Work.OptiScalerIni)));
        }

        // Left off, against a list whose mochizuki has moved on: it comes out with what its runtime wrote,
        // the package's ini names the default runtime again, and nothing is left to be out of date.
        Assert.True(Fixture.HasAny(Work.Preflight(game, newer, Preset.OptiScaler, newerPins), "installing takes out"));
        var report = Work.Install(game, newer, Preset.OptiScaler, newerPins);
        Assert.False(report.Failed, report.ToLog("install unticked"));
        Assert.True(Fixture.HasAny(report, "came out"), report.ToLog("install unticked"));
        Assert.False(File.Exists(Path.Combine(game, "MochizukiNrRuntime.dll")));
        Assert.False(Directory.Exists(data), report.ToLog("install unticked"));
        Assert.Equal(Ini, File.ReadAllText(Path.Combine(game, Work.OptiScalerIni)));
        var m = Manifest.Decode(File.ReadAllText(Path.Combine(game, Route.X64.ManifestFileName())));
        Assert.DoesNotContain(m.Entries, e => e.Name.StartsWith("dlssnr-amd", StringComparison.Ordinal) || e.Name == "MochizukiNrRuntime.dll");
        Assert.False(Work.PayloadMovedOn(game, Payload("0.4.1")));
        Assert.False(Work.HasMochizuki(game));

        // Ticked again it all comes back, and uninstall still leaves the folder as it was.
        Assert.False(Work.Install(game, newer, Preset.OptiScaler, newerPins, mochizuki: true).Failed);
        Assert.True(Work.HasMochizuki(game));
        Assert.False(Work.Uninstall(game, Preset.OptiScaler, removeConfig: true).Failed);
        Assert.Equal(before, Snapshot(game));
    }

    [Fact]
    public void TakenOutUnderAnIniThatIsThePersonsTheReportSaysItStillNamesMochizuki()
    {
        var game = Game("untick-own-ini");
        File.WriteAllText(Path.Combine(game, Work.OptiScalerIni), "[DlssNr]\nNrBackend=mochizuki\n");
        var (src, pins) = Staged("untick-own-ini");
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true).Failed);

        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install unticked"));
        Assert.Equal("[DlssNr]\nNrBackend=mochizuki\n", File.ReadAllText(Path.Combine(game, Work.OptiScalerIni)));
        Assert.Contains(report.Lines, l => l.Level == Level.Warn && l.Text.Contains("still names mochizuki", StringComparison.Ordinal));
    }

    /// <summary>A game folder with mochizuki 0.4.0 copied in by hand, as OptiScaler's own notes say to.</summary>
    private static string HandCopied(string label)
    {
        var game = Game(label);
        foreach (var (path, destination) in MochizukiPaths.Zip(Destinations).Append((ModelPath, ModelPath)))
        {
            var full = Path.Combine(game, destination);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Content(path, path == ModelPath ? "model" : "0.4.0"));
        }
        return game;
    }

    [Fact]
    public void OverAHandCopyThePrewarmListTheRuntimeRewroteDoesNotStopTheNextInstall()
    {
        var game = HandCopied("hand");
        var (src, pins) = Staged("hand");
        var first = Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true);
        Assert.False(first.Failed, first.ToLog("install"));
        var list = Path.Combine(game, "dlssnr-amd", "prewarm", "manifest.txt");
        var manifest = Path.Combine(game, Route.X64.ManifestFileName());
        Assert.Contains(Manifest.Decode(File.ReadAllText(manifest)).Entries, e => e.Name == "dlssnr-amd/prewarm/manifest.txt" && !e.Owned);

        // The runtime makes the list again for this driver: the same build keeps it...
        File.WriteAllText(list, "mochizuki-prewarm 1\nrewritten for this driver\n");
        var again = Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true);
        Assert.False(again.Failed, again.ToLog("reinstall"));
        Assert.Equal("mochizuki-prewarm 1\nrewritten for this driver\n", File.ReadAllText(list));

        // ...and a newer one replaces it, and from then on the list is this app's.
        var (newer, newerPins) = Staged("hand-newer", "0.4.1");
        var update = Work.Install(game, newer, Preset.OptiScaler, newerPins, mochizuki: true);
        Assert.False(update.Failed, update.ToLog("update"));
        Assert.Equal(Content("dlssnr-amd.prewarm/manifest.txt", "0.4.1"), File.ReadAllText(list));
        Assert.Contains(Manifest.Decode(File.ReadAllText(manifest)).Entries,
            e => e.Name == "dlssnr-amd/prewarm/manifest.txt" && e.Owned && e.Backup.Length == 0);

        // Uninstall puts the hand copy back where it was replaced; the list, which the runtime makes
        // again on its first start, goes.
        var gone = Work.Uninstall(game, Preset.OptiScaler, removeConfig: true);
        Assert.False(gone.Failed, gone.ToLog("uninstall"));
        Assert.Equal(Content("MochizukiNrRuntime.dll", "0.4.0"), File.ReadAllText(Path.Combine(game, "MochizukiNrRuntime.dll")));
        Assert.Equal(Content("dlssnr-amd.shaders/g_attn.spv", "0.4.0"),
            File.ReadAllText(Path.Combine(game, "dlssnr-amd", "shaders", "g_attn.spv")));
        Assert.False(File.Exists(list));
    }

    [Fact]
    public void UntickedOverAHandCopyTheCopyStaysAndOnlyTheRecordGoes()
    {
        var game = HandCopied("hand-untick");
        var (src, pins) = Staged("hand-untick");
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: true).Failed);

        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install unticked"));
        Assert.True(Fixture.HasAny(report, "this app did not put it there"), report.ToLog("install unticked"));
        foreach (var (path, destination) in MochizukiPaths.Zip(Destinations))
            Assert.Equal(Content(path, "0.4.0"), File.ReadAllText(Path.Combine(game, destination)));
        var m = Manifest.Decode(File.ReadAllText(Path.Combine(game, Route.X64.ManifestFileName())));
        Assert.DoesNotContain(m.Entries, e => e.Name.StartsWith("dlssnr-amd", StringComparison.Ordinal) || e.Name == "MochizukiNrRuntime.dll");
        Assert.False(Work.HasMochizuki(game));
    }
}
