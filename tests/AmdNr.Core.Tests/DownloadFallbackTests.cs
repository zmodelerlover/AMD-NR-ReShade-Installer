using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>Everything a download does when the first address is not the answer: files already on
/// the machine, a folder somebody filled by hand, a connection that drops part way, an address that
/// cannot be reached at all. In the cache's collection, because every one of these reads it.</summary>
[Collection("AppCache")]
public class DownloadFallbackTests
{
    private static byte[] Blob(int seed) => Enumerable.Range(0, 4096).Select(i => (byte)((i + seed) % 251)).ToArray();

    private static PayloadManifest OneFile(string version, byte[] blob, string? url = null, string? mirror = null) =>
        PayloadManifest.Parse($$"""
            {
              "schema": 1, "owner": "someone", "repo": "Extras", "tag": "{{version}}",
              "components": { "runtime": { "version": "{{version}}", "files": [
                { "name": "dlssnr_amd_pass1.dll", "size": {{blob.Length}}, "sha256": "{{Engine.Sha(blob)}}"
                  {{(url is null ? "" : $", \"url\": \"{url}\"")}}
                  {{(mirror is null ? "" : $", \"mirrors\": [\"{mirror}\"]")}} } ] } }
            }
            """);

    private static string Version(string tag) => $"{tag}-{Guid.NewGuid():N}"[..14];

