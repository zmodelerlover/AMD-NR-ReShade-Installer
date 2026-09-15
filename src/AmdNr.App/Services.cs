// The app's own small services: where its settings live, what the machine can run, what games the
// user has added, and whether there is a newer release.

using System.Diagnostics;
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
}

public sealed class AppConfig
{
    public RepoRef App { get; init; } = new();
    public RepoRef Payload { get; init; } = new();

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

    [JsonIgnore]
    public string Display => Name ?? System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) ?? Path;
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

public sealed record AppRelease(string Version, string Notes, string Url);

/// <summary>Checks whether a newer release is published. It does not download or replace anything:
/// a single-file exe is locked by Windows while the old process is still shutting down, and
/// in-place overwrite is where this class of app reliably goes wrong. The user gets the notes and
/// the link.</summary>
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

        AppRelease Build(JsonDocument doc, string version) => new(
            version,
            doc.RootElement.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
            doc.RootElement.TryGetProperty("html_url", out var link)
                ? link.GetString() ?? $"https://github.com/{repo.Owner}/{repo.Repo}/releases"
                : $"https://github.com/{repo.Owner}/{repo.Repo}/releases");
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
