// What the installer actually does to a folder. No UI -- so the whole of it can be reasoned about,
// and tested, without a window in the way. Ported from installer/src/work.rs.
//
// One deliberate difference from the Rust original: the add-on is not compiled into this assembly.
// The Rust installer embedded it so it could never hand out an add-on from a different release than
// the runtime it was built beside; an app that updates itself and downloads its payloads cannot
// carry that, so the same guarantee is kept by pinning every hash in PayloadPins, which comes from
// the payload manifest and is verified after download and again before a byte is copied.

using System.Text;

namespace AmdNr.Core;

/// <summary>The hashes and sizes a payload set has to match. Defaults are the pinned v0.5.0 values
/// from the add-on's own source; the app overrides them from payload.json.</summary>
public sealed class PayloadPins
{
    public required string AddonSha { get; init; }
    public required ulong AddonSize { get; init; }
    public string RuntimeSha { get; init; } = Engine.RuntimeSha;
    public ulong RuntimeSize { get; init; } = 7_290_880;
    public string WeightsSha { get; init; } = Engine.WeightsSha;
    public ulong WeightsSize { get; init; } = 147_689_451;

    /// <summary>The ReShade64.dll a 64-bit install puts in the game folder when the payload carries
    /// it. Checked like every other payload.</summary>
    public string ReShade64Sha { get; init; } = Engine.ReShade64Sha;

    /// <summary>The companion effect. Empty when the manifest does not carry one, which is how
    /// an install from an older manifest skips it instead of failing.</summary>
    public string ShaderSha { get; init; } = string.Empty;
    public ulong ShaderSize { get; init; }

    /// <summary>The OptiScaler route's files, by their path in the payload folder. Empty when the
    /// manifest does not carry that route.</summary>
    public IReadOnlyDictionary<string, string> OptiFiles { get; init; } = new Dictionary<string, string>();

    /// <summary>The runtime build OptiScaler drives, installed once per pass. Empty without the route.</summary>
    public string OptiRuntimeName { get; init; } = string.Empty;
    public string OptiRuntimeSha { get; init; } = string.Empty;
    public ulong OptiRuntimeSize { get; init; }
}

/// <summary>What the target says about which route applies. A folder can hold a 32-bit launcher
/// next to a 64-bit game, so this detects when the answer is unambiguous and says so when it is
/// not, rather than picking one and being confidently wrong.</summary>
public sealed class Detected
{
    public Route? Route { get; private init; }
    public string? Line { get; private init; }
    public bool IsMixed { get; private init; }

    public static readonly Detected Unknown = new();
    public static Detected On(Route route, string why) => new() { Route = route, Line = why };
    public static Detected Mixed(string why) => new() { IsMixed = true, Line = why };
}

public static partial class Work
{
    public const string AddonName = "amd-nr.addon64";
    public const string RuntimeName = "dlssnr_amd_pass1.dll";
    /// <summary>The companion effect, and where it has to land: ReShade's default
    /// EffectSearchPaths is reshade-shaders/Shaders/**, so anywhere else and ReShade never
    /// compiles it. This is what gives a game with no velocity buffer of its own real motion
    /// vectors -- it reads whatever optical-flow shader is installed above it (iMMERSE
    /// Launchpad, VORT, LumeniteFX) and hands the field to the add-on.</summary>
    public const string ShaderName = "AMD_Neural_Feed.fx";
    public const string ShaderPath = "reshade-shaders/Shaders/" + ShaderName;
    public const string WeightsName = "dlssnr_on_amd_weights.bin";
    public const string Addon32Name = "amd-nr.addon32";
    public const string Host64Name = "amd-nr-host64.exe";

    /// <summary>The files whose presence means "this folder has an install of ours", and exactly the
    /// ones uninstall takes back. Deliberately no manifest and no ini: uninstall keeps the manifest
    /// whenever it preserves a configuration entry, so "a manifest is here" is true long after
    /// everything has been removed.</summary>
    public static readonly string[] InstalledMarkers =
        [AddonName, Addon32Name, Host64Name, RuntimeName, WeightsName];

