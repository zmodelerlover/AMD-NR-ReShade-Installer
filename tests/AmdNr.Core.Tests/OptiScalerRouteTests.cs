using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class OptiScalerRouteTests
{
    /// <summary>Every path the payload manifest's optiscaler component extracts, with stand-in bytes.</summary>
    private static readonly string[] PayloadPaths =
    [
        "OptiScaler.dll", "OptiScaler.ini",
        "OptiScaler/amd_fidelityfx_loader_dx12.dll", "OptiScaler/amd_fidelityfx_upscaler_dx12.dll",
        "OptiScaler/amd_fidelityfx_framegeneration_dx12.dll", "OptiScaler/amd_fidelityfx_denoiser_dx12.dll",
        "OptiScaler/amd_fidelityfx_vk.dll", "OptiScaler/libxess.dll", "OptiScaler/libxess_dx11.dll",
        "OptiScaler/libxess_fg.dll", "OptiScaler/libxell.dll", "D3D12_OptiScaler/D3D12Core.dll",
        "experimental_lighting/GatherCS.cso", "experimental_lighting/ResolveCS.cso",
    ];

    private const string RuntimeFile = "dlssnr_amd_runtime-0.3.1.dll";

    /// <summary>A staging folder the way PayloadCache.Stage lays one out for this route, and the pins.</summary>
    internal static (string Dir, PayloadPins Pins) Payloads(string tag)
    {
        var dir = Fixture.Temp($"opti-payload-{tag}");
        var opti = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in PayloadPaths)
        {
            var bytes = Encoding.UTF8.GetBytes($"stand-in {path}");
            var full = Path.Combine(dir, path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            opti[path] = Engine.Sha(bytes);
        }
        var runtime = Encoding.UTF8.GetBytes("stand-in 0.3.1 runtime");
        var weights = Encoding.UTF8.GetBytes("stand-in weights");
        File.WriteAllBytes(Path.Combine(dir, RuntimeFile), runtime);
        File.WriteAllBytes(Path.Combine(dir, Work.WeightsName), weights);

        return (dir, new PayloadPins
        {
            AddonSha = new string('0', 64),
            AddonSize = 0,
            WeightsSha = Engine.Sha(weights),
            WeightsSize = (ulong)weights.Length,
            OptiFiles = opti,
            OptiRuntimeName = RuntimeFile,
            OptiRuntimeSha = Engine.Sha(runtime),
            OptiRuntimeSize = (ulong)runtime.Length,
        });
    }

    private static string Bytes(string dir, string name) => File.ReadAllText(Path.Combine(dir, name));

    [Fact]
    public void AD3D12GameIsRecommendedTheOptiScalerRouteAndAD3D11OneIsNot()
    {
        var dx12 = Fixture.Temp("opti-detect-12");
        File.WriteAllBytes(Path.Combine(dx12, "Game.exe"), Fixture.PeWithImports(true, ["d3d12.dll"]));
        Assert.Equal(Preset.OptiScaler, GraphicsDetector.Detect(dx12).Preset);

        var dx11 = Fixture.Temp("opti-detect-11");
        File.WriteAllBytes(Path.Combine(dx11, "Game.exe"), Fixture.PeWithImports(true, ["d3d11.dll"]));
        Assert.Equal(Preset.Dx11, GraphicsDetector.Detect(dx11).Preset);

        // Offered on every 64-bit game, and the ReShade D3D12 route stays reachable beside it.
        var offered = Presets.Offered(Detected.On(Route.X64, "Game.exe"));
        Assert.Contains(Preset.OptiScaler, offered);
        Assert.Contains(Preset.Dx12, offered);
        Assert.Equal(Route.X64, Preset.OptiScaler.Route());
    }

    [Fact]
    public void AGameWithBothD3DRenderersIsRecommendedOptiScalerOnlyWhenItShipsAnUpscaler()
    {
        static string Unreal(string tag)
        {
            var root = Fixture.Temp(tag);
            var bin = Path.Combine(root, "Project", "Binaries", "Win64");
            Directory.CreateDirectory(bin);
            File.WriteAllBytes(Path.Combine(bin, "Project-Win64-Shipping.exe"),
                Fixture.PeWithImports(true, ["d3d12.dll", "d3d11.dll"]));
            return root;
        }

        var plain = Unreal("opti-both-plain");
        var without = GraphicsDetector.Detect(plain);
        Assert.Equal(Preset.Dx11, without.Preset);
        Assert.Empty(without.Upscalers);

        // Unreal keeps the DLSS plugin's binaries under Engine\Plugins, not beside the game.
        var dlss = Unreal("opti-both-dlss");
        var plugin = Path.Combine(dlss, "Engine", "Plugins", "Runtime", "Nvidia", "DLSS", "Binaries", "ThirdParty", "Win64");
        Directory.CreateDirectory(plugin);
        File.WriteAllText(Path.Combine(plugin, "nvngx_dlss.dll"), "stand-in");
        var with = GraphicsDetector.Detect(dlss);
        Assert.Equal(Preset.OptiScaler, with.Preset);
        Assert.Equal(["nvngx_dlss.dll"], with.Upscalers);
        // OptiScaler runs on D3D12, so a game offering both is told to switch to that one.
        Assert.Equal(GraphicsApi.D3D12, with.Recommended);
        Assert.True(with.NeedsRendererSwitch);
    }

    [Fact]
    public void InstallPutsEveryFileWhereOptiScalerLooksAndJournalsIt()
    {
        var game = Fixture.Temp("opti-install");
        File.WriteAllBytes(Path.Combine(game, "Game.exe"), Fixture.PeWithImports(true, ["d3d12.dll"]));
        var (src, pins) = Payloads("install");

        Assert.False(Work.Preflight(game, src, Preset.OptiScaler, pins).Failed);
        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install"));

        // OptiScaler under the proxy name, never under its own.
        Assert.Equal("stand-in OptiScaler.dll", Bytes(game, "dxgi.dll"));
        Assert.False(File.Exists(Path.Combine(game, "OptiScaler.dll")));
        Assert.Equal("stand-in D3D12_OptiScaler/D3D12Core.dll", Bytes(game, "OptiScaler/D3D12_OptiScaler/D3D12Core.dll"));
        Assert.Equal("stand-in OptiScaler/libxess.dll", Bytes(game, "OptiScaler/libxess.dll"));
        Assert.True(File.Exists(Path.Combine(game, "experimental_lighting", "GatherCS.cso")));
        Assert.True(File.Exists(Path.Combine(game, Work.OptiScalerIni)));
        foreach (var pass in new[] { "dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll" })
            Assert.Equal("stand-in 0.3.1 runtime", Bytes(game, pass));
        Assert.Equal("stand-in weights", Bytes(game, Work.WeightsName));
        // Nothing of the ReShade route.
        Assert.False(File.Exists(Path.Combine(game, Work.AddonName)));
        Assert.False(File.Exists(Path.Combine(game, "ReShade.ini")));

        var m = Manifest.Decode(File.ReadAllText(Path.Combine(game, Route.X64.ManifestFileName())));
        Assert.Equal("OptiScaler", m.Preset);
        Assert.Contains(m.Entries, e => e.Name == "OptiScaler/D3D12_OptiScaler/D3D12Core.dll" && e.Owned);
        Assert.Contains(m.Entries, e => e.Name == Work.OptiScalerIni && e.Configuration);

        // Installing again over itself changes nothing and fails nothing.
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins).Failed);
    }

    [Fact]
    public void WinmmIsAChoiceAndAnythingElseFallsBackToDxgi()
    {
        var game = Fixture.Temp("opti-winmm");
        var (src, pins) = Payloads("winmm");

        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins, "winmm.dll").Failed);
        Assert.Equal("stand-in OptiScaler.dll", Bytes(game, "winmm.dll"));
        Assert.False(File.Exists(Path.Combine(game, "dxgi.dll")));

        Assert.Equal(["dxgi.dll", "winmm.dll"], Work.ProxyChoicesFor(Preset.OptiScaler));
        Assert.Equal("dxgi.dll", Work.OptiProxyFor("d3d9.dll"));
    }

    [Fact]
    public void UninstallTakesEverythingBackPutsBackWhatItDisplacedAndKeepsTheIni()
    {
        var game = Fixture.Temp("opti-uninstall");
        File.WriteAllText(Path.Combine(game, "dxgi.dll"), "somebody else's dxgi");
        var (src, pins) = Payloads("uninstall");

        var install = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(install.Failed, install.ToLog("install"));
        Assert.Equal("stand-in OptiScaler.dll", Bytes(game, "dxgi.dll"));

        var report = Work.Uninstall(game, Preset.OptiScaler);
        Assert.False(report.Failed, report.ToLog("uninstall"));

        Assert.Equal("somebody else's dxgi", Bytes(game, "dxgi.dll"));
        Assert.False(Directory.Exists(Path.Combine(game, "OptiScaler")));
        Assert.False(Directory.Exists(Path.Combine(game, "experimental_lighting")));
        foreach (var name in Work.InstalledMarkers)
            Assert.False(File.Exists(Path.Combine(game, name)), $"{name} is still there");
        Assert.False(File.Exists(Path.Combine(game, "dlssnr_amd_pass3.dll")));
        // Configuration stays, as it does on every route.
        Assert.True(File.Exists(Path.Combine(game, Work.OptiScalerIni)));
    }

    [Fact]
    public void AnOptiScalerIniAlreadyThereIsKeptAsItIs()
    {
        var game = Fixture.Temp("opti-own-ini");
        File.WriteAllText(Path.Combine(game, Work.OptiScalerIni), "[DlssNr]\nEnabled=true\n");
        var (src, pins) = Payloads("own-ini");

        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.Equal("[DlssNr]\nEnabled=true\n", Bytes(game, Work.OptiScalerIni));
        Assert.True(Fixture.HasAny(report, "it is your configuration"), report.ToLog("install"));
    }

    [Fact]
    public void TheReShadeRouteInstalledHereIsNamedAndNothingIsWritten()
    {
        var game = Fixture.Temp("opti-other-route");
        var (reshadeSrc, reshadePins) = Fixture.Payloads("opti-other-route");
        Assert.False(Work.Install(game, reshadeSrc, Preset.Dx12, reshadePins).Failed);
        var (src, pins) = Payloads("other-route");

        Assert.True(Work.Preflight(game, src, Preset.OptiScaler, pins).Failed);
        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.True(Fixture.HasErr(report, "ReShade route installed"), report.ToLog("install"));
        Assert.False(File.Exists(Path.Combine(game, "dlssnr_amd_pass2.dll")));
        Assert.False(Directory.Exists(Path.Combine(game, "OptiScaler")));
    }

    [Fact]
    public void WhatAnUninstalledReShadeRouteLeftBehindDoesNotBlockThisOne()
    {
        var game = Fixture.Temp("opti-leftover");
        // What the ReShade route leaves after an uninstall: ReShade.ini kept, and its manifest with it.
        var ini = Encoding.UTF8.GetBytes("[GENERAL]\n");
        File.WriteAllBytes(Path.Combine(game, "ReShade.ini"), ini);
        var leftover = new Manifest("D3D12", Route.X64);
        leftover.Entries.Add(new Entry { Name = "ReShade.ini", Hash = Engine.Sha(ini), Owned = true, Configuration = true });
        Manifest.WriteAtomic(game, leftover);
        var (src, pins) = Payloads("leftover");

        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install"));
        var m = Manifest.Decode(File.ReadAllText(Path.Combine(game, Route.X64.ManifestFileName())));
        Assert.Equal("OptiScaler", m.Preset);
        Assert.Contains(m.Entries, e => e.Name == "ReShade.ini");
    }

    [Fact]
    public void AnInstallIsOutOfDateOnlyWhenTheOptiScalerPayloadMovesOn()
    {
        var game = Fixture.Temp("opti-outdated");
        var (src, pins) = Payloads("outdated");
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins).Failed);

        var current = Manifest(pins, pins.OptiRuntimeSha);
        Assert.False(Work.PayloadMovedOn(game, current));

        var moved = Manifest(pins, new string('a', 64));
        Assert.True(Work.PayloadMovedOn(game, moved));

        // The payload's JSON with the add-on's own runtime, as the live manifest carries it: a
        // different build under dlssnr_amd_pass1.dll, which must not make this install look old.
        static PayloadManifest Manifest(PayloadPins pins, string optiRuntimeSha)
        {
            var extract = string.Join(",", pins.OptiFiles.Select(f =>
                $"{{\"name\":\"{Path.GetFileName(f.Key)}\",\"path\":\"{f.Key}\",\"size\":1,\"sha256\":\"{f.Value}\"}}"));
            return PayloadManifest.Parse($$$"""
                {"schema":1,"components":{
                  "addon":{"version":"1","files":[{"name":"amd-nr.addon64","size":1,"sha256":"{{{new string('1', 64)}}}","url":"https://x/a"}]},
                  "runtime":{"version":"1","files":[
                    {"name":"dlssnr_amd_pass1.dll","size":1,"sha256":"{{{new string('2', 64)}}}","url":"https://x/r"},
                    {"name":"dlssnr_on_amd_weights.bin","size":1,"sha256":"{{{pins.WeightsSha}}}","url":"https://x/w"}]},
                  "optiscaler":{"version":"1","files":[{"name":"o.zip","size":1,"sha256":"{{{new string('3', 64)}}}","url":"https://x/o"}],
                    "extract":[{{{extract}}}]},
                  "opti-runtime":{"version":"1","files":[{"name":"{{{RuntimeFile}}}","size":1,"sha256":"{{{optiRuntimeSha}}}","url":"https://x/p"}]}
                }}
                """);
        }
    }

    [Fact]
    public void TheOptiScalerComponentsAreFetchedOnDemandAndPinned()
    {
        var manifest = PayloadManifest.Parse($$$"""
            {"schema":1,"components":{
              "addon":{"version":"1","files":[{"name":"amd-nr.addon64","size":1,"sha256":"{{{new string('1', 64)}}}","url":"https://x/a"}]},
              "runtime":{"version":"1","files":[
                {"name":"dlssnr_amd_pass1.dll","size":1,"sha256":"{{{new string('2', 64)}}}","url":"https://x/r"},
                {"name":"dlssnr_on_amd_weights.bin","size":1,"sha256":"{{{new string('4', 64)}}}","url":"https://x/w"}]},
              "optiscaler":{"version":"1","files":[{"name":"o.zip","size":1,"sha256":"{{{new string('3', 64)}}}","url":"https://x/o"}],
                "extract":[{"name":"D3D12Core.dll","path":"D3D12_OptiScaler/D3D12Core.dll","size":1,"sha256":"{{{new string('5', 64)}}}"}]},
              "opti-runtime":{"version":"1","files":[{"name":"{{{RuntimeFile}}}","size":1,"sha256":"{{{new string('6', 64)}}}","url":"https://x/p"}]}
            }}
            """);

        Assert.Equal(["addon", "runtime"], manifest.Everyday.Select(p => p.Key).OrderBy(k => k));
        var pins = manifest.Pins();
        Assert.Equal(new string('5', 64), pins.OptiFiles["D3D12_OptiScaler/D3D12Core.dll"]);
        Assert.Equal(RuntimeFile, pins.OptiRuntimeName);

        // A manifest from before the route still pins everything else and simply has no OptiScaler.
        var older = PayloadManifest.Parse($$$"""
            {"schema":1,"components":{
              "addon":{"version":"1","files":[{"name":"amd-nr.addon64","size":1,"sha256":"{{{new string('1', 64)}}}","url":"https://x/a"}]},
              "runtime":{"version":"1","files":[
                {"name":"dlssnr_amd_pass1.dll","size":1,"sha256":"{{{new string('2', 64)}}}","url":"https://x/r"},
                {"name":"dlssnr_on_amd_weights.bin","size":1,"sha256":"{{{new string('4', 64)}}}","url":"https://x/w"}]}
            }}
            """).Pins();
        Assert.Empty(older.OptiFiles);
        Assert.True(Work.Preflight(Fixture.Temp("opti-no-route"), Fixture.Temp("opti-no-src"), Preset.OptiScaler, older).Failed);
    }
}
