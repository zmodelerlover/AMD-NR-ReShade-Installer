// Cover art, for the games that have a public one.
//
// Steam publishes library art on its own CDN, keyed by the app id the scan already read, and it
// needs no key and no account. Everything else gets a coloured tile with its initials -- a grid
// where a third of the cards are a grey rectangle looks broken, and one that is deliberately plain
// does not.

namespace AmdNr.Core;

public sealed class CoverCache(HttpClient http)
{
    public static string Folder => Path.Combine(AppPaths.Cache, "covers");

    /// <summary>The file for an app id, if it has already been fetched.</summary>
    public static string? Local(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit)) return null;
        var path = Path.Combine(Folder, $"{appId}.jpg");
        return File.Exists(path) ? path : null;
    }

    /// <summary>Fetches the portrait Steam uses in its own library, falling back to the wide header
    /// art for the games that have no portrait. Returns null rather than throwing: a missing cover
    /// is a tile, not an error.</summary>
    public async Task<string?> EnsureAsync(string? appId, CancellationToken cancel = default)
    {
        if (Local(appId) is { } cached) return cached;
        if (string.IsNullOrWhiteSpace(appId) || !appId.All(char.IsAsciiDigit)) return null;

        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"{appId}.jpg");

        foreach (var art in new[] { "library_600x900.jpg", "header.jpg" })
        {
            try
            {
                var url = $"https://cdn.cloudflare.steamstatic.com/steam/apps/{appId}/{art}";
                using var response = await http.GetAsync(url, cancel);
                if (!response.IsSuccessStatusCode) continue;

                var bytes = await response.Content.ReadAsByteArrayAsync(cancel);
                if (bytes.Length < 1024) continue; // A placeholder, not a picture.

                await File.WriteAllBytesAsync(path, bytes, cancel);
                return path;
            }
            catch (Exception e) when (e is HttpRequestException or IOException or TaskCanceledException)
            {
                return null;
            }
        }
        return null;
    }
}
