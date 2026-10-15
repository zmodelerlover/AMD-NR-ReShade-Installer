using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>OptiScaler from 0.5.2 on: 64-bit D3D10, and 32-bit D3D9 and D3D10 games through the package's 32-bit
/// proxies (x86/) and their 64-bit host, everything else of OptiScaler in OptiScaler64\.</summary>
public class OptiX86Tests
{
    private const string Bridged = "0.5.2-amd-nr";

    /// <summary>The OptiScaler route's staging folder with the package's x86/ folder in it.</summary>
    private static (string Dir, PayloadPins Pins) Payloads(string tag, string version = Bridged)
    {
        var (dir, pins) = OptiScalerRouteTests.Payloads(tag, version);
        // The route's own helper builds the pins on a dictionary of its own.
        var opti = (Dictionary<string, string>)pins.OptiFiles;
        foreach (var path in new[] { "x86/d3d9.dll", "x86/dxgi.dll", "x86/OptiScalerDx9Host.exe" })
        {
            var bytes = Encoding.UTF8.GetBytes($"stand-in {path}");
            Directory.CreateDirectory(Path.Combine(dir, "x86"));
            File.WriteAllBytes(Path.Combine(dir, path), bytes);
            opti[path] = Engine.Sha(bytes);
        }
        return (dir, pins);
    }

    private static string Game(string tag, bool x64, params string[] imports)
    {
        var game = Fixture.Temp(tag);
        File.WriteAllBytes(Path.Combine(game, "Game.exe"), Fixture.PeWithImports(x64, imports));
        return game;
    }

    private static string Bytes(string dir, string name) => File.ReadAllText(Path.Combine(dir, name));

    [Fact]
    public void D3D10AndThe32BitD3D9AndD3D10GamesAreOptiScalersFromTheBridgedVersionOn()
    {
        var x86Dx10 = GraphicsDetector.Detect(Game("x86-detect-10", false, "d3d10.dll"));
        Assert.Equal(Preset.OptiScaler, x86Dx10.PresetFor(true, bridges: true));
        Assert.NotEqual(Preset.OptiScaler, x86Dx10.PresetFor(true));
        Assert.Equal(GraphicsApi.D3D10, x86Dx10.OptiApi(true, true));

        var x86Dx9 = GraphicsDetector.Detect(Game("x86-detect-9", false, "d3d9.dll"));
        Assert.Equal(Preset.OptiScaler, x86Dx9.PresetFor(true, bridges: true));
        Assert.Equal(GraphicsApi.D3D9, x86Dx9.OptiApi(true, true));

        // No 32-bit D3D11 proxy: that one stays on the ReShade route.
        var x86Dx11 = GraphicsDetector.Detect(Game("x86-detect-11", false, "d3d11.dll"));
        Assert.NotEqual(Preset.OptiScaler, x86Dx11.PresetFor(true, bridges: true));
        Assert.False(x86Dx11.CanRunOptiScalerWith(true, true));

        var x64Dx10 = GraphicsDetector.Detect(Game("x64-detect-10", true, "d3d10.dll"));
        Assert.Equal(Preset.OptiScaler, x64Dx10.PresetFor(true, bridges: true));
        Assert.NotEqual(Preset.OptiScaler, x64Dx10.PresetFor(true));
    }