    /// <summary>Whether what is installed in this folder is still what the payload pins. An install
    /// records the SHA-256 of every file it wrote, so a recorded hash that is not the one the
    /// manifest now carries for that name is an install the payload has moved past -- which is how
    /// somebody who installed last week finds out the bridge was rebuilt, rather than by reading it
    /// somewhere. Nothing is changed here; this only answers the question.
    ///
    /// Only the files the payload names are asked about. ReShade is installed under the API's own
    /// name rather than the payload's, and the configuration files belong to the user. A folder
    /// with no manifest of its own, or one this build cannot decode, answers false: "installed by
    /// something else" is not "out of date", and a guess here would nag for ever.</summary>
    public static bool PayloadMovedOn(string folder, PayloadManifest payload)
    {
        foreach (var name in new[] { Engine.ManifestName, Engine.ManifestNameX64 })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            Manifest manifest;
            try { manifest = Manifest.Decode(Encoding.UTF8.GetString(Engine.Read(path))); }
            catch (InstallException) { continue; }
            if (manifest.State != "installed") continue;

            var pinned = PinnedFor(payload, manifest.Route, manifest.Preset);
            foreach (var entry in manifest.Entries)
            {
                if (!entry.Owned || entry.Configuration) continue;
                if ((pinned.TryGetValue(entry.Name, out var want)
                     || pinned.TryGetValue(Path.GetFileName(entry.Name), out want))
                    && Engine.Lower(entry.Hash) != want)
                    return true;
            }
        }
        return false;
    }

    /// <summary>What the payload pins for one route, by file name. Per route, because one name is
    /// two different files: the payload carries ReShade 32-bit as x86-extras\dxgi.dll, and a 64-bit
    /// install writes ReShade 64-bit under that same name. Comparing every component against every
    /// install would call every 64-bit install out of date, for ever, over a file that is exactly
    /// what it should be.
    ///
    /// The reshade component itself is never in here under either route: it is published as
    /// ReShade64.dll and ReShade32.dll and installed as whatever the game's API is called, so its
    /// name in the folder is not a name this can look up.</summary>
    private static Dictionary<string, string> PinnedFor(PayloadManifest payload, Route route, string preset)
    {
        // The OptiScaler route shares the 64-bit manifest but none of its files but the weights,
        // and its runtime is a different build under the same name: compared against the add-on's
        // pins, every OptiScaler install would read as out of date for ever.
        if (preset == Preset.OptiScaler.ManifestPreset()) return PinnedForOptiScaler(payload);

        var pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string[] components = route == Route.X64
            ? [PayloadManifest.AddonComponent, PayloadManifest.RuntimeComponent, PayloadManifest.ShaderComponent]
            : [PayloadManifest.BridgeComponent, PayloadManifest.RuntimeComponent,
               PayloadManifest.ShaderComponent, PayloadManifest.X86ExtrasComponent];
        foreach (var name in components)
        {
            if (!payload.Has(name)) continue;
            foreach (var file in payload.Component(name).Installed)
                pinned[Path.GetFileName(file.Name)] = Engine.Lower(file.Sha256);
        }
        return pinned;
    }

    /// <summary>Known-bad: the runtimes earlier releases pinned. Recognised by their first bytes so
    /// the message can be "you have the old one" instead of "this file is wrong". Each was once the
    /// correct file, so a folder left over from an earlier install lands here rather than in the
    /// generic hash-mismatch branch, which reads like a corrupt download.</summary>
    private static readonly (string Prefix, string Version)[] OldRuntimes =
    [
        ("e145ff963b1ef614", "v0.2.14"),
        ("ddd82d313aa74c2e", "v0.2.17"),
    ];

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

    /// <summary>Files an older layout left behind: one copy of the runtime per pass, which did not
    /// fit in VRAM and has not been used for several releases.</summary>
    /// <summary>Adds the companion effect to whatever a route is about to write, from whichever
    /// layout the payload came in. Both routes call this and neither has its own copy: v0.3.0
    /// shipped the effect on the 64-bit route only, because that was the only place the lines
    /// existed, and a 32-bit install went without it in silence. One implementation is the only
    /// version of that invariant a future change cannot forget half of.</summary>
    public static void AddCompanionEffect(IDictionary<string, byte[]> files, string payloadDir)
    {
        var nested = Path.Combine(payloadDir, "files", ShaderName);
        var flat = Path.Combine(payloadDir, ShaderName);
        var from = File.Exists(nested) ? nested : flat;
        // Absent is not an error: a payload manifest published before the effect was installable
        // has no shader component, and the add-on works without it.
        if (File.Exists(from)) files[ShaderPath] = Engine.Read(from);
    }
    private static IEnumerable<string> DeadFiles() =>
        Enumerable.Range(2, 9).Select(n => $"dlssnr_amd_pass{n}.dll").Concat(Engine.Legacy);

    /// <summary>Whether a ReShade DisabledAddons entry names this add-on, under either the
    /// name it uses now or the one it used before v0.6.5. A folder upgraded in place can
    /// carry either, and matching only one leaves the other silently disabled.</summary>
    private static bool IsOurs(string entry) =>
        entry.Contains("amd-nr", StringComparison.OrdinalIgnoreCase)
        || entry.Contains("dlss5", StringComparison.OrdinalIgnoreCase);

    /// <summary>A release folder keeps its payloads in files\, and a folder holding just the
    /// unzipped files works too. Whichever was given, this is where the payloads are read from.</summary>
    public static string PayloadDir(string source)
    {
        var nested = Path.Combine(source, "files");
        return File.Exists(Path.Combine(nested, RuntimeName)) || File.Exists(Path.Combine(nested, WeightsName))
            ? nested
            : source;
    }

    /// <summary>A dropped executable becomes its folder, which is right for a flow that installs
    /// into one.</summary>
    private static string ResolveSource(string raw)
    {
        var p = raw.Trim().Trim('"');
        if (p.Length == 0) return string.Empty;
        return File.Exists(p) ? Path.GetDirectoryName(p) ?? p : p;
    }

    /// <summary>The path exactly as typed, file or folder -- the 32-bit route needs the executable
    /// itself, because it reads the PE header.</summary>
    private static string ResolveTarget(string raw) => raw.Trim().Trim('"');

    private static string NameOf(string p) => Path.GetFileName(p);

    /// <summary>Three names at most: the point is to show the evidence, not to list a folder.</summary>
    private static string Joined(IReadOnlyList<string> names)
    {
        var shown = names.Take(3).ToList();
        return names.Count > shown.Count
            ? $"{string.Join(", ", shown)} and {names.Count - shown.Count} more"
            : string.Join(", ", shown);
    }

    /// <summary>What a proxy DLL actually is, from its version resource. The filename says nothing:
    /// OptiScaler, DXVK and SpecialK all install as dxgi.dll too, and calling one of them "ReShade
    /// found" sends someone to look for an add-on in a ReShade that is not there.</summary>
    internal static (bool IsReShade, string? Product, string? Version) Identify(string path)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
            var product = string.IsNullOrWhiteSpace(info.ProductName) ? info.FileDescription : info.ProductName;
            var isReShade = (product ?? "").Contains("ReShade", StringComparison.OrdinalIgnoreCase);
            return (isReShade, string.IsNullOrWhiteSpace(product) ? null : product.Trim(), info.ProductVersion?.Trim());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return (false, null, null);
        }
    }

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

    /// <summary>The name ReShade is loaded under. dxgi.dll serves D3D10/11/12 and is what ReShade's
    /// own setup picks; when a ReShade is already there under another proxy name it is replaced in
    /// place rather than doubled; when dxgi.dll belongs to something else -- OptiScaler, DXVK -- the
    /// API's own DLL is used so both keep loading. Vulkan has no proxy at all: ReShade is a layer
    /// there, registered by its own setup, and this returns null.</summary>
    /// <summary>Every name the pinned ReShade can actually be loaded under for this API, best first.
    ///
    /// Not a guess and not ReShade's setup menu: these are the names whose entry points the pinned
    /// ReShade64.dll really exports, read out of it. That is why `version.dll` is not here, however
    /// often it is suggested — this build exports nothing of version.dll's, so a game that imports
    /// it would fail to resolve rather than load ReShade. `dinput8.dll` is here because the build
    /// does export `DirectInput8Create`, and it is the way into a game that reaches its graphics
    /// API through something this list cannot displace.
    ///
    /// The first entry is what the automatic pick prefers; a person may choose any of them, and
    /// some games only ever load one. Vulkan has none: ReShade is a layer there.</summary>
    public static string[] ProxyChoicesFor(Preset preset) => preset switch
    {
        _ when preset.IsVulkan() => [],
        // Not ReShade's list: these are names OptiScaler itself can be loaded under. winmm.dll is
        // the way in when dxgi.dll has to stay something else's.
        Preset.OptiScaler => ["dxgi.dll", "winmm.dll"],
        Preset.X86Dx9 or Preset.X86Dx8 => ["d3d9.dll", "dinput8.dll"],
        Preset.X86Dx11 => ["dxgi.dll", "d3d11.dll", "dinput8.dll"],
        Preset.Dx12 => ["dxgi.dll", "d3d12.dll", "dinput8.dll"],
        // Read out of the pinned ReShade64.dll the same way the rest of this list was: it exports
        // the twenty-four wgl* entry points and the GL 1.1 set, so opengl32.dll is a name it can
        // really be loaded under. dinput8.dll is kept as the way in for a host that loads OpenGL
        // through something a proxy beside the executable cannot displace -- ReShade hooks the
        // system opengl32 once it is in the process, whichever name carried it there.
        Preset.OpenGL => ["opengl32.dll", "dinput8.dll"],
        _ => ["dxgi.dll", "d3d11.dll", "dinput8.dll"],
    };

    /// <summary>Whether a chosen name is one this API can be loaded under at all. A choice that is
    /// not is ignored rather than installed: writing d3d12.dll into a D3D11 game produces a file
    /// nothing opens, and the install would look like it worked.</summary>
    public static bool ProxyAllowed(Preset preset, string? name) =>
        name is { Length: > 0 } &&
        ProxyChoicesFor(preset).Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>The name this install will load ReShade under, whichever route it takes. Null only
    /// on Vulkan, where ReShade is a layer and no file in the folder is it.</summary>
    internal static string? ProxyNameFor(Preset preset, string dir, string? proxy) =>
        preset.IsVulkan() ? null
        : preset.Route() == Route.X86
            ? ProxyAllowed(preset, proxy) ? proxy : Engine.X86ProxyName(preset.ManifestPreset())
            : ReShadeProxyFor(preset, dir, proxy);

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
    internal static string ReadyReShadeIni(string ini, uint width = 1920, uint height = 1080)
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
        return Engine.FirstDock(ini, width, height);
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

    /// <summary>Verify a payload in the folder given and hand back its bytes. Whether it then gets
    /// written is <see cref="Transaction.Apply"/>'s decision, because that is what records ownership
    /// and takes the backup.</summary>
    private static byte[]? VerifiedPayload(string srcDir, string name, string wantSha, Report report)
    {
        var src = Path.Combine(srcDir, name);
        if (!File.Exists(src))
        {
            report.Err($"{name} is not in the payload folder.");
            return null;
        }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(src); }
        catch (Exception e)
        {
            report.Err($"could not read {name}: {e.Message}");
            return null;
        }

        var got = Engine.Sha(bytes);
        if (got == wantSha) return bytes;

        var old = Array.Find(OldRuntimes, r => got.StartsWith(r.Prefix, StringComparison.Ordinal));
        if (name == RuntimeName && old.Version is not null)
        {
            report.Err(
                $"{name} is the old {old.Version} runtime. This release requires v0.3.0 and the "
                + "add-on refuses anything else. Let the app download the current one.");
        }
        else
        {
            report.Err(
                $"{name} does not match the expected SHA-256.\n      expected {wantSha}\n      "
                + $"got      {got}\n      The add-on hashes the runtime at load and will refuse it.");
        }
        return null;
    }

    /// <summary>The engine speaks the x86 installer's vocabulary; this turns it into the sentences
    /// a person reads.</summary>
    private static void Narrate(string line, Report report)
    {
        if (Strip(line, "IDENTICAL: ") is { } identical) report.Ok($"{identical} already correct, left alone.");
        else if (Strip(line, "CREATE: ") is { } created) report.Ok($"{created} copied and verified.");
        else if (Strip(line, "EXTERNAL backed up: ") is { } backed) report.Ok($"{backed} replaced; the previous file was backed up.");
        else if (Strip(line, "RESTORED: ") is { } restored) report.Ok($"restored {restored} from its backup");
        else if (Strip(line, "REMOVED: ") is { } removed) report.Ok($"removed {removed}");
        else if (Strip(line, "WARNING ") is { } warning) report.Warn(warning);
        else if (Strip(line, "FORCED: ") is { } forced)
            report.Warn($"{forced} had changed since the install and was removed anyway, as asked.");
        else report.Info(line); // PRESERVED lines and anything the engine adds later read fine as they are.

        static string? Strip(string s, string prefix) =>
            s.StartsWith(prefix, StringComparison.Ordinal) ? s[prefix.Length..] : null;
    }

    /// <summary>Removes what an older install left under names nothing loads deliberately any more:
    /// the per-pass runtimes, and the add-on under the name it used before v0.6.5. ReShade loads
    /// every .addon64 and every .addon32 in the folder, so one of those left beside the new one is
    /// a second add-on, a second overlay and a second engine competing for the same present.
    ///
    /// Both routes call this, because both can be upgraded over a folder that has them -- and the
    /// 32-bit one is where the old names actually survive, since the rename happened after the
    /// bridge shipped. Returns what it removed so each route can say so in its own words; a file
    /// that will not delete is a warning, never a failed install.</summary>
    internal static IReadOnlyList<string> SweepDead(string dir, Action<string> warn)
    {
        var removed = new List<string>();
        foreach (var name in DeadFiles())
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p)) continue;
            try
            {
                File.Delete(p);
                removed.Add(name);
            }
            catch (Exception e)
            {
                warn($"could not remove {name}: {e.Message}");
            }
        }
        return removed;
    }

    // -- Pre-flight ------------------------------------------------------------------------------
    // Everything that can be known before a single byte is written, and cheap enough to redo while
    // a path is still being pasted: metadata, one open(), one free-space call.

    public static Report Preflight(string gameDir, string payloadDir, Preset preset, PayloadPins pins,
        string? proxy = null)
    {
        if (preset.IsOptiScaler()) return PreflightOptiScaler(gameDir, payloadDir, pins, proxy);

        var report = new Report();
        var dir = ResolveSource(gameDir);
        var src = ResolveSource(payloadDir);

        // --- the payloads, which is where someone starts ---------------------------------------
        if (src.Length == 0)
        {
            report.Info("Waiting for the payload folder: the add-on, the runtime and the weights.");
        }
        else if (!Directory.Exists(src))
        {
            report.Err($"{src} is not a folder.");
        }
        else
        {
            var allThere = true;
            var payloads = PayloadDir(src);

            // The 32-bit route installs its own pair, pinned by payload.sha256, and never the 64-bit
            // add-on -- asking for that file there is asking for something that is not supposed to exist.
            if (preset.Route() == Route.X86)
            {
                foreach (var name in new[] { "payload.sha256", @"files\amd-nr.addon32", @"files\amd-nr-host64.exe" })
                {
                    if (File.Exists(Path.Combine(src, name))) continue;
                    report.Err($"{Path.GetFileName(name)} is not in the payload folder; the 32-bit bridge cannot be installed without it.");
                    allThere = false;
                }
            }

            var expected = preset.Route() == Route.X86
                ? new[] { (RuntimeName, pins.RuntimeSize), (WeightsName, pins.WeightsSize) }
                : [(AddonName, pins.AddonSize), (RuntimeName, pins.RuntimeSize), (WeightsName, pins.WeightsSize)];

            foreach (var (name, want) in expected)
            {
                var nested = Path.Combine(src, "files", name);
                switch (Engine.SizeOf(File.Exists(nested) ? nested : Path.Combine(payloads, name)))
                {
                    case null:
                        report.Err($"{name} is not in that folder.");
                        allThere = false;
                        break;
                    // want == 0 means the size is not known, not that the file should be empty: the
                    // add-on's own size only comes from the payload manifest, and without one the
                    // pins fall back to a zero there. Judging a real file against it said "that is a
                    // different build" about the right file.
                    case { } got when want > 0 && got != want:
                        report.Err(
                            $"{name} is {got} bytes, and this release expects {want}. That is a different "
                            + "build, and the add-on refuses anything but the one it was compiled against.");
                        allThere = false;
                        break;
                }
            }
            if (allThere) report.Ok("Every payload is there and the right size. Installing verifies the SHA-256 too.");
        }

        // --- the target -------------------------------------------------------------------------
        if (dir.Length == 0)
        {
            report.Info($"Waiting for the {preset.FolderLabel().ToLowerInvariant()}.");
            return report;
        }
        if (!Directory.Exists(dir))
        {
            report.Err($"{dir} is not a folder.");
            return report;
        }

        if (!Engine.FolderIsWritable(dir))
        {
            report.Err(
                "That folder cannot be written to. It is either read-only or somewhere that needs "
                + "administrator rights -- run this installer as administrator, or move the game.");
        }

        // Anything already there and held open will fail the copy, so name the files rather than let
        // the copy come back with "access denied" halfway through.
        var held = new[] { AddonName, RuntimeName, WeightsName }
            .Where(n => Engine.IsLocked(Path.Combine(dir, n))).ToList();
        if (held.Count > 0)
        {
            report.Err(
                $"{string.Join(", ", held)} {(held.Count == 1 ? "is" : "are")} open by another program. "
                + "The game or emulator is almost certainly still running -- close it and this line goes away.");
        }

        // --- room for the weights ----------------------------------------------------------------
        ulong need = 0;
        foreach (var (name, size) in new[]
                 {
                     (AddonName, pins.AddonSize),
                     (RuntimeName, pins.RuntimeSize),
                     (WeightsName, pins.WeightsSize),
                 })
        {
            if (Engine.SizeOf(Path.Combine(dir, name)) != size) need += size;
        }
        if (Engine.FreeBytes(dir) is { } free && need > 0 && free < need)
        {
            report.Err(
                $"Not enough room: {free / 1_048_576} MB free, and this needs {need / 1_048_576} MB. "
                + $"The weights alone are {pins.WeightsSize / 1_048_576} MB.");
        }

        CheckExe(dir, preset, report);
        CheckRouteIsReachable(dir, preset, report);

        // The 32-bit route installs the pinned ReShade build itself when the payload carries it, so
        // "no ReShade here" is not a problem to report there -- it is the state before installing.
        var shipped = ShippedReShade(src, preset);
        if (shipped is not null)
            report.Ok(preset.Route() == Route.X86
                ? $"The pinned 32-bit ReShade 6.8.0 is part of this install, as {ProxyNameFor(preset, dir, proxy)}; nothing to install by hand."
                : $"ReShade 6.8.0 with full add-on support is part of this install, as {ReShadeProxyFor(preset, dir, proxy)}; nothing to install by hand.");
        else
            CheckReShade(dir, preset, report);

        // Never inside the else: shipping ReShade is exactly when a second one is most likely, and
        // skipping the whole check there is what let a folder with two of them install cleanly.
        CheckDoubleReShade(dir, preset, report, ProxyNameFor(preset, dir, proxy), shipped);

        CheckDisabledAddons(dir, report);

        var dead = DeadFiles().Where(n => File.Exists(Path.Combine(dir, n))).ToList();
        if (dead.Count > 0)
        {
            report.Info(
                $"{dead.Count} file(s) from an older layout are here and will be removed: "
                + string.Join(", ", dead));
        }

        if (!report.Failed) report.Ok("Nothing in the way.");
        return report;
    }

    // -- Install ---------------------------------------------------------------------------------

    /// <summary>The 32-bit route reads the PE header to be certain, so it needs the executable and
    /// not just the folder. When a folder was given and exactly one 32-bit executable is in it, that
    /// is unambiguous and gets used; anything else is a question only the person can answer.</summary>
    private static string? X86Target(string gameDir, Report report)
    {
        var path = ResolveTarget(gameDir);
        if (File.Exists(path)) return path;
        if (!Directory.Exists(path))
        {
            report.Err($"{path} is not a folder.");
            return null;
        }

        var found = Directory.EnumerateFiles(path, "*.exe")
            .Where(p => Engine.MachineOfFile(p) == Engine.MachineX86)
            .ToList();

        switch (found.Count)
        {
            case 1:
                return found[0];
            case 0:
                report.Err(
                    "No 32-bit executable in that folder. The bridge route needs the game's own .exe: "
                    + "point it straight at the executable.");
                return null;
            default:
                report.Err(
                    $"More than one 32-bit executable here ({string.Join(", ", found.Select(NameOf))}). "
                    + "Point it at the one the game actually runs, rather than at the folder.");
                return null;
        }
    }

    private static Report InstallX86(string gameDir, string releaseDir, Preset preset, string? proxy)
    {
        var report = new Report();
        if (X86Target(gameDir, report) is not { } target) return report;

        var release = ResolveSource(releaseDir);
        if (release.Length == 0)
        {
            report.Err(
                "The 32-bit bridge needs the unpacked x86 release -- the folder holding files\\ and "
                + "payload.sha256. The bridge ships as separate files, so nothing can be installed without it.");
            return report;
        }
        if (!File.Exists(Path.Combine(release, "payload.sha256")))
        {
            report.Err($"{release} does not look like the x86 release: payload.sha256 is not in it.");
            return report;
        }

        report.Info($"target: {target}");
        report.Info($"preset: {preset.Label()}");

        var installDir = Engine.InstallDirectory(target);
        var proxyName = ProxyNameFor(preset, installDir, proxy);
        CheckDoubleReShade(installDir, preset, report, proxyName, ShippedReShade(release, preset));
        if (report.Failed)
        {
            report.Info("Nothing was written: fix the problem above and run it again.");
            return report;
        }

        var app = new X86Installer(release);
        try
        {
            app.Install(target, preset.ManifestPreset(), proxyName);
            foreach (var line in app.Log) Narrate(line, report);
            report.Info(preset.Note());
            report.Info(
                "It starts switched off. Open the overlay with Home, or press Ctrl+End. StartOn=1 in "
                + "amd-nr.ini makes it come up enabled.");
        }
        catch (InstallException e)
        {
            foreach (var line in app.Log) Narrate(line, report);
            report.Err($"{e.Message}. Nothing was left half-written: the install rolled itself back.");
        }
        return report;
    }

    public static Report Install(string gameDir, string payloadDir, Preset preset, PayloadPins pins,
        string? proxy = null)
    {
        if (preset.Route() == Route.X86) return InstallX86(gameDir, payloadDir, preset, proxy);
        if (preset.IsOptiScaler()) return InstallOptiScaler(gameDir, payloadDir, pins, proxy);

        var report = new Report();
        var dir = ResolveSource(gameDir);
        var src = ResolveSource(payloadDir);

        if (dir.Length == 0)
        {
            report.Err("No game folder given.");
            return report;
        }
        if (!Directory.Exists(dir))
        {
            report.Err($"{dir} is not a folder.");
            return report;
        }
        report.Info($"target: {dir}");
        report.Info($"preset: {preset.Label()}");

        CheckExe(dir, preset, report);
        if (src.Length == 0 || !File.Exists(Path.Combine(PayloadDir(src), "ReShade64.dll")) || preset.IsVulkan())
            CheckReShade(dir, preset, report);
        CheckDoubleReShade(dir, preset, report, ReShadeProxyFor(preset, dir, proxy), ShippedReShade(src, preset));

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        if (src.Length == 0)
        {
            report.Err("No payload folder given, and every file this installs comes out of one.");
            return report;
        }
        if (!Directory.Exists(src))
        {
            report.Err($"{src} is not a folder.");
            return report;
        }

        var payloads = PayloadDir(src);
        foreach (var (name, want) in new[]
                 {
                     (AddonName, pins.AddonSha),
                     (RuntimeName, pins.RuntimeSha),
                     (WeightsName, pins.WeightsSha),
                 })
        {
            if (VerifiedPayload(payloads, name, want, report) is { } bytes) files[name] = bytes;
        }

        // The companion effect. Shared with the 32-bit route; see AddCompanionEffect.
        if (pins.ShaderSha.Length > 0)
        {
            var before = files.Count;
            AddCompanionEffect(files, payloads);
            if (files.Count > before)
                report.Info(
                    $"{ShaderName} goes in reshade-shaders\\Shaders. Enable it in ReShade, under a "
                    + "motion-vector shader such as iMMERSE Launchpad, and the add-on gets real "
                    + "motion vectors in a game that has none of its own.");
        }

        // ReShade itself, when the payload carries it: the add-on does nothing without it, and asking
        // someone to run a second installer and pick the right API is the step people get wrong.
        if (File.Exists(Path.Combine(payloads, "ReShade64.dll"))
            && ReShadeProxyFor(preset, dir, proxy) is { } proxyName
            && VerifiedPayload(payloads, "ReShade64.dll", pins.ReShade64Sha, report) is { } reShade)
        {
            files[proxyName] = reShade;
            var iniPath = Path.Combine(dir, "ReShade.ini");
            var before = File.Exists(iniPath) ? File.ReadAllText(iniPath) : "";
            var after = ReadyReShadeIni(before);
            if (after != before) files["ReShade.ini"] = System.Text.Encoding.UTF8.GetBytes(after);
            report.Info($"ReShade 6.8.0 with full add-on support goes in as {proxyName}.");
        }

        // A refused payload stops the whole install rather than leaving the add-on behind on its own.
        // The transaction is all-or-nothing, which is the point of routing through the engine.
        if (report.Failed)
        {
            report.Info("Nothing was written: fix the problem above and run it again.");
            return report;
        }

        var log = new List<string>();
        try
        {
            Transaction.Apply(dir, preset.ManifestPreset(), Route.X64, files, log);
            foreach (var line in log) Narrate(line, report);
        }
        catch (InstallException e)
        {
            foreach (var line in log) Narrate(line, report);
            report.Err($"{e.Message}. Nothing was left half-written: the install rolled itself back.");
            return report;
        }

        if (SweepDead(dir, report.Warn) is { Count: > 0 } swept)
            report.Ok($"removed {swept.Count} file(s) an older install left behind: {string.Join(", ", swept)}");

        if (!report.Failed)
        {
            report.Info(preset.Note());
            report.Info(
                "It starts switched off. Open the overlay with Home, or press Ctrl+End. StartOn=1 in "
                + "amd-nr.ini makes it come up enabled.");
        }
        return report;
    }

    // -- Uninstall -------------------------------------------------------------------------------

    private static Report UninstallX86(string gameDir, bool force)
    {
        var report = new Report();
        var path = ResolveTarget(gameDir);
        if (path.Length == 0)
        {
            report.Err("No game folder given.");
            return report;
        }

        // Uninstall works off the manifest, so the folder is enough -- but accept an executable too,
        // because that is what the same field held during the install.
        string dir;
        if (File.Exists(path))
        {
            try { dir = Engine.InstallDirectory(path); }
            catch (InstallException e)
            {
                report.Err(e.Message);
                return report;
            }
        }
        else
        {
            dir = path;
        }
        report.Info($"target: {dir}");

        // No manifest is not a failure, it is the ordinary state of a folder nothing was installed
        // into -- and of one installed by a build from before the manifest existed. The x64 route
        // has always said so and swept by name; saying "No install manifest" in red here, for the
        // same situation, read as something having gone wrong.
        var hadManifest = File.Exists(Path.Combine(dir, Route.X86.ManifestFileName()));
        var gone = 0;
        if (!hadManifest)
        {
            foreach (var name in new[] { Addon32Name, Host64Name, RuntimeName, WeightsName })
                gone += RemoveFile(dir, name, report);
        }
        else
        {
            var log = new List<string>();
            try
            {
                Transaction.Uninstall(dir, Route.X86, false, log, force);
                foreach (var line in log) Narrate(line, report);
            }
            catch (InstallException e)
            {
                report.Err(e.Message);
            }
        }

        // Counted, the way the x64 branch counts it: the four files above can all be gone by hand
        // and the add-on's own droppings still be there, and saying "Nothing of ours was in that
        // folder" over the top of three "removed ..." lines reads as the tool being broken.
        gone += SweepDroppings(dir, report);
        if (!hadManifest && gone == 0) report.Warn("Nothing of ours was in that folder.");

        // Said only when it is true: when this route installed the pinned ReShade itself, uninstall
        // took it back, and telling someone it was left alone would send them looking for nothing.
        if (new[] { "d3d9.dll", "dxgi.dll", "d3d8.dll" }.Any(n => File.Exists(Path.Combine(dir, n))))
            report.Info("ReShade itself was left alone. Use its own installer to remove it.");
        return report;
    }

    /// <summary>The words a retained-because-it-changed line carries. The window reads it to know
    /// whether offering to remove the file anyway would achieve anything: forcing gets past a file
    /// somebody edited, and gets nowhere against one the running game still has open.</summary>
    public const string ModifiedMarker = "modified after install";

    /// <summary><paramref name="force"/> takes back the files that changed after the install as
    /// well. Off by default, because a file that is not the one written here belongs to whatever
    /// changed it; on only when somebody has been shown that list and asked for it anyway.</summary>
    public static Report Uninstall(string gameDir, Preset preset, bool force = false)
    {
        if (preset.Route() == Route.X86) return UninstallX86(gameDir, force);

        var report = new Report();
        var dir = ResolveSource(gameDir);
        if (dir.Length == 0)
        {
            report.Err("No game folder given.");
            return report;
        }
        if (!Directory.Exists(dir))
        {
            report.Err($"{dir} is not a folder.");
            return report;
        }
        report.Info($"target: {dir}");

        var gone = 0;

        // An install written by this version has a manifest, so it knows what it owned, what it
        // displaced and what the user has changed since. Installs from before the manifest existed
        // have none, and the name sweep below is the only way to take those back.
        var manifest = Path.Combine(dir, Route.X64.ManifestFileName());
        if (File.Exists(manifest))
        {
            var log = new List<string>();
            try
            {
                Transaction.Uninstall(dir, Route.X64, false, log, force);
                foreach (var line in log) Narrate(line, report);
                gone++;
            }
            catch (InstallException e)
            {
                report.Err($"could not undo the recorded install: {e.Message}");
            }
        }
        else if (preset.IsOptiScaler())
        {
            // No manifest means this app never installed OptiScaler here. The by-name sweep below
            // would take the runtime passes and the weights out from under an OptiScaler that some
            // other installer put there, and leave it broken.
            report.Warn(
                "No install record for OptiScaler here, so it was not installed by this app and nothing is "
                + "removed. Use the uninstaller that came with it: the release package puts "
                + "Uninstall_OptiScaler_NR.bat in the game folder.");
            return report;
        }
        else
        {
            // Everything the add-on installs. The ini is deliberately not in this list.
            var names = new List<string> { AddonName, RuntimeName, WeightsName };
            names.AddRange(DeadFiles());
            foreach (var name in names) gone += RemoveFile(dir, name, report);
        }

        gone += SweepDroppings(dir, report);
        if (preset.IsOptiScaler()) AfterOptiScalerUninstall(dir, report);

        if (gone == 0) report.Warn("Nothing of ours was in that folder.");
        if (File.Exists(Path.Combine(dir, "amd-nr.ini")))
        {
            report.Info(
                "amd-nr.ini was left in place: it is your tuning, not ours. Delete it by hand if "
                + "you want a clean slate.");
        }

        // Only true when a ReShade proxy is still there: one this app installed came back out above.
        if (Proxies.Any(n => File.Exists(Path.Combine(dir, n)) && Identify(Path.Combine(dir, n)).IsReShade))
            report.Info("ReShade itself was left alone. Use its own installer to remove it.");
        return report;
    }

    /// <summary>What the add-on itself writes while a game runs: the unpacked runtime, its logs,
    /// its capture folder. They are never in a manifest, because nothing here put them there, so
    /// they are swept by name -- and by both routes. The x86 uninstall used to skip this entirely
    /// and left 7 MB of runtime plus a folder behind every time.</summary>
    private static int SweepDroppings(string dir, Report report)
    {
        var gone = 0;
        foreach (var name in new[]
                 {
                     "amd-nr-pass1.dll", "amd-nr.log", "amd-nr-x86.log",
                     "amd-nr-x86-host.log", "dlssnr_on_amd.log", "dlssnr_on_amd.ini",
                     // What OptiScaler and its bridge into the runtime write while a game runs.
                     "OptiScaler.log", "amd_bridge.log", "amd_presr.log",
                 })
            gone += RemoveFile(dir, name, report);

        foreach (var folder in new[] { "amd-nr-runtime", "dlss5-runtime", "amd-nr-captures", "dlss5-captures" })
        {
            var p = Path.Combine(dir, folder);
            if (!Directory.Exists(p)) continue;
            try
            {
                Directory.Delete(p, recursive: true);
                gone++;
                report.Ok($"removed {folder}\\");
            }
            catch (Exception e)
            {
                report.Err($"could not remove {folder}: {e.Message}");
            }
        }
        return gone;
    }

    private static int RemoveFile(string dir, string name, Report report)
    {
        var p = Path.Combine(dir, name);
        if (!File.Exists(p)) return 0;
        try
        {
            File.Delete(p);
            report.Ok($"removed {name}");
            return 1;
        }
        catch (Exception e)
        {
            report.Err($"could not remove {name}: {e.Message}");
            return 0;
        }
    }

    // -- Detection -------------------------------------------------------------------------------

    private static Route? RouteOf(ushort? machine) => machine switch
    {
        Engine.MachineX86 => Route.X86,
        Engine.MachineX64 => Route.X64,
        _ => null,
    };

    /// <summary>Read the target -- an executable, or the executables sitting in a folder -- and
    /// decide.</summary>
    public static Detected Detect(string target)
    {
        var path = ResolveTarget(target);
        if (path.Length == 0) return Detected.Unknown;

        if (File.Exists(path))
        {
            return RouteOf(Engine.MachineOfFile(path)) switch
            {
                Route.X86 => Detected.On(Route.X86, $"{NameOf(path)} is a 32-bit executable, so this is the bridge route."),
                Route.X64 => Detected.On(Route.X64, $"{NameOf(path)} is a 64-bit executable."),
                _ => Detected.Unknown,
            };
        }
        if (!Directory.Exists(path)) return Detected.Unknown;

        var x86 = new List<string>();
        var x64 = new List<string>();
        try
        {
            foreach (var p in Directory.EnumerateFiles(path, "*.exe"))
            {
                switch (RouteOf(Engine.MachineOfFile(p)))
                {
                    case Route.X86: x86.Add(NameOf(p)); break;
                    case Route.X64: x64.Add(NameOf(p)); break;
                }
            }
        }
        catch
        {
            return Detected.Unknown;
        }

        return (x86.Count > 0, x64.Count > 0) switch
        {
            (false, false) => Detected.Unknown,
            (true, false) => Detected.On(Route.X86,
                $"{Joined(x86)} here {(x86.Count == 1 ? "is" : "are")} 32-bit, so this is the bridge route."),
            (false, true) => Detected.On(Route.X64,
                $"{Joined(x64)} here {(x64.Count == 1 ? "is" : "are")} 64-bit."),
            (true, true) => Detected.Mixed(
                $"Both widths are here: {Joined(x86)} is 32-bit and {Joined(x64)} is 64-bit. A 32-bit "
                + "launcher beside a 64-bit game is normal -- pick the one the game actually runs as."),
        };
    }
}
