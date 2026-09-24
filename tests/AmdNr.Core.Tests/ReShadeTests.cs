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

    /// <summary>An OpenGL game loads opengl32.dll and never dxgi.dll, so the automatic pick has to
    /// be that name and nothing else -- writing dxgi.dll into an OpenGL game produces a file the
    /// game never opens, and the install would look like it worked.</summary>
    [Fact]
    public void OpenGLTakesTheNameAnOpenGLGameActuallyLoads()
    {
        var game = Fixture.Temp("opengl-proxy");
        Assert.Equal("opengl32.dll", Work.ReShadeProxyFor(Preset.OpenGL, game));
        Assert.Equal("opengl32.dll", Work.ProxyNameFor(Preset.OpenGL, game, null));
        // A name from another route is ignored rather than honoured.
        Assert.Equal("opengl32.dll", Work.ProxyNameFor(Preset.OpenGL, game, "dxgi.dll"));
        Assert.Equal("dinput8.dll", Work.ProxyNameFor(Preset.OpenGL, game, "dinput8.dll"));
    }

    // -- ReShade.ini ---------------------------------------------------------------------------------

    [Fact]
    public void TheIniIsReadyOnFirstLaunchAndKeepsEverythingElse()
    {
        const string before = "[GENERAL]\nPerformanceMode=1\n[ADDON]\nDisabledAddons=Other.addon64,dlss5 neural@amd-nr.addon64\n";
        var after = Work.ReadyReShadeIni(before);

        Assert.Equal("1", Engine.GetIni(after, "GENERAL", "PerformanceMode"));
        Assert.Equal("Other.addon64", Engine.GetIni(after, "ADDON", "DisabledAddons"));
        Assert.Equal("4", Engine.GetIni(after, "OVERLAY", "TutorialProgress"));
        Assert.Contains("AMD Neural Rendering", Engine.GetIni(after, "OVERLAY", "Window"), StringComparison.Ordinal);

        // Running it again changes nothing: a reinstall must not churn someone's ini.
        Assert.Equal(after, Work.ReadyReShadeIni(after));
    }

    // -- The install itself --------------------------------------------------------------------------

    [Fact]
    public void UpgradingFromTheOldNamesKeepsOwnershipOfWhatWasInstalled()
    {
        // The manifest was renamed along with everything else in v0.6.5. Without migration an
        // upgrade finds no manifest, treats a folder that already has an install as fresh, and
        // records every file already there as Owned=false -- and uninstall only removes what it
        // owns, so ReShade would be left in the game folder for good.
        var game = Fixture.Temp("upgrade");
        var (src, pins) = Fixture.Payloads("upgrade");
        var reShade = Fixture.Pe(true).Concat(new byte[2048]).ToArray();
        File.WriteAllBytes(Path.Combine(Work.PayloadDir(src), "ReShade64.dll"), reShade);
        pins = new PayloadPins
        {
            AddonSha = pins.AddonSha, AddonSize = pins.AddonSize,
            RuntimeSha = pins.RuntimeSha, RuntimeSize = pins.RuntimeSize,
            WeightsSha = pins.WeightsSha, WeightsSize = pins.WeightsSize,
            ReShade64Sha = Engine.Sha(reShade),
        };

        // Install, then put the folder back the way v0.6.0 left it: old manifest name, and the
        // add-on under the name it used then.
        var first = Work.Install(game, src, Preset.Dx11, pins);
        Assert.False(first.Failed, first.ToLog("first"));
        var newManifest = Path.Combine(game, Engine.ManifestNameX64);
        var oldManifest = Path.Combine(game, "dlss5-neural.install.json");
        var text = File.ReadAllText(newManifest).Replace(Work.AddonName, "dlss5-neural.addon64");
        File.WriteAllText(oldManifest, text);
        File.Delete(newManifest);
        File.Move(Path.Combine(game, Work.AddonName), Path.Combine(game, "dlss5-neural.addon64"));

        // Upgrading now has to reclaim the folder rather than start a new one.
        var second = Work.Install(game, src, Preset.Dx11, pins);
        Assert.False(second.Failed, second.ToLog("second"));
        Assert.False(File.Exists(oldManifest), "the old manifest is carried over, not left beside");
        Assert.False(File.Exists(Path.Combine(game, "dlss5-neural.addon64")),
            "the old add-on is swept, or ReShade would load both");

        var removed = Work.Uninstall(game, Preset.Dx11);
        Assert.False(removed.Failed, removed.ToLog("uninstall"));
        Assert.False(File.Exists(Path.Combine(game, "dxgi.dll")),
            "ReShade was ours before the rename and is still ours after it");
        Assert.False(File.Exists(Path.Combine(game, Work.AddonName)));
    }

    [Fact]
    public void AnEffectWithNothingToCompileAgainstIsTakenOutOnReinstall()
    {
        // Red Dead Redemption: an earlier install put the effect in with no ReShade.fxh beside it,
        // and ReShade reported a compile error in every launch. Installing again has to take it out.
        var game = Fixture.Temp("effect-stale");
        var shaders = Path.Combine(game, "reshade-shaders", "Shaders");
        Directory.CreateDirectory(shaders);
        File.WriteAllText(Path.Combine(shaders, "ReShade.fxh"), "// header");
        var (src, pins) = Fixture.Payloads("effect-stale");
        var effect = System.Text.Encoding.UTF8.GetBytes("// AMD_Neural_Feed");
        File.WriteAllBytes(Path.Combine(Work.PayloadDir(src), Work.ShaderName), effect);
        pins = new PayloadPins
        {
            AddonSha = pins.AddonSha, AddonSize = pins.AddonSize,
            RuntimeSha = pins.RuntimeSha, RuntimeSize = pins.RuntimeSize,
            WeightsSha = pins.WeightsSha, WeightsSize = pins.WeightsSize,
            ShaderSha = Engine.Sha(effect), ShaderSize = (ulong)effect.Length,
        };
        Assert.False(Work.Install(game, src, Preset.Dx11, pins).Failed);
        Assert.True(File.Exists(Path.Combine(shaders, Work.ShaderName)));

        File.Delete(Path.Combine(shaders, "ReShade.fxh"));
        var again = Work.Install(game, src, Preset.Dx11, pins);
        Assert.False(again.Failed, again.ToLog("reinstall"));
        Assert.False(File.Exists(Path.Combine(shaders, Work.ShaderName)), "the effect stayed with nothing to compile against");
        Assert.Contains(again.Lines, l => l.Text.Contains("was left out", StringComparison.Ordinal));
    }

    [Fact]
    public void TheCompanionEffectLandsWhereReShadeLooksForIt()
    {
        // The effect is the only file this installs outside the game's root, and if it lands
        // anywhere but reshade-shaders/Shaders ReShade never compiles it -- so the add-on
        // silently falls back to its own motion estimator and the whole point of shipping it
        // is lost, with nothing in any log saying so.
        var game = Fixture.Temp("with-shader");
        // ReShade's standard shaders, which a motion-vector shader brings and the effect includes.
        Directory.CreateDirectory(Path.Combine(game, "reshade-shaders", "Shaders"));
        File.WriteAllText(Path.Combine(game, "reshade-shaders", "Shaders", "ReShade.fxh"), "// header");
        var (src, pins) = Fixture.Payloads("with-shader");
        var effect = System.Text.Encoding.UTF8.GetBytes("// AMD_Neural_Feed");
        File.WriteAllBytes(Path.Combine(Work.PayloadDir(src), Work.ShaderName), effect);
        pins = new PayloadPins
        {
            AddonSha = pins.AddonSha, AddonSize = pins.AddonSize,
            RuntimeSha = pins.RuntimeSha, RuntimeSize = pins.RuntimeSize,
            WeightsSha = pins.WeightsSha, WeightsSize = pins.WeightsSize,
            ShaderSha = Engine.Sha(effect), ShaderSize = (ulong)effect.Length,
        };

        var report = Work.Install(game, src, Preset.Dx11, pins);
        Assert.False(report.Failed, report.ToLog("install"));
        var landed = Path.Combine(game, "reshade-shaders", "Shaders", Work.ShaderName);
        Assert.True(File.Exists(landed), "the effect must be under reshade-shaders/Shaders");
        Assert.Equal(effect, File.ReadAllBytes(landed));

        // And it comes back out, subdirectory and all.
        var removed = Work.Uninstall(game, Preset.Dx11);
        Assert.False(removed.Failed, removed.ToLog("uninstall"));
        Assert.False(File.Exists(landed), "the effect was ours, so it goes");
    }

    [Fact]
    public void AManifestWithoutAShaderStillInstalls()
    {
        // Every manifest published before v0.6.5 has no shader component. An install reading one
        // has to skip the effect, not fail.
        var game = Fixture.Temp("no-shader");
        var (src, pins) = Fixture.Payloads("no-shader");
        Assert.Equal(string.Empty, pins.ShaderSha);

        var report = Work.Install(game, src, Preset.Dx11, pins);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.False(Directory.Exists(Path.Combine(game, "reshade-shaders")));
    }


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

    /// <summary>The whole OpenGL install, end to end: ReShade lands under the name an OpenGL game
    /// loads, the manifest records the route by a name that has to keep meaning this one, and
    /// uninstall takes back what it put in. This is the route the add-on's gl_route.inc drives.</summary>
    [Fact]
    public void AnOpenGLInstallPutsReShadeInAsOpenGL32AndTakesItBackOut()
    {
        var game = Fixture.Temp("opengl-install");
        var (src, pins) = Fixture.Payloads("opengl-install");
        var reShade = Fixture.Pe(true).Concat(new byte[2048]).ToArray();
        File.WriteAllBytes(Path.Combine(src, "ReShade64.dll"), reShade);
        pins = new PayloadPins
        {
            AddonSha = pins.AddonSha, AddonSize = pins.AddonSize,
            RuntimeSha = pins.RuntimeSha, RuntimeSize = pins.RuntimeSize,
            WeightsSha = pins.WeightsSha, WeightsSize = pins.WeightsSize,
            ReShade64Sha = Engine.Sha(reShade),
        };

        var report = Work.Install(game, src, Preset.OpenGL, pins);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.Equal(reShade, File.ReadAllBytes(Path.Combine(game, "opengl32.dll")));
        Assert.False(File.Exists(Path.Combine(game, "dxgi.dll")), "an OpenGL game never loads dxgi.dll");
        foreach (var name in new[] { Work.AddonName, Work.RuntimeName, Work.WeightsName })
            Assert.True(File.Exists(Path.Combine(game, name)), $"{name} was not installed");

        var m = Manifest.Decode(File.ReadAllText(Path.Combine(game, Route.X64.ManifestFileName())));
        Assert.Equal("OpenGL", m.Preset);
        Assert.Equal(Route.X64, m.Route);

        var removed = Work.Uninstall(game, Preset.OpenGL);
        Assert.False(removed.Failed, removed.ToLog("uninstall"));
        Assert.False(File.Exists(Path.Combine(game, "opengl32.dll")), "ReShade was ours, so it goes");
        Assert.False(GameScanner.IsInstalled(game));
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

    /// <summary>Two ReShades in one process and the game does not start at all -- no window, no line
    /// in any log. It came in as "tried it with the installer and the extras files, game refuses to
    /// launch once these dlls are dropped": the extras ship ReShade32 under the name dxgi.dll, which
    /// is only ever meant to be *written as* d3d9.dll, and dropping it in by hand leaves two.
    ///
    /// The check that should have caught it looked only at the names this route loads, so the file
    /// that collides -- by definition the other one -- was the one it could not see.</summary>
    [Fact]
    public void ASecondReShadeUnderAnotherNameIsRefusedBeforeAnythingIsWritten()
    {
        var game = Fixture.Temp("two-reshades");
        var (src, pins) = Fixture.Payloads("two-reshades");
        var reShade = Fixture.Pe(true).Concat(new byte[2048]).ToArray();
        File.WriteAllBytes(Path.Combine(src, "ReShade64.dll"), reShade);
        pins = new PayloadPins
        {
            AddonSha = pins.AddonSha, AddonSize = pins.AddonSize,
            RuntimeSha = pins.RuntimeSha, RuntimeSize = pins.RuntimeSize,
            WeightsSha = pins.WeightsSha, WeightsSize = pins.WeightsSize,
            ReShade64Sha = Engine.Sha(reShade),
        };

        // The same bytes this install is about to write, under a name this route never loads.
        var dropped = Path.Combine(game, "d3d9.dll");
        File.WriteAllBytes(dropped, reShade);

        var pre = Work.Preflight(game, src, Preset.Dx11, pins);
        Assert.True(Fixture.HasErr(pre, "second ReShade"), pre.ToLog("pre"));
        Assert.True(Fixture.HasAny(pre, "d3d9.dll"), pre.ToLog("pre"));

        var refused = Work.Install(game, src, Preset.Dx11, pins);
        Assert.True(refused.Failed, refused.ToLog("refused"));
        Assert.False(File.Exists(Path.Combine(game, "dxgi.dll")), "nothing may be written over a folder that cannot launch");

        // And the same folder with the stray file gone installs exactly as before: the check has to
        // stay silent about the one ReShade that is supposed to be there.
        File.Delete(dropped);
        var ok = Work.Install(game, src, Preset.Dx11, pins);
        Assert.False(ok.Failed, ok.ToLog("install"));
        Assert.Equal(reShade, File.ReadAllBytes(Path.Combine(game, "dxgi.dll")));
    }

    /// <summary>The "ReShade loads as" menu, honoured. The 32-bit route ignored it and wrote the
    /// API's own name regardless, and dinput8.dll -- offered on every route -- was not in
    /// <see cref="Engine.Allowed"/> at all, so choosing it made the transaction refuse the install.</summary>
    [Fact]
    public void EveryProxyNameTheMenuOffersIsOneThatCanBeInstalled()
    {
        var dir = Fixture.Temp("proxy-choice");

        Assert.Equal("d3d9.dll", Work.ProxyNameFor(Preset.X86Dx9, dir, null));
        Assert.Equal("dinput8.dll", Work.ProxyNameFor(Preset.X86Dx9, dir, "dinput8.dll"));
        Assert.Equal("dxgi.dll", Work.ProxyNameFor(Preset.X86Dx11, dir, null));
        Assert.Equal("d3d9.dll", Work.ProxyNameFor(Preset.X86Dx8, dir, "d3d12.dll"));
        Assert.Null(Work.ProxyNameFor(Preset.Vulkan, dir, "dxgi.dll"));

        foreach (var preset in Presets.All)
            Assert.All(Work.ProxyChoicesFor(preset), name => Assert.Contains(name, Engine.Allowed));
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
