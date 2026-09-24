// Ported from installer/src/work.rs. The prose here is the product: these are the sentences the
// screen shows, and they are the difference between "it does nothing" and "it is on D3D12, which
// is the degraded case".

using System.Text;

namespace AmdNr.Core;

public enum Preset
{
    Pcsx2,
    Rpcs3,
    Dx11,
    Dx12,
    Vulkan,
    OpenGL,
    X86Dx11,
    X86Dx9,
    X86Dx8,
    // Last, so every games.json written before it still reads the same names.
    OptiScaler,
}

/// <summary>The two ways into a game. They are alternatives rather than layers: both write the
/// 64-bit manifest, and Transaction.Apply refuses a second preset in a folder that already has one.
/// ReShade is every preset but one -- the API, the width and the emulators are choices inside it.</summary>
public enum RouteFamily
{
    ReShade,
    OptiScaler,
}

public static class Presets
{
    public static readonly Preset[] All =
    [
        Preset.Pcsx2, Preset.Rpcs3, Preset.Dx11, Preset.OptiScaler, Preset.Dx12, Preset.Vulkan, Preset.OpenGL,
        Preset.X86Dx11, Preset.X86Dx9, Preset.X86Dx8,
    ];

    private static readonly Preset[] X64 =
        [Preset.Pcsx2, Preset.Rpcs3, Preset.Dx11, Preset.OptiScaler, Preset.Dx12, Preset.Vulkan, Preset.OpenGL];

    /// <summary>The route that installs OptiScaler instead of ReShade and the add-on. It runs the same
    /// network inside the game's own upscaler call, where depth and motion vectors are handed over,
    /// so on D3D12 it replaces a route that sees only the finished frame.</summary>
    public static bool IsOptiScaler(this Preset p) => p == Preset.OptiScaler;

    public static RouteFamily Family(this Preset p) => p.IsOptiScaler() ? RouteFamily.OptiScaler : RouteFamily.ReShade;

    private static readonly Preset[] X86 = [Preset.X86Dx11, Preset.X86Dx9, Preset.X86Dx8];

    public static Route Route(this Preset p) =>
        p is Preset.X86Dx11 or Preset.X86Dx9 or Preset.X86Dx8 ? AmdNr.Core.Route.X86 : AmdNr.Core.Route.X64;

    /// <summary>What the target row offers: the routes the detected width says can work, first, and
    /// then every other one.
    ///
    /// It used to return only the first group. That reads as helpful and is not, because when the
    /// width is wrong it is wrong about *which executable is the game* — BeamNG.drive keeps a
    /// 32-bit launcher in the root and the real game in Bin64, and a folder detected as 32-bit then
    /// offered the three 32-bit routes and nothing else, with no way back. A detection this app
    /// makes has to be correctable by the person looking at it; the ordering is the recommendation,
    /// and the rest of the list is the way out when the recommendation is wrong.</summary>
    public static IReadOnlyList<Preset> Offered(Detected detected)
    {
        IReadOnlyList<Preset> first = detected.Route switch
        {
            AmdNr.Core.Route.X64 => X64,
            AmdNr.Core.Route.X86 => X86,
            _ => All,
        };
        return [.. first, .. All.Where(p => !first.Contains(p))];
    }

    /// <summary>Whether this route matches what the detection read off the executable. False is not
    /// a refusal -- it is the line that has to be said out loud next to the choice.</summary>
    public static bool MatchesDetected(this Preset p, Detected detected) =>
        detected.Route is null || p.Route() == detected.Route;

