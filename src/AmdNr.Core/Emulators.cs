// Which emulator a folder holds, and what to tell someone once it is known.
//
// This exists because an emulator is the one case where reading the executable is not enough. An
// emulator links every renderer it can offer -- PCSX2's executable names D3D11, D3D12, Vulkan and
// OpenGL all at once -- so the import table says "all of them" and the route picked from it is a
// coin toss. Worse, several of them load their renderer dynamically and the table says nothing.
//
// What actually decides it is a setting inside the emulator, which no file on disk reveals. So the
// answer here is a name, a route, and the sentence that says which setting to change. The API list
// is a fallback for when the imports are inconclusive; where the executable does say something, the
// executable wins, because only the copy on this disk knows which build it is.

namespace AmdNr.Core;

/// <summary>One known emulator: how to recognise it, where it should be pointed, and what the
/// person has to change inside it.</summary>
public sealed record EmulatorInfo(
    string Id,
    string Name,
    string System,
    string[] Executables,
    GraphicsApi[] Renderers,
    GraphicsApi Best,
    string Setting)
{
    /// <summary>An explicit route, for the two the engine has its own preset for. Null means the
    /// route follows <see cref="Best"/> like any other program.</summary>
    public Preset? Route { get; init; }

    public string PrimaryExecutable => Executables[0];
}

public static class Emulators
{
    /// <summary>Best renderer first in each list, in this project's order of preference: D3D11 is
    /// the only route where depth and motion reach the network, so an emulator that offers it is
    /// pointed there even when it defaults to something else.
    ///
    /// Renderers are what the emulator can be set to, not what it is set to. Nothing on disk says
    /// which is selected -- that is the whole reason <see cref="EmulatorInfo.Setting"/> exists.</summary>
    public static readonly EmulatorInfo[] Known =
    [
        new("pcsx2", "PCSX2", "PlayStation 2",
            ["pcsx2-qt.exe", "pcsx2-qtx64-avx2.exe", "pcsx2x64-avx2.exe", "pcsx2x64.exe", "pcsx2.exe"],
            [GraphicsApi.D3D11, GraphicsApi.D3D12, GraphicsApi.Vulkan, GraphicsApi.OpenGL],
            GraphicsApi.D3D11,
            "Settings > Graphics > Renderer: Direct3D 11. Watch for a per-game override in the game "
            + "properties -- it beats the global setting silently, and it is the most common way this "
            + "looks broken when it is not.")
        { Route = Preset.Pcsx2 },

        new("rpcs3", "RPCS3", "PlayStation 3",
            ["rpcs3.exe"],
            [GraphicsApi.Vulkan, GraphicsApi.OpenGL],
            GraphicsApi.Vulkan,
            "Settings > GPU > Renderer: Vulkan. There is no Direct3D here at all, so this route gets "
            + "colour and estimated motion only -- no depth.")
        { Route = Preset.Rpcs3 },

        new("duckstation", "DuckStation", "PlayStation 1",
            ["duckstation-qt-x64-ReleaseLTCG.exe", "duckstation-qt.exe", "duckstation.exe"],
            [GraphicsApi.D3D11, GraphicsApi.D3D12, GraphicsApi.Vulkan, GraphicsApi.OpenGL],
            GraphicsApi.D3D11,
            "Settings > Graphics > Renderer: Direct3D 11."),

        new("dolphin", "Dolphin", "GameCube and Wii",
            ["Dolphin.exe", "DolphinQt.exe"],
            [GraphicsApi.D3D11, GraphicsApi.D3D12, GraphicsApi.Vulkan, GraphicsApi.OpenGL],
            GraphicsApi.D3D11,
            "Graphics > General > Backend: Direct3D 11."),

        new("ppsspp", "PPSSPP", "PSP",
            ["PPSSPPWindows64.exe", "PPSSPPWindows.exe"],
            [GraphicsApi.D3D11, GraphicsApi.Vulkan, GraphicsApi.OpenGL],
            GraphicsApi.D3D11,
            "Settings > Graphics > Backend: Direct3D 11."),

        new("flycast", "Flycast", "Dreamcast",
            ["flycast.exe"],
            [GraphicsApi.D3D11, GraphicsApi.Vulkan, GraphicsApi.OpenGL],
            GraphicsApi.D3D11,
            "Settings > Graphics > Graphics API: DirectX 11."),

        new("xenia", "Xenia", "Xbox 360",
            ["xenia_canary.exe", "xenia.exe"],
            [GraphicsApi.D3D12, GraphicsApi.Vulkan],
            GraphicsApi.D3D12,
            "It runs on D3D12, which is the degraded route: the add-on is shown the finished frame "
            + "and nothing else. It works; expect less from it."),

        new("cemu", "Cemu", "Wii U",
            ["Cemu.exe"],
            [GraphicsApi.Vulkan, GraphicsApi.OpenGL],
            GraphicsApi.Vulkan,
            "Options > General settings > Graphics > Graphics API: Vulkan. No depth on Vulkan: "
            + "colour and estimated motion only."),

        new("ryujinx", "Ryujinx", "Switch",
            ["Ryujinx.exe", "Ryujinx.Ava.exe"],
            [GraphicsApi.Vulkan, GraphicsApi.OpenGL],
            GraphicsApi.Vulkan,
            "Options > Settings > Graphics > Graphics Backend: Vulkan. No depth on Vulkan."),

        new("vita3k", "Vita3K", "PS Vita",
            ["Vita3K.exe"],
            [GraphicsApi.Vulkan, GraphicsApi.OpenGL],
            GraphicsApi.Vulkan,
            "Vulkan is the default backend. No depth on Vulkan."),

        new("shadps4", "shadPS4", "PlayStation 4",
            ["shadPS4.exe"],
            [GraphicsApi.Vulkan],
            GraphicsApi.Vulkan,
            "Vulkan only. No depth on Vulkan, and this emulator is itself early -- expect trouble."),

        new("azahar", "Azahar", "3DS",
            ["azahar.exe", "citra-qt.exe", "citra.exe"],
            [GraphicsApi.OpenGL, GraphicsApi.Vulkan],
            GraphicsApi.Vulkan,
            "Emulation > Configure > Graphics > Graphics API: Vulkan. OpenGL now has a route too, "
            + "but Vulkan is the one with the miles on it here."),

        new("xemu", "xemu", "Xbox",
            ["xemu.exe"],
            [GraphicsApi.OpenGL],
            GraphicsApi.OpenGL,
            "xemu renders with OpenGL and nothing else, so the OpenGL route is the only one there "
            + "is here. It is the newest of the routes and it is experimental: colour and "
            + "estimated motion, no depth."),

        new("melonds", "melonDS", "Nintendo DS",
            ["melonDS.exe"],
            [GraphicsApi.OpenGL],
            GraphicsApi.OpenGL,
            "Emu settings > Video > 3D renderer: OpenGL. The software renderer draws on the CPU "
            + "and there is nothing for an add-on to reach there, so it has to be the OpenGL one "
            + "-- and that route is experimental: colour and estimated motion, no depth."),
    ];

