// What the ReShade routes look at before they write: which ReShade is in the folder and under which
// name, whether there is a second one, whether ReShade.ini has the add-on switched off, and whether
// the game links the renderer the route needs. Shared by the 64-bit route and the 32-bit bridge.

namespace AmdNr.Core;

public static partial class Work
{
    /// <summary>A ReShade proxy, by the name it has to be loaded under. Used where the route is not
    /// known -- uninstall, which only wants to know whether one is still there, and the check for a
    /// second ReShade, which has to look at every name rather than this route's.</summary>
    private static readonly string[] Proxies =
        ["d3d11.dll", "dxgi.dll", "d3d12.dll", "opengl32.dll", "d3d9.dll", "d3d8.dll", "dinput8.dll"];

    /// <summary>The names ReShade can be loaded under *for this route*. A 32-bit D3D9 game loads
    /// d3d9.dll and never dxgi.dll, so checking the 64-bit names there reported "no ReShade proxy
    /// DLL found" about a folder with ReShade sitting in it -- on every 32-bit install this route
    /// performs, including the ones it had just written itself.</summary>
    private static string[] ProxiesFor(Preset preset) => preset switch
    {
        Preset.X86Dx9 or Preset.X86Dx8 => ["d3d9.dll", "d3d8.dll"],
        Preset.X86Dx11 => ["dxgi.dll", "d3d11.dll"],
        Preset.Dx12 => ["dxgi.dll", "d3d12.dll", "d3d11.dll"],
        // An OpenGL game never loads dxgi.dll, so looking for the D3D names there would report
        // "no ReShade proxy found" about a folder with ReShade sitting in it -- the same mistake
        // the 32-bit line above was added to fix.
        Preset.OpenGL => ["opengl32.dll", "dinput8.dll"],
        _ => ["dxgi.dll", "d3d11.dll", "d3d12.dll"],
    };

    /// <summary>Adds the companion effect to whatever a route is about to write, from whichever
    /// layout the payload came in. Both routes call this and neither has its own copy: v0.3.0
    /// shipped the effect on the 64-bit route only, because that was the only place the lines
    /// existed, and a 32-bit install went without it in silence. One implementation is the only
    /// version of that invariant a future change cannot forget half of.
    ///
    /// It goes in only beside ReShade's standard shaders. The effect includes ReShade.fxh, which
    /// comes with those and not with the DLL this installs, and ReShade compiles every .fx it finds:
    /// alone it was a compile error in ReShade's log and overlay, in every game. It reads a
    /// motion-vector shader such as iMMERSE Launchpad and does nothing without one, and each of
    /// those brings ReShade.fxh -- so the header is the sign the effect has something to read.
    /// Returns whether it was added.</summary>
    public static bool AddCompanionEffect(IDictionary<string, byte[]> files, string payloadDir, string gameDir)
    {
        var nested = Path.Combine(payloadDir, "files", ShaderName);
        var flat = Path.Combine(payloadDir, ShaderName);
        var from = File.Exists(nested) ? nested : flat;
        // Absent is not an error: a payload manifest published before the effect was installable
        // has no shader component, and the add-on works without it.
        if (!File.Exists(from) || !HasStandardShaders(gameDir)) return false;
        files[ShaderPath] = Engine.Read(from);
        return true;
    }

    public static bool HasStandardShaders(string gameDir) =>
        File.Exists(Path.Combine(gameDir, "reshade-shaders", "Shaders", "ReShade.fxh"));

    /// <summary>Says the effect was left out, and takes out one an earlier install put there with
    /// nothing to compile against: that copy is the compile error in ReShade's log on every launch.
    /// The name is this project's alone, so it is never somebody else's file.</summary>
    public static void LeaveOutEffect(string gameDir, Report report)
    {
        report.Info(EffectLeftOut);
        var stale = Path.Combine(gameDir, "reshade-shaders", "Shaders", ShaderName);
        if (!File.Exists(stale)) return;
        try
        {
            Engine.SafePath(stale);
            File.Delete(stale);
            report.Ok($"removed the {ShaderName} an earlier install left without ReShade.fxh");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstallException)
        {
            report.Warn($"{ShaderName} could not be removed ({e.Message}); ReShade will keep reporting it until it goes.");
        }
    }

