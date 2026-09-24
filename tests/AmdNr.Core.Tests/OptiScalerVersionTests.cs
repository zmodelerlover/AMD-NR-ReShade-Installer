using System.IO.Compression;
using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>More than one OptiScaler version in the payload, and the lmxxf runtime that comes with
/// 0.2.0: its modules, its shaders and its weights.</summary>
public class OptiScalerVersionTests
{
    private const string RuntimeFile = "dlssnr_amd_runtime-0.3.1.dll";

    private static readonly string[] OptiPaths =
    [
        "OptiScaler.dll", "OptiScaler.ini", "OptiScaler/libxess_fg.dll", "OptiScaler/libxell.dll",
        "D3D12_OptiScaler/D3D12Core.dll", "experimental_lighting/GatherCS.cso",
    ];

    private static readonly string[] LmxxfPaths =
    [
        "LmxxfNrRuntime.dll", "lmxxf-modules/SHA256SUMS", "lmxxf-modules/c32_fast.hsaco",
        "shaders/native_codec_encode.hlsl", "shaders/native_codec_decode.hlsl",
    ];

    private static readonly string[] WeightPaths =
    [
        "native-game-tiled-assets/block0-ffn.f16", "native-game-tiled-assets/noise.f32",
        // The same name as a shader above, in another folder, with other bytes.
        "native-game-tiled-assets/native_codec_encode.hlsl",
    ];

    private static string Sha(string s) => Engine.Sha(Encoding.UTF8.GetBytes(s));

    /// <summary>Extract pins for these paths, each file holding "<paramref name="tag"/> path".</summary>
    private static string Extract(IEnumerable<string> paths, string tag = "stand-in") => string.Join(",", paths.Select(p =>
        $"{{\"name\":\"{Path.GetFileName(p)}\",\"path\":\"{p}\",\"size\":1,\"sha256\":\"{Sha($"{tag} {p}")}\"}}"));

    /// <summary>A payload manifest shaped like the live one: 0.1.1 in the components, the way v0.4.0
    /// reads it, and 0.2.0 with the lmxxf weights under releases.</summary>
    private static PayloadManifest Payload(string releases = "") => PayloadManifest.Parse($$$"""
        {"schema":1,"components":{
          "addon":{"version":"1","files":[{"name":"amd-nr.addon64","size":1,"sha256":"{{{new string('1', 64)}}}","url":"https://x/a"}]},
          "runtime":{"version":"1","files":[
            {"name":"dlssnr_amd_pass1.dll","size":1,"sha256":"{{{new string('2', 64)}}}","url":"https://x/r"},
            {"name":"dlssnr_on_amd_weights.bin","size":1,"sha256":"{{{Sha("stand-in weights")}}}","url":"https://x/w"}]},
          "optiscaler":{"version":"0.1.1-amd-nr","files":[{"name":"o11.zip","size":1,"sha256":"{{{new string('3', 64)}}}","url":"https://x/o11"}],
            "extract":[{{{Extract(OptiPaths, "0.1.1")}}}]},
          "opti-runtime":{"version":"0.3.1","files":[{"name":"{{{RuntimeFile}}}","size":1,"sha256":"{{{Sha("stand-in 0.3.1 runtime")}}}","url":"https://x/p"}]}
        }{{{releases}}}}
        """);

    private static string Releases020 => $$$"""
        ,"releases":{"optiscaler":[{"version":"0.2.0-amd-nr","components":{
          "optiscaler":{"version":"0.2.0-amd-nr","published":"2026-09-23","files":[{"name":"o20.zip","size":1,"sha256":"{{{new string('4', 64)}}}","url":"https://x/o20"}],
            "extract":[{{{Extract(OptiPaths.Concat(LmxxfPaths))}}}]},
          "lmxxf-weights":{"version":"1","files":[{"name":"native-game-tiled-assets.zip","size":1,"sha256":"{{{new string('5', 64)}}}","url":"https://x/w20"}],
            "extract":[{{{Extract(WeightPaths)}}}]}
        }}]}
        """;