    /// <summary>Counts requests, and fails every one: a test that passes with this never went to
    /// the network.</summary>
    private sealed class Refusing : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests++;
            throw new HttpRequestException("no network in this test", new SocketException((int)SocketError.HostNotFound));
        }
    }

    [Fact]
    public async Task AFileAlreadyNearbyIsTakenInsteadOfDownloaded()
    {
        var blob = Blob(1);
        var manifest = OneFile(Version("nb"), blob);
        var nearby = Fixture.Temp("nearby");
        await File.WriteAllBytesAsync(Path.Combine(nearby, Work.RuntimeName), blob);

        var server = new Refusing();
        var dir = await new PayloadCache(new HttpClient(server), [nearby]).EnsureAsync(manifest, PayloadManifest.RuntimeComponent);

        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(dir, Work.RuntimeName)));
        Assert.Equal(0, server.Requests);
        Assert.True(File.Exists(Path.Combine(nearby, Work.RuntimeName)), "the copy somebody downloaded is theirs to keep");
    }

    [Fact]
    public async Task ANearbyFileThatIsNotThePinnedOneIsLeftAlone()
    {
        var blob = Blob(2);
        var manifest = OneFile(Version("nw"), blob);
        var nearby = Fixture.Temp("nearby-wrong");
        var wrong = Blob(3);
        await File.WriteAllBytesAsync(Path.Combine(nearby, Work.RuntimeName), wrong);

        var cache = new PayloadCache(new HttpClient(new Refusing()), [nearby]);
        var error = await Assert.ThrowsAsync<InstallException>(() => cache.EnsureAsync(manifest, PayloadManifest.RuntimeComponent));
        Assert.Contains("could not be looked up", error.Message, StringComparison.Ordinal);
    }

    /// <summary>A version bump that did not change this file is no reason to download it again.</summary>
    [Fact]
    public async Task TheSameFileUnderAnotherVersionIsReused()
    {
        var blob = Blob(4);
        var old = OneFile(Version("ov"), blob);
        var dir = PayloadCache.FolderFor(PayloadManifest.RuntimeComponent, old.Component("runtime").Version);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, Work.RuntimeName), blob);

        var server = new Refusing();
        var bumped = OneFile(Version("nv"), blob);
        var moved = await new PayloadCache(new HttpClient(server), []).EnsureAsync(bumped, PayloadManifest.RuntimeComponent);

        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(moved, Work.RuntimeName)));
        Assert.Equal(0, server.Requests);
    }

    /// <summary>The last resort somebody asked for in so many words: "I can get the files myself, but
    /// I don't know where they go". They go wherever they like; the app finds them, a few folders
    /// deep, and takes what hashes right.</summary>
    [Fact]
    public void AFolderFilledByHandIsImported()
    {
        var blob = Blob(5);
        var manifest = OneFile(Version("im"), blob);
        var picked = Fixture.Temp("picked");
        var deep = Path.Combine(picked, "release", "files");
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, Work.RuntimeName), blob);
        File.WriteAllText(Path.Combine(picked, "readme.txt"), "not a payload");

        var result = PayloadCache.Import(manifest, [PayloadManifest.RuntimeComponent], picked);

        Assert.Equal([Work.RuntimeName], result.Taken);
        Assert.Empty(result.Missing);
        Assert.True(PayloadCache.IsComplete(manifest, PayloadManifest.RuntimeComponent));
    }

    /// <summary>A browser names a file after the address it came from: the mirror's copy of the weights
    /// lands as s1sh5d.bin, and a second download of anything as "name (1).ext". The app's own words
    /// are "names do not need changing", so neither the Downloads check nor an import may care.</summary>
    [Fact]
    public async Task AFileSavedUnderAnotherNameIsFoundByItsSize()
    {
        var blob = Blob(16);
        var nearby = Fixture.Temp("nearby-renamed");
        await File.WriteAllBytesAsync(Path.Combine(nearby, "s1sh5d.bin"), blob);
        await File.WriteAllBytesAsync(Path.Combine(nearby, "same size, other bytes.bin"), Blob(17));
        var server = new Refusing();
        var dir = await new PayloadCache(new HttpClient(server), [nearby])
            .EnsureAsync(OneFile(Version("rn"), blob), PayloadManifest.RuntimeComponent);
        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(dir, Work.RuntimeName)));
        Assert.Equal(0, server.Requests);

        var other = Blob(18);
        var picked = Fixture.Temp("picked-renamed");
        File.WriteAllBytes(Path.Combine(picked, "dlssnr_amd_pass1 (1).dll"), other);
        var manifest = OneFile(Version("ri"), other);
        Assert.Empty(PayloadCache.Import(manifest, [PayloadManifest.RuntimeComponent], picked).Missing);
    }

    [Fact]
    public void AnImportSaysWhatItStillCouldNotFind()
    {
        var manifest = OneFile(Version("mi"), Blob(6));
        var result = PayloadCache.Import(manifest, [PayloadManifest.RuntimeComponent], Fixture.Temp("picked-empty"));
        Assert.Empty(result.Taken);
        Assert.Equal([Work.RuntimeName], result.Missing);
    }

    private static (PayloadManifest Manifest, byte[] Archive, byte[] Inner) Archived(string version)
    {
        var inner = Blob(7);
        using var buffer = new MemoryStream();
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var entry = zip.CreateEntry("ReShade64.dll").Open();
            entry.Write(inner);
        }
        var archive = buffer.ToArray();
        var manifest = PayloadManifest.Parse($$"""
            {
              "schema": 1, "components": { "reshade": { "version": "{{version}}",
                "files": [ { "name": "setup.exe", "size": {{archive.Length}}, "sha256": "{{Engine.Sha(archive)}}",
                             "url": "https://reshade.example/setup.exe" } ],
                "extract": [ { "name": "ReShade64.dll", "size": {{inner.Length}}, "sha256": "{{Engine.Sha(inner)}}" } ] } }
            }
            """);
        return (manifest, archive, inner);
    }

    /// <summary>What a person downloads for ReShade is its setup, and what an install reads is the DLL
    /// inside it. Either one handed over does the job.</summary>
    [Fact]
    public async Task AnImportTakesTheArchiveOrWhatIsInsideIt()
    {
        var (manifest, archive, inner) = Archived(Version("ar"));
        var withArchive = Fixture.Temp("picked-archive");
        await File.WriteAllBytesAsync(Path.Combine(withArchive, "setup.exe"), archive);
        Assert.Empty(PayloadCache.Import(manifest, ["reshade"], withArchive).Missing);
        Assert.True(PayloadCache.IsComplete(manifest, "reshade"));

        var (loose, _, looseInner) = Archived(Version("ls"));
        var withDll = Fixture.Temp("picked-dll");
        await File.WriteAllBytesAsync(Path.Combine(withDll, "ReShade64.dll"), looseInner);
        Assert.Empty(PayloadCache.Import(loose, ["reshade"], withDll).Missing);

        // And the archive it came out of is not then fetched for nothing.
        var server = new Refusing();
        await new PayloadCache(new HttpClient(server)).EnsureAsync(loose, "reshade");
        Assert.Equal(0, server.Requests);
        Assert.Equal(inner, looseInner);
    }

    /// <summary>Different reasons at different addresses, each named: "the download failed" is what
    /// nobody can do anything with.</summary>
    [Fact]
    public async Task EveryAddressIsNamedWithWhatWentWrongThere()
    {
        var manifest = OneFile(Version("why"), Blob(8), "https://primary.example/f", "https://mirror.example/f");
        var cache = new PayloadCache(new HttpClient(new Answers(request => request.RequestUri!.Host == "primary.example"
            ? throw new HttpRequestException("dns", new SocketException((int)SocketError.HostNotFound))
            : new HttpResponseMessage(HttpStatusCode.NotFound))));

        var error = await Assert.ThrowsAsync<InstallException>(() => cache.EnsureAsync(manifest, PayloadManifest.RuntimeComponent));
        Assert.Contains("primary.example: the address could not be looked up", error.Message, StringComparison.Ordinal);
        Assert.Contains("mirror.example: ", error.Message, StringComparison.Ordinal);
        Assert.Contains("404", error.Message, StringComparison.Ordinal);
    }

    /// <summary>Not reaching the server at all -- what a stale DNS cache causes, and clearing it can
    /// fix -- is told apart from an answer that came back wrong, which it cannot. Only the first is
    /// offered the fix.</summary>
    [Fact]
    public async Task AFailureToReachTheServerIsToldApartFromAWrongAnswer()
    {
        var unreachable = new PayloadCache(new HttpClient(new Refusing()));
        var dns = await Assert.ThrowsAsync<InstallException>(() =>
            unreachable.EnsureAsync(OneFile(Version("net"), Blob(12), "https://gone.example/f"), PayloadManifest.RuntimeComponent));
        Assert.True(dns.Network);

        var missing = new PayloadCache(new HttpClient(new Answers(_ => new HttpResponseMessage(HttpStatusCode.NotFound))));
        var notFound = await Assert.ThrowsAsync<InstallException>(() =>
            missing.EnsureAsync(OneFile(Version("404"), Blob(13), "https://here.example/f"), PayloadManifest.RuntimeComponent));
        Assert.False(notFound.Network);

        var list = await Assert.ThrowsAsync<InstallException>(() => unreachable.FetchManifestAsync(["https://gone.example/p.json"]));
        Assert.True(list.Network);

        // Clearing it is Windows' call: whatever Windows says is an answer, not an exception.
        _ = PayloadCache.FlushDns();
    }

    /// <summary>A connection cut part way is the network, not the disk: it read as "the file could not
    /// be written -- an antivirus can block this", which sent people to the wrong fix. .NET hands it
    /// over as an IOException, the same type a write that failed is.</summary>
    [Fact]
    public void AConnectionCutPartWayIsNotAFileThatCouldNotBeWritten()
    {
        var reset = new IOException("Unable to read data", new SocketException((int)SocketError.ConnectionReset));
        var ended = new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.");
        var early = new HttpRequestException("reset", new IOException("x", new SocketException((int)SocketError.ConnectionReset)));
        foreach (var cut in new Exception[] { reset, ended, early })
        {
            Assert.Contains("connection was cut", PayloadCache.Describe(cut), StringComparison.Ordinal);
            Assert.True(PayloadCache.IsNetwork(cut));
        }
        var tls = new HttpRequestException(HttpRequestError.SecureConnectionError, "tls", new IOException("x"));
        Assert.Contains("secure connection", PayloadCache.Describe(tls), StringComparison.Ordinal);
        Assert.Contains("could not be written", PayloadCache.Describe(new IOException("denied")), StringComparison.Ordinal);
    }

    /// <summary>A full disk is not the server's fault. It was taken for one: the part was thrown away
    /// and the mirror downloaded it again, into the same full disk, and the message blamed an
    /// antivirus. It stops at once, keeps what arrived, and says which drive.</summary>
    [Fact]
    public async Task AFullDiskStopsAtOnceAndSaysSo()
    {
        var requests = 0;
        var manifest = OneFile(Version("full"), Blob(15), "https://primary.example/f", "https://mirror.example/f");
        var cache = new PayloadCache(new HttpClient(new Answers(_ =>
        {
            requests++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DiskFull()) };
        })));
        var error = await Assert.ThrowsAsync<InstallException>(() => cache.EnsureAsync(manifest, PayloadManifest.RuntimeComponent));
        Assert.Contains("full", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, requests);
    }

    private sealed class DiskFull : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default) =>
            throw new IOException("There is not enough space on the disk.", unchecked((int)0x80070070));
    }

    /// <summary>A verified file gone by the time the install stages it is an antivirus, and says so,
    /// rather than "Could not find file" and a cache path.</summary>
    [Fact]
    public void AFileTakenAfterItWasVerifiedIsPutDownToTheAntivirus()
    {
        var manifest = OneFile(Version("av"), Blob(14));
        var error = Assert.Throws<InstallException>(() =>
            new PayloadCache(new HttpClient(new Refusing())).Stage(manifest, PayloadManifest.RuntimeComponent));
        Assert.Contains("antivirus", error.Message, StringComparison.Ordinal);
    }

    private sealed class Answers(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel) =>
            Task.FromResult(answer(request));
    }

    /// <summary>Sends the first half and then drops the connection, once; after that, honours Range.</summary>
    private sealed class DropsOnce(byte[] blob) : HttpMessageHandler
    {
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests++;
            var from = (int)(request.Headers.Range?.Ranges.First().From ?? 0);
            HttpContent content = Requests == 1
                ? new StreamContent(new Dropping(blob[..(blob.Length / 2)]))
                : new ByteArrayContent(blob[from..]);
            return Task.FromResult(new HttpResponseMessage(from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = content,
            });
        }

        private sealed class Dropping(byte[] half) : MemoryStream(half)
        {
            public override int Read(byte[] buffer, int offset, int count)
            {
                var read = base.Read(buffer, offset, count);
                return read > 0 ? read : throw new IOException("connection reset");
            }

            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancel = default)
            {
                var read = await base.ReadAsync(buffer, cancel);
                return read > 0 ? read : throw new IOException("connection reset");
            }
        }
    }

    /// <summary>A connection that drops after half the file is picked up again where it stopped, on
    /// the same address, rather than being reported as a failed download.</summary>
    [Fact]
    public async Task AConnectionThatDropsPartWayIsResumed()
    {
        var blob = Blob(9);
        var manifest = OneFile(Version("dr"), blob, "https://only.example/f");
        var server = new DropsOnce(blob);

        var dir = await new PayloadCache(new HttpClient(server)).EnsureAsync(manifest, PayloadManifest.RuntimeComponent);

        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(dir, Work.RuntimeName)));
        Assert.Equal(2, server.Requests);
    }

    /// <summary>A hash already worked out is not worked out again, and a file that changed is.</summary>
    [Fact]
    public void AHashIsRememberedUntilTheFileChanges()
    {
        var file = Path.Combine(Fixture.Temp("memo"), "f.bin");
        var first = Blob(10);
        File.WriteAllBytes(file, first);
        Assert.True(PayloadCache.Verified(file, (ulong)first.Length, Engine.Sha(first)));

        var second = Blob(11);
        File.WriteAllBytes(file, second);
        File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
        Assert.False(PayloadCache.Verified(file, (ulong)first.Length, Engine.Sha(first)));
        Assert.True(PayloadCache.Verified(file, (ulong)second.Length, Engine.Sha(second)));
    }

    /// <summary>The bug this exists for: an address that goes nowhere ahead of one that works. .NET
    /// waited out the first and never reached the second.</summary>
    [Fact]
    public async Task AnAddressThatGoesNowhereDoesNotHoldUpOneThatWorks()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptTcpClientAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            // 2001:db8::/32 is reserved for documentation: nothing ever answers there.
            await using var stream = await PayloadCache.ConnectAsync(
                [IPAddress.Parse("2001:db8::1"), IPAddress.Loopback], port, timeout.Token);

            Assert.True(stream.CanWrite);
            using var accepted = await accept;
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task TheListIsReadFromTheSecondAddressWhenTheFirstIsUnreachable()
    {
        var json = PayloadCache.Embedded("payload.json");
        Assert.NotNull(json);
        var http = new HttpClient(new Answers(request => request.RequestUri!.Host == "first.example"
            ? throw new HttpRequestException("dns", new SocketException((int)SocketError.HostNotFound))
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json!) }));

        var manifest = await new PayloadCache(http).FetchManifestAsync(["https://first.example/p.json", "https://second.example/p.json"]);
        Assert.True(manifest.Has(PayloadManifest.RuntimeComponent));
    }

    /// <summary>The copy built into the executable is the one in the repository, so a lone executable
    /// knows exactly what one beside its json files knows.</summary>
    [Fact]
    public void TheBuiltInListIsTheOneThatShips()
    {
        var shipped = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "payload", "payload.json");
        Assert.Equal(File.ReadAllText(shipped), PayloadCache.Embedded("payload.json"));
        Assert.NotNull(PayloadCache.Embedded("api-db.json"));
    }
}
