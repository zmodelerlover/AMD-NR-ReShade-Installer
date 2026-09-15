// Which graphics API a game renders with, read from its files rather than guessed from its name.
//
// Three signals, strongest first:
//
//   1. The main executable's import tables, ordinary and delay-loaded. A game that imports
//      d3d12.dll talks to D3D12; one that imports only vulkan-1.dll talks to Vulkan. This is the
//      same header the bitness already comes from, one table further in.
//   2. The same tables in the engine's own module beside it, for engines that keep the renderer out
//      of the executable -- Unity's UnityPlayer.dll is the common one.
//   3. Files the game ships that only exist for one API: the D3D12 Agility SDK folder, the DX12 or
//      DX11 builds of the FidelityFX and XeSS runtimes. The manager this follows reads those same
//      files to show its upscaler tags; here they name the API instead.
//
// Where the executable itself is, is decided the way that manager decides it -- Unreal's
// Binaries\Win64, including the Phoenix layout -- plus the shapes other engines use.
//
// Every answer carries the evidence for it, because a route picked on a guess is exactly the thing
// this project refuses to do silently.

namespace AmdNr.Core;

public enum GraphicsApi
{
    Unknown,
    D3D8,
    D3D9,
    D3D11,
    D3D12,
    Vulkan,
    OpenGL,
}