    /// <summary>A staging folder holding every file the manifest pins, with the bytes it pins.</summary>
    private static string Stage(string tag, PayloadManifest manifest, params string[] components)
    {
        var dir = Fixture.Temp($"opti-versions-{tag}");
        foreach (var name in components)
            foreach (var file in manifest.Component(name).Installed)
            {
                var full = Path.Combine(dir, file.RelativePath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, $"stand-in {file.RelativePath}");
            }
        File.WriteAllText(Path.Combine(dir, RuntimeFile), "stand-in 0.3.1 runtime");
        File.WriteAllText(Path.Combine(dir, Work.WeightsName), "stand-in weights");
        return dir;
    }

    [Fact]
    public void VersionsAreOfferedNewestFirstAndAReleaseBringsWhatItNeeds()
    {
        var manifest = Payload(Releases020);
        Assert.Equal(["0.2.0-amd-nr", "0.1.1-amd-nr"],
            manifest.Offered(PayloadManifest.OptiScalerComponent).Select(r => r.Version));

        var newest = manifest.Newest(PayloadManifest.OptiScalerComponent);
        Assert.Equal("0.2.0-amd-nr", newest.Component(PayloadManifest.OptiScalerComponent).Version);
        Assert.Equal("2026-09-23", newest.Component(PayloadManifest.OptiScalerComponent).Published);
        Assert.True(newest.Has(PayloadManifest.LmxxfWeightsComponent));
        // What the release does not carry stays as the manifest pins it.
        Assert.Equal("0.3.1", newest.Component(PayloadManifest.OptiRuntimeComponent).Version);

        var pins = newest.Pins();
        Assert.Contains("LmxxfNrRuntime.dll", pins.OptiFiles.Keys);
        Assert.Contains("native-game-tiled-assets/noise.f32", pins.OptiFiles.Keys);

        // The version an older app reads is untouched, and without the lmxxf weights.
        Assert.Equal("0.1.1-amd-nr", manifest.Component(PayloadManifest.OptiScalerComponent).Version);
        Assert.False(manifest.Has(PayloadManifest.LmxxfWeightsComponent));
        Assert.DoesNotContain(manifest.Everyday, p => p.Key == PayloadManifest.LmxxfWeightsComponent);

        var older = manifest.With(manifest.Offered(PayloadManifest.OptiScalerComponent)[1]);
        Assert.Equal("0.1.1-amd-nr", older.Component(PayloadManifest.OptiScalerComponent).Version);
        Assert.DoesNotContain("LmxxfNrRuntime.dll", older.Pins().OptiFiles.Keys);

        // A manifest without releases offers the one version it pins.
        Assert.Equal(["0.1.1-amd-nr"], Payload().Offered(PayloadManifest.OptiScalerComponent).Select(r => r.Version));
    }

