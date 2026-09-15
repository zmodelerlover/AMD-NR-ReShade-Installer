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
            try { games = source().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                return;
            }
            lock (found) found.AddRange(games);
        });

        return found
            .Where(g => !IsExcluded(g.InstallPath) && Directory.Exists(g.InstallPath))
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
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;

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

    // -- What the folder turns out to be ----------------------------------------------------------

    /// <summary>A first guess at the route, from what is in the folder. Always overridable, and the
    /// install still reads the PE header itself -- this only decides which row is preselected.</summary>
    public static Preset GuessPreset(string folder)
    {
        if (File.Exists(Path.Combine(folder, "pcsx2-qt.exe")) || File.Exists(Path.Combine(folder, "pcsx2.exe")))
            return Preset.Pcsx2;
        if (File.Exists(Path.Combine(folder, "rpcs3.exe"))) return Preset.Rpcs3;
        return Work.Detect(folder).Route == Route.X86 ? Preset.X86Dx11 : Preset.Dx11;
    }

    /// <summary>Whether this folder already has an install of ours, by the manifest either route
    /// writes. Cheap enough to ask for every card on every refresh.</summary>
    public static bool IsInstalled(string folder) =>
        File.Exists(Path.Combine(folder, Route.X64.ManifestFileName()))
        || File.Exists(Path.Combine(folder, Route.X86.ManifestFileName()))
        || File.Exists(Path.Combine(folder, Work.AddonName));
}