    /// <summary>The emulator a folder holds, by the executable in it. Case-insensitive, because the
    /// name on disk is whatever the person's unzip produced.
    ///
    /// The first match in <see cref="Known"/> order wins, and each emulator's own list is ordered
    /// most-specific first, so a folder holding both pcsx2-qt.exe and a stub pcsx2.exe picks the
    /// one that actually runs.</summary>
    public static EmulatorInfo? Identify(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;

        HashSet<string> present;
        try
        {
            if (!Directory.Exists(folder)) return null;
            present = new HashSet<string>(
                Directory.EnumerateFiles(folder, "*.exe").Select(Path.GetFileName)!,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        return Known.FirstOrDefault(emulator => emulator.Executables.Any(present.Contains));
    }

    /// <summary>The executable of a known emulator inside a folder, or null. Used to point the
    /// engine at the emulator rather than at whatever else is in there -- a launcher, an updater.</summary>
    public static string? ExecutableIn(string folder, EmulatorInfo emulator)
    {
        foreach (var name in emulator.Executables)
        {
            var path = Path.Combine(folder, name);
            if (File.Exists(path)) return path;
        }
        return null;
    }

    public static EmulatorInfo? ById(string id) =>
        Known.FirstOrDefault(e => string.Equals(e.Id, id, StringComparison.OrdinalIgnoreCase));
}