    [Fact]
    public void TheShippedManifestOffersEveryOptiScalerVersionAndEveryFileMayBeInstalled()
    {
        var shipped = PayloadManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "payload.json")));
        var offered = shipped.Offered(PayloadManifest.OptiScalerComponent);
        Assert.Equal("0.2.0-amd-nr", offered[0].Version);
        Assert.Contains(offered, r => r.Version == "0.1.1-amd-nr");
        // What v0.4.0 reads stays the version it knows how to install.
        Assert.Equal("0.1.1-amd-nr", shipped.Component(PayloadManifest.OptiScalerComponent).Version);

        foreach (var release in offered)
        {
            var pins = shipped.With(release).Pins();
            foreach (var path in pins.OptiFiles.Keys)
            {
                var destination = path == Work.OptiScalerDllPayload ? "dxgi.dll"
                    : path == "D3D12_OptiScaler/D3D12Core.dll" ? "OptiScaler/D3D12_OptiScaler/D3D12Core.dll"
                    : path;
                Assert.True(Engine.IsAllowed(destination), $"{release.Version}: {destination} would be refused");
            }
        }
        Assert.Contains("native-game-tiled-assets/block0-ffn.f16", shipped.With(offered[0]).Pins().OptiFiles.Keys);
    }

    [Fact]
    public void AReleaseIsCheckedLikeEveryComponent()
    {
        // It has to carry its own component, at its own version.
        Assert.Throws<InstallException>(() => Payload("""
            ,"releases":{"optiscaler":[{"version":"0.2.0","components":{}}]}
            """));
        // And nothing in it may climb, or reach deeper than two levels.
        Assert.Throws<InstallException>(() => Payload($$$"""
            ,"releases":{"optiscaler":[{"version":"0.2.0","components":{
              "optiscaler":{"version":"0.2.0","files":[{"name":"o.zip","size":1,"sha256":"{{{new string('4', 64)}}}","url":"https://x/o"}],
                "extract":[{"name":"x.hsaco","path":"native-game-tiled-assets/HIP/x.hsaco","size":1,"sha256":"{{{new string('5', 64)}}}"}]}
            }}]}
            """));
    }

    [Fact]
    public void OnlyTheLmxxfFoldersAreMatchedByRule()
    {
        Assert.True(Engine.IsAllowed("LmxxfNrRuntime.dll"));
        Assert.True(Engine.IsAllowed("lmxxf-modules/c32_fast.hsaco"));
        Assert.True(Engine.IsAllowed("native-game-tiled-assets/noise.f32"));
        Assert.True(Engine.IsAllowed("shaders/native_codec_encode.hlsl"));
        Assert.True(Engine.IsAllowed("dxgi.dll"));

        // Somebody else's shaders, anything deeper, anything that climbs, anything elsewhere.
        Assert.False(Engine.IsAllowed("shaders/lighting.hlsl"));
        Assert.False(Engine.IsAllowed("shaders/native_codec_encode.cso"));
        Assert.False(Engine.IsAllowed("native-game-tiled-assets/HIP/x.hsaco"));
        Assert.False(Engine.IsAllowed("native-game-tiled-assets/.."));
        Assert.False(Engine.IsAllowed("native-game-tiled-assets/"));
        Assert.False(Engine.IsAllowed("../native-game-tiled-assets/x"));
        Assert.False(Engine.IsAllowed("Engine/x.dll"));
        Assert.False(Engine.IsAllowed("version.dll"));
    }

    [Fact]
    public void TheLmxxfVersionInstallsEverythingAndUninstallTakesItAllBack()
    {
        var game = Fixture.Temp("opti-versions-install");
        File.WriteAllBytes(Path.Combine(game, "Game.exe"), Fixture.PeWithImports(true, ["d3d12.dll"]));
        var manifest = Payload(Releases020).Newest(PayloadManifest.OptiScalerComponent);
        var src = Stage("install", manifest, PayloadManifest.OptiScalerComponent, PayloadManifest.LmxxfWeightsComponent);
        var pins = manifest.Pins();

        Assert.False(Work.Preflight(game, src, Preset.OptiScaler, pins).Failed);
        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.True(Fixture.HasAny(report, "lmxxf runtime went in"), report.ToLog("install"));

        foreach (var path in LmxxfPaths.Concat(WeightPaths))
            Assert.Equal($"stand-in {path}", File.ReadAllText(Path.Combine(game, path)));
        Assert.Equal("stand-in D3D12_OptiScaler/D3D12Core.dll",
            File.ReadAllText(Path.Combine(game, "OptiScaler", "D3D12_OptiScaler", "D3D12Core.dll")));

        // The manifest names every one of them and reads back.
        var m = Manifest.Decode(File.ReadAllText(Path.Combine(game, Route.X64.ManifestFileName())));
        Assert.Contains(m.Entries, e => e.Name == "native-game-tiled-assets/noise.f32" && e.Owned);
        Assert.Contains(m.Entries, e => e.Name == "shaders/native_codec_encode.hlsl" && e.Owned);

        // Installed at the newest version, it is not out of date; compared with an older pin it is.
        Assert.False(Work.PayloadMovedOn(game, Payload(Releases020)));
        Assert.True(Work.PayloadMovedOn(game, Payload()));

        // What the runtime compiles while a game runs sits beside its shaders.
        Directory.CreateDirectory(Path.Combine(game, "shaders", "shader-cache"));
        File.WriteAllText(Path.Combine(game, "shaders", "shader-cache", "0123456789abcdef.dxbc"), "compiled");

        var gone = Work.Uninstall(game, Preset.OptiScaler);
        Assert.False(gone.Failed, gone.ToLog("uninstall"));
        foreach (var folder in new[] { "lmxxf-modules", "native-game-tiled-assets", "shaders", "OptiScaler" })
            Assert.False(Directory.Exists(Path.Combine(game, folder)), $"{folder} is still there");
        Assert.False(File.Exists(Path.Combine(game, "LmxxfNrRuntime.dll")));
        Assert.True(File.Exists(Path.Combine(game, Work.OptiScalerIni)));
    }

    [Fact]
    public void ASharedShadersFolderIsLeftWithWhatElseIsInIt()
    {
        var game = Fixture.Temp("opti-versions-shared");
        Directory.CreateDirectory(Path.Combine(game, "shaders"));
        File.WriteAllText(Path.Combine(game, "shaders", "lighting.hlsl"), "the game's own");
        var manifest = Payload(Releases020).Newest(PayloadManifest.OptiScalerComponent);
        var src = Stage("shared", manifest, PayloadManifest.OptiScalerComponent, PayloadManifest.LmxxfWeightsComponent);

        Assert.False(Work.Install(game, src, Preset.OptiScaler, manifest.Pins()).Failed);
        Assert.False(Work.Uninstall(game, Preset.OptiScaler).Failed);
        Assert.Equal("the game's own", File.ReadAllText(Path.Combine(game, "shaders", "lighting.hlsl")));
        Assert.False(File.Exists(Path.Combine(game, "shaders", "native_codec_encode.hlsl")));
    }

    [Fact]
    public void AnArchiveEntryIsFoundByItsPathWhenTheNameRepeats()
    {
        var dir = Fixture.Temp("opti-versions-zip");
        var archive = Path.Combine(dir, "package.zip");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            foreach (var (path, text) in new[] { ("README.md", "root"), ("lmxxf-modules/README.md", "modules"),
                         ("OptiScaler/libxess.dll", "xess") })
            {
                using var writer = new StreamWriter(zip.CreateEntry(path).Open());
                writer.Write(text);
            }
        }

        var entries = new[]
        {
            new PayloadFile { Name = "README.md", Path = "lmxxf-modules/README.md", Size = 7, Sha256 = Sha("modules") },
            new PayloadFile { Name = "README.md", Size = 4, Sha256 = Sha("root") },
            new PayloadFile { Name = "libxess.dll", Path = "OptiScaler/libxess.dll", Size = 4, Sha256 = Sha("xess") },
        };
        var target = Path.Combine(dir, "out");
        PayloadCache.ExtractVerified(archive, entries, target);

        Assert.Equal("modules", File.ReadAllText(Path.Combine(target, "lmxxf-modules", "README.md")));
        Assert.Equal("root", File.ReadAllText(Path.Combine(target, "README.md")));
        Assert.Equal("xess", File.ReadAllText(Path.Combine(target, "OptiScaler", "libxess.dll")));

        // A pin that does not match leaves nothing behind, not even the partial file.
        var wrong = new PayloadFile { Name = "libxess.dll", Path = "bad/libxess.dll", Size = 4, Sha256 = new string('a', 64) };
        Assert.Throws<InstallException>(() => PayloadCache.ExtractVerified(archive, [wrong], target));
        Assert.Empty(Directory.GetFiles(Path.Combine(target, "bad")));
    }
}