    public const string EffectLeftOut =
        ShaderName + " was left out: it needs ReShade's standard shaders (ReShade.fxh), which come "
        + "with a motion-vector shader such as iMMERSE Launchpad. Install one, then Reinstall here, "
        + "and the add-on gets real motion vectors in a game that has none of its own.";

    /// <summary>Whether a ReShade DisabledAddons entry names this add-on, under either the
    /// name it uses now or the one it used before v0.6.5. A folder upgraded in place can
    /// carry either, and matching only one leaves the other silently disabled.</summary>
    private static bool IsOurs(string entry) =>
        entry.Contains("amd-nr", StringComparison.OrdinalIgnoreCase)
        || entry.Contains("dlss5", StringComparison.OrdinalIgnoreCase);

    private static void CheckReShade(string dir, Preset preset, Report report)
    {
        var names = ProxiesFor(preset);
        var present = names.Where(n => File.Exists(Path.Combine(dir, n))).ToList();

        // Anything identified as something else is named, and not counted as ReShade. A proxy with no
        // version resource at all is kept as possibly-ReShade: every ReShade build carries one, but
        // saying "not ReShade" about a file nothing can be read from would be a guess.
        var found = new List<string>();
        foreach (var name in present)
        {
            var (isReShade, product, version) = Identify(Path.Combine(dir, name));
            if (isReShade || product is null)
            {
                found.Add(isReShade && version is not null ? $"{name} (ReShade {version})" : name);
                continue;
            }
            report.Warn(
                $"{name} here is {product}{(version is null ? "" : $" {version}")}, not ReShade. The add-on only loads "
                + "inside ReShade with full add-on support; if both are meant to run, ReShade has to be loaded "
                + "under another name, and two programs cannot both be the game's " + name + ".");
        }

        if (preset.IsVulkan())
        {
            if (found.Count == 0)
            {
                report.Info(
                    "No ReShade proxy DLL here, which is correct for Vulkan: ReShade loads as a global "
                    + $"layer instead. Make sure you ran its installer against {preset.ExpectedExe() ?? "the game's own .exe"} "
                    + "and picked Vulkan.");
            }
            else
            {
                report.Warn(
                    $"Found {string.Join(", ", found)} here. On Vulkan ReShade loads as a global layer, "
                    + "and a proxy DLL as well means two ReShade instances in one process. Remove it if "
                    + "Vulkan is what you run.");
            }
            return;
        }

        switch (found.Count)
        {
            case 0:
                report.Warn(
                    $"No ReShade proxy DLL found here ({string.Join(", ", names)}). The add-on cannot "
                    + "load without ReShade, and it has to be the build with full add-on support. Files "
                    + "are still copied, so installing ReShade afterwards is enough.");
                break;
            case 1:
                report.Ok($"ReShade found: {found[0]}");
                break;
            default:
                report.Warn(
                    $"More than one ReShade proxy here ({string.Join(", ", found)}). Only one is loaded, "
                    + "and which one depends on the game. Keep the one that matches the renderer.");
                break;
        }
    }

    /// <summary>The ReShade this install would write, when it carries one. Both routes ship it under
    /// the name dxgi.dll or ReShade64.dll in the payload, whatever it is written as in the end.</summary>
    private static string? ShippedReShade(string src, Preset preset)
    {
        if (src.Length == 0 || preset.IsVulkan()) return null;
        var path = preset.Route() == Route.X86
            ? Path.Combine(src, "files", "dxgi.dll")
            : Path.Combine(PayloadDir(src), "ReShade64.dll");
        return File.Exists(path) ? path : null;
    }

    /// <summary>ReShade, by its version resource -- or by being byte for byte the file this install
    /// is about to write, which is ReShade by definition however the copy got there. The second arm
    /// is the one that matters for the extras bundle: it ships ReShade32 under the name dxgi.dll,
    /// which is only ever meant to be *written as* d3d9.dll, and dropping it into a D3D9 game folder
    /// by hand is what the failure below actually looks like in the wild.</summary>
    private static bool IsReShadeFile(string path, string? shipped)
    {
        var (isReShade, product, _) = Identify(path);
        if (isReShade) return true;
        // Size first, so this stays a stat() for every file that cannot be a copy of it.
        return product is null && shipped is not null
               && Engine.SizeOf(path) == Engine.SizeOf(shipped)
               && Engine.HashFile(path) == Engine.HashFile(shipped);
    }

