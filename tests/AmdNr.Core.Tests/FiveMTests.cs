using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class FiveMTests
{
    /// <summary>A FiveM folder the way a player has it: FiveM.exe above FiveM.app, the game process in
    /// data\cache\subprocess, and a working plugins\ with ENB, another add-on, a preset, shaders and
    /// ReShade.ini. Returns the FiveM folder, FiveM.app, and every file of the player's with its bytes.</summary>
    private static (string Root, string App, Dictionary<string, byte[]> Theirs) Player(string tag)
    {
        var root = Path.Combine(Fixture.Temp($"fivem-{tag}"), "FiveM");
        var app = Path.Combine(root, "FiveM.app");
        var theirs = new Dictionary<string, byte[]>
        {
            [@"plugins\d3d11.dll"] = Fixture.Pe(x64: true).Concat("ENBSeries"u8.ToArray()).ToArray(),
            [@"plugins\ShaderToggler.addon64"] = Fixture.Pe(x64: true).Concat("toggler"u8.ToArray()).ToArray(),
            [@"plugins\ReShade.ini"] = Encoding.UTF8.GetBytes(
                "[GENERAL]\r\nEffectSearchPaths=.\\reshade-shaders\\Shaders\\**\r\nPresetPath=E:\\presets\\mine.ini\r\n"
                + "[ADDON]\r\nDisabledAddons=amd-nr.addon64,Other Addon\r\n"),
            [@"plugins\reshade-shaders\Shaders\ReShade.fxh"] = "// header"u8.ToArray(),
            [@"plugins\reshade-shaders\Shaders\MartysMods_RTGI.fx"] = "// rtgi"u8.ToArray(),
            [@"data\cache\subprocess\FiveM_b3751_GTAProcess.exe"] = Fixture.Pe(x64: true),
            ["CitizenFX.ini"] = "[Game]\r\nIVPath=Z:\\nowhere\r\n"u8.ToArray(),
            ["FiveM_Diag.exe"] = Fixture.Pe(x64: true),
        };
        foreach (var (name, bytes) in theirs)
        {
            var path = Path.Combine(app, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        File.WriteAllBytes(Path.Combine(root, "FiveM.exe"), Fixture.Pe(x64: true));
        return (root, app, theirs);
    }

    /// <summary>Payloads with ReShade and the companion effect, as the app downloads them.</summary>
    private static (string Src, PayloadPins Pins, byte[] ReShade) Payloads(string tag)
    {
        var (src, pins) = Fixture.Payloads($"fivem-{tag}");
        var reShade = Fixture.Pe(x64: true).Concat(new byte[4096]).ToArray();
        var effect = "// feed"u8.ToArray();
        File.WriteAllBytes(Path.Combine(src, "ReShade64.dll"), reShade);
        File.WriteAllBytes(Path.Combine(src, Work.ShaderName), effect);
        return (src, new PayloadPins
        {
            AddonSha = pins.AddonSha, AddonSize = pins.AddonSize,
            RuntimeSha = pins.RuntimeSha, RuntimeSize = pins.RuntimeSize,
            WeightsSha = pins.WeightsSha, WeightsSize = pins.WeightsSize,
            ReShade64Sha = Engine.Sha(reShade),
            ShaderSha = Engine.Sha(effect), ShaderSize = (ulong)effect.Length,
        }, reShade);
    }

    [Fact]
    public void TheFilesGoWhereFiveMLooksAndThePlayersSetupComesBackByteForByte()
    {
        var (root, app, theirs) = Player("round-trip");
        var (src, pins, reShade) = Payloads("round-trip");

        // Pointed at the FiveM folder, the way somebody picks it: the route finds FiveM.app itself.
        var report = Work.Install(root, src, Preset.FiveM, pins);
        Assert.False(report.Failed, report.ToLog("install"));

        Assert.True(File.Exists(Path.Combine(app, "plugins", Work.AddonName)));
        Assert.Equal(reShade, File.ReadAllBytes(Path.Combine(app, "plugins", "dxgi.dll")));
        Assert.True(File.Exists(Path.Combine(app, "plugins", "reshade-shaders", "Shaders", Work.ShaderName)));
        var game = Path.Combine(app, "data", "cache", "subprocess");
        Assert.True(File.Exists(Path.Combine(game, Work.RuntimeName)));
        Assert.True(File.Exists(Path.Combine(game, Work.WeightsName)));
        // Nothing of the add-on's lands where FiveM does not look.
        Assert.False(File.Exists(Path.Combine(app, Work.AddonName)));
        Assert.False(File.Exists(Path.Combine(app, "plugins", Work.RuntimeName)));

        // Everything of the player's is untouched, and the ini only lost this add-on from DisabledAddons.
        foreach (var (name, bytes) in theirs.Where(t => !t.Key.EndsWith("ReShade.ini", StringComparison.Ordinal)))
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(app, name)));
        var ini = File.ReadAllText(Path.Combine(app, "plugins", "ReShade.ini"));
        Assert.Contains(@"PresetPath=E:\presets\mine.ini", ini);
        Assert.Contains("DisabledAddons=Other Addon", ini);
        Assert.DoesNotContain("amd-nr.addon64", ini);

        Assert.True(GameScanner.IsInstalled(app));
        Assert.Equal(RouteFamily.ReShade, GameScanner.InstalledAs(app));

        // What the add-on writes beside the game while it runs.
        File.WriteAllText(Path.Combine(game, "amd-nr.log"), "log");
        File.WriteAllText(Path.Combine(game, "amd-nr-pass1.dll"), "copy");

        var gone = Work.Uninstall(root, Preset.FiveM);
        Assert.False(gone.Failed, gone.ToLog("uninstall"));
        foreach (var (name, bytes) in theirs)
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(app, name)));
        foreach (var name in new[]
                 {
                     @"plugins\dxgi.dll", @"plugins\" + Work.AddonName, @"plugins\reshade-shaders\Shaders\" + Work.ShaderName,
                     @"data\cache\subprocess\" + Work.RuntimeName, @"data\cache\subprocess\" + Work.WeightsName,
                     @"data\cache\subprocess\amd-nr.log", @"data\cache\subprocess\amd-nr-pass1.dll",
                 })
            Assert.False(File.Exists(Path.Combine(app, name)), $"{name} was left behind");
        Assert.False(GameScanner.IsInstalled(app));
    }

    /// <summary>A dxgi.dll in plugins that nothing identifies stands for the older ReShade most FiveM
    /// players have: it goes to the backup, and Uninstall puts that exact file back.</summary>
    [Fact]
    public void AReShadeThatHasToGoIsBackedUpAndComesBack()
    {
        var (root, app, _) = Player("swap");
        var (src, pins, reShade) = Payloads("swap");
        var mine = Fixture.Pe(x64: true).Concat("ReShade 6.7.3"u8.ToArray()).ToArray();
        var dxgi = Path.Combine(app, "plugins", "dxgi.dll");
        File.WriteAllBytes(dxgi, mine);

        var install = Work.Install(app, src, Preset.FiveM, pins);
        Assert.False(install.Failed, install.ToLog("install"));
        Assert.Equal(reShade, File.ReadAllBytes(dxgi));
        var gone = Work.Uninstall(app, Preset.FiveM);
        Assert.False(gone.Failed, gone.ToLog("uninstall"));
        Assert.True(mine.SequenceEqual(File.ReadAllBytes(dxgi)), install.ToLog("install") + gone.ToLog("uninstall"));
    }

    /// <summary>A dxgi.dll that says it is something else is the player's, and is never replaced.
    /// Windows' own dxgi.dll stands in: it names its product, and it is not ReShade.</summary>
    [Fact]
    public void SomebodyElsesDxgiIsLeftAloneAndNothingIsWritten()
    {
        var (_, app, _) = Player("foreign");
        var (src, pins, _) = Payloads("foreign");
        var theirs = Path.Combine(app, "plugins", "dxgi.dll");
        File.Copy(Path.Combine(Environment.SystemDirectory, "dxgi.dll"), theirs);
        var before = File.ReadAllBytes(theirs);

        var report = Work.Install(app, src, Preset.FiveM, pins);
        Assert.True(report.Failed);
        Assert.True(Fixture.HasErr(report, "not ReShade"), report.ToLog("foreign"));
        Assert.Equal(before, File.ReadAllBytes(theirs));
        Assert.False(File.Exists(Path.Combine(app, "plugins", Work.AddonName)));
        Assert.False(File.Exists(Path.Combine(app, "data", "cache", "subprocess", Work.WeightsName)));
    }

    [Fact]
    public void AFiveMThatNeverStartedTheGameIsToldTo()
    {
        var (_, app, _) = Player("fresh");
        Directory.Delete(Path.Combine(app, "data"), recursive: true);
        var (src, pins, _) = Payloads("fresh");
        Assert.True(Fixture.HasErr(Work.Preflight(app, src, Preset.FiveM, pins), "Start FiveM once"));
    }

    [Fact]
    public void FiveMAppIsFoundFromAnywhereSomebodyMightPoint()
    {
        var (root, app, _) = Player("resolve");
        foreach (var given in new[]
                 {
                     root, app, Path.Combine(root, "FiveM.exe"), Path.Combine(app, "plugins"),
                     Path.Combine(app, "data", "cache", "subprocess"),
                 })
            Assert.Equal(app, Work.FiveMApp(given));
        Assert.Null(Work.FiveMApp(Fixture.Temp("not-fivem")));

        Assert.Equal(Preset.FiveM, GameScanner.GuessPreset(app));
        Assert.Equal(Preset.FiveM, GraphicsDetector.Detect(root).Preset);

        // What Play starts is FiveM.exe, never FiveM_Diag.exe beside the card's folder (CfxDiag).
        var detected = GraphicsDetector.Detect(app);
        Assert.Equal(Preset.FiveM, detected.Preset);
        Assert.Equal(Path.Combine(root, "FiveM.exe"), detected.Executable);
    }

    /// <summary>6.7.3 carries ReShade API 18 and the add-on needs 20, first in 6.8.0; the signed build is the
    /// normal one, whose add-on support is limited and off in an online game.</summary>
    [Fact]
    public void OnlyAnUnsignedReShadeFromTheFirstWithApi20OnStays()
    {
        Assert.Equal(Work.ReShadeFit.TooOld, Work.FitOf("6.7.3", signed: false));
        Assert.Equal(Work.ReShadeFit.TooOld, Work.FitOf(null, signed: false));
        Assert.Equal(Work.ReShadeFit.Fits, Work.FitOf("6.8.0", signed: false));
        Assert.Equal(Work.ReShadeFit.Fits, Work.FitOf("6.9.1", signed: false));
        Assert.Equal(Work.ReShadeFit.Signed, Work.FitOf("6.8.0", signed: true));
    }

    [Fact]
    public void ASignatureIsSeenWhereThereIsOne()
    {
        Assert.False(Engine.IsSigned(Fixture.Pe(x64: true)));
        Assert.False(Engine.IsSigned(Fixture.Pe(x64: false)));
        // The runtime's own assemblies carry an embedded Authenticode signature.
        Assert.True(Engine.IsSignedFile(typeof(object).Assembly.Location));
    }
}
