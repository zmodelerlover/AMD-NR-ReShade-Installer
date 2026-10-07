using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>OptiScaler 0.5.0 and later: the network on the finished frame of a game without an upscaler, on
/// 64-bit D3D9, D3D11, D3D12, Vulkan and OpenGL. The route is recommended on all of them for that version and
/// only for D3D12 games with an upscaler before it.</summary>
public class OptiEverywhereTests
{
    private static GraphicsDetection Game(string tag, bool x64, params string[] imports)
    {
        var dir = Fixture.Temp($"everywhere-{tag}");
        File.WriteAllBytes(Path.Combine(dir, "Game.exe"), Fixture.PeWithImports(x64, imports));
        return GraphicsDetector.Detect(dir);
    }

    [Fact]
    public void OnlyAVersionFrom050OnRunsEverywhere()
    {
        Assert.True(Work.OptiRunsEverywhere("0.5.0-amd-nr"));
        Assert.True(Work.OptiRunsEverywhere("0.5.1-amd-nr"));
        Assert.False(Work.OptiRunsEverywhere("0.4.11-amd-nr"));
        Assert.False(Work.OptiRunsEverywhere(null));
        Assert.False(Work.OptiRunsEverywhere(""));
    }

    [Fact]
    public void From050OptiScalerIsRecommendedOnEveryApiItRunsOnAndBeforeItOnlyOnD3D12()
    {
        var dx11 = Game("dx11", true, "d3d11.dll");
        var gl = Game("gl", true, "opengl32.dll");
        var vk = Game("vk", true, "vulkan-1.dll");
        var dx9 = Game("dx9", true, "d3d9.dll");
        var dx12 = Game("dx12", true, "d3d12.dll");

        foreach (var game in new[] { dx11, gl, vk, dx9, dx12 })
        {
            Assert.Equal(Preset.OptiScaler, game.PresetFor(true));
            Assert.True(game.CanRunOptiScalerWith(true));
        }
        Assert.Equal(GraphicsApi.OpenGL, gl.OptiApi(true));
        Assert.Equal(GraphicsApi.D3D9, dx9.OptiApi(true));

        // An older OptiScaler keeps the rule it had.
        Assert.Equal(Preset.Dx11, dx11.PresetFor(false));
        Assert.Equal(Preset.OpenGL, gl.PresetFor(false));
        Assert.Equal(Preset.Vulkan, vk.PresetFor(false));
        Assert.False(gl.CanRunOptiScalerWith(false));
        Assert.Equal(Preset.OptiScaler, dx12.PresetFor(false));
    }

    [Fact]
    public void A32BitGameStaysOnReShade()
    {
        var x86 = Game("x86", false, "d3d9.dll");
        Assert.Equal(Preset.X86Dx9, x86.PresetFor(true));
        Assert.False(x86.CanRunOptiScalerWith(true));
    }

    [Fact]
    public void TheApiNamesTheFileOptiScalerGoesInAsUnlessSomethingElseDoes()
    {
        Assert.Equal("opengl32.dll", Work.OptiProxyFor(null, api: GraphicsApi.OpenGL));
        Assert.Equal("d3d9.dll", Work.OptiProxyFor(null, api: GraphicsApi.D3D9));
        Assert.Equal("winmm.dll", Work.OptiProxyFor(null, api: GraphicsApi.Vulkan));
        Assert.Equal("dxgi.dll", Work.OptiProxyFor(null, api: GraphicsApi.D3D11));
        Assert.Equal("dxgi.dll", Work.OptiProxyFor(null));
        Assert.Equal("winhttp.dll", Work.OptiProxyFor("winhttp.dll", api: GraphicsApi.OpenGL));
        Assert.Equal("version.dll", Work.OptiProxyFor(null, suggested: "version.dll", api: GraphicsApi.OpenGL));
    }

    [Fact]
    public void An050InstallOnAnOpenGLGameGoesInAsOpengl32AndRunsWithoutUpscaler()
    {
        var game = Fixture.Temp("everywhere-install-gl");
        File.WriteAllBytes(Path.Combine(game, "Game.exe"), Fixture.PeWithImports(true, ["opengl32.dll"]));
        var (src, pins) = OptiScalerRouteTests.Payloads("everywhere-gl", "0.5.0-amd-nr");

        var report = Work.Install(game, src, Preset.OptiScaler, pins, api: GraphicsApi.OpenGL);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.True(File.Exists(Path.Combine(game, "opengl32.dll")));
        Assert.False(File.Exists(Path.Combine(game, "dxgi.dll")));
        var ini = File.ReadAllText(Path.Combine(game, Work.OptiScalerIni));
        Assert.Equal("true", Engine.Trim(Engine.GetIni(ini, "DlssNr", "PresentWithoutUpscaler")));
        Assert.True(Fixture.HasAny(report, "NR without upscaling on"), report.ToLog("install"));

        // An older OptiScaler on the same game ignores the API and keeps the package's ini.
        var old = Fixture.Temp("everywhere-install-old");
        File.WriteAllBytes(Path.Combine(old, "Game.exe"), Fixture.PeWithImports(true, ["opengl32.dll"]));
        var (src2, pins2) = OptiScalerRouteTests.Payloads("everywhere-old", "0.4.11-amd-nr");
        Assert.False(Work.Install(old, src2, Preset.OptiScaler, pins2, api: GraphicsApi.OpenGL).Failed);
        Assert.True(File.Exists(Path.Combine(old, "dxgi.dll")));
        Assert.Empty(Engine.GetIni(File.ReadAllText(Path.Combine(old, Work.OptiScalerIni)), "DlssNr", "PresentWithoutUpscaler") ?? "");
    }

