using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using AmdNr.Core;
using static AmdNr.Core.Tests.DownloadFallbackTests;

namespace AmdNr.Core.Tests;

/// <summary>What the download log is made of: every address tried, what it answered, and the kind
/// of each failure in words a log can be searched by. The addresses are literals, so the name
/// lookup the trace does is never a real one.</summary>
[Collection("AppCache")]
public class DownloadTraceTests
{
    /// <summary>One file at exactly these addresses: no release to fall back to, whose host would be
    /// a real name to look up.</summary>
    private static PayloadManifest At(string version, byte[] blob, string url, string? mirror = null) =>
        PayloadManifest.Parse($$"""
            { "schema": 1, "components": { "runtime": { "version": "{{version}}", "files": [
                { "name": "dlssnr_amd_pass1.dll", "size": {{blob.Length}}, "sha256": "{{Engine.Sha(blob)}}",
                  "url": "{{url}}"{{(mirror is null ? "" : $", \"mirrors\": [\"{mirror}\"]")}} } ] } } }
            """);

    [Fact]
    public async Task EveryAddressTriedIsRecordedWithWhyItFailed()
    {
        var manifest = At(Version("tr"), Blob(20), "https://192.0.2.1/f", "https://192.0.2.2/f");
        var trace = new DownloadTrace();
        var cache = new PayloadCache(new HttpClient(new Answers(request => request.RequestUri!.Host == "192.0.2.1"
            ? throw new HttpRequestException("dns", new SocketException((int)SocketError.HostNotFound))
            : new HttpResponseMessage(HttpStatusCode.NotFound)))) { Trace = trace };

        await Assert.ThrowsAsync<InstallException>(() => cache.EnsureAsync(manifest, PayloadManifest.RuntimeComponent));

        Assert.Equal(2, trace.Attempts.Count);
        var (first, second) = (trace.Attempts[0], trace.Attempts[1]);
        Assert.Equal(("dns", true, (int?)null), (first.Kind, first.FellThrough, first.Status));
        Assert.Contains("HostNotFound", PayloadCache.Chain(first.Error!), StringComparison.Ordinal);
        Assert.Contains("nothing to look up", first.Lookup, StringComparison.Ordinal);
        Assert.Equal(("http 404", false, (int?)404), (second.Kind, second.FellThrough, second.Status));
        Assert.Contains(trace.Files, f => f.File == Work.RuntimeName && f.What.Contains("fetched"));
        Assert.Contains(PayloadCache.CacheState(manifest, PayloadManifest.RuntimeComponent), s => s.State == "missing");
    }

    [Fact]
    public async Task TheWrongBytesAreRecordedAsTheWrongBytes()
    {
        var blob = Blob(21);
        var trace = new DownloadTrace();
        var cache = new PayloadCache(new HttpClient(new Answers(_ =>
            new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(Blob(22)) }))) { Trace = trace };

        await Assert.ThrowsAsync<InstallException>(() =>
            cache.EnsureAsync(At(Version("sh"), blob, "https://192.0.2.3/f"), PayloadManifest.RuntimeComponent));

        var attempt = Assert.Single(trace.Attempts);
        Assert.Equal(("sha mismatch", (int?)200, (long)blob.Length), (attempt.Kind, attempt.Status, attempt.Received));
    }

    [Fact]
    public async Task AResumeIsAnAttemptOfItsOwnAndSaysWhereItPickedUp()
    {
        var blob = Blob(23);
        var manifest = At(Version("rs"), blob, "https://192.0.2.4/f");
        var trace = new DownloadTrace();
        var cache = new PayloadCache(new HttpClient(new DropsOnce(blob))) { Trace = trace };

        await cache.EnsureAsync(manifest, PayloadManifest.RuntimeComponent);

        Assert.Equal(2, trace.Attempts.Count);
        Assert.NotNull(trace.Attempts[0].Kind);
        Assert.Equal(blob.Length / 2, trace.Attempts[0].Received);
        Assert.Equal(((ulong)blob.Length / 2, (string?)null), (trace.Attempts[1].ResumedFrom, trace.Attempts[1].Kind));
        Assert.Contains(PayloadCache.CacheState(manifest, PayloadManifest.RuntimeComponent),
            s => s.State.EndsWith("verified", StringComparison.Ordinal));
    }

    [Fact]
    public void EachFailureHasAKindALogCanBeSearchedBy()
    {
        Assert.Equal("dns", PayloadCache.Classify(new HttpRequestException("x", new SocketException((int)SocketError.HostNotFound))));
        Assert.Equal("refused", PayloadCache.Classify(new HttpRequestException("x", new SocketException((int)SocketError.ConnectionRefused))));
        Assert.Equal("never answered", PayloadCache.Classify(new HttpRequestException("x", new SocketException((int)SocketError.TimedOut))));
        Assert.Equal("never answered", PayloadCache.Classify(new TaskCanceledException("x", new TimeoutException())));
        Assert.Equal("stopped answering", PayloadCache.Classify(new TaskCanceledException("x")));
        Assert.Equal("tls", PayloadCache.Classify(new HttpRequestException("x", new AuthenticationException("y"))));
        Assert.Equal("http 503", PayloadCache.Classify(new HttpRequestException("x", null, HttpStatusCode.ServiceUnavailable)));
        Assert.Equal("cut", PayloadCache.Classify(new IOException("x", new SocketException((int)SocketError.ConnectionReset))));
        Assert.Equal("disk full", PayloadCache.Classify(new IOException("full", unchecked((int)0x80070070))));
        Assert.Contains("antivirus", PayloadCache.Classify(new UnauthorizedAccessException("denied")), StringComparison.Ordinal);
    }
}