    [Fact]
    public void A32BitD3D10GameGetsTheDxgiProxyBesideItAndOptiScalerWithItsHostInOptiScaler64()
    {
        var game = Game("x86-install-10", false, "d3d10.dll");
        var (src, pins) = Payloads("x86-install-10");

        var pre = Work.Preflight(game, src, Preset.OptiScaler, pins);
        Assert.False(pre.Failed, pre.ToLog("pre"));
        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install"));

        Assert.Equal("stand-in x86/dxgi.dll", Bytes(game, "dxgi.dll"));
        Assert.False(File.Exists(Path.Combine(game, "d3d9.dll")));
        var host = Path.Combine(game, "OptiScaler64");
        Assert.Equal("stand-in OptiScaler.dll", Bytes(host, "OptiScaler.dll"));
        Assert.Equal("stand-in x86/OptiScalerDx9Host.exe", Bytes(host, "OptiScalerDx9Host.exe"));
        Assert.Equal("stand-in OptiScaler/libxess.dll", Bytes(host, "OptiScaler/libxess.dll"));
        Assert.Equal("stand-in D3D12_OptiScaler/D3D12Core.dll", Bytes(host, "OptiScaler/D3D12_OptiScaler/D3D12Core.dll"));
        Assert.Equal("stand-in 0.3.1 runtime", Bytes(host, "dlssnr_amd_pass1.dll"));
        Assert.Equal("stand-in weights", Bytes(host, Work.WeightsName));
        Assert.Contains("PresentWithoutUpscaler=true", Bytes(host, Work.OptiScalerIni).Replace(" ", ""));
        // Nothing of the 64-bit layout beside the game, and nothing of the package's x86\ folder as such.
        Assert.False(File.Exists(Path.Combine(game, "dlssnr_amd_pass1.dll")));
        Assert.False(Directory.Exists(Path.Combine(game, "OptiScaler")));
        Assert.False(Directory.Exists(Path.Combine(game, "x86")));

        Assert.False(File.Exists(Path.Combine(game, Route.X64.ManifestFileName())));
        var m = Manifest.Decode(File.ReadAllText(Path.Combine(game, Route.X86.ManifestFileName())));
        Assert.Equal("OptiScaler", m.Preset);
        Assert.Contains(m.Entries, e => e.Name == "OptiScaler64/OptiScalerDx9Host.exe" && e.Owned);
        Assert.Contains(m.Entries, e => e.Name == "OptiScaler64/OptiScaler.ini" && e.Configuration);
        Assert.Equal(RouteFamily.OptiScaler, GameScanner.InstalledAs(game));

        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins).Failed);
    }

    [Fact]
    public void A32BitD3D9GameGetsTheD3D9Proxy()
    {
        var game = Game("x86-install-9", false, "d3d9.dll");
        var (src, pins) = Payloads("x86-install-9");
        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.Equal("stand-in x86/d3d9.dll", Bytes(game, "d3d9.dll"));
        Assert.False(File.Exists(Path.Combine(game, "dxgi.dll")));
    }

    [Fact]
    public void An32BitGameOnAnOlderOptiScalerIsRefusedAndNothingIsWritten()
    {
        var game = Game("x86-old", false, "d3d10.dll");
        var (src, pins) = Payloads("x86-old", "0.5.1-amd-nr");
        Assert.True(Work.Preflight(game, src, Preset.OptiScaler, pins).Failed);
        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.True(Fixture.HasErr(report, "32-bit game"), report.ToLog("install"));
        Assert.False(File.Exists(Path.Combine(game, "dxgi.dll")));
        Assert.False(Directory.Exists(Path.Combine(game, "OptiScaler64")));
    }

    [Fact]
    public void A64BitGameLeavesThePackagesX86FolderOut()
    {
        var game = Game("x64-skips-x86", true, "d3d12.dll");
        var (src, pins) = Payloads("x64-skips-x86");
        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.Equal("stand-in OptiScaler.dll", Bytes(game, "dxgi.dll"));
        Assert.False(Directory.Exists(Path.Combine(game, "x86")));
        Assert.False(Directory.Exists(Path.Combine(game, "OptiScaler64")));
        Assert.False(File.Exists(Path.Combine(game, "OptiScalerDx9Host.exe")));
    }

    [Fact]
    public void UninstallTakesThe32BitInstallBackWithWhatTheHostWroteAndKeepsTheIni()
    {
        var game = Game("x86-uninstall", false, "d3d10.dll");
        File.WriteAllText(Path.Combine(game, "dxgi.dll"), "somebody else's dxgi");
        var (src, pins) = Payloads("x86-uninstall");
        var install = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(install.Failed, install.ToLog("install"));
        var host = Path.Combine(game, "OptiScaler64");
        foreach (var log in new[] { "OptiScaler.log", "dx9-host.log", "dxgi-x86.log", "mochizuki_nr.log" })
            File.WriteAllText(Path.Combine(host, log), "written while the game ran");

        var report = Work.Uninstall(game, Preset.OptiScaler);
        Assert.False(report.Failed, report.ToLog("uninstall"));
        Assert.Equal("somebody else's dxgi", Bytes(game, "dxgi.dll"));
        Assert.False(File.Exists(Path.Combine(host, "OptiScaler.dll")));
        Assert.False(File.Exists(Path.Combine(host, "OptiScalerDx9Host.exe")));
        Assert.False(Directory.Exists(Path.Combine(host, "OptiScaler")));
        foreach (var log in new[] { "OptiScaler.log", "dx9-host.log", "dxgi-x86.log", "mochizuki_nr.log" })
            Assert.False(File.Exists(Path.Combine(host, log)), $"{log} is still there");
        // The settings stay, as on every route.
        Assert.True(File.Exists(Path.Combine(host, Work.OptiScalerIni)));
    }
}