public sealed record GraphicsDetection(
    string? Executable,
    Route? Width,
    GraphicsApi Api,
    bool AlsoD3D11,
    string Why)
{
    /// <summary>Every API the game can render with, best source first. Empty means only
    /// <see cref="Api"/> is known.</summary>
    public IReadOnlyList<GraphicsApi> Supported { get; init; } = [];

    /// <summary>"PCGamingWiki" when the supported list came from there, null when it came from the
    /// game's own files.</summary>
    public string? Source { get; init; }

    /// <summary>The emulator this folder holds, when it is one this app knows. An emulator links
    /// every renderer it can offer, so its import table cannot pick a route; this can, and it also
    /// carries the sentence naming the setting that actually decides it.</summary>
    public EmulatorInfo? Emulator { get; init; }

    /// <summary>The file whose folder the install has to write into, when that is not the folder the
    /// executable is in. Null means they are the same, which is the ordinary case.
    ///
    /// Source is why this exists. hl2.exe sits in the root and imports nothing; the module that
    /// creates the D3D9 device is bin\shaderapidx9.dll, and Source loads bin\ with
    /// LOAD_WITH_ALTERED_SEARCH_PATH, so that module resolves its own d3d9.dll against bin\ and
    /// never looks at the root. ReShade in the root is never loaded, and ReShade in bin\ searches
    /// bin\ for add-ons. Everything has to go there together.</summary>
    public string? InstallTarget { get; init; }

    /// <summary>What an install is pointed at: the renderer's folder when those differ, the
    /// executable otherwise.</summary>
    public string? Target => InstallTarget ?? Executable;

    public IReadOnlyList<GraphicsApi> All =>
        Supported.Count > 0 ? Supported
        : Api == GraphicsApi.Unknown ? []
        : AlsoD3D11 ? [Api, GraphicsApi.D3D11] : [Api];

    /// <summary>The order this add-on would rather run on. D3D11 first because it is the only route
    /// where the game's own depth and motion reach the network; D3D12 and Vulkan get colour only.</summary>
    private static readonly GraphicsApi[] Preference =
        [GraphicsApi.D3D11, GraphicsApi.D3D12, GraphicsApi.Vulkan, GraphicsApi.D3D9, GraphicsApi.D3D8];

    public static Preset? RouteFor(Route? width, GraphicsApi api) => (width, api) switch
    {
        (Route.X64, GraphicsApi.D3D11) => Core.Preset.Dx11,
        (Route.X64, GraphicsApi.D3D12) => Core.Preset.Dx12,
        (Route.X64, GraphicsApi.Vulkan) => Core.Preset.Vulkan,
        (Route.X86, GraphicsApi.D3D11) => Core.Preset.X86Dx11,
        (Route.X86, GraphicsApi.D3D9) => Core.Preset.X86Dx9,
        (Route.X86, GraphicsApi.D3D8) => Core.Preset.X86Dx8,
        _ => null,
    };

    /// <summary>The best route among everything the game supports, or null when none of it has one
    /// -- OpenGL, a 64-bit D3D9 game, a 32-bit D3D12 one.</summary>
    public Preset? Preset
    {
        get
        {
            // A known emulator names its own route. Without this the import table decides, and an
            // emulator links every renderer at once, so the answer was whichever this loop hit
            // first -- which is how the PCSX2 and RPCS3 routes were being overwritten with D3D11.
            if (Emulator is { } emulator)
                return emulator.Route ?? RouteFor(Width, emulator.Best);

            foreach (var api in Preference)
                if (All.Contains(api) && RouteFor(Width, api) is { } route) return route;
            return null;
        }
    }

    /// <summary>The API that route runs on.</summary>
    public GraphicsApi Recommended =>
        Emulator?.Best ?? Preference.FirstOrDefault(api => All.Contains(api) && RouteFor(Width, api) is not null);

    /// <summary>When the game offers more than one API and the best route is not the only one, the
    /// game has to be told which to use -- which is the difference between "it works" and "it does
    /// nothing because the game started on D3D12".</summary>
    public bool NeedsRendererSwitch =>
        Preset is not null && All.Count(a => RouteFor(Width, a) is not null || a == GraphicsApi.OpenGL) > 1;

    /// <summary>A short label for a tile: "DX11 · DX12", "DX9 · 32-bit", "Vulkan".</summary>
    public string Tag
    {
        get
        {
            var apis = All.Count == 0 ? "?" : string.Join(" · ", All.Select(Short));
            return Width == Route.X86 ? $"{apis} · 32-bit" : apis;
        }
    }

    public static string Short(GraphicsApi api) => api switch
    {
        GraphicsApi.D3D8 => "DX8",
        GraphicsApi.D3D9 => "DX9",
        GraphicsApi.D3D11 => "DX11",
        GraphicsApi.D3D12 => "DX12",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        _ => "?",
    };

    /// <summary>What PCGamingWiki says replaces what the files suggested about *which* APIs exist;
    /// the executable, and so the bitness, still comes from the files, because only the installed
    /// copy can say which build is on this disk.</summary>
    public GraphicsDetection With(PcgwApi? wiki)
    {
        if (wiki is null || wiki.Supported.Count == 0) return this;

        var ordered = wiki.Supported
            .OrderBy(a => Array.IndexOf(Preference, a) is var i && i < 0 ? 99 : i)
            .ToList();

        var width = Width ?? (wiki.Has64Bit == true ? Route.X64 : wiki.Has32Bit == true ? Route.X86 : null);
        var local = Api == GraphicsApi.Unknown ? "" : $" The executable itself links {Short(Api)}.";
        // A layout note is not something the wiki can know or replace: it says which folder the
        // files go in, which is the difference between an install that loads and one that does not.
        var layout = InstallTarget is null ? "" : $" {Why}";
        return this with
        {
            Width = width,
            Supported = ordered,
            Source = "PCGamingWiki",
            Why = $"PCGamingWiki ({wiki.Page}): {string.Join(", ", ordered.Select(Short))}.{local}{layout}",
        };
    }
}

public static class GraphicsDetector
{
    // -- Finding the executable -----------------------------------------------------------------

    /// <summary>Not games: crash reporters, installers, anti-cheat services, launchers, browsers
    /// embedded for a store page. A folder full of these is ordinary, and picking one of them as
    /// the game would read its imports and report the wrong API with total confidence.</summary>
    private static readonly string[] NotTheGame =
    [
        "crash", "unins", "setup", "redist", "vcredist", "dxsetup", "dotnet", "launcher", "updater",
        "report", "helper", "easyanticheat", "eac_", "beservice", "battleye", "_be.exe", "installer",
        "cleanup", "touchup", "cefprocess", "cefsharp", "qtwebengine", "webhelper", "overlay",
        "uploader", "prereq", "7z", "unitycrashhandler", "subprocess", "benchmark", "configtool",
        "settings", "editor", "server.exe", "dedicated", "languageselect", "_trial", "trial.exe",
        "activation", "register", "patcher", "repair", "diagnostic", "support", "feedback",
        "bootstrap", "startup", "splash", "vrmonitor", "handler", "service", "agent",
    ];

    private static bool LooksLikeTheGame(string exe)
    {
        var name = Path.GetFileName(exe).ToLowerInvariant();
        return !NotTheGame.Any(name.Contains);
    }

