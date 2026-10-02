// Games that need something their files do not say: the API they really render with, or a file of
// ours that the game itself would take away. Each one was found in a real install, and is matched
// by the executable's name, and by a file beside it where the name alone is too common.

namespace AmdNr.Core;

/// <param name="Beside">A file that has to sit next to the executable as well, or null.</param>
/// <param name="Api">What the game renders with, whatever its import table says.</param>
/// <param name="KeepProxy">The game deletes a DLL of ReShade's name from its folder when it starts,
/// so the proxy is written read-only, which makes the deletion fail and the game carry on.</param>
public sealed record GameQuirk(string Name, string Executable, string? Beside, GraphicsApi Api, bool KeepProxy, string Why);

public static class GameQuirks
{
    public static readonly GameQuirk[] Known =
    [
        new("Just Cause 2", "JustCause2.exe", null, GraphicsApi.D3D10, false,
            "JustCause2.exe imports only d3d9.dll and loads d3d10_1.dll itself: Just Cause 2 renders "
            + "with D3D10 and nothing else."),
        // Half-Life, Opposing Force, Blue Shift and the rest of GoldSrc share hl.exe. hw.dll is the
        // OpenGL renderer; a folder with only sw.dll is the software one, which has nothing to reach.
        new("GoldSrc (Half-Life and its expansions)", "hl.exe", "hw.dll", GraphicsApi.OpenGL, true,
            "hl.exe renders with OpenGL through hw.dll, and deletes opengl32.dll from its folder every "
            + "time it starts, an old guard against cheats sold under that name."),
    ];

    /// <summary>The quirk for this executable, or null.</summary>
    public static GameQuirk? For(string? exe)
    {
        if (exe is null || !File.Exists(exe)) return null;
        var name = Path.GetFileName(exe);
        var dir = Path.GetDirectoryName(exe) ?? "";
        return Known.FirstOrDefault(q => string.Equals(q.Executable, name, StringComparison.OrdinalIgnoreCase)
                                         && (q.Beside is null || File.Exists(Path.Combine(dir, q.Beside))));
    }

    /// <summary>Marks the proxy just installed read-only where the game would delete it. The line to
    /// say, or null when this game needs nothing. Install and uninstall clear the flag before they
    /// write or remove (Engine.Writable).</summary>
    public static string? Seal(string exe, string dir, string? proxy)
    {
        if (For(exe) is not { KeepProxy: true } quirk || proxy is null) return null;
        var path = Path.Combine(dir, proxy);
        if (!File.Exists(path)) return null;
        try
        {
            File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.ReadOnly);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return $"{proxy} could not be made read-only ({e.Message}); {quirk.Name} will delete it when it starts.";
        }
        return $"{proxy} is read-only: {quirk.Name} deletes a file of that name from its folder when it starts, "
               + "and cannot while it is read-only.";
    }
}
