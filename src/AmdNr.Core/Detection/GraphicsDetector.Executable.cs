// Which file in a game's folder is the game: the one the width and the API are read off, and the one
// Play starts. Launchers, crash reporters and installers sit beside it and must not win.

namespace AmdNr.Core;

public static partial class GraphicsDetector
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
        // Ours: the 64-bit host the bridge route puts beside a 32-bit game.
        "amd-nr",
    ];

    internal static bool LooksLikeTheGame(string exe)
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
        // bin\win_x64 is SCS (Euro Truck Simulator 2, American Truck Simulator); game\bin\win64 is
        // Source 2 (Counter-Strike 2, Dota 2).
        foreach (var sub in new[] { "bin", @"bin\x64", @"bin\win64", @"bin\win_x64", @"game\bin\win64", "x64",
                                    "Bin64", "Game", "bin_x64", "Binaries" })
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

        var hints = Hints(root, gameName);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var folder in CandidateFolders(root))
        {
            if (!seen.Add(folder)) continue;
            if (PickIn(folder, hints) is not { } exe) continue;

            // A 32-bit executable beside a folder holding the same game built 64-bit is a launcher,
            // not the game. BeamNG.drive ships exactly that -- BeamNG.drive.exe in the root starts
            // Bin64\BeamNG.drive.x64.exe -- and the root is searched first, so it was detected as a
            // 32-bit game. That is not a cosmetic mistake: the width then filtered the route list
            // down to the three 32-bit routes, none of which can work, with no way to pick another.
            if (Engine.MachineOfFile(exe) == Engine.MachineX86 && SixtyFourTwin(root, hints) is { } real)
                return real;
            return exe;
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

    /// <summary>The likeliest game executable in one folder, or null when it holds none. An Unreal
    /// shipping binary wins outright; otherwise the one whose name looks like the folder or the
    /// game, and failing that the largest.</summary>
    private static string? PickIn(string folder, List<string> hints)
    {
        if (!Directory.Exists(folder)) return null;

        List<string> exes;
        try
        {
            exes = Directory.EnumerateFiles(folder, "*.exe")
                .Where(LooksLikeTheGame)
                .Where(e => Engine.MachineOfFile(e) is not null)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        if (exes.Count == 0) return null;

        var shipping = exes.FirstOrDefault(e =>
            e.EndsWith("-Win64-Shipping.exe", StringComparison.OrdinalIgnoreCase)
            || e.EndsWith("-WinGDK-Shipping.exe", StringComparison.OrdinalIgnoreCase));
        if (shipping is not null) return shipping;

        var named = exes
            .Select(e => (Exe: e, Score: Similarity(Path.GetFileNameWithoutExtension(e), hints)))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenByDescending(x => Engine.SizeOf(x.Exe) ?? 0)
            .FirstOrDefault();
        if (named.Exe is not null) return named.Exe;

        return exes.OrderByDescending(e => Engine.SizeOf(e) ?? 0).First();
    }

    /// <summary>Folders a game keeps its 64-bit build in when the root holds only a launcher.</summary>
    private static readonly string[] WideFolders =
        ["Bin64", @"bin\x64", @"bin\win64", @"bin\win_x64", @"game\bin\win64", "x64", "bin_x64", @"Binaries\Win64"];

    /// <summary>The 64-bit build of the same game, in one of the folders that only ever hold one.
    /// The name still has to look like the game: a crash handler in x64\ is not the game, and taking
    /// it would trade one wrong executable for another.</summary>
    private static string? SixtyFourTwin(string root, List<string> hints)
    {
        foreach (var sub in WideFolders)
        {
            if (PickIn(Path.Combine(root, sub), hints) is not { } found) continue;
            if (Engine.MachineOfFile(found) == Engine.MachineX64
                && Similarity(Path.GetFileNameWithoutExtension(found), hints) > 0)
                return found;
        }
        return null;
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

    /// <summary>Whether an executable is plainly named after a folder -- GTAIV.exe and "Grand Theft
    /// Auto IV" -- by a whole name or its initials of four letters or more, starting the same way.
    /// Two-letter initials are left out: "Electronic Arts" would claim anything starting "ea".</summary>
    internal static bool NamedLike(string folder, string exe) =>
        Similarity(Path.GetFileNameWithoutExtension(exe), Hints(folder, null).Where(h => h.Length >= 4).ToList()) >= 60;

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
}
