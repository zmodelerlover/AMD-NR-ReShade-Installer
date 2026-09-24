// Finding the games that are already installed.
//
// Same sources and same filters as the manager this follows -- Steam, Epic, GOG, EA, Ubisoft,
// Battle.net and Xbox -- written here rather than taken from it, because that project is GPL-3.0
// and this one is MIT. Registry keys and folder layouts are facts about Windows, not code.
//
// Nothing here decides anything: a scan produces folders with names on them, and the install
// engine still reads the PE header and the pre-flight still runs. A wrong guess costs a row in a
// list, never a write.

using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace AmdNr.Core;

public enum GamePlatform
{
    Manual,
    Steam,
    Epic,
    Gog,
    Ea,
    Ubisoft,
    BattleNet,
    Xbox,
}

public sealed record ScannedGame(string Name, string InstallPath, GamePlatform Platform, string? AppId = null);

public static partial class GameScanner
{
    /// <summary>Folders that live in a library but are not games. The Proton and Steam Linux
    /// Runtime entries are here because a Steam library on a shared drive carries them even when
    /// the games are played on Windows.</summary>
    private static readonly string[] Excluded =
    [
        "wallpaper_engine", "Steamworks Shared", "GameSave", "SteamVR", "SteamLinuxRuntime",
        "Proton ", "Proton Hotfix", "Proton EasyAntiCheat Runtime", "Proton BattlEye Runtime",
    ];

