using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>Issue #8: v0.8.0 recommended OptiScaler on every D3D11 game, and Fallout 4 did not start while Batman:
/// Arkham Knight crashed, both fine on ReShade. api-db.json names games to keep off a route, by executable.</summary>
public class AvoidRouteTests
{
    [Fact]
    public void AGameTheDatabaseKeepsOffOptiScalerIsRecommendedReShade()
    {
        var db = ApiDatabase.Parse(PayloadCache.Embedded("api-db.json")!);
        foreach (var exe in new[] { @"C:\Games\Fallout 4\Fallout4.exe", @"C:\Games\BAK\Binaries\Win64\batmanak.exe" })
            Assert.Contains("issue #8", db.OptiAvoided(exe));
        Assert.Null(db.OptiAvoided(@"C:\Games\Other\Game.exe"));

        var root = Fixture.Temp("avoid-fallout4");
        File.WriteAllBytes(Path.Combine(root, "Fallout4.exe"), Fixture.PeWithImports(true, ["d3d11.dll"]));
        var detected = GraphicsDetector.Detect(root);
        Assert.Equal(Preset.OptiScaler, detected.PresetFor(optiEverywhere: true));
        var avoided = detected with { OptiAvoided = db.OptiAvoided(detected.Executable) };
        Assert.Equal(Preset.Dx11, avoided.PresetFor(optiEverywhere: true));

        // An app that writes the file back keeps the entries.
        Assert.Contains("issue #8", ApiDatabase.Parse(db.Serialise()).OptiAvoided("Fallout4.exe"));
    }
}
