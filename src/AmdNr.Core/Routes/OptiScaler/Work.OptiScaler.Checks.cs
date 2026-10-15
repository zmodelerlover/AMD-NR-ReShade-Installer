// What the OptiScaler route looks at before it writes: what already has the name OptiScaler goes in as,
// another AMD NR mod in the folder and whose OptiScaler.ini is here, whether the game has an upscaler
// for OptiScaler to take over, and whether the files here are an API it runs on.

namespace AmdNr.Core;

public static partial class Work
{
    /// <param name="preflight">Said before Install is pressed: the sheet asks to switch and takes the ReShade
    /// route out first, so there it is what Install does, not a problem.</param>
    private static void CheckOptiInTheWay(string dir, string proxyName, Manifest? m, Report report, bool preflight = false)
    {
        if (OtherRouteInstalled(m, dir))
        {
            if (preflight)
                report.Info($"This folder has the {m!.Preset} ReShade route installed. The two routes are alternatives: "
                            + "Install asks, then takes that one out before this goes in.");
            else
                report.Err(
                    $"This folder has the {m!.Preset} ReShade route installed. The two routes are alternatives: "
                    + "uninstall that one here first, then install this.");
            return;
        }

        var existing = Path.Combine(dir, proxyName);
        if (!File.Exists(existing) || m?.Entries.Any(e => e.Name == proxyName && e.Owned) == true) return;

        var (isReShade, product, version) = Identify(existing);
        var named = product is null ? "a file with no version information" : $"{product}{(version is null ? "" : $" {version}")}";
        if (product?.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) == true)
            report.Info($"{proxyName} here is already {named}. It is backed up and replaced, and uninstall puts it back.");
        else if (isReShade)
            report.Warn(
                $"{proxyName} here is ReShade. OptiScaler takes that name, so this ReShade is backed up and stops "
                + "loading until you uninstall. ReShade can run beside OptiScaler under another name, such as d3d12.dll.");
        else
            report.Warn(
                $"{proxyName} here is {named}. OptiScaler replaces it, the original is backed up and uninstall "
                + "puts it back. Pick winmm.dll as the name to keep it loading.");
    }

    /// <summary>Files only the AMD NR mods of other projects leave: the AMDNR mod's packed lmxxf weights and its
    /// NGX shim.</summary>
    private static readonly string[] ForeignNrFiles = ["LmxxfNrRuntime.pak", "nvngx.dll_dlssnr.dll"];

    /// <summary>Sections only another project's OptiScaler.ini has.</summary>
    private static readonly string[] ForeignIniSections = ["[AmdGi]", "[AmdRtgi]", "[AmdLook]"];

    /// <summary>Another AMD NR mod in this folder, each file with what it is: an OptiScaler that is not this
    /// project's build (every one of ours calls its version "...-amd-nr"), an lmxxf runtime that names
    /// another product, and the files only those mods leave. Empty when there is none, or what is here was
    /// installed by this app.</summary>
    internal static List<string> ForeignNrMod(string dir, Manifest? m)
    {
        bool Recorded(string name) =>
            m?.Entries.Any(e => e.Owned && string.Equals(e.Name, name, StringComparison.OrdinalIgnoreCase)) == true;
        var found = new List<string>();
        foreach (var name in ProxyChoicesFor(Preset.OptiScaler).Where(n => !Recorded(n)))
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            var (_, product, version) = Identify(path);
            if (product?.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) == true
                && version?.Contains("amd-nr", StringComparison.OrdinalIgnoreCase) != true)
                found.Add($"{name} ({product}{(version is null ? "" : $" {version}")})");
        }
        var lmxxf = Path.Combine(dir, LmxxfRuntimeName);
        if (File.Exists(lmxxf) && !Recorded(LmxxfRuntimeName)
            && Identify(lmxxf).Product is { } other && other.Contains("AMDNR", StringComparison.OrdinalIgnoreCase))
            found.Add($"{LmxxfRuntimeName} ({other})");
        found.AddRange(ForeignNrFiles.Where(n => File.Exists(Path.Combine(dir, n))));
        return found;
    }

    /// <summary>Whether the OptiScaler.ini here is another project's: one beside a foreign OptiScaler, or one
    /// with sections only another build has. Its settings are not this build's, so it is backed up and
    /// replaced instead of kept.</summary>
    private static bool ForeignIni(string dir, bool foreignMod)
    {
        var path = Path.Combine(dir, OptiScalerIni);
        if (!File.Exists(path)) return false;
        if (foreignMod) return true;
        try
        {
            var text = File.ReadAllText(path);
            return ForeignIniSections.Any(s => text.Contains(s, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void CheckForeignMod(string dir, Manifest? m, Report report)
    {
        if (ForeignNrMod(dir, m) is not { Count: > 0 } found) return;
        report.Warn(
            $"Another AMD NR mod is in this folder: {Joined(found)}. It is not this project's, and two of them in one "
            + $"folder load over each other. Its OptiScaler and its {OptiScalerIni} are backed up and replaced, and "
            + "uninstall puts them back; the rest of it stays, so remove that mod with its own uninstaller first.");
    }

    /// <summary>A renamed copy of one of Streamline's own DLLs: Streamline loads every sl.*.dll beside it as a plugin,
    /// and a backup such as sl.common.bak.dll crashes it ("plugin 'sl.common.bak' with priority &lt;= 0"). Only once
    /// OptiScaler shows an NVIDIA GPU does the game start Streamline, so the ReShade route runs and this one closes on
    /// launch. The Last of Us Part I with sl.common.dll renamed to sl.common.bak.dll (2026-10-12).</summary>
    internal static void CheckRenamedStreamline(string dir, Report report)
    {
        if (!File.Exists(Path.Combine(dir, "sl.interposer.dll"))) return;
        string[] renamed;
        try
        {
            renamed = Directory.EnumerateFiles(dir, "sl.*.dll")
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => n.Count(c => c == '.') > 2)
                .ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (renamed.Length == 0) return;
        report.Warn($"{Joined(renamed)} in this folder {(renamed.Length == 1 ? "is a renamed copy" : "are renamed copies")} "
                    + "of the game's Streamline files. Streamline loads every sl.*.dll as a plugin and crashes the game "
                    + "on these once OptiScaler is in. Rename each back to its original name (sl.common.bak.dll to "
                    + "sl.common.dll), or verify the game's files.");
    }

    private static void CheckUpscaler(string dir, Report report, bool everywhere, string? executable)
    {
        if (everywhere)
        {
            var deep = GraphicsDetector.UpscalersDeep(dir, null, executable);
            report.Info(deep.Count > 0
                ? $"{Joined(deep)} found: in this game OptiScaler runs the network inside its upscaler, with the game's "
                  + "own depth and motion, once that upscaler is switched on in the game's settings. With it off, the "
                  + "network runs on the finished frame instead (NR without upscaling)."
                : "No DLSS, FSR or XeSS found in the game's files, so OptiScaler runs the network on the finished frame "
                  + "(NR without upscaling), with danielblnc's runtime. A game that does have one runs it inside that "
                  + "upscaler as soon as it is switched on.");
            return;
        }
        var found = GraphicsDetector.Upscalers(dir);
        if (found.Count > 0)
            report.Ok(
                $"{Joined(found)} {(found.Count == 1 ? "is" : "are")} here, so the game has an upscaler for "
                + "OptiScaler to take over. Switch it on in the game's settings.");
        else
            report.Warn(
                "No DLSS, FSR or XeSS files next to the game. OptiScaler only runs the network where the game "
                + "calls one of those upscalers, so a game without any has nothing for it to take over. The "
                + "D3D12 ReShade route is the one for that. Some engines keep these files elsewhere, so this is "
                + "only a warning.");
    }

    private static void CheckOptiRouteIsReachable(string dir, Report report, bool everywhere, bool bridges = false)
    {
        var local = GraphicsDetector.Detect(dir);
        // An engine's default, not read from the game (a Godot 4 pack that cannot be read), says nothing against a route.
        if (local.Guessed) return;
        if (everywhere)
        {
            if (local.CanRunOptiScalerWith(true, bridges)) return;
            report.Warn(
                $"The files here read as {local.Tag}. OptiScaler runs on 64-bit D3D9, "
                + (bridges ? "D3D10, D3D11, D3D12, Vulkan and OpenGL games, and 32-bit D3D9 and D3D10 games. "
                           : "D3D11, D3D12, Vulkan and OpenGL games. ")
                + local.Why);
            return;
        }
        if (local.All.Count == 0 || local.All.Contains(GraphicsApi.D3D12)) return;
        report.Warn(
            $"The files here link {string.Join(", ", local.All.Select(GraphicsDetection.Short))}, not D3D12. "
            + $"{local.Why} OptiScaler's neural pass is built for D3D12; a D3D11 or Vulkan game reaches it only "
            + "through OptiScaler's D3D12-bridged upscalers.");
    }
}
