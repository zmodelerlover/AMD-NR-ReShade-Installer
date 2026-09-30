// Cover art, for the games that have a public one.
//
// Steam publishes library art on its own CDN, and it needs no key and no account. A game from
// another launcher, or a folder added by hand, is looked up on Steam by its title -- in the API
// database first, then in Steam's store search, and only on an exact match of the normalised title:
// another game's cover would be worse than none. Everything else gets a coloured tile with its
// initials -- a grid where a third of the cards are a grey rectangle looks broken, and one that is
// deliberately plain does not.

using System.Text.Json;

namespace AmdNr.Core;

public sealed class CoverCache(HttpClient http, ApiDatabase? db = null)
{
    public static string Folder => Path.Combine(AppPaths.Cache, "covers");

    /// <summary>Folder names that say where the executable sits rather than what the game is: a folder
    /// added by hand as ...\Crimson Desert\bin64 is Crimson Desert.</summary>
    private static readonly string[] Plumbing = ["bin", "bin64", "bin32", "x64", "win64", "binaries", "retail", "game"];

    /// <summary>The file for an app id, if it has already been fetched.</summary>
    public static string? Local(string? appId)
    {
        if (!IsSteamId(appId)) return null;
        var path = Path.Combine(Folder, $"{appId}.jpg");
        return File.Exists(path) ? path : null;
    }