    [Fact]
    public void AnUpscalerBuiltIntoTheExecutableIsFoundEvenAcrossAReadBoundary()
    {
        var dir = Fixture.Temp("everywhere-builtin");
        var exe = Path.Combine(dir, "Game.exe");
        var mark = Encoding.ASCII.GetBytes("NVSDK_NGX_D3D12_Init");
        // The mark straddles the first 8 MB read.
        var bytes = new byte[(8 << 20) + 64];
        mark.CopyTo(bytes, (8 << 20) - 7);
        Encoding.Unicode.GetBytes("FSR3UPSCALER_InputColor").CopyTo(bytes, 100);
        File.WriteAllBytes(exe, bytes);
        Assert.Equal(["DLSS", "FSR"], GraphicsDetector.BuiltIn(exe));

        var plain = Path.Combine(dir, "Plain.exe");
        File.WriteAllBytes(plain, Encoding.ASCII.GetBytes("FidelityFX CAS only"));
        Assert.Empty(GraphicsDetector.BuiltIn(plain));
    }

    [Fact]
    public void AnUpscalerInASubfolderIsFoundByTheDeepSearchOnly()
    {
        var dir = Fixture.Temp("everywhere-unity");
        var plugins = Path.Combine(dir, "Game_Data", "Plugins", "x86_64");
        Directory.CreateDirectory(plugins);
        File.WriteAllText(Path.Combine(plugins, "nvngx_dlss.dll"), "x");
        Assert.Empty(GraphicsDetector.Upscalers(dir));
        Assert.Contains("nvngx_dlss.dll", GraphicsDetector.UpscalersDeep(dir));
    }

    [Fact]
    public void AnotherAmdNrModIsNamedAndItsIniIsReplacedNotKept()
    {
        var game = Fixture.Temp("everywhere-foreign");
        File.WriteAllBytes(Path.Combine(game, "Game.exe"), Fixture.PeWithImports(true, ["d3d12.dll"]));
        File.WriteAllText(Path.Combine(game, "LmxxfNrRuntime.pak"), "theirs");
        File.WriteAllText(Path.Combine(game, Work.OptiScalerIni), "[DlssNr]\r\nNrBackend=daniel\r\n\r\n[AmdLook]\r\nX=1\r\n");
        var (src, pins) = OptiScalerRouteTests.Payloads("everywhere-foreign");

        Assert.Contains("LmxxfNrRuntime.pak", Work.ForeignNrMod(game, null));
        var report = Work.Install(game, src, Preset.OptiScaler, pins);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.True(Fixture.HasAny(report, "Another AMD NR mod"), report.ToLog("install"));
        Assert.Equal("stand-in OptiScaler.ini", File.ReadAllText(Path.Combine(game, Work.OptiScalerIni)));

        // Uninstall puts theirs back.
        Assert.False(Work.Uninstall(game, Preset.OptiScaler).Failed);
        Assert.Contains("[AmdLook]", File.ReadAllText(Path.Combine(game, Work.OptiScalerIni)));
    }

    [Fact]
    public void UninstallTakesTheRuntimeCopiesNrWithoutUpscalingMakes()
    {
        var game = Fixture.Temp("everywhere-present-copies");
        File.WriteAllBytes(Path.Combine(game, "Game.exe"), Fixture.PeWithImports(true, ["d3d12.dll"]));
        var (src, pins) = OptiScalerRouteTests.Payloads("everywhere-present", "0.5.0-amd-nr");
        Assert.False(Work.Install(game, src, Preset.OptiScaler, pins).Failed);
        foreach (var n in new[] { 1, 2, 3 })
            File.WriteAllText(Path.Combine(game, $"dlssnr_amd_present{n}.dll"), "runtime copy");

        Assert.False(Work.Uninstall(game, Preset.OptiScaler).Failed);
        foreach (var n in new[] { 1, 2, 3 })
            Assert.False(File.Exists(Path.Combine(game, $"dlssnr_amd_present{n}.dll")));
    }
}