    /// <summary>Where executables are looked for, nearest first. Unreal keeps the real binary two
    /// levels down under &lt;Project&gt;\Binaries\Win64, and the root holds only a stub that starts
    /// it; the stub imports no renderer at all.</summary>
    internal static IEnumerable<string> CandidateFolders(string root)
    {
        yield return Path.Combine(root, "Phoenix", "Binaries", "Win64");
        yield return Path.Combine(root, "Binaries", "Win64");

        IEnumerable<string> children;
        try { children = Directory.EnumerateDirectories(root).ToList(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { children = []; }

        foreach (var child in children)
        {
            yield return Path.Combine(child, "Binaries", "Win64");
            yield return Path.Combine(child, "Binaries", "Win32");
        }

        yield return root;
        foreach (var sub in new[] { "bin", @"bin\x64", @"bin\win64", "x64", "Bin64", "Game", "bin_x64", "Binaries" })
            yield return Path.Combine(root, sub);
        foreach (var child in children) yield return child;
    }

    /// <summary>The executable most likely to be the game. An Unreal shipping binary wins outright;
    /// otherwise the one whose name looks like the folder or the game, and failing that the largest,
    /// because the game is nearly always the biggest thing that is not an installer.</summary>
    public static string? FindExecutable(string root, string? gameName = null)
    {
        if (File.Exists(root) && root.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return root;
        if (!Directory.Exists(root)) return null;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in CandidateFolders(root))
        {
            if (!seen.Add(folder) || !Directory.Exists(folder)) continue;

            List<string> exes;
            try
            {
                exes = Directory.EnumerateFiles(folder, "*.exe")
                    .Where(LooksLikeTheGame)
                    .Where(e => Engine.MachineOfFile(e) is not null)
                    .ToList();
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            if (exes.Count == 0) continue;

            var shipping = exes.FirstOrDefault(e =>
                e.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
                || e.EndsWith("-WinGDK-Shipping.exe", StringComparison.OrdinalIgnoreCase));
            if (shipping is not null) return shipping;

            var hints = Hints(root, gameName);
            var named = exes
                .Select(e => (Exe: e, Score: Similarity(Path.GetFileNameWithoutExtension(e), hints)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => Engine.SizeOf(x.Exe) ?? 0)
                .FirstOrDefault();
            if (named.Exe is not null) return named.Exe;

            return exes.OrderByDescending(e => Engine.SizeOf(e) ?? 0).First();
        }

        // Every executable matched a not-the-game word. Rather than report no game at all, take the
        // largest in the root: a wrong pick here is still overridable, an empty answer is not.
        try
        {
            return Directory.EnumerateFiles(root, "*.exe")
                .Where(e => Engine.MachineOfFile(e) is not null)
                .OrderByDescending(e => Engine.SizeOf(e) ?? 0)
                .FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Unreal ships both RHIs in every Windows build, so an Unreal executable importing
    /// d3d11 and d3d12 genuinely offers both. Any other engine importing the pair is almost always
    /// using D3D11 for video or UI interop on top of a D3D12 renderer.</summary>
    internal static bool IsUnreal(string root, string exe) =>
        exe.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase)
        || Directory.Exists(Path.Combine(root, "Engine", "Binaries"))
        || Directory.Exists(Path.Combine(root, "Engine", "Content"));

    /// <summary>A Unity player keeps its data in &lt;exe name&gt;_Data beside it.</summary>
    internal static bool IsUnity(string exe) =>
        Directory.Exists(Path.Combine(Path.GetDirectoryName(exe)!, Path.GetFileNameWithoutExtension(exe) + "_Data"))
        || File.Exists(Path.Combine(Path.GetDirectoryName(exe)!, "UnityPlayer.dll"));

    private static readonly Dictionary<string, string> Roman = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ii"] = "2", ["iii"] = "3", ["iv"] = "4", ["v"] = "5", ["vi"] = "6", ["vii"] = "7", ["viii"] = "8",
        ["ix"] = "9", ["x"] = "10",
    };

    /// <summary>Names an executable is likely to carry, derived from the folder and the store name:
    /// the name itself, its initials ("God of War" -> gow), and both again with a trailing roman
    /// numeral as a digit, because games are sold as "V" and shipped as "5".</summary>
    private static List<string> Hints(string root, string? gameName)
    {
        var words = new List<string>();
        foreach (var source in new[] { Path.GetFileName(root.TrimEnd('\\', '/')), gameName })
        {
            if (string.IsNullOrWhiteSpace(source)) continue;
            var parts = source.Split([' ', '-', '_', ':', '.'], StringSplitOptions.RemoveEmptyEntries)
                .Where(w => char.IsLetterOrDigit(w[0]))
                .ToList();

            foreach (var numbered in new[] { parts, parts.Select(p => Roman.GetValueOrDefault(p, p)).ToList() })
            {
                words.Add(Normalise(string.Concat(numbered)));
                var initials = string.Concat(numbered.Select(w => char.IsDigit(w[0]) ? w : w[..1].ToLowerInvariant()));
                if (initials.Length >= 2) words.Add(Normalise(initials));
            }
        }
        return words.Where(w => w.Length >= 2).Distinct().ToList();
    }

    private static string Normalise(string s) =>
        new(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static int Similarity(string exeName, List<string> hints)
    {
        var exe = Normalise(exeName);
        if (exe.Length < 2) return 0;
        var best = 0;
        foreach (var hint in hints)
        {
            if (exe == hint) best = Math.Max(best, 100);
            else if (hint.StartsWith(exe) || exe.StartsWith(hint)) best = Math.Max(best, 60);
            else if (hint.Contains(exe) || exe.Contains(hint)) best = Math.Max(best, 40);
        }
        return best;
    }

    // -- Deciding ----------------------------------------------------------------------------------

    /// <summary>Modules that ship beside a game but render nothing of it. Their imports would put
    /// D3D11 on every game with an embedded browser or a store overlay.</summary>
    private static readonly string[] NotTheRenderer =
    [
        "steam", "cef", "chrome", "discord", "eos", "galaxy", "bink", "fmod", "wwise", "physx", "nvngx",
        "amd_", "ffx_", "libxess", "openvr", "ovr", "sl.", "nvapi", "gfsdk", "d3dcompiler", "dxcompiler",
        "dxil", "vcruntime", "msvcp", "ucrt", "api-ms-", "concrt", "qt", "icu", "libcrypto", "libssl",
        "zlib", "sdl", "xinput", "xaudio", "dinput", "d3d12core", "d3d12sdklayers", "dstorage", "tobii",
        "powrprof", "dbghelp", "crashpad", "sentry", "bugsplat", "overlay", "gameoverlay", "rtss",
        "reshade", "dlss5", "dlssnr", "dxgi", "d3d11", "d3d12", "d3d9", "d3d8", "opengl32", "vulkan",
    ];

    /// <summary>Engine modules worth opening when the executable imports no renderer itself.</summary>
    private static readonly string[] EngineModules = ["UnityPlayer.dll", "GameAssembly.dll", "engine.dll", "Engine.dll", "shaderapidx9.dll"];

    /// <summary>A Source game. The width and the API come from the renderer module, because the
    /// executable in the root is a stub that imports nothing; the executable is still what gets
    /// launched, and the renderer's folder is where the files go.</summary>
    private static GraphicsDetection ForSource(string root, string exe, string renderer)
    {
        var folder = Path.GetDirectoryName(renderer)!;
        var width = Engine.MachineOfFile(renderer) switch
        {
            Engine.MachineX86 => Route.X86,
            Engine.MachineX64 => Route.X64,
            _ => (Route?)null,
        };

        // Source ships one shaderapi per backend. The Vulkan one is DXVK, which is D3D9 underneath,
        // so every backend this engine offers reaches the network through the same D3D9 route.
        var vulkan = File.Exists(Path.Combine(folder, "shaderapivk.dll"));
        var relative = Path.GetRelativePath(root, renderer);

        return new GraphicsDetection(exe, width, GraphicsApi.D3D9, false,
            $"{Path.GetFileName(exe)} is a Source launcher; {relative} is what creates the device, and it "
            + $"imports d3d9.dll.{(vulkan ? " The Vulkan backend beside it is DXVK, which is D3D9 underneath." : "")} "
            + $"Everything goes in {Path.GetFileName(folder)}, because that is where Source loads its "
            + "modules from and where ReShade looks for add-ons.")
        {
            InstallTarget = renderer,
        };
    }

    /// <summary>Source keeps its renderer in bin\ (or bin\x64\ on the 64-bit builds) and leaves a
    /// stub in the root. shaderapidx9.dll is the module that creates the device, so its folder is
    /// where ReShade and the add-on have to sit; the root is where the launcher lives and nothing
    /// that matters ever loads from there.</summary>
    private static string? SourceRenderer(string root)
    {
        foreach (var relative in new[] { @"bin\x64\shaderapidx9.dll", @"bin\shaderapidx9.dll" })
        {
            var path = Path.Combine(root, relative);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>A detection for a folder already known to hold <paramref name="emulator"/>. The
    /// renderer list is the emulator's declared one rather than its import table, and the width
    /// still comes from the executable on this disk, because only that says which build is here.</summary>
    private static GraphicsDetection ForEmulator(string root, EmulatorInfo emulator)
    {
        var exe = Emulators.ExecutableIn(root, emulator);
        var width = exe is null
            ? Route.X64
            : Engine.MachineOfFile(exe) switch
            {
                Engine.MachineX86 => Route.X86,
                Engine.MachineX64 => Route.X64,
                _ => Route.X64,
            };

        return new GraphicsDetection(exe, width, emulator.Best, false,
            $"{Path.GetFileName(exe ?? emulator.PrimaryExecutable)} is {emulator.Name} "
            + $"({emulator.System}). Which renderer it uses is a setting inside it, not something its "
            + "files say.")
        {
            Supported = emulator.Renderers,
            Emulator = emulator,
        };
    }

    public static GraphicsDetection Detect(string root, string? gameName = null)
    {
        // 0. A known emulator, before anything else. Its executable links every renderer it can be
        //    set to, so reading the imports here answers a different question than the one asked:
        //    what decides the route is a setting inside the emulator, which no file reveals.
        if (Emulators.Identify(root) is { } emulator)
            return ForEmulator(root, emulator);

        var exe = FindExecutable(root, gameName);
        if (exe is null) return new GraphicsDetection(null, null, GraphicsApi.Unknown, false, "No executable found in that folder.");

        // A Source game answers from its renderer module, not from the launcher stub in the root.
        if (SourceRenderer(root) is { } renderer) return ForSource(root, exe, renderer);

        var width = Engine.MachineOfFile(exe) switch
        {
            Engine.MachineX86 => Route.X86,
            Engine.MachineX64 => Route.X64,
            _ => (Route?)null,
        };
        var exeName = Path.GetFileName(exe);
        var folder = Path.GetDirectoryName(exe)!;

        // 1. The executable's own imports.
        var imports = PeImports.Read(exe);
        var fromExe = Decide(imports);

        // Unity loads its renderer dynamically, so the executable shows only the OpenGL import it
        // keeps as a fallback, or nothing. Unity has rendered with D3D11 by default on Windows since
        // 5.x, which is the honest answer when nothing more specific is linked.
        if (IsUnity(exe) && fromExe is null or { Api: GraphicsApi.OpenGL })
        {
            var player = Path.Combine(folder, "UnityPlayer.dll");
            var fromPlayer = File.Exists(player) ? Decide(PeImports.Read(player)) : null;
            if (fromPlayer is { Api: not GraphicsApi.OpenGL })
                return new GraphicsDetection(exe, width, fromPlayer.Api, false,
                    $"{exeName} is a Unity player; UnityPlayer.dll imports {fromPlayer.Evidence}.");
            return new GraphicsDetection(exe, width, GraphicsApi.D3D11, false,
                $"{exeName} is a Unity player, which renders with D3D11 on Windows unless the game was built otherwise.");
        }

        if (fromExe is not null)
        {
            var both = fromExe.Both && IsUnreal(root, exe);
            var evidence = fromExe.Both && !both
                ? $"{fromExe.Evidence}; outside Unreal the D3D11 import is interop, not a second renderer"
                : fromExe.Evidence;
            return new GraphicsDetection(exe, width, fromExe.Api, both, $"{exeName} imports {evidence}.");
        }

        // 2. The engine module beside it.
        foreach (var module in EngineModules)
        {
            var path = Path.Combine(folder, module);
            if (!File.Exists(path)) continue;
            if (Decide(PeImports.Read(path)) is { } fromEngine)
                return new GraphicsDetection(exe, width, fromEngine.Api, fromEngine.Both,
                    $"{exeName} renders through {module}, which imports {fromEngine.Evidence}.");
        }

        // 3. Files that only exist for one API.
        if (Fingerprint(root, folder) is { } print)
            return new GraphicsDetection(exe, width, print.Api, print.Both, $"{exeName}: {print.Evidence}");

        // 4. Every other module in the executable's folder, minus the ones that are known not to
        //    render anything. Weakest signal, so it is last.
        try
        {
            foreach (var dll in Directory.EnumerateFiles(folder, "*.dll"))
            {
                var name = Path.GetFileName(dll).ToLowerInvariant();
                if (NotTheRenderer.Any(name.StartsWith)) continue;
                if (Decide(PeImports.Read(dll)) is { } fromModule)
                    return new GraphicsDetection(exe, width, fromModule.Api, fromModule.Both,
                        $"{exeName} loads {Path.GetFileName(dll)}, which imports {fromModule.Evidence}.");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fall through to unknown.
        }

        return new GraphicsDetection(exe, width, GraphicsApi.Unknown, false,
            $"{exeName} imports no graphics API directly, and nothing beside it names one. Pick the route by hand.");
    }

    private sealed record Decision(GraphicsApi Api, bool Both, string Evidence);

    /// <summary>What a set of imported module names says. D3D12 outranks D3D11 because a D3D12 game
    /// usually still imports D3D11 for video or UI interop, while the reverse never happens -- but the
    /// pair is reported, because several engines do genuinely offer both.</summary>
    private static Decision? Decide(IReadOnlyCollection<string> imports)
    {
        if (imports.Count == 0) return null;
        bool Has(string dll) => imports.Contains(dll, StringComparer.OrdinalIgnoreCase);

        var d3d12 = Has("d3d12.dll");
        var d3d11 = Has("d3d11.dll");
        var vulkan = Has("vulkan-1.dll");

        if (d3d12) return new Decision(GraphicsApi.D3D12, d3d11, d3d11 ? "d3d12.dll and d3d11.dll" : "d3d12.dll");
        if (d3d11) return new Decision(GraphicsApi.D3D11, false, vulkan ? "d3d11.dll (and vulkan-1.dll)" : "d3d11.dll");
        if (vulkan) return new Decision(GraphicsApi.Vulkan, false, "vulkan-1.dll");
        if (Has("d3d9.dll")) return new Decision(GraphicsApi.D3D9, false, "d3d9.dll");
        if (Has("d3d8.dll")) return new Decision(GraphicsApi.D3D8, false, "d3d8.dll");
        // dxgi alone is D3D10/11/12 without saying which; on its own it is a D3D11 game far more
        // often than not, but it is reported as such rather than dressed up as certainty.
        if (Has("dxgi.dll")) return new Decision(GraphicsApi.D3D11, false, "dxgi.dll only (D3D10/11 family)");
        if (Has("opengl32.dll")) return new Decision(GraphicsApi.OpenGL, false, "opengl32.dll");
        return null;
    }

    private static Decision? Fingerprint(string root, string exeFolder)
    {
        bool Any(params string[] relative) =>
            relative.Any(r => File.Exists(Path.Combine(exeFolder, r)) || File.Exists(Path.Combine(root, r)));

        if (Any(@"D3D12\D3D12Core.dll", "D3D12Core.dll"))
            return new Decision(GraphicsApi.D3D12, false, "ships the D3D12 Agility SDK (D3D12Core.dll).");
        if (Any("amd_fidelityfx_dx12.dll", "ffx_fsr2_api_dx12_x64.dll", "ffx_fsr3_api_dx12_x64.dll",
                "amd_fidelityfx_framegeneration_dx12.dll", "amd_fidelityfx_loader_dx12.dll"))
            return new Decision(GraphicsApi.D3D12, false, "ships the DX12 build of FidelityFX.");
        if (Any("amd_fidelityfx_vk.dll", "ffx_fsr2_api_vk_x64.dll"))
            return new Decision(GraphicsApi.Vulkan, false, "ships the Vulkan build of FidelityFX.");
        if (Any("libxess_dx11.dll"))
            return new Decision(GraphicsApi.D3D11, false, "ships the DX11 build of XeSS.");
        return null;
    }
}

/// <summary>The names in a PE image's import and delay-import tables, read by seeking rather than
/// by loading the file: a game executable can be several hundred megabytes and only a few kilobytes
/// of it matter here.</summary>
public static class PeImports
{
    public static IReadOnlyCollection<string> Read(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return Read(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstallException
                                      or ArgumentException or EndOfStreamException)
        {
            return [];
        }
    }

    internal static IReadOnlyCollection<string> Read(Stream f)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var peAt = U32(f, 0x3c);
        if (U16(f, 0) != 0x5a4d || U32(f, peAt) != 0x4550) return names;

        var sections = U16(f, peAt + 6);
        var optionalSize = U16(f, peAt + 20);
        var optional = peAt + 24;
        var magic = U16(f, optional);
        if (magic is not (0x10b or 0x20b)) return names;

        var pe32Plus = magic == 0x20b;
        ulong imageBase = pe32Plus ? U64(f, optional + 24) : U32(f, optional + 28);
        var directories = optional + (pe32Plus ? 112u : 96u);
        var directoryCount = U32(f, optional + (pe32Plus ? 108u : 92u));

        // RVA -> file offset, through the section table.
        var table = new List<(uint Va, uint Size, uint Raw, uint RawSize)>();
        var sectionTable = optional + optionalSize;
        for (uint i = 0; i < Math.Min(sections, (ushort)96); i++)
        {
            var s = sectionTable + i * 40;
            table.Add((U32(f, s + 12), U32(f, s + 8), U32(f, s + 20), U32(f, s + 16)));
        }

        long? Offset(ulong rva)
        {
            foreach (var (va, size, raw, rawSize) in table)
            {
                var span = Math.Max(size, rawSize);
                if (rva >= va && rva < (ulong)va + span) return (long)(rva - va + raw);
            }
            return null;
        }

        // Ordinary imports: 20-byte descriptors, name RVA at +12, ended by a zeroed one.
        if (directoryCount > 1 && Offset(U32(f, directories + 8)) is { } imports)
        {
            for (var i = 0; i < 512; i++)
            {
                var d = imports + i * 20;
                var nameRva = U32(f, d + 12);
                if (nameRva == 0 && U32(f, d) == 0 && U32(f, d + 16) == 0) break;
                if (Offset(nameRva) is { } at && Name(f, at) is { } name) names.Add(name);
            }
        }

        // Delay-loaded imports: 32-byte descriptors, name at +4. The oldest linkers wrote VAs here
        // instead of RVAs, which bit 0 of Attributes distinguishes.
        if (directoryCount > 13 && Offset(U32(f, directories + 13 * 8)) is { } delayed)
        {
            for (var i = 0; i < 512; i++)
            {
                var d = delayed + i * 32;
                var attributes = U32(f, d);
                ulong nameRef = U32(f, d + 4);
                if (nameRef == 0) break;
                var rva = (attributes & 1) == 0 && nameRef >= imageBase ? nameRef - imageBase : nameRef;
                if (Offset(rva) is { } at && Name(f, at) is { } name) names.Add(name);
            }
        }

        return names;
    }

    private static string? Name(Stream f, long at)
    {
        if (at < 0 || at >= f.Length) return null;
        f.Position = at;
        Span<byte> buffer = stackalloc byte[128];
        var read = f.Read(buffer);
        var end = buffer[..read].IndexOf((byte)0);
        if (end <= 0) return null;
        foreach (var b in buffer[..end])
            if (b is < 0x20 or > 0x7e) return null;
        return System.Text.Encoding.ASCII.GetString(buffer[..end]);
    }

    private static ushort U16(Stream f, long at)
    {
        Span<byte> b = stackalloc byte[2];
        Fill(f, at, b);
        return (ushort)(b[0] | b[1] << 8);
    }

    private static uint U32(Stream f, long at)
    {
        Span<byte> b = stackalloc byte[4];
        Fill(f, at, b);
        return (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
    }

    private static ulong U64(Stream f, long at) => U32(f, at) | (ulong)U32(f, at + 4) << 32;

    private static void Fill(Stream f, long at, Span<byte> into)
    {
        if (at < 0 || at + into.Length > f.Length) throw new EndOfStreamException();
        f.Position = at;
        f.ReadExactly(into);
    }
}
