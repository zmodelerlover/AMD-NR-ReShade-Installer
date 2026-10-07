using AmdNr.Core;
using Microsoft.Win32;

namespace AmdNr.Core.Tests;

/// <summary>Registering a layer nobody else may see: the run's layer key is shared, and a foreign layer in it
/// would fail every other Vulkan install running beside these.</summary>
[CollectionDefinition("VulkanLayers", DisableParallelization = true)]
public class VulkanLayersCollection;

[Collection("VulkanLayers")]
public class ForeignLayerTests
{
    /// <summary>Somebody else's ReShade layer is used as it is, so one the add-on does not load in (Until Then had
    /// 6.1.1 machine-wide: "limited add-on functionality") fails the pre-flight and the install, and nothing goes in.</summary>
    [Fact]
    public void AForeignVulkanLayerTheAddonCannotLoadInFailsTheInstall()
    {
        var game = Fixture.Temp("vulkan-foreign");
        var (src, pins) = Fixture.Payloads("vulkan-foreign");
        var layerDir = Fixture.Temp("vulkan-foreign-layer");
        File.WriteAllBytes(Path.Combine(layerDir, "ReShade64.dll"), Fixture.Pe(true));
        var manifest = Path.Combine(layerDir, "ReShade64.json");
        File.WriteAllText(manifest, """{ "layer": { "name": "VK_LAYER_reshade", "library_path": ".\\ReShade64.dll" } }""");

        using var key = Registry.CurrentUser.CreateSubKey(Environment.GetEnvironmentVariable("AMDNR_LAYER_KEY")!);
        // Whatever layer an earlier test registered is switched off meanwhile, so this one is the layer found.
        var before = key.GetValueNames().ToDictionary(n => n, n => key.GetValue(n)!);
        foreach (var name in before.Keys) key.SetValue(name, 1, RegistryValueKind.DWord);
        key.SetValue(manifest, 0, RegistryValueKind.DWord);
        try
        {
            Assert.False(Work.FindReShadeLayer()!.Ours);
            var preflight = Work.Preflight(game, src, Preset.Vulkan, pins);
            Assert.True(preflight.Failed && Fixture.HasAny(preflight, "limited add-on functionality"), preflight.ToLog("preflight"));
            var report = Work.Install(game, src, Preset.Vulkan, pins);
            Assert.True(report.Failed && Fixture.HasAny(report, "as administrator"), report.ToLog("install"));
            Assert.False(File.Exists(Path.Combine(game, Work.AddonName)), "nothing goes in");
        }
        finally
        {
            key.DeleteValue(manifest, throwOnMissingValue: false);
            foreach (var (name, value) in before) key.SetValue(name, value, RegistryValueKind.DWord);
        }
    }
}
