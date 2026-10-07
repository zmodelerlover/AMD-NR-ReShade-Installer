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
    public ulong RuntimeSize { get; init; } = 56_677_888;
    public string WeightsSha { get; init; } = Engine.WeightsSha;
    public ulong WeightsSize { get; init; } = 147_689_451;

    /// <summary>The ReShade64.dll a 64-bit install puts in the game folder when the payload carries
    /// it. Checked like every other payload.</summary>
    public string ReShade64Sha { get; init; } = Engine.ReShade64Sha;

    /// <summary>The 32-bit route's ReShade and d3d8to9, from the payload's x86-extras. The bridge installer
    /// checked the engine's own constants instead, so a payload that moved any pin -- the runtime's first
    /// -- failed every 32-bit install with "SHA256 mismatch" over the file it had just downloaded.</summary>
    public string ReShade32Sha { get; init; } = Engine.ReShadeSha;
    public string D3d8To9Sha { get; init; } = Engine.D3d8To9Sha;

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

    /// <summary>The OptiScaler release ("0.4.3-amd-nr") and the runtime version it ships ("0.4.2"): what
    /// decides whether a runtime already in the game folder can be its runtime instead. Empty without the route.</summary>
    public string OptiScalerVersion { get; init; } = string.Empty;
    public string OptiRuntimeVersion { get; init; } = string.Empty;

    /// <summary>The person picked danielblnc's version for this game: it goes in as pinned, in place of a
    /// newer build already in the folder.</summary>
    public bool RuntimeChosen { get; init; }

    /// <summary>The mochizuki runtime's files -- the runtime, its shaders, its prewarm list and its
    /// model -- by their path in the payload folder. Empty when the OptiScaler version chosen does
    /// not carry it; installed only when it is asked for.</summary>
    public IReadOnlyDictionary<string, string> MochizukiFiles { get; init; } = new Dictionary<string, string>();

    /// <summary>The add-on and bridge versions pinned: which of <see cref="UserRuntimes"/> the ReShade
    /// routes can run. Empty when not known, which runs none of them.</summary>
    public string AddonVersion { get; init; } = string.Empty;
    public string BridgeVersion { get; init; } = string.Empty;

    /// <summary>danielblnc's builds a person supplies themselves (payload.json's user_runtimes).</summary>
    public IReadOnlyList<UserRuntime> UserRuntimes { get; init; } = [];
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

    /// <summary>The files whose presence means "this folder has an install of ours". Uninstall takes
    /// every one of them by name, in or out of a manifest (see <see cref="OurNames"/>), which is what
    /// keeps the badge and the uninstall agreeing. Deliberately no manifest and no ini: uninstall keeps
    /// the manifest whenever it preserves a configuration entry, so "a manifest is here" is true long
    /// after everything has been removed.</summary>
    public static readonly string[] InstalledMarkers =
        [AddonName, Addon32Name, Host64Name, RuntimeName, WeightsName];

    /// <summary>The same, for the FiveM route, which writes under FiveM.app rather than beside an
    /// executable. Kept apart so every other route's folder is still asked only for its own names.</summary>
    public static readonly string[] FiveMMarkers =
        [$"{FiveMPlugins}/{AddonName}", $"{FiveMGame}/{RuntimeName}", $"{FiveMGame}/{WeightsName}"];

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
                    && Engine.Lower(entry.Hash) != want
                    && !(manifest.Preset == Preset.OptiScaler.ManifestPreset()
                        ? OwnRuntimeIsCurrent(payload, entry.Name, Engine.Lower(entry.Hash))
                        : UserRuntimeIsCurrent(payload, manifest.Route, entry.Name, Engine.Lower(entry.Hash))))
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
        payload = payload.WithMochizuki();

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
        // Asked about only where an install has them, as on the OptiScaler route.
        if (payload.Has(PayloadManifest.MochizukiComponent) && payload.Has(PayloadManifest.MochizukiModelComponent))
            foreach (var name in new[] { PayloadManifest.MochizukiComponent, PayloadManifest.MochizukiModelComponent })
                foreach (var file in payload.Component(name).Installed)
                    pinned[MochizukiDestination(file.RelativePath)] = Engine.Lower(file.Sha256);
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

    /// <summary>Files an older layout left behind: one copy of the runtime per pass, which did not
    /// fit in VRAM and has not been used for several releases.</summary>
    private static IEnumerable<string> DeadFiles() =>
        Enumerable.Range(2, 9).Select(n => $"dlssnr_amd_pass{n}.dll").Concat(Engine.Legacy);

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

    /// <summary>A transaction's refusal and that nothing was left behind. Some refusals are sentences of their own
    /// (<see cref="Engine.OpenElsewhere"/>) and end with a period, so one is not added twice.</summary>
    internal static string RolledBack(InstallException e) =>
        $"{e.Message.TrimEnd().TrimEnd('.')}. Nothing was left half-written: the install rolled itself back.";

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
        // FiveM: whatever name the ReShade in plugins already has, or dxgi.dll. Not a choice.
        _ when preset.IsVulkan() || preset == Preset.FiveM => [],
        // Not ReShade's list: every name OptiScaler's dllmain answers to. winmm.dll is the way in
        // when dxgi.dll has to stay something else's; the rest are what the OptiScaler wiki names
        // for games that load neither (Forspoken wants d3d12.dll, No Man's Sky dbghelp.dll). d3d9.dll and
        // opengl32.dll are the D3D9 and OpenGL games' own names, from OptiScaler 0.5.0 on.
        Preset.OptiScaler =>
            ["dxgi.dll", "winmm.dll", "version.dll", "d3d12.dll", "dbghelp.dll", "wininet.dll", "winhttp.dll", "d3d9.dll", "opengl32.dll"],
        Preset.X86Dx9 or Preset.X86Dx8 => ["d3d9.dll", "dinput8.dll"],
        Preset.X86Dx11 => ["dxgi.dll", "d3d11.dll", "dinput8.dll"],
        Preset.Dx12 => ["dxgi.dll", "d3d12.dll", "dinput8.dll"],
        // Read out of the pinned ReShade64.dll the same way the rest of this list was: it exports
        // the twenty-four wgl* entry points and the GL 1.1 set, so opengl32.dll is a name it can
        // really be loaded under. dinput8.dll is kept as the way in for a host that loads OpenGL
        // through something a proxy beside the executable cannot displace -- ReShade hooks the
        // system opengl32 once it is in the process, whichever name carried it there.
        Preset.OpenGL or Preset.X86OpenGL => ["opengl32.dll", "dinput8.dll"],
        _ => ["dxgi.dll", "d3d11.dll", "dinput8.dll"],
    };

    /// <summary>Whether a chosen name is one this API can be loaded under at all. A choice that is
    /// not is ignored rather than installed: writing d3d12.dll into a D3D11 game produces a file
    /// nothing opens, and the install would look like it worked.</summary>
    public static bool ProxyAllowed(Preset preset, string? name) =>
        name is { Length: > 0 } &&
        ProxyChoicesFor(preset).Contains(name, StringComparer.OrdinalIgnoreCase);

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
        else if (Strip(line, "REPLACED changed since the install, backed up: ") is { } replaced)
            report.Ok($"{replaced} had been changed since the install; that copy was backed up and replaced.");
        else if (Strip(line, "RESTORED: ") is { } restored) report.Ok($"restored {restored} from its backup");
        else if (Strip(line, "REMOVED: ") is { } removed) report.Ok($"removed {removed}");
        else if (Strip(line, "WARNING ") is { } warning) report.Warn(warning);
        else if (Strip(line, "CHANGED: ") is { } changed)
            report.Ok($"{changed} had changed since the install; it is still this app's, so it went too.");
        else if (Strip(line, "KEPT as the runtime updated it: ") is { } kept)
            report.Ok($"{kept} kept as the runtime rewrote it for this machine.");
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

    /// <summary>The programs running out of the executables in the folder an install writes to: the game, or an
    /// emulator. Files it has loaded cannot be replaced or taken out, and Cyberpunk 2077 left running failed six
    /// installs and five rollbacks in a row. FiveM is left out: its game process runs from another folder.</summary>
    public static IReadOnlyList<(int Pid, string Name)> RunningFrom(string gameDir, Preset preset)
    {
        try
        {
            return preset != Preset.FiveM && InstallFolder(gameDir, preset) is { } dir
                ? Engine.ProcessesHolding(Directory.EnumerateFiles(dir, "*.exe"))
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstallException)
        {
            return [];
        }
    }

    /// <summary>The error a running game is, or null when nothing runs from the folder.</summary>
    private static string? Running(string gameDir, Preset preset) =>
        RunningFrom(gameDir, preset) is { Count: > 0 } running
            ? $"{Joined(running.Select(p => p.Name).ToList())} is running from this folder. Files it has loaded cannot be "
              + "replaced or taken out while it runs: close it first. A game that crashed or froze can stay running with "
              + "no window; end it in Task Manager (Details tab)."
            : null;

    // -- Pre-flight ------------------------------------------------------------------------------
    // Everything that can be known before a single byte is written, and cheap enough to redo while
    // a path is still being pasted: metadata, one open(), one free-space call.

    /// <param name="mochizuki">Install the mochizuki runtime as well. No route makes it the NR runtime:
    /// danielblnc stays it until the person picks mochizuki in OptiScaler's Neural tab or the add-on's
    /// panel.</param>
    /// <param name="ownRuntime">A file holding a danielblnc build the person supplied, one the payload lists
    /// under user_runtimes (see <see cref="UserRuntime.Read"/>), to install in place of the download's
    /// runtime: patched on the ReShade routes, as it is on OptiScaler. Null is the download's -- and on
    /// OptiScaler, as before, a build of the author's already in the folder.</param>
    /// <param name="wantedRuntime">The build chosen for the game when no file of it was found: the download
    /// goes in, and the report says the build is not used and why.</param>
    /// <param name="suggestedProxy">The name the OptiScaler wiki gives for this game (<see cref="ApiDatabase.OptiScalerNames"/>):
    /// what OptiScaler goes in as when no name was picked and no install of it is here already.</param>
    /// <param name="api">The API the game runs OptiScaler on (<see cref="GraphicsDetection.OptiApi"/>). From OptiScaler
    /// 0.5.0 on it names OptiScaler's default name: opengl32.dll, d3d9.dll, winmm.dll for Vulkan, dxgi.dll otherwise.</param>
    public static Report Preflight(string gameDir, string payloadDir, Preset preset, PayloadPins pins,
        string? proxy = null, bool mochizuki = false, string? ownRuntime = null, UserRuntime? wantedRuntime = null,
        string? suggestedProxy = null, GraphicsApi? api = null)
    {
        var report = preset.IsOptiScaler()
            ? PreflightOptiScaler(gameDir, payloadDir, pins, proxy, mochizuki, ownRuntime, wantedRuntime, suggestedProxy, api)
            : preset == Preset.FiveM ? PreflightFiveM(gameDir, payloadDir, pins, ownRuntime, wantedRuntime)
            : PreflightReShade(gameDir, payloadDir, preset, pins, proxy, mochizuki, ownRuntime, wantedRuntime);
        if (Running(gameDir, preset) is { } running) report.Err(running);
        // Said before Install is pressed, as what Install does about it rather than as a problem.
        try
        {
            if (InstallFolder(gameDir, preset) is { } dir && InTheWay(dir, preset) is { } why)
                report.Info($"{why} {ClearsTheWay}");
        }
        catch (InstallException) { /* the route's own lines already say what is wrong with the folder */ }
        return report;
    }

    // -- Install ---------------------------------------------------------------------------------

    /// <param name="mochizuki">See <see cref="Preflight"/>.</param>
    /// <param name="ownRuntime">See <see cref="Preflight"/>.</param>
    /// <param name="wantedRuntime">See <see cref="Preflight"/>.</param>
    /// <param name="suggestedProxy">See <see cref="Preflight"/>.</param>
    /// <param name="api">See <see cref="Preflight"/>.</param>
    public static Report Install(string gameDir, string payloadDir, Preset preset, PayloadPins pins,
        string? proxy = null, bool mochizuki = false, string? ownRuntime = null, UserRuntime? wantedRuntime = null,
        string? suggestedProxy = null, GraphicsApi? api = null)
    {
        if (Running(gameDir, preset) is { } running)
        {
            var refused = new Report();
            refused.Err(running);
            refused.Info("Nothing was written.");
            return refused;
        }
        // What would make the transaction refuse, cleared first, on every route: see InTheWay.
        var report = ClearTheWay(gameDir, preset, pins) ?? new Report();
        if (report.Failed)
        {
            report.Info("Nothing new was written: fix the problem above and run it again.");
            return report;
        }
        report.Append(preset.Route() == Route.X86 ? InstallX86(gameDir, payloadDir, preset, pins, proxy, mochizuki, ownRuntime, wantedRuntime)
            : preset.IsOptiScaler() ? InstallOptiScaler(gameDir, payloadDir, pins, proxy, mochizuki, ownRuntime, wantedRuntime, suggestedProxy, api)
            : preset == Preset.FiveM ? InstallFiveM(gameDir, payloadDir, pins, ownRuntime, wantedRuntime)
            : InstallReShade(gameDir, payloadDir, preset, pins, proxy, mochizuki, ownRuntime, wantedRuntime));
        return report;
    }
}
