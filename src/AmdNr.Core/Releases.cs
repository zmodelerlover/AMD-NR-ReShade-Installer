// Which versions of the add-on can be installed, read from the add-on repository's GitHub releases.
//
// The download path still never touches the REST API: this spends one call to *list* the versions,
// once per launch, and every file still comes from a release asset address. The answer is written
// to the cache, so a machine that is offline -- or one that has spent the anonymous 60-an-hour on
// something else -- still gets the list it saw last time.
//
// A release is only offered when it publishes what pins it. The size of each asset comes from the
// API's own record of it and the hash from the release's SHA256SUMS.txt, so a version discovered
// today is pinned exactly as tightly as one written into payload.json months ago. A release that
// publishes neither is left out rather than installed on trust.

using System.Text.Json;

namespace AmdNr.Core;

public sealed record ReleaseAsset(string Name, string Url, ulong Size);

public sealed class AddonRelease
{
    public required Version Version { get; init; }
    public required string Tag { get; init; }
    public required string Title { get; init; }
    public DateTimeOffset Published { get; init; }
    public bool PreRelease { get; init; }

    /// <summary>The release's assets by file name, and the hashes its SHA256SUMS.txt gives for
    /// them. A name missing from either is a file this release cannot install.</summary>
    public required IReadOnlyDictionary<string, ReleaseAsset> Assets { get; init; }
    public required IReadOnlyDictionary<string, string> Sums { get; init; }

    /// <summary>What the menu reads. The version alone is what anyone chooses by; the date settles
    /// which of two similar numbers is the newer one.</summary>
    public string Label =>
        $"v{Version} - {Published.LocalDateTime:yyyy-MM-dd}" + (PreRelease ? " (pre-release)" : "");

    public bool Has(string file) => Assets.ContainsKey(file) && Sums.ContainsKey(file);

    /// <summary>Whether this release publishes everything a route installs. The bridge pair is
    /// published loose, beside the add-on, exactly so that this question has an answer without
    /// downloading anything first.</summary>
    public bool Covers(Route route) => route == Route.X86
        ? Has(Work.Addon32Name) && Has(Work.Host64Name) && Has(AddonReleases.BridgeSums)
        : Has(Work.AddonName);
}

public static class AddonReleases
{
    /// <summary>The first release this installer knows how to install. Everything before it
    /// predates the 32-bit bridge and the one-download packaging, and its assets are not the shape
    /// the engine reads.</summary>
    public static readonly Version Earliest = new(0, 5, 0);

    public const string SumsAsset = "SHA256SUMS.txt";
    public const string BridgeSums = "payload.sha256";

    private static string CachePath => Path.Combine(AppPaths.Cache, "releases.json");

    /// <summary>Every installable release, newest first. An empty list is not an error: it means
    /// the list could not be read and has never been read, and the caller falls back to the one
    /// version the payload manifest pins.</summary>
    public static async Task<IReadOnlyList<AddonRelease>> ListAsync(HttpClient http, string owner, string repo,
        CancellationToken cancel = default)
    {
        var json = await FetchAsync(http, owner, repo, cancel);
        if (json is null) return [];

        List<(AddonRelease Bare, string SumsUrl)> found;
        try { found = Parse(json); }
        catch (JsonException) { return []; }

        var releases = new List<AddonRelease>();
        foreach (var (bare, sumsUrl) in found)
        {
            var sums = await SumsAsync(http, sumsUrl, cancel);
            if (sums.Count == 0) continue; // Unpinnable: not offered.
            releases.Add(new AddonRelease
            {
                Version = bare.Version,
                Tag = bare.Tag,
                Title = bare.Title,
                Published = bare.Published,
                PreRelease = bare.PreRelease,
                Assets = bare.Assets,
                Sums = sums,
            });
        }
        return releases;
    }

