using System.Net;
using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class PayloadTests
{
    private const string Minimal = """
        {
          "schema": 1,
          "owner": "someone", "repo": "Extras", "tag": "payload-v1",
          "components": {
            "addon":   { "version": "0.5.0", "files": [
              { "name": "dlss5-neural.addon64", "size": 550400, "sha256": "203f0278b64c5786da2bb95e8635de86bf3b18a852f098c4c0378661e8b48cf7" } ] },
            "runtime": { "version": "0.2.17", "files": [
              { "name": "dlssnr_amd_pass1.dll", "size": 7248384, "sha256": "ddd82d313aa74c2e7602d17dfb7e7cd90cca9bfc0306f581684d35d75d1b350b" },
              { "name": "dlssnr_on_amd_weights.bin", "size": 147689451, "sha256": "6bf8dc931ef3ccffe18c82de26ab374156e7f19539ffcf8eabaa25dca5cf15ab" } ] }
          }
        }
        """;

    [Fact]
    public void TheManifestBecomesThePinsAnInstallChecksAgainst()
    {
        var pins = PayloadManifest.Parse(Minimal).Pins();
        Assert.Equal(Engine.RuntimeSha, pins.RuntimeSha);
        Assert.Equal(Engine.WeightsSha, pins.WeightsSha);
        Assert.Equal(147_689_451ul, pins.WeightsSize);
        Assert.Equal(550_400ul, pins.AddonSize);
    }

    [Fact]
    public void AssetUrlsPointAtTheReleaseThatPublishesThem()
    {
        var m = PayloadManifest.Parse(Minimal);
        var file = m.Component(PayloadManifest.RuntimeComponent).Files[0];
        Assert.Equal("https://github.com/someone/Extras/releases/download/payload-v1/dlssnr_amd_pass1.dll",
            m.DownloadUrl(PayloadManifest.RuntimeComponent, file).ToString());
    }

    /// <summary>The manifest is fetched over the network and it names the path the cache writes to,
    /// so a path that climbs out of the cache has to be refused when it is read, not when it is
    /// written.</summary>
    [Theory]
    [InlineData("\"path\": \"../../evil.dll\",")]
    [InlineData("\"path\": \"files/../../evil.dll\",")]
    [InlineData("\"path\": \"C:/Windows/System32/evil.dll\",")]
    [InlineData("\"path\": \"a/b/c/evil.dll\",")]
    public void APathThatClimbsOutOfTheCacheIsRefused(string injected)
    {
        var tampered = Minimal.Replace("{ \"name\": \"dlssnr_amd_pass1.dll\",",
            "{ " + injected + " \"name\": \"dlssnr_amd_pass1.dll\",");
        Assert.Throws<InstallException>(() => PayloadManifest.Parse(tampered));
    }

    [Fact]
    public void AManifestFromTheFutureIsRefusedRatherThanGuessedAt()
    {
        Assert.Throws<InstallException>(() => PayloadManifest.Parse(Minimal.Replace("\"schema\": 1", "\"schema\": 2")));
        Assert.Throws<InstallException>(() => PayloadManifest.Parse("{ \"schema\": 1, \"components\": {} }"));
        Assert.Throws<InstallException>(() => PayloadManifest.Parse("not json at all"));
    }

    [Fact]
    public void AComponentThatIsNotThereIsNamed()
    {
        var m = PayloadManifest.Parse(Minimal);
        Assert.Contains("bridge", Assert.Throws<InstallException>(() => m.Component("bridge")).Message,
            StringComparison.Ordinal);
    }

    /// <summary>The manifest that actually ships. If a hash here drifts from the add-on's own
    /// constants, every install would be refused at copy time -- so it is checked at build time
    /// instead.</summary>
    [Fact]
    public void TheShippedManifestAgreesWithTheAddOnsOwnConstants()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "payload.json");
        Assert.True(File.Exists(path), "payload.json should be copied next to the tests");

        var m = PayloadManifest.Parse(File.ReadAllText(path));
        var pins = m.Pins();
        Assert.Equal(Engine.RuntimeSha, pins.RuntimeSha);
        Assert.Equal(Engine.WeightsSha, pins.WeightsSha);

        var extras = m.Component("x86-extras").Files;
        Assert.Equal(Engine.ReShadeSha, extras.Single(f => f.Name == "dxgi.dll").Sha256);
        Assert.Equal(Engine.D3d8To9Sha, extras.Single(f => f.Name == "d3d8to9.dll").Sha256);

        // The bridge keeps the shape its own installer reads: payload.sha256 at the root, the pair
        // under files\.
        var bridge = m.Component("bridge").Files;
        Assert.Equal("payload.sha256", bridge.Single(f => f.Name == "payload.sha256").RelativePath);
        Assert.Equal("files/dlss5-neural.addon32", bridge.Single(f => f.Name == "dlss5-neural.addon32").RelativePath);
    }

    // -- Downloading ------------------------------------------------------------------------------

    /// <summary>Serves one blob, honours Range, and counts how many bytes it was asked for -- which
    /// is how "it resumed" is told apart from "it started over".</summary>
    private sealed class BlobServer(byte[] blob) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public long BytesServed { get; private set; }
        public bool IgnoreRange { get; set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            Requests++;
            var from = IgnoreRange ? 0 : request.Headers.Range?.Ranges.FirstOrDefault()?.From ?? 0;
            if (from >= blob.Length)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestedRangeNotSatisfiable));

            var slice = blob.AsSpan((int)from).ToArray();
            BytesServed += slice.Length;
            return Task.FromResult(new HttpResponseMessage(
                from > 0 ? HttpStatusCode.PartialContent : HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(slice),
            });
        }
    }

    private static (PayloadManifest Manifest, byte[] Blob, string Component) OneFileManifest(string tag)
    {
        var blob = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var json = $$"""
            {
              "schema": 1, "owner": "someone", "repo": "Extras", "tag": "{{tag}}",
              "components": { "runtime": { "version": "{{tag}}", "files": [
                { "name": "dlssnr_amd_pass1.dll", "size": {{blob.Length}}, "sha256": "{{Engine.Sha(blob)}}" } ] } }
            }
            """;
        return (PayloadManifest.Parse(json), blob, PayloadManifest.RuntimeComponent);
    }

    [Fact]
    public async Task ADownloadIsVerifiedAndKept()
    {
        var (manifest, blob, component) = OneFileManifest($"dl-{Guid.NewGuid():N}"[..12]);
        var server = new BlobServer(blob);
        var cache = new PayloadCache(new HttpClient(server));

        var dir = await cache.EnsureAsync(manifest, component);
        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(dir, Work.RuntimeName)));
        Assert.True(PayloadCache.IsComplete(manifest, component));
        Assert.Empty(Directory.GetFiles(dir, "*.part"));

        // Asking again costs nothing: it is already there and it already hashes right.
        await cache.EnsureAsync(manifest, component);
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public async Task AnInterruptedDownloadResumesInsteadOfStartingOver()
    {
        var (manifest, blob, component) = OneFileManifest($"rs-{Guid.NewGuid():N}"[..12]);
        var dir = PayloadCache.FolderFor(component, manifest.Component(component).Version);
        Directory.CreateDirectory(dir);
        // Half a file from a download that was cut off.
        await File.WriteAllBytesAsync(Path.Combine(dir, Work.RuntimeName + ".part"), blob[..2048]);

        var server = new BlobServer(blob);
        await new PayloadCache(new HttpClient(server)).EnsureAsync(manifest, component);

        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(dir, Work.RuntimeName)));
        Assert.Equal(2048, server.BytesServed);
    }

    /// <summary>A server that ignores Range and sends the whole file must not produce a spliced
    /// file that is half old bytes and half new ones.</summary>
    [Fact]
    public async Task AServerThatIgnoresTheRangeStartsTheFileAgain()
    {
        var (manifest, blob, component) = OneFileManifest($"ig-{Guid.NewGuid():N}"[..12]);
        var dir = PayloadCache.FolderFor(component, manifest.Component(component).Version);
        Directory.CreateDirectory(dir);
        await File.WriteAllBytesAsync(Path.Combine(dir, Work.RuntimeName + ".part"), blob[..2048]);

        var server = new BlobServer(blob) { IgnoreRange = true };
        await new PayloadCache(new HttpClient(server)).EnsureAsync(manifest, component);

        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(dir, Work.RuntimeName)));
    }

    [Fact]
    public async Task AFileThatDoesNotMatchItsHashIsThrownAwayNotKept()
    {
        var (manifest, blob, component) = OneFileManifest($"bad-{Guid.NewGuid():N}"[..12]);
        var corrupted = blob.ToArray();
        corrupted[100] ^= 0xff;

        var cache = new PayloadCache(new HttpClient(new BlobServer(corrupted)));
        var error = await Assert.ThrowsAsync<InstallException>(() => cache.EnsureAsync(manifest, component));
        Assert.Contains("does not match the SHA-256", error.Message, StringComparison.Ordinal);

        var dir = PayloadCache.FolderFor(component, manifest.Component(component).Version);
        Assert.False(File.Exists(Path.Combine(dir, Work.RuntimeName)), "a corrupt download must not be kept");
        Assert.Empty(Directory.GetFiles(dir, "*.part"));
    }

    [Fact]
    public async Task ProgressIsReportedWhileItDownloads()
    {
        var (manifest, blob, component) = OneFileManifest($"pg-{Guid.NewGuid():N}"[..12]);
        var seen = new List<DownloadProgress>();
        var cache = new PayloadCache(new HttpClient(new BlobServer(blob)));

        await cache.EnsureAsync(manifest, component, new Progress<DownloadProgress>(p =>
        {
            lock (seen) seen.Add(p);
        }));

        Assert.NotEmpty(seen);
        Assert.Equal(Work.RuntimeName, seen[^1].File);
        Assert.Equal(blob.Length, seen[^1].Received);
    }

    [Fact]
    public void AComponentNameFromAManifestCannotEscapeTheCacheFolder()
    {
        Assert.Throws<InstallException>(() => PayloadCache.FolderFor("..", "1"));
        Assert.Throws<InstallException>(() => PayloadCache.FolderFor("runtime", "../../windows"));
        Assert.StartsWith(AppPaths.Cache, PayloadCache.FolderFor("runtime", "0.2.17"), StringComparison.Ordinal);
    }

    // -- Staging ----------------------------------------------------------------------------------

    /// <summary>What an install actually reads from: one folder holding several components, built
    /// by hard-linking out of the cache. The 141 MB of weights must not be copied a second time on
    /// the way there, and the bridge's files\ layout has to survive the trip.</summary>
    [Fact]
    public async Task StagingBuildsOneFolderOutOfSeveralComponents()
    {
        var blob = Enumerable.Range(0, 2048).Select(i => (byte)(i % 97)).ToArray();
        var tag = $"st-{Guid.NewGuid():N}"[..12];
        var json = $$"""
            {
              "schema": 1, "owner": "someone", "repo": "Extras", "tag": "{{tag}}",
              "components": {
                "runtime": { "version": "{{tag}}", "files": [
                  { "name": "dlssnr_amd_pass1.dll", "size": {{blob.Length}}, "sha256": "{{Engine.Sha(blob)}}" } ] },
                "bridge":  { "version": "{{tag}}", "files": [
                  { "name": "dlss5-neural.addon32", "path": "files/dlss5-neural.addon32",
                    "size": {{blob.Length}}, "sha256": "{{Engine.Sha(blob)}}" } ] }
              }
            }
            """;

        var manifest = PayloadManifest.Parse(json);
        var cache = new PayloadCache(new HttpClient(new BlobServer(blob)));
        await cache.EnsureAsync(manifest, "runtime");
        await cache.EnsureAsync(manifest, "bridge");

        var staged = cache.Stage(manifest, "runtime", "bridge");
        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(staged, Work.RuntimeName)));
        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(staged, "files", "dlss5-neural.addon32")));

        // Staging again is free and changes nothing.
        Assert.Equal(staged, cache.Stage(manifest, "runtime", "bridge"));

        // And it is the shape Work reads: payloads found without a files\ hop for the x64 route.
        Assert.Equal(staged, Work.PayloadDir(staged));
    }
}