    /// <summary>The string recorded in the install manifest. Kept separate from <see cref="Label"/>,
    /// which is prose that can be reworded, while this one has to keep matching manifests already
    /// on disk.</summary>
    public static string ManifestPreset(this Preset p) => p switch
    {
        Preset.Pcsx2 => "PCSX2",
        Preset.Rpcs3 => "RPCS3",
        Preset.Dx11 => "D3D11",
        Preset.Dx12 => "D3D12",
        Preset.Vulkan => "Vulkan",
        Preset.OpenGL => "OpenGL",
        Preset.X86Dx11 => "D3D11",
        Preset.X86Dx9 => "D3D9",
        Preset.X86Dx8 => "D3D8",
        Preset.OptiScaler => "OptiScaler",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    public static string Label(this Preset p) => p switch
    {
        Preset.OptiScaler => "OptiScaler: D3D12 game with DLSS, FSR or XeSS",
        Preset.Pcsx2 => "PCSX2",
        Preset.Rpcs3 => "RPCS3",
        Preset.Dx11 => "D3D11 game",
        Preset.Dx12 => "D3D12 game",
        Preset.Vulkan => "Vulkan game",
        Preset.OpenGL => "OpenGL game",
        Preset.X86Dx11 => "D3D11 game, 32-bit",
        Preset.X86Dx9 => "D3D9 game, 32-bit",
        Preset.X86Dx8 => "D3D8 game, 32-bit",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };

    /// <summary>Vulkan is not a proxy DLL: ReShade loads as a global layer and the add-on sits
    /// beside the executable all the same.</summary>
    public static bool IsVulkan(this Preset p) => p is Preset.Rpcs3 or Preset.Vulkan;

    /// <summary>"Game" is wrong for an emulator, and the people most likely to get the folder wrong
    /// are exactly the emulator users -- the files go beside the emulator, not beside the ROM.</summary>
    public static string FolderLabel(this Preset p) =>
        p is Preset.Pcsx2 or Preset.Rpcs3 ? "Emulator folder or executable" : "Game folder or executable";

    /// <summary>An executable whose presence says the folder is the right one. Absent means "warn",
    /// never "refuse": there is no whitelist anywhere in this project and there is not going to be
    /// one here either.</summary>
    public static string? ExpectedExe(this Preset p) =>
        Emulators.Known.FirstOrDefault(e => e.Route == p)?.PrimaryExecutable;

    public static string Note(this Preset p) => p switch
    {
        Preset.Pcsx2 =>
            "Set the renderer to Direct3D 11 -- it is the one where the network gets the most: the "
            + "emulator's depth, and motion from the companion effect when it is installed. Watch for a "
            + "per-game override: it beats the global setting silently, and it is the most common way "
            + "this looks broken when it is not.",
        Preset.Rpcs3 =>
            "EXPERIMENTAL. ReShade on Vulkan is a global layer, not a proxy DLL: run the ReShade "
            + "installer against rpcs3.exe and pick Vulkan, or nothing will load. The network gets "
            + "colour and estimated motion only -- there is no depth on Vulkan.",
        Preset.Dx11 =>
            "The best case. D3D11 is the only route where the game's own motion vectors reach the "
            + "network, together with its depth.",
        Preset.Dx12 =>
            "The degraded case. On D3D12 the add-on finds the game's depth but not its motion "
            + "vectors, so the network gets colour and depth and estimates the motion. It works; "
            + "expect less from it. A game with DLSS, FSR or XeSS in its settings is better served by "
            + "the OptiScaler route.",
        Preset.OptiScaler =>
            "The route for D3D12 games. Instead of ReShade this installs OptiScaler, which takes over "
            + "the game's DLSS, FSR or XeSS call and runs the network inside it, with the game's own "
            + "depth and motion vectors. The game has to offer one of those upscalers, and it has to be "
            + "switched on in its settings. In game, open OptiScaler with Insert, go to the Neural tab "
            + "and turn on Enable NR; a game that uses Ray Reconstruction needs \"After the finished "
            + "frame\" as the processing point.",
        Preset.Vulkan =>
            "EXPERIMENTAL. ReShade on Vulkan is a global layer, not a proxy DLL: run its installer "
            + "against the game's own .exe and pick Vulkan, or nothing loads. The game also has to "
            + "import vkCreateDevice statically -- one that resolves Vulkan through "
            + "vkGetInstanceProcAddr cannot be hooked, and the add-on stands down rather than guess. "
            + "No depth on Vulkan either way: colour and estimated motion.",
        Preset.OpenGL =>
            "EXPERIMENTAL. Unlike Vulkan this one is an ordinary proxy DLL: ReShade goes in as "
            + "opengl32.dll beside the game, no separate installer run. The network gets colour "
            + "and estimated motion; the game's own depth is reachable on this API but is not "
            + "wired up yet. A 32-bit OpenGL game has no route at all -- the 32-bit pair covers "
            + "D3D8, D3D9 and D3D11 only.",
        Preset.X86Dx11 =>
            "EXPERIMENTAL. A 32-bit game cannot load the 64-bit runtime, so the add-on runs as a pair: "
            + "a 32-bit frontend inside the game and a 64-bit helper beside it, sharing frames on the "
            + "same adapter. Install ReShade with full add-on support as the 32-bit dxgi.dll.",
        Preset.X86Dx9 =>
            "EXPERIMENTAL. The same 32-bit pair as D3D11, reached through a private D3D9/D3D11 stage. "
            + "D3D9Ex shares GPU textures; plain D3D9 falls back to a CPU round trip that costs a fixed "
            + "few milliseconds every frame, no matter how far the scale is turned down. Install "
            + "ReShade as the 32-bit d3d9.dll.",
        Preset.X86Dx8 =>
            "EXPERIMENTAL. D3D8 is translated to D3D9 by the pinned d3d8to9 build and then takes the "
            + "D3D9 route above; there is no second renderer here. A game that already ships its own "
            + "d3d8.dll wrapper keeps it, and the translator is installed beside it as d3d8R.dll. "
            + "Install ReShade as the 32-bit d3d9.dll.",
        _ => throw new ArgumentOutOfRangeException(nameof(p)),
    };
}

public enum Level
{
    Ok,
    Warn,
    Err,
    Info,
}

public sealed class Report
{
    public List<(Level Level, string Text)> Lines { get; } = [];
    public bool Failed { get; private set; }

    public void Ok(string s) => Lines.Add((Level.Ok, s));
    public void Info(string s) => Lines.Add((Level.Info, s));
    public void Warn(string s) => Lines.Add((Level.Warn, s));

    public void Err(string s)
    {
        Lines.Add((Level.Err, s));
        Failed = true;
    }

    /// <summary>The report as a file, for the user to hand over when something went wrong.
    /// Everything the installer saw is in here; there is nothing it knows that this does not say.</summary>
    public string ToLog(string header)
    {
        var o = new StringBuilder();
        o.AppendLine("AMD-NR ReShade Installer log");
        o.AppendLine(header);
        o.AppendLine(new string('-', 70));
        foreach (var (level, line) in Lines)
        {
            var tag = level switch
            {
                Level.Ok => "ok  ",
                Level.Warn => "warn",
                Level.Err => "ERR ",
                _ => "    ",
            };
            o.AppendLine($"{tag} {line}");
        }
        return o.ToString();
    }
}
