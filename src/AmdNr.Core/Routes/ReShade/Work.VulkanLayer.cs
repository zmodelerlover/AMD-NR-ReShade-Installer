// ReShade on Vulkan is not a file beside the program but a layer the Vulkan loader offers every Vulkan
// program, and it stays out of any program without a ReShade.ini beside its executable (ReShade's
// dll_main: "only actually load when a configuration file exists for the target executable"). So an
// install is two things: the layer, once per machine, and the ReShade.ini, once per program.
//
// ReShade's own setup registers the layer for the whole machine, which needs an administrator. When a
// ReShade layer is registered already -- that setup's, or an earlier install's of this app -- it is the
// one used, and nothing is registered beside it: two would be two ReShades in one process. When none is,
// the pinned ReShade goes into a folder of this app's and is registered for the current Windows user,
// which needs no administrator; the loader reads HKCU's implicit layers as well as HKLM's.

using System.Text.Json;
using Microsoft.Win32;

namespace AmdNr.Core;

public static partial class Work
{
    /// <summary>Where the loader reads implicit layers, under HKLM and HKCU. AMDNR_LAYER_KEY moves it to a key
    /// of the test suite's under HKCU alone, so a test run neither sees this PC's layers nor registers one.</summary>
    private static readonly string? TestLayerKey = Environment.GetEnvironmentVariable("AMDNR_LAYER_KEY") is { Length: > 0 } k ? k : null;
    private static string ImplicitLayersKey => TestLayerKey ?? @"SOFTWARE\Khronos\Vulkan\ImplicitLayers";
    private const string ReShadeLayerName = "VK_LAYER_reshade";

    /// <summary>Where this app keeps the ReShade it registers as a layer itself.</summary>
    internal static string OwnLayerFolder => Path.Combine(AppPaths.Root, "vulkan-layer");

    /// <param name="Manifest">The layer's JSON, as registered.</param>
    /// <param name="Library">The ReShade64.dll it loads.</param>
    /// <param name="Ours">Registered by this app, from <see cref="OwnLayerFolder"/>.</param>
    internal sealed record ReShadeLayer(string Manifest, string Library, bool Ours);

    /// <summary>The 64-bit ReShade layer the Vulkan loader would load, machine-wide first, or null.</summary>
    internal static ReShadeLayer? FindReShadeLayer()
    {
        foreach (var hive in TestLayerKey is null ? new[] { Registry.LocalMachine, Registry.CurrentUser } : [Registry.CurrentUser])
        {
            using var key = hive.OpenSubKey(ImplicitLayersKey);
            if (key is null) continue;
            foreach (var manifest in key.GetValueNames())
            {
                // Zero is enabled; anything else is a layer the loader skips.
                if (key.GetValue(manifest) is not 0 || LayerLibrary(manifest) is not { } library) continue;
                var ours = Path.GetFullPath(manifest).StartsWith(Path.GetFullPath(OwnLayerFolder) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase);
                return new ReShadeLayer(manifest, library, ours);
            }
        }
        return null;
    }

    /// <summary>The 64-bit library a layer manifest names, when it is ReShade's and the file is there.</summary>
    private static string? LayerLibrary(string manifest)
    {
        try
        {
            if (!File.Exists(manifest)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
            var layers = doc.RootElement.TryGetProperty("layers", out var many) ? many.EnumerateArray().ToList()
                : doc.RootElement.TryGetProperty("layer", out var one) ? [one] : [];
            foreach (var layer in layers)
            {
                if (layer.TryGetProperty("name", out var name) && name.GetString() == ReShadeLayerName
                    && layer.TryGetProperty("library_path", out var lib) && lib.GetString() is { } relative
                    && Path.GetFullPath(Path.Combine(Path.GetDirectoryName(manifest)!, relative)) is var library
                    && File.Exists(library) && Path.GetFileName(library).Contains("64", StringComparison.Ordinal))
                    return library;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException
                                      or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            // A manifest somebody else wrote that cannot be read is not ReShade's as far as this goes.
        }
        return null;
    }

    /// <summary>What the pre-flight says about ReShade on a Vulkan route.</summary>
    private static void NoteVulkanLayer(bool shipsReShade, Report report)
    {
        var layer = FindReShadeLayer();
        if (layer is { Ours: false })
            report.Ok($"ReShade is already a Vulkan layer on this PC ({layer.Library}). This install puts a ReShade.ini "
                      + "beside the executable, which is what turns it on for this program. The add-on needs that ReShade "
                      + "to be the build with full add-on support.");
        else if (shipsReShade)
            report.Ok("ReShade 6.8.0 with full add-on support is part of this install, as a Vulkan layer for your Windows user "
                      + "(no administrator needed), turned on for this program alone by a ReShade.ini beside it; nothing to install by hand.");
        else
            report.Warn("No ReShade Vulkan layer is registered on this PC, and the payload this app read carries no ReShade to "
                        + "register. Run ReShade's own setup against the executable and pick Vulkan.");
    }

    /// <summary>Registers the pinned ReShade as the Vulkan layer when no ReShade layer is registered, and
    /// brings this app's own copy up to the pinned build. A ReShade layer somebody else registered is left as it is.</summary>
    private static void EnsureVulkanLayer(byte[] reShade64, Report report)
    {
        var layer = FindReShadeLayer();
        if (layer is { Ours: false }) return;
        try
        {
            Directory.CreateDirectory(OwnLayerFolder);
            var library = Path.Combine(OwnLayerFolder, "ReShade64.dll");
            var manifest = Path.Combine(OwnLayerFolder, "ReShade64.json");
            var fresh = !File.Exists(library) || Engine.HashFile(library) != Engine.Sha(reShade64);
            if (fresh) File.WriteAllBytes(library, reShade64);
            File.WriteAllText(manifest, LayerManifest);
            using var key = Registry.CurrentUser.CreateSubKey(ImplicitLayersKey);
            key.SetValue(manifest, 0, RegistryValueKind.DWord);
            if (layer is null)
                report.Info($"ReShade 6.8.0 with full add-on support is registered as a Vulkan layer for your Windows user, from {OwnLayerFolder}. "
                            + "It loads only in programs with a ReShade.ini beside them.");
            else if (fresh)
                report.Info("The ReShade Vulkan layer this app registered is now the pinned build.");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            report.Err($"ReShade could not be registered as a Vulkan layer: {e.Message}. A Vulkan program still running "
                       + "with ReShade holds its DLL; close it and install again.");
        }
    }

    /// <summary>ReShade 6.8.0's own layer manifest, as its setup writes it.</summary>
    private const string LayerManifest = """
        {
        	"file_format_version": "1.0.0",
        	"layer": {
        		"name": "VK_LAYER_reshade",
        		"type": "GLOBAL",
        		"library_path": ".\\ReShade64.dll",
        		"api_version": "1.3.268",
        		"implementation_version": "1",
        		"description": "crosire's ReShade post-processing injector for 64-bit",
        		"device_extensions": [
        			{
        				"name": "VK_EXT_tooling_info",
        				"spec_version": "1",
        				"entrypoints": [ "vkGetPhysicalDeviceToolPropertiesEXT" ]
        			}
        		],
        		"disable_environment": {
        			"DISABLE_VK_LAYER_reshade_1": "1"
        		}
        	}
        }
        """;
}
