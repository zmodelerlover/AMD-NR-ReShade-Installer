// The app's own small services: where its settings live, what the machine can run, what games the
// user has added, and whether there is a newer release.

using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using System.Management;
using System.Text.Json;
using System.Text.Json.Serialization;
using AmdNr.Core;

namespace AmdNr.App;

public sealed class RepoRef
{
    public string Owner { get; init; } = "";
    public string Repo { get; init; } = "";
    public string Branch { get; init; } = "main";
    public string File { get; init; } = "payload.json";

    /// <summary>A full address for the payload list, when it is not published on GitHub. Set, it
    /// wins over Owner/Repo/Branch/File; unset, that pair is used as before.</summary>
    public string? ManifestUrl { get; init; }

    /// <summary>The same, for api-db.json.</summary>
    public string? ApiDbUrl { get; init; }
}

public sealed class AppConfig
{
    // The defaults are the real repositories, so config.json is an override and not a requirement:
    // a copy of the exe on its own still knows where to look.
    public RepoRef App { get; init; } = new() { Owner = "zmodelerlover", Repo = "AMD-NR-ReShade-Installer" };
    public RepoRef Payload { get; init; } = new() { Owner = "zmodelerlover", Repo = "AMD-NR-Extras" };

    /// <summary>The add-on's own repository. Only its releases are read, to know which versions
    /// exist and what each one publishes; the files still come from release asset addresses.
    /// </summary>
    public RepoRef Addon { get; init; } = new() { Owner = "zmodelerlover", Repo = "dlss5-neural-amd" };

    /// <summary>The chat everyone is actually in, and the project the network comes from. Both are
    /// here rather than in the code because an invite can be rotated and a repository can move, and
    /// neither should need a new build of this.</summary>
    public string DiscordUrl { get; init; } = "https://discord.gg/wYhvS3JSHM";

    public string RuntimeUrl { get; init; } = "https://github.com/danielblnc/DLSS-NR-on-AMD";

    /// <summary>Where someone can put money in if they want to. Nothing is behind either of them.
    /// </summary>
    public string KofiUrl { get; init; } = "https://ko-fi.com/proceduralnilo";

    public string VakinhaUrl { get; init; } =
        "https://www.vakinha.com.br/vaquinha/open-source-dlss-amd-nr";

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true };

    public static AppConfig Load()
    {
        // The copy next to the exe is the shipped default; one in %AppData% wins, so a mirror can be
        // pointed somewhere else without a new build.
        foreach (var path in new[]
                 {
                     Path.Combine(AppPaths.Root, "config.json"),
                     Path.Combine(AppContext.BaseDirectory, "config.json"),
                 })
        {
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(path), Options) is { } c)
                    return c;
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
                // A broken config must not stop the app: fall through to the next one, then defaults.
            }
        }
        return new AppConfig();
    }
}

/// <summary>What the person chose last time. Small enough that it is one file and no schema.</summary>
public sealed class Settings
{
    public string? Language { get; set; }

    /// <summary>True once someone reached the end of the first-run wizard. Closing that window with
    /// the X leaves it false, so the wizard asks again rather than silently never running.</summary>
    public bool SetupDone { get; set; }

    /// <summary>True when the wizard's games step was left without a scan being run. The main
    /// window used to scan on its own whenever the list was empty, which is exactly the state
    /// skipping that step leaves behind -- so the one person who said no was the one person it ran
    /// for. Scan games is still there; it just has to be asked for.</summary>
    public bool ScanDeclined { get; set; }

    private static string Path => System.IO.Path.Combine(AppPaths.Root, "settings.json");
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public static Settings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(Path), Options) ?? new Settings()
                : new Settings();
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, Options)); }
        catch (IOException)
        {
            // A preference that did not persist is not worth interrupting anyone over.
        }
    }
}

public sealed class GameEntry
{
    public required string Path { get; set; }
    public string? Name { get; set; }
    public Preset Preset { get; set; } = Preset.Dx11;

    /// <summary>Which library it came from, for the badge on the tile.</summary>
    public GamePlatform Platform { get; set; } = GamePlatform.Manual;

    /// <summary>Steam's app id, when it has one. It is what the cover art is keyed by.</summary>
    public string? AppId { get; set; }

    /// <summary>True once the person picked a route by hand. Until then the route follows what the
    /// detection says, including when a newer API database changes its mind.</summary>
    public bool PresetChosen { get; set; }

