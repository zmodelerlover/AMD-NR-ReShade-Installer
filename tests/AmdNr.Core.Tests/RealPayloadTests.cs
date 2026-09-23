using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class RealPayloadTests
{
    // -- The real payloads -------------------------------------------------------------------------

    /// <summary>End to end against the genuine runtime and weights. Set AMDNR_TEST_PAYLOAD_DIR to a
    /// folder holding dlssnr_amd_pass1.dll, dlssnr_on_amd_weights.bin and amd-nr.addon64;
    /// skipped otherwise, because those are 141 MB and not in any repository.</summary>
    [Fact]
    public void ARealPayloadRoundTrip()
    {
        var payloads = Environment.GetEnvironmentVariable("AMDNR_TEST_PAYLOAD_DIR");
        if (string.IsNullOrWhiteSpace(payloads) || !Directory.Exists(payloads)) return;

        var dir = Work.PayloadDir(payloads);
        var addon = Path.Combine(dir, Work.AddonName);
        if (!File.Exists(addon)) return;

        var pins = new PayloadPins
        {
            AddonSha = Engine.HashFile(addon),
            AddonSize = Engine.SizeOf(addon)!.Value,
        };

        var game = Fixture.Temp("real");
        var report = Work.Install(game, payloads, Preset.Dx11, pins);
        Assert.False(report.Failed, report.ToLog("real install"));
        Assert.Equal(Engine.RuntimeSha, Engine.HashFile(Path.Combine(game, Work.RuntimeName)));
        Assert.Equal(Engine.WeightsSha, Engine.HashFile(Path.Combine(game, Work.WeightsName)));

        // Running it twice is what a person does when they are not sure it worked.
        var manifestBefore = File.ReadAllBytes(Path.Combine(game, Route.X64.ManifestFileName()));
        var again = Work.Install(game, payloads, Preset.Dx11, pins);
        Assert.False(again.Failed, again.ToLog("real reinstall"));
        Assert.Equal(manifestBefore, File.ReadAllBytes(Path.Combine(game, Route.X64.ManifestFileName())));

        var removed = Work.Uninstall(game, Preset.Dx11);
        Assert.False(removed.Failed, removed.ToLog("real uninstall"));
        Assert.False(File.Exists(Path.Combine(game, Work.WeightsName)));
    }

    /// <summary>The name ReShade goes in as can be chosen, and only from names this API can be
    /// loaded under. Automatic takes dxgi.dll because it serves every Direct3D, which is wrong for
    /// the games that only ever load d3d12.dll -- that is the whole reason the choice exists.
    /// A name from another API is ignored rather than installed: writing d3d12.dll into a D3D11
    /// game leaves a file nothing opens, and the install would look like it worked.</summary>
    [Fact]
    public void TheProxyNameCanBeChosenFromWhatTheApiCanActuallyLoad()
    {
        Assert.Equal(["dxgi.dll", "d3d12.dll", "dinput8.dll"], Work.ProxyChoicesFor(Preset.Dx12));
        Assert.Equal(["dxgi.dll", "d3d11.dll", "dinput8.dll"], Work.ProxyChoicesFor(Preset.Dx11));
        Assert.Empty(Work.ProxyChoicesFor(Preset.Vulkan));

        // version.dll is the one people ask for, and the pinned ReShade exports nothing of it --
        // a game importing it would fail to resolve rather than load ReShade.
        Assert.False(Work.ProxyAllowed(Preset.Dx12, "version.dll"));
        Assert.False(Work.ProxyAllowed(Preset.Dx11, "d3d12.dll"));
        Assert.True(Work.ProxyAllowed(Preset.Dx12, "d3d12.dll"));
        Assert.True(Work.ProxyAllowed(Preset.Dx12, "D3D12.DLL"));
        Assert.False(Work.ProxyAllowed(Preset.Dx12, null));
    }

    /// <summary>The update is checked against the hash its own release publishes before anything is
    /// written, because this is the one path that writes a file the app then runs as itself. And the
    /// swap never leaves the app without an executable: Windows will not let a running image be
    /// overwritten but will let it be renamed, so the outgoing one is moved aside and put back if
    /// the replacement cannot land.</summary>
    [Fact]
    public void AnUpdateIsVerifiedBeforeItReplacesAnything()
    {
        var dir = Path.Combine(Path.GetTempPath(), "upd-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var bytes = "a new build"u8.ToArray();
        var sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        var sums = string.Join('\n', $"{sha} *AMD-NR-ReShade-Installer.exe", "0000 not-a-pin", "");

        Assert.True(AppUpdater.Verify(bytes, sums, "AMD-NR-ReShade-Installer.exe"));
        Assert.False(AppUpdater.Verify("something else"u8.ToArray(), sums, "AMD-NR-ReShade-Installer.exe"));
        Assert.False(AppUpdater.Verify(bytes, sums, "not-listed.exe"));
        Assert.False(AppUpdater.Verify(bytes, "", "AMD-NR-ReShade-Installer.exe"));

        // The exact shape the release publishes: two spaces, no asterisk, CRLF, more than one file
        // listed. A sums file this cannot parse is not an error anywhere -- CanSelfUpdate goes false
        // and the app quietly opens the browser instead, which is what v0.1.0 did for want of one.
        Assert.True(AppUpdater.Verify(bytes,
            $"{sha}  AMD-NR-ReShade-Installer.exe\r\n{new string('0', 64)}  AMD-NR-ReShade-Installer-v0.1.1.zip\r\n",
            "AMD-NR-ReShade-Installer.exe"));

        var current = Path.Combine(dir, "app.exe");
        var staged = Path.Combine(dir, "staged.exe");
        File.WriteAllBytes(current, "the old build"u8.ToArray());
        File.WriteAllBytes(staged, bytes);

        var parked = AppUpdater.Swap(current, staged);
        Assert.Equal(bytes, File.ReadAllBytes(current));
        Assert.Equal("the old build"u8.ToArray(), File.ReadAllBytes(parked));
        Assert.False(File.Exists(staged));

        // And the sweep takes the parked one, which is what the next start does.
        AppUpdater.SweepOld(dir);
        Assert.False(File.Exists(parked));
        Assert.True(File.Exists(current));

        // A staged file that is not there is refused before the running one is moved anywhere.
        Assert.Throws<InstallException>(() => AppUpdater.Swap(current, Path.Combine(dir, "gone.exe")));
        Assert.Equal(bytes, File.ReadAllBytes(current));

        Directory.Delete(dir, recursive: true);
    }
}