    /// <summary>The cover for a game: by its Steam app id, or for any other game by the Steam app id its
    /// title finds. Returns null rather than throwing: a missing cover is a tile, not an error.</summary>
    public async Task<string?> EnsureAsync(string? appId, string? name = null, string? folder = null,
        CancellationToken cancel = default)
    {
        try
        {
            var id = await IdAsync(appId, name, folder, cancel);
            return id is null ? null : Local(id) ?? await FetchAsync(id, Path.Combine(Folder, $"{id}.jpg"),
                [("library_600x900.jpg", "library_capsule"), ("header.jpg", "header")], cancel);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The wide banner Steam shows across the top of a game in its own library, for the list view's
    /// game page. Null when Steam has none for it; the page then shows the cover instead.</summary>
    public async Task<string?> EnsureHeroAsync(string? appId, string? name = null, string? folder = null,
        CancellationToken cancel = default)
    {
        try
        {
            if (await IdAsync(appId, name, folder, cancel) is not { } id) return null;
            var path = Path.Combine(Folder, $"{id}-hero.jpg");
            return File.Exists(path) ? path : await FetchAsync(id, path, [("library_hero.jpg", "library_hero")], cancel);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>Titles already looked up this session, so the banner does not ask Steam's search again
    /// for what the cover just found.</summary>
    private static readonly Dictionary<string, string?> Found = new(StringComparer.Ordinal);

    private async Task<string?> IdAsync(string? appId, string? name, string? folder, CancellationToken cancel)
    {
        if (IsSteamId(appId)) return appId;
        var key = $"{name}|{folder}";
        lock (Found)
            if (Found.TryGetValue(key, out var known)) return known;
        var id = await SteamIdForAsync(Titles(name, folder), cancel);
        lock (Found) Found[key] = id;
        return id;
    }

    /// <summary>The first of these pieces of art Steam has for the game, into <paramref name="path"/>. The
    /// plain addresses are tried first; a game published since Steam moved its art under content hashes has
    /// none there, and its store record says where they are.</summary>
    private async Task<string?> FetchAsync(string appId, string path, (string Plain, string Key)[] art, CancellationToken cancel)
    {
        Directory.CreateDirectory(Folder);
        const string cdn = "https://shared.akamai.steamstatic.com/store_item_assets/steam/apps/";

        Dictionary<string, string>? assets = null;
        foreach (var (plain, key) in art)
        {
            if (await DownloadAsync($"{cdn}{appId}/{plain}", path, cancel)) return path;
            assets ??= await AssetsAsync(appId, cancel);
            if (assets.TryGetValue(key, out var hashed) && hashed != plain
                && await DownloadAsync($"{cdn}{appId}/{hashed}", path, cancel))
                return path;
        }
        return null;
    }

    private async Task<bool> DownloadAsync(string url, string path, CancellationToken cancel)
    {
        using var response = await http.GetAsync(url, cancel);
        if (!response.IsSuccessStatusCode) return false;
        var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
        if (bytes.Length < 1024) return false; // A placeholder, not a picture.
        await File.WriteAllBytesAsync(path, bytes, cancel);
        return true;
    }

    /// <summary>The art file names in a game's store record, by kind ("library_capsule", "header").</summary>
    private async Task<Dictionary<string, string>> AssetsAsync(string appId, CancellationToken cancel)
    {
        var input = $"{{\"ids\":[{{\"appid\":{appId}}}],\"context\":{{\"language\":\"english\",\"country_code\":\"US\"}},"
                    + "\"data_request\":{\"include_assets\":true}}";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var doc = await JsonAsync(
            "https://api.steampowered.com/IStoreBrowseService/GetItems/v1?input_json=" + Uri.EscapeDataString(input), cancel);
        if (doc is null
            || !doc.RootElement.TryGetProperty("response", out var response)
            || !response.TryGetProperty("store_items", out var items) || items.GetArrayLength() == 0
            || !items[0].TryGetProperty("assets", out var assets))
            return result;
        foreach (var asset in assets.EnumerateObject())
            if (asset.Value.ValueKind == JsonValueKind.String)
                result[asset.Name] = asset.Value.GetString()!;
        return result;
    }

    /// <summary>The Steam app id of the first title that has one: from the API database, or else the store
    /// search result whose name normalises to exactly that title.</summary>
    private async Task<string?> SteamIdForAsync(IEnumerable<string> titles, CancellationToken cancel)
    {
        foreach (var title in titles)
        {
            if (db?.SteamAppIdFor(title) is { } known) return known;
            var wanted = Bare(title);
            using var doc = await JsonAsync(
                $"https://store.steampowered.com/api/storesearch/?term={Uri.EscapeDataString(title)}&cc=US&l=english", cancel);
            if (doc is null || !doc.RootElement.TryGetProperty("items", out var items)) continue;
            foreach (var item in items.EnumerateArray())
                if (item.TryGetProperty("name", out var n) && Bare(n.GetString()) == wanted
                    && item.TryGetProperty("id", out var id))
                    return id.GetRawText();
        }
        return null;
    }

    /// <summary>What a game may be called on Steam: its name, the part before a " - " in it (an Epic entry
    /// named "Cyberpunk 2077 - REDmod"), and its folder, or the folder above one named after the plumbing.</summary>
    internal static List<string> Titles(string? name, string? folder)
    {
        var titles = new List<string>();
        if (!string.IsNullOrWhiteSpace(name))
        {
            titles.Add(name.Trim());
            if (name.IndexOf(" - ", StringComparison.Ordinal) is > 0 and var dash) titles.Add(name[..dash].Trim());
        }
        for (var dir = string.IsNullOrWhiteSpace(folder) ? null : new DirectoryInfo(folder); dir?.Parent is not null; dir = dir.Parent)
        {
            if (Plumbing.Contains(dir.Name, StringComparer.OrdinalIgnoreCase)) continue;
            titles.Add(dir.Name);
            break;
        }
        return titles.Where(t => PcgwParser.NormaliseTitle(t).Length >= 2)
            .DistinctBy(PcgwParser.NormaliseTitle).ToList();
    }

    /// <summary>A normalised title without the re-release word Steam adds to a store page that replaced the
    /// first one: the Crimson Desert folder is "Crimson Desert Enhanced" there.</summary>
    internal static string Bare(string? title)
    {
        var t = PcgwParser.NormaliseTitle(title);
        foreach (var suffix in new[] { "enhanced", "remastered" })
            if (t.Length > suffix.Length + 2 && t.EndsWith(suffix, StringComparison.Ordinal)) return t[..^suffix.Length];
        return t;
    }

    private async Task<JsonDocument?> JsonAsync(string url, CancellationToken cancel)
    {
        using var response = await http.GetAsync(url, cancel);
        return response.IsSuccessStatusCode ? JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel)) : null;
    }

    private static bool IsSteamId(string? appId) => !string.IsNullOrWhiteSpace(appId) && appId.All(char.IsAsciiDigit);
}
