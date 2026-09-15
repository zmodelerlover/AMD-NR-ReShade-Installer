using System.IO.Compression;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class ReShadeTests
{
    /// <summary>An executable with a zip appended to it, the way ReShade's setup ships its DLLs: the
    /// zip's own offsets count from where the zip starts, which a plain ZipArchive over the whole file
    /// refuses.</summary>
    private static string SetupWithAppendedZip(string dir, params (string Name, byte[] Bytes)[] entries)
    {
        using var zipBytes = new MemoryStream();
        using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (name, bytes) in entries)
            {
                using var stream = zip.CreateEntry(name).Open();
                stream.Write(bytes);
            }
        }
        var path = Path.Combine(dir, "ReShade_Setup.exe");
        File.WriteAllBytes(path, Fixture.Pe(false).Concat(new byte[4096]).Concat(zipBytes.ToArray()).ToArray());
        return path;
    }

    [Fact]
    public void ADllIsExtractedFromTheZipAppendedToTheSetupAndVerified()
    {
        var dir = Fixture.Temp("appended");
        var dll = Fixture.Pe(true).Concat(new byte[1000]).ToArray();
        var setup = SetupWithAppendedZip(dir, ("ReShade64.dll", dll), ("ReShade64.json", "{}"u8.ToArray()));

        var target = Path.Combine(dir, "out", "ReShade64.dll");
        PayloadCache.ExtractVerified(setup,
            new PayloadFile { Name = "ReShade64.dll", Size = (ulong)dll.Length, Sha256 = Engine.Sha(dll) }, target);
        Assert.Equal(dll, File.ReadAllBytes(target));

        // A different build inside the same envelope is refused and nothing is written.
        var other = Path.Combine(dir, "out", "wrong.dll");
        Assert.Throws<InstallException>(() => PayloadCache.ExtractVerified(setup,
            new PayloadFile { Name = "ReShade64.dll", Size = 1, Sha256 = new string('a', 64) }, other));
        Assert.False(File.Exists(other));
    }

    [Fact]
    public void TheShippedManifestPinsTheSameReShadeBuildAsTheEngine()
    {
        var m = PayloadManifest.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "payload.json")));
        var reshade = m.Component(PayloadManifest.ReShadeComponent);
        Assert.StartsWith("https://reshade.me/", reshade.Files[0].Url, StringComparison.Ordinal);
        Assert.Equal(Engine.ReShade64Sha, reshade.Installed.Single(f => f.Name == "ReShade64.dll").Sha256);
        Assert.Equal(Engine.ReShadeSha, reshade.Installed.Single(f => f.Name == "ReShade32.dll").Sha256);
    }

    [Fact]
    public void AManifestAddressThatIsNotHttpsIsRefused()
    {
        const string json = """
            { "schema": 1, "components": { "reshade": { "version": "1", "files": [
              { "name": "setup.exe", "url": "http://example.com/setup.exe", "size": 1,
                "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa" } ] } } }
            """;
        Assert.Throws<InstallException>(() => PayloadManifest.Parse(json));
    }

    // -- Proxy name ---------------------------------------------------------------------------------

    [Fact]
    public void ReShadeTakesDxgiWhenTheFolderIsClear() =>
        Assert.Equal("dxgi.dll", Work.ReShadeProxyFor(Preset.Dx11, Fixture.Temp("clear")));

    /// <summary>OptiScaler installs as dxgi.dll. Overwriting it would silently remove it; ReShade goes
    /// in under the API's own DLL instead and both load.</summary>
    [Fact]
    public void ADxgiThatBelongsToSomethingElseIsLeftAndTheApiDllIsUsed()
    {
        var game = Fixture.Temp("optiscaler-like");
        File.Copy(Path.Combine(Environment.SystemDirectory, "dxgi.dll"), Path.Combine(game, "dxgi.dll"));
        Assert.Equal("d3d11.dll", Work.ReShadeProxyFor(Preset.Dx11, game));
        Assert.Equal("d3d12.dll", Work.ReShadeProxyFor(Preset.Dx12, game));
    }

    [Fact]
    public void VulkanHasNoProxyToInstall() =>
        Assert.Null(Work.ReShadeProxyFor(Preset.Vulkan, Fixture.Temp("vulkan")));

    // -- ReShade.ini ---------------------------------------------------------------------------------

    [Fact]
    public void TheIniIsReadyOnFirstLaunchAndKeepsEverythingElse()
    {
        const string before = "[GENERAL]\nPerformanceMode=1\n[ADDON]\nDisabledAddons=Other.addon64,dlss5 neural@dlss5-neural.addon64\n";
        var after = Work.ReadyReShadeIni(before);

        Assert.Equal("1", Engine.GetIni(after, "GENERAL", "PerformanceMode"));
        Assert.Equal("Other.addon64", Engine.GetIni(after, "ADDON", "DisabledAddons"));
        Assert.Equal("4", Engine.GetIni(after, "OVERLAY", "TutorialProgress"));
        Assert.Contains("DLSS Neural Rendering (AMD)", Engine.GetIni(after, "OVERLAY", "Window"), StringComparison.Ordinal);

        // Running it again changes nothing: a reinstall must not churn someone's ini.
        Assert.Equal(after, Work.ReadyReShadeIni(after));
    }

    // -- The install itself --------------------------------------------------------------------------

    [Fact]
    public void AnX64InstallPutsReShadeInAndTakesItBackOut()
    {
        var game = Fixture.Temp("with-reshade");
        var (src, pins) = Fixture.Payloads("with-reshade");
        var reShade = Fixture.Pe(true).Concat(new byte[2048]).ToArray();
        File.WriteAllBytes(Path.Combine(src, "ReShade64.dll"), reShade);
        pins = new PayloadPins
        {
            AddonSha = pins.AddonSha, AddonSize = pins.AddonSize,
            RuntimeSha = pins.RuntimeSha, RuntimeSize = pins.RuntimeSize,
            WeightsSha = pins.WeightsSha, WeightsSize = pins.WeightsSize,
            ReShade64Sha = Engine.Sha(reShade),
        };

        var pre = Work.Preflight(game, src, Preset.Dx11, pins);
        Assert.False(Fixture.HasAny(pre, "No ReShade proxy DLL found"), pre.ToLog("pre"));
        Assert.True(Fixture.HasAny(pre, "part of this install"), pre.ToLog("pre"));

        var report = Work.Install(game, src, Preset.Dx11, pins);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.Equal(reShade, File.ReadAllBytes(Path.Combine(game, "dxgi.dll")));
        Assert.Equal("4", Engine.GetIni(File.ReadAllText(Path.Combine(game, "ReShade.ini")), "OVERLAY", "TutorialProgress"));

        var removed = Work.Uninstall(game, Preset.Dx11);
        Assert.False(removed.Failed, removed.ToLog("uninstall"));
        Assert.False(File.Exists(Path.Combine(game, "dxgi.dll")), "ReShade was ours, so it goes");

        // This is the route the reported bug came in on. Installing ReShade writes ReShade.ini as an
        // owned configuration entry, uninstall preserves it, and preserving it keeps the manifest --
        // so the manifest is still here, and reading the state off it reported "installed" forever.
        Assert.True(File.Exists(Path.Combine(game, Route.X64.ManifestFileName())),
            "the manifest is kept on purpose, to hold the preserved ReShade.ini entry");
        Assert.False(GameScanner.IsInstalled(game),
            "and the folder must still stop reporting itself installed");
    }

    /// <summary>A 32-bit D3D9 game loads d3d9.dll and never dxgi.dll. Checking the 64-bit names
    /// there reported "no ReShade proxy DLL found" about a folder with ReShade sitting in it -- on
    /// every 32-bit install, including the ones this installer had just written itself.</summary>
    [Fact]
    public void TheReShadeCheckLooksForTheNameThisRouteActuallyLoads()
    {
        var game = Fixture.Temp("proxy-names");
        var (src, pins) = Fixture.Payloads("proxy-names");
        File.WriteAllBytes(Path.Combine(game, "game.exe"), Fixture.Pe(false));
        File.WriteAllBytes(Path.Combine(game, "d3d9.dll"), Fixture.Pe(false));

        var d3d9 = Work.Preflight(game, src, Preset.X86Dx9, pins);
        Assert.False(Fixture.HasAny(d3d9, "No ReShade proxy DLL found"), d3d9.ToLog("x86 d3d9"));

        // And the same folder on a route that really does want dxgi.dll still says so.
        var d3d11 = Work.Preflight(game, src, Preset.X86Dx11, pins);
        Assert.True(Fixture.HasAny(d3d11, "No ReShade proxy DLL found"), d3d11.ToLog("x86 d3d11"));
        Assert.False(Fixture.HasAny(d3d11, "d3d12.dll"), d3d11.ToLog("x86 d3d11"));
    }

    [Fact]
    public void AReShadeThatDoesNotMatchItsPinIsRefusedAndNothingIsWritten()
    {
        var game = Fixture.Temp("bad-reshade");
        var (src, pins) = Fixture.Payloads("bad-reshade");
        File.WriteAllBytes(Path.Combine(src, "ReShade64.dll"), Fixture.Pe(true));

        var report = Work.Install(game, src, Preset.Dx11, pins); // pins still expect the real build
        Assert.True(report.Failed);
        Assert.False(File.Exists(Path.Combine(game, "dxgi.dll")));
        Assert.False(File.Exists(Path.Combine(game, Work.AddonName)));
    }
}