    /// <summary>A second ReShade in the same folder, under a name this route does not load.
    ///
    /// This is the one failure in this project that produces no evidence at all: the game closes
    /// before a window appears, nothing is written to any log, and the folder looks correct. ReShade
    /// hooks dxgi.dll, d3d11.dll and the rest itself, and the game folder comes before system32 in
    /// the search order, so a stray dxgi.dll beside the d3d9.dll a 32-bit game loads is picked up and
    /// initialised a second time inside the same process.
    ///
    /// It is checked over every proxy name rather than this route's, because the file that collides
    /// is by definition the one this route was not looking for -- which is exactly why a check
    /// scoped to the route's own names never saw it.</summary>
    private static void CheckDoubleReShade(string dir, Preset preset, Report report, string? keep,
        string? shipped)
    {
        keep ??= ProxiesFor(preset).FirstOrDefault(n => File.Exists(Path.Combine(dir, n)));
        var extra = Proxies
            .Where(n => !string.Equals(n, keep, StringComparison.OrdinalIgnoreCase)
                        && File.Exists(Path.Combine(dir, n))
                        && IsReShadeFile(Path.Combine(dir, n), shipped))
            .ToList();
        if (extra.Count == 0) return;

        report.Err(
            $"There is a second ReShade in this folder: {Joined(extra)}. "
            + (keep is null
                ? "Only one of them is ever loaded"
                : $"This route loads ReShade as {keep}")
            + ", and a process that loads two ReShades does not start at all -- the game closes before "
            + "a window appears and writes nothing anywhere saying why. Delete "
            + $"{string.Join(" and ", extra)} from the game folder and run this again.");
    }

    /// <summary>The name this install will load ReShade under, whichever route it takes. Null only
    /// on Vulkan, where ReShade is a layer and no file in the folder is it.</summary>
    internal static string? ProxyNameFor(Preset preset, string dir, string? proxy) =>
        preset.IsVulkan() ? null
        : preset.Route() == Route.X86
            ? ProxyAllowed(preset, proxy) ? proxy : Engine.X86ProxyName(preset.ManifestPreset())
            : ReShadeProxyFor(preset, dir, proxy);

    /// <summary>The name ReShade is loaded under. dxgi.dll serves D3D10/11/12 and is what ReShade's
    /// own setup picks; when a ReShade is already there under another proxy name it is replaced in
    /// place rather than doubled; when dxgi.dll belongs to something else -- OptiScaler, DXVK -- the
    /// API's own DLL is used so both keep loading. Vulkan has no proxy at all: ReShade is a layer
    /// there, registered by its own setup, and this returns null.</summary>
    internal static string? ReShadeProxyFor(Preset preset, string dir, string? wanted = null)
    {
        if (preset.IsVulkan()) return null;

        // What the person asked for, when this API can be loaded under it. Deliberately ahead of
        // everything below: a game that only loads d3d12.dll is exactly the case the automatic pick
        // gets wrong, because dxgi.dll is free there and so it takes it.
        if (ProxyAllowed(preset, wanted))
            return ProxyChoicesFor(preset).First(n => string.Equals(n, wanted, StringComparison.OrdinalIgnoreCase));

        string[] candidates =
            preset == Preset.OpenGL ? ["opengl32.dll"]
            : preset == Preset.Dx12 ? ["dxgi.dll", "d3d12.dll"]
            : ["dxgi.dll", "d3d11.dll"];
        var existing = candidates.FirstOrDefault(n =>
            File.Exists(Path.Combine(dir, n)) && Identify(Path.Combine(dir, n)).IsReShade);
        if (existing is not null) return existing;

        return candidates.FirstOrDefault(n =>
                   !File.Exists(Path.Combine(dir, n)) || Identify(Path.Combine(dir, n)).Product is null)
               ?? candidates[^1];
    }

    /// <summary>ReShade.ini as it has to be for the add-on to be usable the first time the game
    /// starts: the add-on not disabled, the tutorial that covers the screen already dismissed, and the
    /// panel docked. Every other key someone has set is left exactly as it was.</summary>
    internal static string ReadyReShadeIni(string ini, uint width = 1920, uint height = 1080,
        string panel = Engine.PanelTitle)
    {
        var disabled = Engine.GetIni(ini, "ADDON", "DisabledAddons");
        if (IsOurs(disabled))
        {
            var kept = disabled.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(a => !IsOurs(a));
            ini = Engine.SetIni(ini, "ADDON", "DisabledAddons", string.Join(",", kept));
        }
        if (Engine.GetIni(ini, "OVERLAY", "TutorialProgress").Length == 0)
            ini = Engine.SetIni(ini, "OVERLAY", "TutorialProgress", "4");
        return Engine.FirstDock(ini, width, height, panel);
    }

