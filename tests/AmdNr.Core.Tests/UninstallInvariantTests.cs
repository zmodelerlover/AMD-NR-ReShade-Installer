// These tests exist to stop "installed that will not uninstall": a game whose tile says installed,
// whose Uninstall runs clean, and whose folder goes on saying installed for ever -- which is what
// Castle Crashers did. They iterate the lists the app itself reads, so a marker, a route or a name
// added later without the uninstall knowing about it breaks here rather than on somebody's machine.

using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class UninstallInvariantTests
{
    /// <summary>Every preset the sheet can hand the uninstall: one per route it knows.</summary>
    private static readonly Preset[] Routes = [Preset.Dx11, Preset.X86Dx11, Preset.OptiScaler];

    /// <summary>What an uninstall that kept the settings leaves behind, on either route.</summary>
    private static void ConfigOnlyManifest(string dir, Route route)
    {
        var ini = "[GENERAL]\n"u8.ToArray();
        File.WriteAllBytes(Path.Combine(dir, "ReShade.ini"), ini);
        var m = new Manifest(route == Route.X86 ? "D3D9" : "D3D11", route);
        m.Entries.Add(new Entry { Name = "ReShade.ini", Hash = Engine.Sha(ini), Owned = true, Configuration = true });
        Manifest.WriteAtomic(dir, m);
    }

    [Fact]
    public void WhateverDetectionCallsInstalledUninstallTakesOut()
    {
        // Every name that makes a folder count as installed, and every name the engine may write at
        // all: a marker added later, or a file detection starts to read, is covered without a word here.
        var names = Work.InstalledMarkers.Concat(Engine.Allowed).Distinct().ToList();
        var failures = new List<string>();
        foreach (var name in names)
        foreach (var preset in Routes)
        foreach (var manifest in new Route?[] { null, Route.X64, Route.X86 })
        {
            var dir = Fixture.Temp("marker");
            var path = Path.Combine(dir, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"left loose: {name}");
            if (manifest is { } route) ConfigOnlyManifest(dir, route);

            var report = Work.Uninstall(dir, preset);
            if (report.Failed || GameScanner.IsInstalled(dir) || GameScanner.InstalledAs(dir) is not null)
                failures.Add($"{name}, uninstalled as {preset}, manifest {manifest?.ToString() ?? "none"}: "
                             + (report.Failed ? report.ToLog("failed") : "still installed"));
        }
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void EveryRouteInstalledAndUninstalledLeavesTheFolderAsItWas()
    {
        foreach (var theirs in new[] { false, true })
        {
            var tag = theirs ? "theirs" : "clean";

            var game = Game($"rt-x64-{tag}", x64: true, theirs);
            var (src, pins) = ReShadePayloads($"rt-x64-{tag}");
            RoundTrip($"x64 D3D11 ({tag})", game, game, Preset.Dx11,
                () => Succeeded(Work.Install(game, src, Preset.Dx11, pins)), [pins.ReShade64Sha]);

            foreach (var (preset, name) in new[] { (Preset.X86Dx8, "D3D8"), (Preset.X86Dx9, "D3D9"), (Preset.X86Dx11, "D3D11") })
            {
                var x86 = Game($"rt-{name}-{tag}", x64: false, theirs);
                var exe = Path.Combine(x86, "Game.exe");
                var (installer, pinned) = X86Release($"{name}-{tag}");
                RoundTrip($"x86 {name} ({tag})", x86, exe, preset, () => installer.Install(exe, name), pinned);
            }

            var opti = Game($"rt-opti011-{tag}", x64: true, theirs);
            var (optiSrc, optiPins) = OptiScalerRouteTests.Payloads($"rt-{tag}");
            RoundTrip($"OptiScaler 0.1.1 ({tag})", opti, opti, Preset.OptiScaler,
                () => Succeeded(Work.Install(opti, optiSrc, Preset.OptiScaler, optiPins)), []);

            var lmxxf = Game($"rt-opti020-{tag}", x64: true, theirs);
            var newest = OptiScalerVersionTests.Payload(OptiScalerVersionTests.Releases020)
                .Newest(PayloadManifest.OptiScalerComponent);
            var staged = OptiScalerVersionTests.Stage($"rt-{tag}", newest,
                PayloadManifest.OptiScalerComponent, PayloadManifest.LmxxfWeightsComponent);
            RoundTrip($"OptiScaler 0.2.0 ({tag})", lmxxf, lmxxf, Preset.OptiScaler,
                () => Succeeded(Work.Install(lmxxf, staged, Preset.OptiScaler, newest.Pins())), []);
        }
    }

    [Fact]
    public void NothingThatIsNotOursIsTakenOut()
    {
        foreach (var preset in Routes)
        {
            var dir = Fixture.Temp("not-ours");
            foreach (var marker in Work.InstalledMarkers) File.WriteAllText(Path.Combine(dir, marker), "left loose");
            var theirs = new Dictionary<string, string>
            {
                ["d3d9.dll"] = "a ReShade this app does not pin",
                ["dxgi.dll"] = "the game's own dxgi",
                ["dinput8.dll"] = "somebody's mod loader",
                ["Settings.ini"] = "another tool's settings",
                [Work.OptiScalerIni] = "an OptiScaler another setup put here",
                ["reshade-shaders/Shaders/Other.fx"] = "somebody's effect",
            };
            foreach (var (name, text) in theirs)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, name))!);
                File.WriteAllText(Path.Combine(dir, name), text);
            }
            // The pinned ReShade itself, which its owner installed before this app came along: the
            // manifest recorded it as theirs, so the hash alone does not make it ours.
            var own = "the pinned ReShade, installed by its owner"u8.ToArray();
            File.WriteAllBytes(Path.Combine(dir, "opengl32.dll"), own);
            var m = new Manifest("OpenGL", Route.X64);
            m.Entries.Add(new Entry { Name = "opengl32.dll", Hash = Engine.Sha(own), Owned = false });
            Manifest.WriteAtomic(dir, m);

            var report = Work.Uninstall(dir, preset, removeConfig: true, pinned: [Engine.Sha(own)]);
            Assert.False(report.Failed, report.ToLog(preset.ToString()));
            foreach (var (name, text) in theirs)
                Assert.True(File.Exists(Path.Combine(dir, name)) && File.ReadAllText(Path.Combine(dir, name)) == text,
                    $"{preset}: {name} was not ours to take");
            Assert.Equal(own, File.ReadAllBytes(Path.Combine(dir, "opengl32.dll")));
            Assert.False(GameScanner.IsInstalled(dir), $"{preset}: still installed");
        }
    }

    // -- Round trip ------------------------------------------------------------------------------

    private static void Succeeded(Report report) => Assert.False(report.Failed, report.ToLog("install"));

    /// <summary>A game folder: its executable, and with <paramref name="theirs"/> the files a real one
    /// has under names this app also writes, or beside them.</summary>
    private static string Game(string tag, bool x64, bool theirs)
    {
        var dir = Fixture.Temp(tag);
        File.WriteAllBytes(Path.Combine(dir, "Game.exe"), Fixture.Pe(x64));
        if (!theirs) return dir;
        File.WriteAllText(Path.Combine(dir, "dxgi.dll"), "the game's own dxgi");
        File.WriteAllText(Path.Combine(dir, "Settings.ini"), "another tool's settings");
        Directory.CreateDirectory(Path.Combine(dir, "reshade-shaders", "Shaders"));
        File.WriteAllText(Path.Combine(dir, "reshade-shaders", "Shaders", "Other.fx"), "somebody's effect");
        return dir;
    }

    /// <summary>Every file and folder under <paramref name="dir"/>, each file with its hash.</summary>
    private static SortedDictionary<string, string> Snapshot(string dir)
    {
        var all = new SortedDictionary<string, string>(StringComparer.Ordinal);
        foreach (var entry in Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories))
            all[Path.GetRelativePath(dir, entry)] = File.Exists(entry) ? Engine.HashFile(entry) : "folder";
        return all;
    }

    /// <summary>Install, then uninstall keeping the settings, then again taking them: the folder has
    /// to end exactly as it started, and in between differ only by the settings it was asked to keep.</summary>
    private static void RoundTrip(string what, string game, string target, Preset preset, Action install,
        IEnumerable<string> pinned)
    {
        var before = Snapshot(game);
        install();
        Assert.True(GameScanner.IsInstalled(game), $"{what}: not installed");

        var kept = Work.Uninstall(target, preset, pinned: pinned);
        Assert.False(kept.Failed, kept.ToLog(what));
        Assert.False(GameScanner.IsInstalled(game), $"{what}: still installed after uninstall");
        var settings = Work.KeptConfiguration(target, preset).Append(Engine.ManifestName).Append(Engine.ManifestNameX64).ToHashSet();
        Assert.Equal(before.Where(e => !settings.Contains(e.Key)), Snapshot(game).Where(e => !settings.Contains(e.Key)));

        var clean = Work.Uninstall(target, preset, removeConfig: true, pinned: pinned);
        Assert.False(clean.Failed, clean.ToLog(what));
        Assert.Empty(Work.KeptConfiguration(target, preset));
        Assert.Equal(before, Snapshot(game));
    }

    /// <summary>The 64-bit payloads with ReShade and the companion effect in them, so an install
    /// writes a proxy and a file in a folder of its own.</summary>
    internal static (string Src, PayloadPins Pins) ReShadePayloads(string tag)
    {
        var (src, pins) = Fixture.Payloads(tag);
        byte[] reShade = [.. Fixture.Pe(true), .. "stand-in ReShade64"u8];
        var effect = "stand-in companion effect"u8.ToArray();
        File.WriteAllBytes(Path.Combine(src, "ReShade64.dll"), reShade);
        File.WriteAllBytes(Path.Combine(src, Work.ShaderName), effect);
        return (src, new PayloadPins
        {
            AddonSha = pins.AddonSha, AddonSize = pins.AddonSize,
            RuntimeSha = pins.RuntimeSha, RuntimeSize = pins.RuntimeSize,
            WeightsSha = pins.WeightsSha, WeightsSize = pins.WeightsSize,
            ReShade64Sha = Engine.Sha(reShade),
            ShaderSha = Engine.Sha(effect), ShaderSize = (ulong)effect.Length,
        });
    }

    /// <summary>An unpacked x86 release of stand-ins, and an installer pinned to them.</summary>
    private static (X86Installer Installer, string[] Pinned) X86Release(string tag)
    {
        var release = Fixture.Temp($"x86-release-{tag}");
        var files = Path.Combine(release, "files");
        Directory.CreateDirectory(files);
        byte[] Pe(bool x64, string what) => [.. Fixture.Pe(x64), .. Encoding.UTF8.GetBytes(what)];
        var parts = new Dictionary<string, byte[]>
        {
            ["amd-nr.addon32"] = Pe(false, "addon32"),
            ["amd-nr-host64.exe"] = Pe(true, "host64"),
            ["dlssnr_amd_pass1.dll"] = "stand-in runtime"u8.ToArray(),
            ["dlssnr_on_amd_weights.bin"] = "stand-in weights"u8.ToArray(),
            ["dxgi.dll"] = Pe(false, "ReShade32"),
            ["d3d8to9.dll"] = Pe(false, "d3d8to9"),
            [Work.ShaderName] = "stand-in companion effect"u8.ToArray(),
        };
        foreach (var (name, bytes) in parts) File.WriteAllBytes(Path.Combine(files, name), bytes);
        File.WriteAllText(Path.Combine(release, "payload.sha256"),
            $"{Engine.Sha(parts["amd-nr.addon32"])}  amd-nr.addon32\n{Engine.Sha(parts["amd-nr-host64.exe"])}  amd-nr-host64.exe\n");

        string Sha(string name) => Engine.Sha(parts[name]);
        return (new X86Installer(release)
        {
            RuntimeSha = Sha("dlssnr_amd_pass1.dll"), WeightsSha = Sha("dlssnr_on_amd_weights.bin"),
            ReShadeSha = Sha("dxgi.dll"), D3d8To9Sha = Sha("d3d8to9.dll"),
        }, [Sha("dxgi.dll"), Sha("d3d8to9.dll")]);
    }
}