    /// <summary>The releases JSON, from GitHub when it answers and from the last copy when it does
    /// not. A successful read replaces that copy.</summary>
    private static async Task<string?> FetchAsync(HttpClient http, string owner, string repo, CancellationToken cancel)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo)) return null;
        try
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/releases?per_page=30";
            using var response = await http.GetAsync(url, cancel);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancel);
                try { await File.WriteAllTextAsync(CachePath, json, cancel); }
                catch (IOException) { /* The list still works for this run. */ }
                return json;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            // Offline, rate-limited, or behind something that ate the request: use what is cached.
        }

        try { return File.Exists(CachePath) ? await File.ReadAllTextAsync(CachePath, cancel) : null; }
        catch (IOException) { return null; }
    }

    /// <summary>The releases worth offering, each with the address of the file that pins it.</summary>
    internal static List<(AddonRelease Bare, string SumsUrl)> Parse(string json)
    {
        var found = new List<(AddonRelease, string)>();
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array) return found;

        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (Bool(release, "draft")) continue;
            if (Version(Text(release, "tag_name")) is not { } version || version < Earliest) continue;

            var assets = new Dictionary<string, ReleaseAsset>(StringComparer.OrdinalIgnoreCase);
            if (release.TryGetProperty("assets", out var list) && list.ValueKind == JsonValueKind.Array)
                foreach (var asset in list.EnumerateArray())
                {
                    var name = Text(asset, "name");
                    var url = Text(asset, "browser_download_url");
                    if (name.Length == 0 || !url.StartsWith("https://", StringComparison.Ordinal)) continue;
                    var size = asset.TryGetProperty("size", out var s) && s.TryGetInt64(out var bytes) && bytes > 0
                        ? (ulong)bytes
                        : 0;
                    if (size == 0) continue;
                    assets[name] = new ReleaseAsset(name, url, size);
                }

            if (!assets.TryGetValue(SumsAsset, out var sums)) continue; // Nothing pins it.

            found.Add((new AddonRelease
            {
                Version = version,
                Tag = Text(release, "tag_name"),
                Title = Text(release, "name"),
                Published = DateTimeOffset.TryParse(Text(release, "published_at"), out var when)
                    ? when
                    : DateTimeOffset.MinValue,
                PreRelease = Bool(release, "prerelease"),
                Assets = assets,
                Sums = new Dictionary<string, string>(),
            }, sums.Url));
        }

        found.Sort((a, b) => b.Item1.Version.CompareTo(a.Item1.Version));
        return found;

        static string Text(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

        static bool Bool(JsonElement e, string name) =>
            e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    }

    /// <summary>A tag as a version: "v0.5.1" and "0.5.1" both read, and anything else is not a
    /// release this understands and is left out.</summary>
    public static Version? Version(string tag)
    {
        var t = tag.Trim();
        if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
        var mark = t.IndexOfAny(['-', '+']);
        if (mark >= 0) t = t[..mark];
        return System.Version.TryParse(t, out var version) ? version : null;
    }

    private static async Task<IReadOnlyDictionary<string, string>> SumsAsync(HttpClient http, string url,
        CancellationToken cancel)
    {
        try
        {
            using var response = await http.GetAsync(url, cancel);
            if (!response.IsSuccessStatusCode) return new Dictionary<string, string>();
            return ParseSums(await response.Content.ReadAsStringAsync(cancel));
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return new Dictionary<string, string>();
        }
    }

    /// <summary>sha256sum output: the hash, then the name, with or without the asterisk that means
    /// "read as binary". A line that is not that is not a pin, and is skipped rather than guessed
    /// at. The name is taken without its folder, because the same file is published loose and
    /// listed with a path inside the archive.</summary>
    public static IReadOnlyDictionary<string, string> ParseSums(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length < 66) continue;
            var hash = line[..64].ToLowerInvariant();
            if (!Engine.IsHex(hash, 64)) continue;
            var name = line[64..].TrimStart();
            if (name.StartsWith('*')) name = name[1..];
            name = name.Trim().Replace('\\', '/');
            var slash = name.LastIndexOf('/');
            if (slash >= 0) name = name[(slash + 1)..];
            if (name.Length > 0) sums[name] = hash;
        }
        return sums;
    }

    /// <summary>The manifest to install one chosen version with: the add-on and bridge components
    /// replaced by that release's assets. Everything else -- the runtime, the weights, ReShade --
    /// is left exactly as the manifest has it, because none of those are versioned with the
    /// add-on.</summary>
    public static PayloadManifest With(PayloadManifest manifest, AddonRelease release)
    {
        var components = new Dictionary<string, PayloadComponent>(manifest.Components, StringComparer.Ordinal);
        var version = release.Version.ToString();

        if (release.Has(Work.AddonName))
            components[PayloadManifest.AddonComponent] = new PayloadComponent
            {
                Version = version,
                Files = [Pinned(release, Work.AddonName)],
            };

        if (release.Covers(Route.X86))
            components[PayloadManifest.BridgeComponent] = new PayloadComponent
            {
                Version = version,
                Files =
                [
                    Pinned(release, Work.Addon32Name, $"files/{Work.Addon32Name}"),
                    Pinned(release, Work.Host64Name, $"files/{Work.Host64Name}"),
                    Pinned(release, BridgeSums),
                ],
            };

        return new PayloadManifest
        {
            Schema = manifest.Schema,
            Owner = manifest.Owner,
            Repo = manifest.Repo,
            Tag = manifest.Tag,
            Components = components,
        };
    }

    private static PayloadFile Pinned(AddonRelease release, string name, string? path = null) => new()
    {
        Name = name,
        Path = path,
        Size = release.Assets[name].Size,
        Sha256 = release.Sums[name],
        Url = release.Assets[name].Url,
    };
}