    private static bool IsExcluded(string path) =>
        Excluded.Any(e => path.Contains(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>Every source, in parallel, deduplicated by folder. One failing source cannot stop
    /// the others -- a launcher that is not installed simply has nothing to read.</summary>
    public static IReadOnlyList<ScannedGame> ScanAll()
    {
        var sources = new Func<IEnumerable<ScannedGame>>[]
        {
            () => Steam(), Epic, Gog, Ea, Ubisoft, BattleNet, Xbox,
        };

        var found = new List<ScannedGame>();
        Parallel.ForEach(sources, source =>
        {
            List<ScannedGame> games;
            // Anything at all: one launcher's registry key, manifest or library file being in a
            // shape this does not expect must cost that launcher's games, never the whole scan --
            // and never the window, because the only caller is an async void click handler.
            try { games = source().ToList(); }
            catch (Exception)
            {
                return;
            }
            lock (found) found.AddRange(games);
        });

        return found
            .Where(g => !IsExcluded(g.InstallPath) && HasContent(g.InstallPath))
            .GroupBy(g => Engine.WeaklyCanonical(g.InstallPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    // -- Steam -----------------------------------------------------------------------------------

    /// <summary>Valve's KeyValues text format, as much of it as this needs: every "key" "value"
    /// pair. libraryfolders.vdf and appmanifest_*.acf are both this shape, and the three fields
    /// wanted here -- path, appid, name, installdir -- are all leaves.</summary>
    [GeneratedRegex("\"([^\"]+)\"\\s+\"([^\"]*)\"", RegexOptions.None, 2000)]
    private static partial Regex KeyValues();

    internal static List<KeyValuePair<string, string>> ParseKeyValues(string text) =>
        KeyValues().Matches(text)
            .Select(m => new KeyValuePair<string, string>(m.Groups[1].Value, m.Groups[2].Value))
            .ToList();

    /// <summary>Steam's own install path. The 32-bit registry view first, because Steam is a
    /// 32-bit application and that is where it writes.</summary>
    public static string? SteamPath()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32);
            if (machine.OpenSubKey(@"SOFTWARE\Valve\Steam")?.GetValue("InstallPath") is string path
                && Directory.Exists(path)) return path;

            using var user = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Default);
            if (user.OpenSubKey(@"Software\Valve\Steam")?.GetValue("SteamPath") is string userPath
                && Directory.Exists(userPath)) return userPath.Replace('/', '\\');
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // No Steam, or no permission to ask: the other sources still run.
        }
        return null;
    }

    public static IEnumerable<ScannedGame> Steam(string? steamPath = null)
    {
        steamPath ??= SteamPath();
        if (steamPath is null) yield break;

        foreach (var library in SteamLibraries(steamPath))
        {
            var steamapps = Path.Combine(library, "steamapps");
            string[] manifests;
            try { manifests = Directory.GetFiles(steamapps, "appmanifest_*.acf"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var manifest in manifests)
            {
                ScannedGame? game = null;
                try
                {
                    var fields = ParseKeyValues(File.ReadAllText(manifest));
                    var installDir = Value(fields, "installdir");
                    var name = Value(fields, "name") ?? installDir;
                    if (installDir is null || name is null) continue;

                    var path = Path.Combine(steamapps, "common", installDir);
                    if (Directory.Exists(path))
                        game = new ScannedGame(name, path, GamePlatform.Steam, Value(fields, "appid"));
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // A manifest being written while we read it is not worth failing the scan.
                }
                if (game is not null) yield return game;
            }
        }

        static string? Value(List<KeyValuePair<string, string>> fields, string key) =>
            fields.FirstOrDefault(f => string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)).Value is
                { Length: > 0 } v
                ? v
                : null;
    }

    /// <summary>Steam's own folder plus every library in libraryfolders.vdf. In the current format
    /// each library is an object with a "path"; the older one listed them as numbered values, and
    /// reading every "path" leaf covers both.</summary>
    internal static List<string> SteamLibraries(string steamPath)
    {
        var libraries = new List<string> { steamPath };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        try
        {
            if (File.Exists(vdf))
            {
                libraries.AddRange(ParseKeyValues(File.ReadAllText(vdf))
                    .Where(f => f.Key.Equals("path", StringComparison.OrdinalIgnoreCase))
                    .Select(f => f.Value.Replace(@"\\", @"\")));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Fall back to the main folder, which is the common case anyway.
        }
        return libraries
            .Where(l => l.Length > 0 && Directory.Exists(Path.Combine(l, "steamapps")))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    // -- Epic ------------------------------------------------------------------------------------

    /// <summary>Epic writes one JSON manifest per installed game, and they are readable without
    /// the launcher running.</summary>
    public static IEnumerable<ScannedGame> Epic()
    {
        var manifests = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            @"Epic\EpicGamesLauncher\Data\Manifests");
        if (!Directory.Exists(manifests)) yield break;

        foreach (var file in Directory.GetFiles(manifests, "*.item"))
        {
            ScannedGame? game = null;
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(file));
                var name = doc.RootElement.TryGetProperty("DisplayName", out var n) ? n.GetString() : null;
                var path = doc.RootElement.TryGetProperty("InstallLocation", out var p) ? p.GetString() : null;
                var id = doc.RootElement.TryGetProperty("AppName", out var a) ? a.GetString() : null;
                if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path) && Directory.Exists(path))
                    game = new ScannedGame(name, path, GamePlatform.Epic, id);
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // One unreadable manifest, not a failed scan.
            }
            if (game is not null) yield return game;
        }
    }

    // -- Registry-backed launchers ---------------------------------------------------------------

    public static IEnumerable<ScannedGame> Gog() =>
        FromSubKeys(RegistryView.Registry32, @"SOFTWARE\WOW6432Node\GOG.com\Games",
            (id, key) => Make(key.GetValue("gameName") as string, key.GetValue("path") as string,
                GamePlatform.Gog, id));

    public static IEnumerable<ScannedGame> Ea() =>
        FromSubKeys(RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Electronic Arts\EA Games",
                (id, key) => Make(key.GetValue("DisplayName") as string ?? id,
                    key.GetValue("Install Dir") as string, GamePlatform.Ea, id))
            .Concat(FromSubKeys(RegistryView.Registry64, @"SOFTWARE\Electronic Arts\EA Games",
                (id, key) => Make(key.GetValue("DisplayName") as string ?? id,
                    key.GetValue("Install Dir") as string, GamePlatform.Ea, id)));

    /// <summary>Ubisoft Connect registers its games in the ordinary uninstall list, under keys
    /// named "Uplay Install &lt;id&gt;".</summary>
    public static IEnumerable<ScannedGame> Ubisoft() =>
        FromSubKeys(RegistryView.Registry64, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            (id, key) => id.StartsWith("Uplay Install ", StringComparison.OrdinalIgnoreCase)
                ? Make(key.GetValue("DisplayName") as string, key.GetValue("InstallLocation") as string,
                    GamePlatform.Ubisoft, id["Uplay Install ".Length..].Trim())
                : null);

    /// <summary>Battle.net games are uninstall entries published by Blizzard. The launcher itself
    /// is one of them, and it is not a game.</summary>
    public static IEnumerable<ScannedGame> BattleNet() =>
        new[]
            {
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
                @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            }
            .SelectMany(path => FromSubKeys(RegistryView.Registry64, path, (id, key) =>
            {
                if (key.GetValue("Publisher") is not string publisher
                    || !publisher.Contains("Blizzard Entertainment", StringComparison.OrdinalIgnoreCase))
                    return null;

                var name = key.GetValue("DisplayName") as string;
                if (name is null or "Battle.net" or "Blizzard Battle.net App") return null;

                return Make(name.Replace(" (PTR)", ""), key.GetValue("InstallLocation") as string,
                    GamePlatform.BattleNet, id);
            }));

    /// <summary>Game Pass installs land in XboxGames at the root of a fixed drive.</summary>
    public static IEnumerable<ScannedGame> Xbox()
    {
        foreach (var drive in DriveInfo.GetDrives())
        {
            // The type first: IsReady on a mapped drive that is offline waits out a network timeout,
            // and the whole scan waited with it.
            if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;

            var root = Path.Combine(drive.Name, "XboxGames");
            string[] folders;
            try
            {
                if (!Directory.Exists(root)) continue;
                folders = Directory.GetDirectories(root);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }

            foreach (var folder in folders)
            {
                // The game itself lives under Content; the folder above it holds licence data.
                var content = Path.Combine(folder, "Content");
                var path = Directory.Exists(content) ? content : folder;
                if (SafeIsEmpty(path)) continue;
                yield return new ScannedGame(new DirectoryInfo(folder).Name, path, GamePlatform.Xbox);
            }
        }

        static bool SafeIsEmpty(string path)
        {
            try { return !Directory.EnumerateFileSystemEntries(path).Any(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return true; }
        }
    }

    private static ScannedGame? Make(string? name, string? path, GamePlatform platform, string? id)
    {
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(path)) return null;
        path = path.Trim().Trim('"');
        return Directory.Exists(path) ? new ScannedGame(name.Trim(), path, platform, id) : null;
    }

    private static IEnumerable<ScannedGame> FromSubKeys(RegistryView view, string path,
        Func<string, RegistryKey, ScannedGame?> read)
    {
        List<ScannedGame> games = [];
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
            using var root = baseKey.OpenSubKey(path);
            if (root is null) return games;

            foreach (var name in root.GetSubKeyNames())
            {
                try
                {
                    using var key = root.OpenSubKey(name);
                    if (key is null) continue;
                    if (read(name, key) is { } game) games.Add(game);
                }
                catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // One key we cannot read is not a reason to abandon the rest of the list.
                }
            }
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // The launcher is not installed, or the hive is not readable.
        }
        return games;
    }

    /// <summary>A folder that exists and has something in it. A launcher keeps the folder of a game
    /// it knows about but has not downloaded, and listing one of those put a card on screen whose
    /// tags read "no route" in red -- which is a statement about the game, when the truth was only
    /// that nothing had been installed yet.</summary>
    private static bool HasContent(string folder)
    {
        try
        {
            return Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    // -- What the folder turns out to be ----------------------------------------------------------

    /// <summary>A first guess at the route, from what is in the folder. Always overridable, and the
    /// install still reads the PE header itself -- this only decides which row is preselected.</summary>
    public static Preset GuessPreset(string folder)
    {
        // One table, shared with the detection, so the two cannot disagree about what a folder is.
        var width = Work.Detect(folder).Route;
        if (Emulators.Identify(folder) is { } emulator)
            return emulator.Route ?? GraphicsDetection.RouteFor(width ?? Route.X64, emulator.Best) ?? Preset.Dx11;
        return width == Route.X86 ? Preset.X86Dx11 : Preset.Dx11;
    }

    /// <summary>Whether this folder still has an install of ours, judged by the payload files being
    /// there. Cheap enough to ask for every card on every refresh.
    ///
    /// Not by the manifest: uninstall keeps that file whenever it preserves a configuration entry --
    /// ReShade.ini and amd-nr.ini are kept on purpose -- so a folder that has been fully
    /// uninstalled normally still has one. Reading it as "installed" is what left the badge on after
    /// Uninstall said it was done.</summary>
    /// <summary>Every game under one folder, for somebody who keeps them in a folder rather than
    /// in a launcher: a Games drive, an old library, a copy off another machine.
    ///
    /// A child folder is a game when the detection finds an executable to point at, which is the
    /// same judgement the rest of the app installs by -- and it is the reason this cannot just look
    /// for a .exe in the folder itself: Source keeps one in bin\, Unreal in Binaries\Win64, and
    /// neither has anything worth finding at the top. A folder taken as a game is not descended
    /// into, so its own bin\ never comes back as a second game.</summary>
    public static IReadOnlyList<ScannedGame> UnderFolder(string root, int maxDepth = 3)
    {
        var found = new List<ScannedGame>();
        Walk(root, 0);
        return found
            .GroupBy(g => Engine.WeaklyCanonical(g.InstallPath), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        void Walk(string dir, int depth)
        {
            string[] children;
            try { children = Directory.GetDirectories(dir); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // A folder that will not open costs that folder, never the search.
                return;
            }

            foreach (var child in children)
            {
                if (IsExcluded(child) || !HasContent(child)) continue;

                // Only an executable that looks like a game makes its folder one. The detection's
                // last resort takes the largest executable of any kind, which is right for a game it
                // already knows is one and wrong here: EasyAntiCheat's setup made a game of its folder.
                var exe = GraphicsDetector.FindExecutable(child);
                if (exe is not null && GraphicsDetector.LooksLikeTheGame(exe) && !IsContainer(child, exe))
                {
                    found.Add(new ScannedGame(Path.GetFileName(child), child, GamePlatform.Manual));
                    continue;
                }
                if (depth + 1 < maxDepth) Walk(child, depth + 1);
            }
        }
    }

    /// <summary>Whether a folder holds games rather than being one.
    ///
    /// The detection reaches one level down on its own -- a Source game keeps its executable in
    /// bin\, Unreal in Binaries\Win64 -- so a publisher folder with the game inside it answers
    /// exactly as a game folder does, and a search that trusted that answer added "Rockstar Games"
    /// as a game and never saw the two games under it. The difference is which subfolder the
    /// executable turned up in: one of the engine's, or a plain one that is really the game.</summary>
    private static bool IsContainer(string folder, string exe)
    {
        var relative = Path.GetRelativePath(folder, exe);
        var segments = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Length < 2) return false; // Straight in the folder: it is the game.
        if (EngineFolders.Contains(segments[0])) return false;
        // Unreal keeps the game under a folder named after its project, <Project>\Binaries\Win64,
        // with Engine beside it -- which a publisher folder holding one Unreal game does not have.
        if (segments.Length > 2 && EngineFolders.Contains(segments[1]) && Directory.Exists(Path.Combine(folder, "Engine")))
            return false;
        // The game's executable in a folder of its own name, GTAIV\GTAIV.exe under Grand Theft Auto
        // IV, is the game again, not a second one inside it.
        return !GraphicsDetector.NamedLike(folder, exe);
    }

    /// <summary>Where an engine puts its executable, as the first segment under the game's root.
    /// Taken from the folders the detection itself searches.</summary>
    private static readonly HashSet<string> EngineFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "bin", "bin64", "bin_x64", "x64", "game", "binaries", "phoenix", "win64", "win32",
    };

    public static bool IsInstalled(string folder) =>
        Work.InstalledMarkers.Any(name => File.Exists(Path.Combine(folder, name)));
}
