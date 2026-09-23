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

public static partial class GraphicsDetector
{
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
        "reshade", "amd-nr", "dlss5", "dlssnr", "dxgi", "d3d11", "d3d12", "d3d9", "d3d8", "opengl32", "vulkan",
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

    /// <param name="executable">The executable to read, when somebody has pointed at one. Detection
    /// picks the game's binary out of a folder and is occasionally wrong about which file that is --
    /// and everything downstream, the width above all, is read off whichever file it picked. This is
    /// how that is corrected without asking anyone to believe the guess.</param>
    public static GraphicsDetection Detect(string root, string? gameName = null, string? executable = null)
    {
        var detected = DetectRenderer(root, gameName, executable);
        if (detected.Emulator is not null || detected.Target is not { } target) return detected;
        var folder = Directory.Exists(target) ? target : Path.GetDirectoryName(target);
        return folder is null ? detected : detected with { Upscalers = Upscalers(folder, root) };
    }

    /// <summary>What a game ships for DLSS, FSR or XeSS to be switched on: beside its executable, in
    /// its root, and for Unreal in the Plugins folders, where those plugins keep their binaries under
    /// Binaries\ThirdParty rather than beside the game.</summary>
    public static IReadOnlyList<string> Upscalers(string folder, string? root = null)
    {
        var found = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var dir in new[] { folder, root }.OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase))
            foreach (var name in Work.UpscalerFiles)
                if (File.Exists(Path.Combine(dir, name))) found.Add(name);

        // One walk per plugin tree, not one per name: this runs for every game when the app opens.
        var names = new HashSet<string>(Work.UpscalerFiles, StringComparer.OrdinalIgnoreCase);
        foreach (var plugins in PluginFolders(folder, root))
        {
            try
            {
                foreach (var dll in Directory.EnumerateFiles(plugins, "*.dll", SearchOption.AllDirectories))
                    if (names.Contains(Path.GetFileName(dll))) found.Add(Path.GetFileName(dll).ToLowerInvariant());
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A plugin tree that cannot be read says nothing either way.
            }
        }
        return [.. found];
    }

    /// <summary>Unreal's plugin folders for an executable in <c>Project\Binaries\Win64</c>: the
    /// project's own and the engine's.</summary>
    private static IEnumerable<string> PluginFolders(string folder, string? root)
    {
        var win64 = new DirectoryInfo(folder);
        if (!win64.Name.Equals("Win64", StringComparison.OrdinalIgnoreCase)
            || win64.Parent is not { Name: var binaries } bin
            || !binaries.Equals("Binaries", StringComparison.OrdinalIgnoreCase)
            || bin.Parent is not { } project)
            yield break;

        var candidates = new List<string> { Path.Combine(project.FullName, "Plugins") };
        if (project.Parent is { } gameRoot) candidates.Add(Path.Combine(gameRoot.FullName, "Engine", "Plugins"));
        if (root is not null) candidates.Add(Path.Combine(root, "Engine", "Plugins"));
        foreach (var dir in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
            if (Directory.Exists(dir)) yield return dir;
    }

    private static GraphicsDetection DetectRenderer(string root, string? gameName, string? executable)
    {
        // 0. A known emulator, before anything else. Its executable links every renderer it can be
        //    set to, so reading the imports here answers a different question than the one asked:
        //    what decides the route is a setting inside the emulator, which no file reveals.
        if (Emulators.Identify(root) is { } emulator)
            return ForEmulator(root, emulator);

        var chosen = executable is { Length: > 0 } && File.Exists(executable)
                     && Engine.MachineOfFile(executable) is not null
            ? executable
            : null;
        var exe = chosen ?? FindExecutable(root, gameName);
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