    /// <summary>The add-on version this game was last installed with, or last set to by hand. It
    /// lives per game rather than per app because that is the scope it means anything in: pinning
    /// one game to an older build is a thing people do, and the rest of the library should not
    /// follow it. Unset until an install lands or the menu is touched, and a version that is no
    /// longer published quietly falls back to the newest offered.</summary>
    public string? AddonVersion { get; set; }

    [JsonIgnore]
    public string Display => Name ?? System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) ?? Path;

    public static GameEntry From(ScannedGame game) => new()
    {
        Path = game.InstallPath,
        Name = game.Name,
        Platform = game.Platform,
        AppId = game.AppId,
        Preset = GameScanner.GuessPreset(game.InstallPath),
    };
}

/// <summary>games.json. Written whole every time -- it is a list of folders, not a database.</summary>
public static class GameStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    public static List<GameEntry> Load()
    {
        try
        {
            return File.Exists(AppPaths.GamesFile)
                ? JsonSerializer.Deserialize<List<GameEntry>>(File.ReadAllText(AppPaths.GamesFile), Options) ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException)
        {
            return [];
        }
    }

    public static void Save(IEnumerable<GameEntry> games)
    {
        try { File.WriteAllText(AppPaths.GamesFile, JsonSerializer.Serialize(games, Options)); }
        catch (IOException)
        {
            // Losing the list is an annoyance, not a reason to take the window down mid-install.
        }
    }
}

public sealed record SystemState(string Gpu, string Driver, bool Hip7, bool LooksLikeRadeon)
{
    public bool Ready => Hip7 && LooksLikeRadeon;
}

/// <summary>What the machine says about itself. This exists because "nothing happens in game" is
/// nearly always one of two things -- not a Radeon, or no HIP 7 -- and both are answerable here
/// instead of in a support thread.</summary>
public static class GpuService
{
    public static SystemState Read()
    {
        var name = "unknown";
        var driver = "unknown";
        try
        {
            using var search = new ManagementObjectSearcher(
                "SELECT Name, DriverVersion FROM Win32_VideoController");
            foreach (var o in search.Get())
            {
                var candidate = o["Name"]?.ToString() ?? "";
                // A laptop reports the integrated adapter too; the Radeon is the one that matters.
                if (name == "unknown" || candidate.Contains("Radeon", StringComparison.OrdinalIgnoreCase))
                {
                    name = candidate;
                    driver = o["DriverVersion"]?.ToString() ?? driver;
                }
            }
        }
        catch (ManagementException)
        {
            // WMI can be disabled or broken; the rest of the app does not depend on this.
        }

        var radeon = name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                     || name.Contains("AMD", StringComparison.OrdinalIgnoreCase);
        return new SystemState(name, driver, FindHip7() is not null, radeon);
    }

    /// <summary>amdhip64_7.dll on the search path. HIP 6 does not count, which is why the name is
    /// checked rather than "some HIP".</summary>
    public static string? FindHip7()
    {
        var places = new List<string> { Environment.SystemDirectory };
        places.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var dir in places)
        {
            try
            {
                var candidate = Path.Combine(dir, "amdhip64_7.dll");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth a crash.
            }
        }
        return null;
    }
}

public sealed record AppRelease(string Version, string Notes, string Url,
    IReadOnlyDictionary<string, string> Assets)
{
    /// <summary>Whether this release publishes both the executable and the sums that pin it.
    /// Without the pin there is nothing to check the download against, and an update that runs
    /// unverified bytes is worse than one the person fetches by hand.</summary>
    public bool CanSelfUpdate =>
        Assets.ContainsKey(AppUpdate.ExeAsset) && Assets.ContainsKey(AppUpdate.SumsAsset);
}

/// <summary>Checks whether a newer release is published. It does not download or replace anything:
/// a single-file exe is locked by Windows while the old process is still shutting down, and
/// in-place overwrite is where this class of app reliably goes wrong. The user gets the notes and
/// the link.</summary>
/// <summary>A window that opens taller than the screen loses its footer, and the footer is where
/// the buttons are. Nothing here touches a window that already fits, so a large screen is left
/// alone.</summary>
public static class WindowFit
{
    public static void ToScreen(Window window)
    {
        var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
        if (screen is null) return;

        // WorkingArea is in physical pixels; Width and Height are in the layout's own units.
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        var maxWidth = screen.WorkingArea.Width / scale - 40;
        var maxHeight = screen.WorkingArea.Height / scale - 40;
        if (maxWidth <= 0 || maxHeight <= 0) return;

        var width = Math.Min(window.Width, maxWidth);
        var height = Math.Min(window.Height, maxHeight);
        if (width >= window.Width && height >= window.Height) return;

        window.Width = width;
        window.Height = height;
        // Centred again on the size it actually got, or shrinking leaves it hanging off one edge.
        window.Position = new PixelPoint(
            (int)(screen.WorkingArea.X + (screen.WorkingArea.Width - width * scale) / 2),
            (int)(screen.WorkingArea.Y + (screen.WorkingArea.Height - height * scale) / 2));
    }
}

