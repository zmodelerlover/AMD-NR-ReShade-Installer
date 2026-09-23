// The client the downloads share, and the payload list itself: fetched when it can be, read from
// the copy beside the executable when it cannot.

using System.Net;

namespace AmdNr.Core;

public sealed partial class PayloadCache
{
    /// <summary>The manifest itself. From <paramref name="url"/> when config.json names one, so the
    /// list can live anywhere; otherwise from raw.githubusercontent.com, which unlike the REST API
    /// has no rate limit to share with everyone else on the same address.</summary>
    public async Task<PayloadManifest> FetchManifestAsync(string owner, string repo, string branch = "main",
        string file = "payload.json", string? url = null, CancellationToken cancel = default)
    {
        var address = string.IsNullOrWhiteSpace(url)
            ? $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{file}"
            : url;
        using var response = await Http.GetAsync(address, cancel);
        Engine.Require(response.IsSuccessStatusCode,
            $"Could not read the payload list: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        return PayloadManifest.Parse(await response.Content.ReadAsStringAsync(cancel));
    }

    /// <summary>The copy that ships beside the executable, or one the user dropped in the app's own
    /// folder. This is what makes the app work offline, on a first run behind a captive portal, and
    /// before the content repository exists at all -- the hashes are the same either way, so a local
    /// manifest weakens nothing.</summary>
    public static PayloadManifest? LoadLocalManifest(string fileName = "payload.json")
    {
        foreach (var path in new[]
                 {
                     Path.Combine(AppPaths.Root, fileName),
                     Path.Combine(AppContext.BaseDirectory, fileName),
                 })
        {
            try
            {
                if (File.Exists(path)) return PayloadManifest.Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or InstallException)
            {
                // Try the next one; a broken local copy must not stop the app from starting.
            }
        }
        return null;
    }

    /// <summary>A default client with a User-Agent, because GitHub refuses requests without one.</summary>
    public static HttpClient DefaultClient(string version)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // An address that is down has a mirror behind it; without this the connect attempt sits
            // there until the timeout below, which is not a fall-through, it is a hang.
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
        {
            // The whole of a 141 MB download, on a slow line. What ends a dead one is the stall
            // timer, not this.
            Timeout = TimeSpan.FromMinutes(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AMD-NR-ReShade-Installer/{version}");
        return client;
    }
}