    /// <summary>For an emulator route, whether the emulator is actually in this folder. Any of its
    /// known executable names counts: PCSX2 alone ships as five, and warning about the absence of
    /// pcsx2-qt.exe next to a perfectly good pcsx2x64-avx2.exe is noise that teaches people to
    /// ignore warnings.</summary>
    private static void CheckExe(string dir, Preset preset, Report report)
    {
        var emulator = Emulators.Known.FirstOrDefault(e => e.Route == preset);
        if (emulator is null) return;

        var found = emulator.Executables.FirstOrDefault(n => File.Exists(Path.Combine(dir, n)));
        if (found is not null)
            report.Ok($"{found} is here, so this is the right folder.");
        else
            report.Warn(
                $"No {emulator.Name} executable in this folder (looked for {string.Join(", ", emulator.Executables)}). "
                + "That is only a warning -- nothing checks which program it is -- but it is usually a "
                + "sign the path is wrong.");
    }

    /// <summary>ReShade writes DisabledAddons= into its own ini the first time anyone unticks an
    /// add-on, and from then on it never loads it again and says nothing anywhere. It is the one
    /// failure in this project that looks exactly like a broken install.</summary>
    private static void CheckDisabledAddons(string dir, Report report)
    {
        var ini = Path.Combine(dir, "ReShade.ini");
        string text;
        try { text = File.ReadAllText(ini); }
        catch { return; }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("DisabledAddons=", StringComparison.Ordinal)) continue;
            var list = line["DisabledAddons=".Length..];
            if (list.Contains(AddonName, StringComparison.Ordinal)
                || IsOurs(list))
            {
                report.Err(
                    "ReShade.ini has this add-on in DisabledAddons=. ReShade writes that line if the "
                    + "add-on is ever unticked, and then it never loads it again, with no error anywhere. "
                    + "Clear that line before blaming the install.");
            }
            else if (list.Length > 0)
            {
                report.Info($"ReShade.ini disables other add-ons: {list}");
            }
        }
    }

    /// <summary>Whether the renderer this route needs is one the files here actually link.
    ///
    /// Half-Life is why this exists. The database says D3D9 and OpenGL, which was true of the build
    /// it was written about; the copy on disk renders through hw.dll, which imports opengl32.dll and
    /// nothing else. The D3D9 route installed perfectly and the game never loaded a byte of it,
    /// because there is no Direct3D renderer in that build to load it with.
    ///
    /// This does not refuse -- a renderer can be picked in a launcher, a config file or a launch
    /// option, and none of that is visible here. It says what the files show, which is the piece of
    /// evidence nobody has when an install that "worked" does nothing.</summary>
    private static void CheckRouteIsReachable(string dir, Preset preset, Report report)
    {
        var wanted = preset switch
        {
            Preset.Dx11 or Preset.X86Dx11 => GraphicsApi.D3D11,
            Preset.Dx12 => GraphicsApi.D3D12,
            Preset.X86Dx9 => GraphicsApi.D3D9,
            Preset.X86Dx8 => GraphicsApi.D3D8,
            _ => GraphicsApi.Unknown,   // Vulkan loads as a layer, and the emulator routes are settings.
        };
        if (wanted == GraphicsApi.Unknown) return;

        // Only the game's own files, never the database: the question is what this copy links.
        var local = GraphicsDetector.Detect(dir);
        if (local.Api == GraphicsApi.Unknown || local.All.Count == 0) return;
        if (local.All.Contains(wanted)) return;

        var found = string.Join(", ", local.All.Select(GraphicsDetection.Short));
        report.Warn(
            $"This route needs {GraphicsDetection.Short(wanted)}, and the files here link {found} instead. "
            + $"{local.Why} If this build has no {GraphicsDetection.Short(wanted)} renderer, everything will "
            + "install correctly and nothing will ever load it -- switch the game to "
            + $"{GraphicsDetection.Short(wanted)} first, or pick a route that matches what it runs.");
    }

}