public static class AppUpdate
{
    public static async Task<AppRelease?> CheckAsync(HttpClient http, RepoRef repo, string currentVersion,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(repo.Owner) || string.IsNullOrWhiteSpace(repo.Repo)) return null;

        try
        {
            var url = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/releases/latest";
            using var response = await http.GetAsync(url, cancel);
            if (!response.IsSuccessStatusCode) return null;

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            if (!doc.RootElement.TryGetProperty("tag_name", out var tag)) return null;

            var latest = Normalise(tag.GetString() ?? "");
            if (latest.Length == 0) return null;
            if (!Version.TryParse(latest, out var newer) || !Version.TryParse(Normalise(currentVersion), out var mine))
                return latest == Normalise(currentVersion) ? null : Build(doc, latest);
            return newer > mine ? Build(doc, latest) : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null; // Offline is not an error worth a dialog.
        }

        static string Normalise(string tag)
        {
            var t = tag.Trim();
            if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
            var plus = t.IndexOf('+');
            return plus >= 0 ? t[..plus] : t;
        }

        AppRelease Build(JsonDocument doc, string version)
        {
            var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("assets", out var list) &&
                list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                    if (item.TryGetProperty("name", out var n) &&
                        item.TryGetProperty("browser_download_url", out var u) &&
                        n.GetString() is { Length: > 0 } name && u.GetString() is { Length: > 0 } url)
                        assets[name] = url;
            }
            return new AppRelease(
                version,
                doc.RootElement.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                doc.RootElement.TryGetProperty("html_url", out var link)
                    ? link.GetString() ?? $"https://github.com/{repo.Owner}/{repo.Repo}/releases"
                    : $"https://github.com/{repo.Owner}/{repo.Repo}/releases",
                assets);
        }
    }

    public const string ExeAsset = "AMD-NR-ReShade-Installer.exe";
    public const string SumsAsset = "SHA256SUMS.txt";

    /// <summary>Fetches the new executable and checks it against the sums the same release
    /// publishes, then puts it beside the running one. Returns where it landed.
    ///
    /// The verification is not optional: this writes a file the app is about to run as itself, so
    /// an unverified update is a remote code execution with extra steps. A release that does not
    /// publish its sums is refused rather than trusted -- that is what CanSelfUpdate is for.</summary>
    public static async Task<string> FetchAsync(HttpClient http, AppRelease release,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        Engine.Require(release.CanSelfUpdate,
            "That release does not publish the file and the hashes this would need. Use the link instead.");

        var sums = await http.GetStringAsync(release.Assets[SumsAsset], cancel);

        using var response = await http.GetAsync(release.Assets[ExeAsset],
            HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        await using var body = await response.Content.ReadAsStreamAsync(cancel);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await body.ReadAsync(chunk, cancel)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (total is > 0) progress?.Report((double)buffer.Length / total.Value);
        }
        var bytes = buffer.ToArray();

        Engine.Require(AppUpdater.Verify(bytes, sums, ExeAsset),
            "The update downloaded, but it does not match the SHA-256 the release publishes. "
            + "Nothing was replaced.");

        var staged = Path.Combine(AppPaths.Root, $"update-{release.Version}.exe");
        await File.WriteAllBytesAsync(staged, bytes, cancel);
        return staged;
    }

    /// <summary>Swaps the staged executable in and starts it. The caller closes the window straight
    /// after: two copies of this app in one folder is the state the swap exists to pass through
    /// quickly, and the outgoing one is swept on the next start.</summary>
    public static void ApplyAndRestart(string staged)
    {
        var current = Environment.ProcessPath
                      ?? throw new InstallException("Cannot tell which file this app is running from.");
        AppUpdater.Swap(current, staged);
        Process.Start(new ProcessStartInfo(current) { UseShellExecute = true });
    }

    public static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // No browser association: the link is on screen either way.
        }
    }
}
