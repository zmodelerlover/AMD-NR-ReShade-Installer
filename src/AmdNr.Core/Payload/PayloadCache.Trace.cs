// What a download did, address by address, kept for the log somebody sends when it did not work.
//
// The message on screen says in a sentence what went wrong at each address. That is enough to act
// on and not enough to diagnose: "it never answered" behind a proxy that no longer exists and the
// same words on a network that filters one host are two different fixes, and only the proxy, the
// name lookup, the status line and the bytes that arrived tell them apart.

using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AmdNr.Core;

/// <summary>One file from one address, once. A resume after a cut is an attempt of its own.</summary>
public sealed class DownloadAttempt
{
    public required string File { get; init; }
    public required Uri Url { get; init; }
    public required ulong Expected { get; init; }
    public string Proxy { get; init; } = "direct";
    public string Lookup { get; set; } = "-";
    public ulong ResumedFrom { get; set; }
    public int? Status { get; set; }
    public long Received { get; set; }
    public TimeSpan Took { get; set; }

    /// <summary>What kind of failure, in the few words a log is searched by; null when it worked.</summary>
    public string? Kind { get; set; }

    public Exception? Error { get; set; }

    /// <summary>It failed and the next address was tried.</summary>
    public bool FellThrough { get; set; }
}

/// <summary>Everything one <see cref="PayloadCache.EnsureAsync"/> did: what each file came from,
/// and every address it asked.</summary>
public sealed class DownloadTrace
{
    public List<(string File, string What)> Files { get; } = [];
    public List<DownloadAttempt> Attempts { get; } = [];

    /// <summary>Each host looked up once: every file on it gets the same answer.</summary>
    internal Dictionary<string, string> Lookups { get; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed partial class PayloadCache
{
    /// <summary>Set to have <see cref="EnsureAsync"/> record what it does. Null records nothing and
    /// looks nothing up, which is what a test that counts requests wants.</summary>
    public DownloadTrace? Trace { get; set; }

    /// <summary>The kind of a failure, in a word or two: what <see cref="Describe"/> says in a
    /// sentence, for a log that has to be read across many attempts.</summary>
    public static string Classify(Exception e) => e switch
    {
        _ when IsDiskFull(e) => "disk full",
        HttpRequestException { StatusCode: { } code } => $"http {(int)code}",
        HttpRequestException { InnerException: SocketException s } => s.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain => "dns",
            SocketError.TimedOut => "never answered",
            SocketError.ConnectionRefused => "refused",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown => "unreachable",
            SocketError.ConnectionReset or SocketError.ConnectionAborted => "cut",
            var other => $"socket {other}",
        },
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }
            or HttpRequestException { InnerException: AuthenticationException } => "tls",
        HttpRequestException { InnerException: IOException } or HttpIOException
            or IOException { InnerException: SocketException } => "cut",
        HttpRequestException => "network",
        // The client's own timeouts carry a TimeoutException; the stall timer's does not.
        OperationCanceledException { InnerException: TimeoutException } => "never answered",
        OperationCanceledException => "stopped answering",
        UnauthorizedAccessException or IOException => "could not write (antivirus?)",
        InstallException { Network: true } => "network",
        _ => e.GetType().Name,
    };

    /// <summary>An exception with every one inside it, type and message: the outer message is the
    /// sentence, and the inner ones are what it was made from.</summary>
    public static string Chain(Exception e)
    {
        var parts = new List<string>();
        for (Exception? at = e; at is not null && parts.Count < 6; at = at.InnerException)
            parts.Add($"{at.GetType().Name}: {at.Message.ReplaceLineEndings(" ")}"
                      + (at is SocketException s ? $" [{s.SocketErrorCode}]" : "")
                      + (at is HttpRequestException { HttpRequestError: var r and not HttpRequestError.Unknown } ? $" [{r}]" : ""));
        return string.Join("\n  <- ", parts);
    }

    /// <summary>The proxy Windows would send this address through, or "direct".</summary>
    public static string ProxyFor(Uri url)
    {
        try
        {
            var proxy = HttpClient.DefaultProxy.GetProxy(url);
            return proxy is null || proxy == url ? "direct" : proxy.ToString();
        }
        catch (Exception e) when (e is InvalidOperationException or PlatformNotSupportedException or UriFormatException)
        {
            return $"could not be read ({e.Message})";
        }
    }

    /// <summary>What a name resolves to right now, or why it does not; never longer than
    /// <paramref name="limit"/>, because a resolver that hangs is itself the answer.</summary>
    public static async Task<string> LookUpAsync(string host, TimeSpan limit)
    {
        if (IPAddress.TryParse(host, out _)) return "an address already, nothing to look up";
        using var timeout = new CancellationTokenSource(limit);
        try
        {
            var found = await Dns.GetHostAddressesAsync(host, timeout.Token);
            return found.Length == 0 ? "no addresses" : string.Join(", ", found.Select(a => a.ToString()));
        }
        catch (SocketException e)
        {
            return $"failed: {e.SocketErrorCode} ({e.Message})";
        }
        catch (OperationCanceledException)
        {
            return $"no answer in {limit.TotalSeconds:0} s";
        }
    }

    private async Task<string> LookUpOnceAsync(string host)
    {
        if (Trace is null) return "-";
        if (!Trace.Lookups.TryGetValue(host, out var found))
            Trace.Lookups[host] = found = await LookUpAsync(host, TimeSpan.FromSeconds(3));
        return found;
    }

    /// <summary>Every file a component is made of as it sits in the cache now: verified, the wrong
    /// bytes, or missing, and any part a download left behind.</summary>
    public static List<(string Name, string State)> CacheState(PayloadManifest manifest, string component)
    {
        var c = manifest.Component(component);
        var dir = FolderFor(component, c.Version);
        return c.Files.Concat(c.Installed).DistinctBy(f => f.RelativePath).Select(f =>
        {
            var path = Path.Combine(dir, f.RelativePath);
            var size = Engine.SizeOf(path);
            var state = size is null ? "missing"
                : Verified(path, f.Size, f.Sha256) ? $"{size:N0} bytes, verified"
                : $"{size:N0} bytes, NOT the pinned file (expected {f.Size:N0})";
            if (Engine.SizeOf(path + ".part") is { } part) state += $"; {part:N0} bytes left in .part";
            return (f.RelativePath, state);
        }).ToList();
    }
}
