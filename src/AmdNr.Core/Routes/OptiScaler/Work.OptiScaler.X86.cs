// A 32-bit game's OptiScaler (0.5.2 and later): its 32-bit proxy beside the game hands every frame to a
// 64-bit host in OptiScaler64\, which runs OptiScaler with everything a 64-bit game gets beside it.

namespace AmdNr.Core;

public static partial class Work
{
    /// <summary>The first OptiScaler release that runs 64-bit D3D10 games and, through its 32-bit proxies (x86/ in
    /// its package) and their 64-bit host, 32-bit D3D9 and D3D10 games (<see cref="GraphicsDetection.OptiX86Apis"/>).</summary>
    public static readonly Version OptiBridges = new(0, 5, 2);

    /// <summary>Whether this OptiScaler version runs D3D10 and the 32-bit games.</summary>
    public static bool OptiRunsBridged(string? version) =>
        version is not null && AddonReleases.Version(version) is { } v && v >= OptiBridges;

    /// <summary>A 32-bit game's OptiScaler: everything a 64-bit game gets beside it goes in this folder, where the
    /// 64-bit host loads it; the 32-bit proxy goes beside the game.</summary>
    internal const string OptiHostFolder = "OptiScaler64";

    /// <summary>The 64-bit host the 32-bit proxies start, and the folder of the package that holds it and them.</summary>
    internal const string OptiHostName = "OptiScalerDx9Host.exe";
    private const string OptiX86Payload = "x86/";

    /// <summary>The 32-bit proxy for a 32-bit game's API: d3d9.dll for D3D9, dxgi.dll (which D3D10 makes its swap
    /// chains through) for D3D10.</summary>
    internal static string OptiX86Proxy(GraphicsApi? api) => api == GraphicsApi.D3D9 ? "d3d9.dll" : "dxgi.dll";

    /// <summary>What a 32-bit game's host never loads: lmxxf, which does not run without an upscaler, and its data.</summary>
    private static bool LmxxfOnly(string path) =>
        path == LmxxfRuntimeName || path.StartsWith("lmxxf-modules", StringComparison.Ordinal)
        || path.StartsWith(LmxxfWeightsFolder, StringComparison.Ordinal) || path.StartsWith("shaders/", StringComparison.Ordinal);
}
