// The client the downloads share, and the payload list itself: fetched when it can be, read from
// the copy beside the executable when it cannot, and from the one built into it when there is no
// copy beside it either.

using System.Net;
using System.Net.Sockets;
using System.Reflection;

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

    /// <summary>The first of several addresses that answers with a manifest this build can read.
    /// The list is published in two places, and one being unreachable from somebody's network --
    /// a filtered host, a broken route -- is no reason to fall back to a copy that may be older when
    /// the other one answers. Throws with every address and its reason when none does.</summary>
    public async Task<PayloadManifest> FetchManifestAsync(IEnumerable<string> addresses, CancellationToken cancel = default)
    {
        var failures = new List<string>();
        foreach (var address in addresses.Where(a => a.Length > 0).Distinct(StringComparer.Ordinal))
        {
            try { return await FetchManifestAsync("", "", url: address, cancel: cancel); }
            catch (Exception e) when (e is HttpRequestException or InstallException
                                          or TaskCanceledException && !cancel.IsCancellationRequested)
            {
                failures.Add($"{new Uri(address).Host}: {(e is TaskCanceledException ? "it did not answer in time." : Describe(e))}");
            }
        }
        throw new InstallException("Could not read the payload list.\n      " + string.Join("\n      ", failures));
    }

    /// <summary>The copy that ships beside the executable, or one the user dropped in the app's own
    /// folder. This is what makes the app work offline, on a first run behind a captive portal, and
    /// before the content repository exists at all -- the hashes are the same either way, so a local
    /// manifest weakens nothing.
    ///
    /// The one built into the executable comes last. It is what the app was published with, which
    /// the files beside it are too, unless somebody put newer ones there -- and a lone executable
    /// copied to the desktop, which is how this app is most often run, has no files beside it.</summary>
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
            catch (Exception e) when (e is IOException or InstallException or UnauthorizedAccessException)
            {
                // Try the next one; a broken local copy must not stop the app from starting.
            }
        }

        try { return Embedded(fileName) is { } json ? PayloadManifest.Parse(json) : null; }
        catch (InstallException) { return null; }
    }

    /// <summary>A file built into this assembly, by name, or null when this build carries none.</summary>
    internal static string? Embedded(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
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
            ConnectCallback = (context, cancel) => ConnectAsync(context.DnsEndPoint, cancel),
        })
        {
            // The whole of a 141 MB download, on a slow line. What ends a dead one is the stall
            // timer, not this.
            Timeout = TimeSpan.FromMinutes(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AMD-NR-ReShade-Installer/{version}");
        return client;
    }

    /// <summary>How long one address gets on its own before the next one is tried beside it.</summary>
    private static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(250);

    /// <summary>A connection to whichever of the host's addresses answers first.
    ///
    /// .NET tries them one after another and waits out each before the next, and the connect
    /// timeout above is shared by all of them. On a network where IPv6 is configured but goes
    /// nowhere -- a router that hands out addresses it cannot route, a VPN -- the first address is
    /// IPv6, it swallows the whole timeout, and IPv4 is never tried: the download fails every time
    /// while a browser, which races them, loads the same page at once. Hugging Face, where nearly
    /// every payload lives, answers on both. So this races them the way a browser does: each
    /// address in turn, a quarter of a second apart, and the first one through wins.</summary>
    internal static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken cancel)
    {
        var addresses = IPAddress.TryParse(endpoint.Host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(endpoint.Host, cancel);
        return await ConnectAsync(addresses, endpoint.Port, cancel);
    }

    internal static async ValueTask<Stream> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port,
        CancellationToken cancel)
    {
        if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

        // The families alternate, starting with whichever the resolver put first.
        var first = addresses[0].AddressFamily;
        var queue = new List<IPAddress>();
        var same = addresses.Where(a => a.AddressFamily == first).ToList();
        var other = addresses.Where(a => a.AddressFamily != first).ToList();
        for (var i = 0; i < Math.Max(same.Count, other.Count); i++)
        {
            if (i < same.Count) queue.Add(same[i]);
            if (i < other.Count) queue.Add(other[i]);
        }

        using var race = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var running = new List<Task<Socket>>();
        Exception? last = null;
        var next = 0;
        try
        {
            while (next < queue.Count || running.Count > 0)
            {
                if (next < queue.Count) running.Add(ConnectOneAsync(queue[next++], port, race.Token));
                var waiting = Task.WhenAny(running);
                if (next < queue.Count) await Task.WhenAny(waiting, Task.Delay(Stagger, race.Token));
                else await waiting;

                foreach (var done in running.Where(t => t.IsCompleted).ToList())
                {
                    running.Remove(done);
                    if (done.IsCompletedSuccessfully) return new NetworkStream(done.Result, ownsSocket: true);
                    last = done.Exception?.InnerException ?? last;
                }
                cancel.ThrowIfCancellationRequested();
            }
        }
        finally
        {
            race.Cancel();
            // The ones still in flight are abandoned; one that connects anyway is closed, not leaked.
            foreach (var loser in running)
                _ = loser.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result.Dispose(); },
                    TaskScheduler.Default);
        }
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    private static async Task<Socket> ConnectOneAsync(IPAddress address, int port, CancellationToken cancel)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(address, port), cancel);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
