// api-db.json: which graphics APIs each game supports, read ahead of time so the app never has to
// ask anyone at run time.
//
// Keyed by Steam app id where there is one, because that is exact, and indexed by normalised title
// as well, which is how a copy bought on Epic, GOG or Xbox -- or a folder added by hand -- finds the
// same record. It is published beside payload.json and fetched the same way, so it inherits the
// same property: no API calls, no rate limit, works on any machine the app runs on.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmdNr.Core;

public sealed class ApiRecord
{
    /// <summary>The wiki page title.</summary>
    [JsonPropertyName("t")] public required string Title { get; init; }

    /// <summary>Supported APIs, as names: "D3D11", "D3D12", "Vulkan", ...</summary>
    [JsonPropertyName("api")] public required List<string> Apis { get; init; }

    [JsonPropertyName("x86")] public bool? Has32Bit { get; init; }
    [JsonPropertyName("x64")] public bool? Has64Bit { get; init; }

    public PcgwApi ToApi() => new(
        Title,
        Apis.Select(a => Enum.TryParse<GraphicsApi>(a, out var api) ? api : GraphicsApi.Unknown)
            .Where(a => a != GraphicsApi.Unknown).Distinct().ToList(),
        Has32Bit, Has64Bit);

    public static ApiRecord From(PcgwApi api) => new()
    {
        Title = api.Page,
        Apis = api.Supported.Select(a => a.ToString()).ToList(),
        Has32Bit = api.Has32Bit,
        Has64Bit = api.Has64Bit,
    };
}

public sealed class ApiDatabase
{
    [JsonPropertyName("schema")] public int Schema { get; init; } = 1;
    [JsonPropertyName("generated")] public DateTime Generated { get; set; } = DateTime.UtcNow;
    [JsonPropertyName("source")] public string Source { get; init; } = "PCGamingWiki (CC BY-NC-SA 3.0)";

    /// <summary>"steam:12210" -> record. Also "name:&lt;normalised title&gt;" for games with no app id.</summary>
    [JsonPropertyName("games")] public Dictionary<string, ApiRecord> Games { get; init; } = new(StringComparer.Ordinal);

    private Dictionary<string, ApiRecord>? _byTitle;

    private static readonly JsonSerializerOptions Options = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static ApiDatabase Parse(string json)
    {
        ApiDatabase? db;
        try { db = JsonSerializer.Deserialize<ApiDatabase>(json, Options); }
        catch (JsonException e) { throw new InstallException($"The game API database is not readable: {e.Message}"); }
        Engine.Require(db is not null, "The game API database is empty.");
        Engine.Require(db!.Schema == 1, $"Game API database schema {db.Schema} is newer than this app understands.");
        return db;
    }

    public string Serialise() => JsonSerializer.Serialize(this, Options);

    public static string SteamKey(string appId) => $"steam:{appId}";
    public static string NameKey(string title) => $"name:{PcgwParser.NormaliseTitle(title)}";

    /// <summary>By app id first, which is exact; then by title, matched only when the normalised
    /// titles are identical.</summary>
    public PcgwApi? Lookup(string? steamAppId, string? name)
    {
        if (!string.IsNullOrWhiteSpace(steamAppId) && Games.TryGetValue(SteamKey(steamAppId), out var bySteam))
            return bySteam.ToApi();

        var wanted = PcgwParser.NormaliseTitle(name);
        if (wanted.Length < 2) return null;

        _byTitle ??= Games.Values
            .GroupBy(r => PcgwParser.NormaliseTitle(r.Title))
            .ToDictionary(g => g.Key, g => g.First());
        return _byTitle.TryGetValue(wanted, out var byTitle) ? byTitle.ToApi() : null;
    }

    public void Put(string key, ApiRecord record)
    {
        Games[key] = record;
        _byTitle = null;
    }

    // -- Where the app gets it ----------------------------------------------------------------------

    public static string CachePath => Path.Combine(AppPaths.Root, "api-db.json");

    /// <summary>The newest copy it can find: fetched from the content repository when reachable,
    /// otherwise the last one fetched, otherwise the one shipped beside the executable. An app that
    /// cannot reach anything still detects every game from its own files.</summary>
    public static async Task<ApiDatabase?> LoadAsync(HttpClient http, string owner, string repo, string branch = "main",
        CancellationToken cancel = default)
    {
        try
        {
            var url = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/api-db.json";
            using var response = await http.GetAsync(url, cancel);
            if (response.IsSuccessStatusCode)
            {
                var json = await response.Content.ReadAsStringAsync(cancel);
                var db = Parse(json);
                try { await File.WriteAllTextAsync(CachePath, json, cancel); }
                catch (IOException)
                {
                    // Not caching only costs the next offline launch.
                }
                return db;
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InstallException)
        {
            // Fall through to the local copies.
        }

        foreach (var path in new[] { CachePath, Path.Combine(AppContext.BaseDirectory, "api-db.json") })
        {
            try
            {
                if (File.Exists(path)) return Parse(await File.ReadAllTextAsync(path, cancel));
            }
            catch (Exception e) when (e is IOException or InstallException)
            {
                // Try the next one.
            }
        }
        return null;
    }
}
