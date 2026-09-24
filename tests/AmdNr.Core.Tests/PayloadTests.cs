using System.Net;
using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>Shares one AppPaths.Cache with every other test in this collection, so it
/// runs alone: the cache-clearing test wipes the folder these read their caches out of.
/// </summary>
[Collection("AppCache")]
public class PayloadTests
{
    private const string Minimal = """
        {
          "schema": 1,
          "owner": "someone", "repo": "Extras", "tag": "payload-v1",
          "components": {
            "addon":   { "version": "0.5.0", "files": [
              { "name": "amd-nr.addon64", "size": 550400, "sha256": "203f0278b64c5786da2bb95e8635de86bf3b18a852f098c4c0378661e8b48cf7" } ] },
            "runtime": { "version": "0.3.0", "files": [
              { "name": "dlssnr_amd_pass1.dll", "size": 7290880, "sha256": "70af3fb757f83f71ec947ce461970fdecc9636864bc01d952abffb36ae310be6" },
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
        Assert.Equal("files/amd-nr.addon32", bridge.Single(f => f.Name == "amd-nr.addon32").RelativePath);
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

    /// <summary>A component re-cut and published under a version that did not change is the same
    /// number of bytes with different content. The component cache notices, because it hashes; the
    /// staging folder did not, because its key is the versions and it skipped any file whose size
    /// already matched. The install then failed verification against a file the app had just
    /// downloaded correctly -- reported from a real run, on an add-on that came out at 550912 bytes
    /// both times.</summary>
    [Fact]
    public async Task StagingFollowsBytesThatChangedUnderAVersionThatDidNot()
    {
        var version = $"st-{Guid.NewGuid():N}"[..12];
        var first = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var second = (byte[])first.Clone();
        second[0] ^= 0xFF; // Same length, different bytes: exactly what a size check cannot see.

        static PayloadManifest ManifestFor(string version, byte[] blob) => PayloadManifest.Parse($$"""
            {
              "schema": 1, "owner": "someone", "repo": "Extras", "tag": "{{version}}",
              "components": { "runtime": { "version": "{{version}}", "files": [
                { "name": "dlssnr_amd_pass1.dll", "size": {{blob.Length}}, "sha256": "{{Engine.Sha(blob)}}" } ] } }
            }
            """);

        var before = ManifestFor(version, first);
        var cache = new PayloadCache(new HttpClient(new BlobServer(first)));
        await cache.EnsureAsync(before, PayloadManifest.RuntimeComponent);
        var staged = cache.Stage(before, PayloadManifest.RuntimeComponent);
        Assert.Equal(first, await File.ReadAllBytesAsync(Path.Combine(staged, Work.RuntimeName)));

        var after = ManifestFor(version, second);
        var recut = new PayloadCache(new HttpClient(new BlobServer(second)));
        await recut.EnsureAsync(after, PayloadManifest.RuntimeComponent);
        var restaged = recut.Stage(after, PayloadManifest.RuntimeComponent);

        // The same folder, because the key is the versions and neither moved. What is in it has to
        // be the new bytes all the same.
        Assert.Equal(staged, restaged);
        Assert.Equal(second, await File.ReadAllBytesAsync(Path.Combine(restaged, Work.RuntimeName)));
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

    /// <summary>A host that accepts the connection and then never answers, which is the shape a
    /// dropped-rather-than-refused address takes. Everything else is served.</summary>
    private sealed class SilentServer(byte[] blob, string? silentHost) : HttpMessageHandler
    {
        public int Served { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancel)
        {
            if (silentHost is null || request.RequestUri!.Host == silentHost)
                await Task.Delay(Timeout.Infinite, cancel);
            Served++;
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(blob) };
        }
    }

    private static (PayloadManifest Manifest, byte[] Blob, string Component) MirroredManifest(string tag)
    {
        var blob = Enumerable.Range(0, 4096).Select(i => (byte)(i % 251)).ToArray();
        var json = $$"""
            {
              "schema": 1, "owner": "someone", "repo": "Extras", "tag": "{{tag}}",
              "components": { "runtime": { "version": "{{tag}}", "files": [
                { "name": "dlssnr_amd_pass1.dll", "size": {{blob.Length}}, "sha256": "{{Engine.Sha(blob)}}",
                  "url": "https://primary.example/pass1.dll",
                  "mirrors": [ "https://mirror.example/pass1.dll" ] } ] } }
            }
            """;
        return (PayloadManifest.Parse(json), blob, PayloadManifest.RuntimeComponent);
    }

    /// <summary>The whole point of having a mirror. A primary that refuses reports it as an
    /// HttpRequestException and always fell through; one that accepts and then goes quiet ends as a
    /// timeout, which is a TaskCanceledException -- a type the fall-through did not list, so the
    /// mirror was never tried and the exception escaped instead.</summary>
    [Fact]
    public async Task APrimaryThatGoesQuietFallsThroughToTheMirror()
    {
        var (manifest, blob, component) = MirroredManifest($"si-{Guid.NewGuid():N}"[..12]);
        var server = new SilentServer(blob, "primary.example");
        var cache = new PayloadCache(new HttpClient(server) { Timeout = TimeSpan.FromMilliseconds(250) });

        var dir = await cache.EnsureAsync(manifest, component);

        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(dir, Work.RuntimeName)));
        Assert.Equal(1, server.Served);
    }

    /// <summary>And when no address answers it has to arrive as an InstallException: that is the
    /// type every caller filters on -- the three EnsureAsync call sites are all async void handlers
    /// -- so a TaskCanceledException here is a lost window, not a message.</summary>
    [Fact]
    public async Task ATimeoutOnEveryAddressIsReportedAsAFailedDownload()
    {
        var (manifest, blob, component) = MirroredManifest($"sa-{Guid.NewGuid():N}"[..12]);
        var cache = new PayloadCache(new HttpClient(new SilentServer(blob, null))
        {
            Timeout = TimeSpan.FromMilliseconds(250),
        });

        var error = await Assert.ThrowsAsync<InstallException>(() => cache.EnsureAsync(manifest, component));
        // Nothing ever came back from either address, which is not the same as a download that stopped.
        Assert.Contains("never answered", error.Message, StringComparison.Ordinal);
    }

    /// <summary>The one call in here that deletes. It has to take the whole cache and leave the
    /// folder itself standing, and the figure it reports is what somebody decides by -- this test
    /// is in the same class as the downloads on purpose, because xUnit runs a class in order and
    /// the cache is shared by every test that touches it.</summary>
    [Fact]
    public async Task ClearingTheCacheEmptiesItAndSaysWhatWasFreed()
    {
        var (manifest, blob, component) = OneFileManifest($"cl-{Guid.NewGuid():N}"[..12]);
        await new PayloadCache(new HttpClient(new BlobServer(blob))).EnsureAsync(manifest, component);
        Assert.True(PayloadCache.IsComplete(manifest, component));

        var freed = PayloadCache.Clear();

        Assert.True(freed >= (ulong)blob.Length, $"{freed} bytes freed should cover the {blob.Length} cached");
        Assert.False(PayloadCache.IsComplete(manifest, component));
        Assert.Empty(Directory.GetFileSystemEntries(AppPaths.Cache));
        Assert.True(Directory.Exists(AppPaths.Cache), "the cache folder itself has to survive being emptied");
    }

    [Fact]
    public void AComponentNameFromAManifestCannotEscapeTheCacheFolder()
    {
        Assert.Throws<InstallException>(() => PayloadCache.FolderFor("..", "1"));
        Assert.Throws<InstallException>(() => PayloadCache.FolderFor("runtime", "../../windows"));
        Assert.StartsWith(AppPaths.Cache, PayloadCache.FolderFor("runtime", "0.3.0"), StringComparison.Ordinal);
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
                  { "name": "amd-nr.addon32", "path": "files/amd-nr.addon32",
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
        Assert.Equal(blob, await File.ReadAllBytesAsync(Path.Combine(staged, "files", "amd-nr.addon32")));

        // Staging again is free and changes nothing.
        Assert.Equal(staged, cache.Stage(manifest, "runtime", "bridge"));

        // And it is the shape Work reads: payloads found without a files\ hop for the x64 route.
        Assert.Equal(staged, Work.PayloadDir(staged));
    }

    [Fact]
    public void TheAppDeclaresAVersionTheAutoUpdaterCanSettleOn()
    {
        // v0.3.0 shipped with <Version>0.2.0</Version> still in the csproj. The updater compares
        // the newest GitHub release tag against the running assembly's own version, so an exe that
        // under-reports itself is offered the same update forever: download, replace, restart,
        // still older than the tag, offer again.
        //
        // What a unit test can catch is a missing or default version here. The tag is not known
        // at build time, so the other half of the invariant lives in tools/check-release.ps1,
        // which compares this number against the release actually published.
        var csproj = FindUp("src/AmdNr.App/AmdNr.App.csproj");
        var text = File.ReadAllText(csproj);
        var m = System.Text.RegularExpressions.Regex.Match(text, @"<Version>([^<]+)</Version>");
        Assert.True(m.Success, "AmdNr.App must declare a <Version>, or the exe reports 1.0.0");
        Assert.True(Version.TryParse(m.Groups[1].Value, out var declared), m.Groups[1].Value);
        Assert.NotEqual(new Version(1, 0, 0), declared);
    }

    [Fact]
    public void BothRoutesInstallTheCompanionEffectFromOneImplementation()
    {
        // v0.3.0 shipped the effect on the 64-bit route only, because that was the only place
        // the lines existed: a 32-bit install succeeded, said every file was copied and verified,
        // and simply never had it. A user's report is what found it.
        //
        // Both routes now call AddCompanionEffect, so this exercises the real thing rather than
        // reading the source. A full 32-bit install cannot be faked in a test -- it verifies
        // ReShade against a pinned hash, on purpose -- but the file planning can.
        foreach (var layout in new[] { "files", "" })
        {
            var dir = Fixture.Temp($"effect-{(layout.Length == 0 ? "flat" : layout)}");
            var into = Path.Combine(dir, layout);
            Directory.CreateDirectory(into);
            var bytes = System.Text.Encoding.UTF8.GetBytes("// AMD_Neural_Feed");
            File.WriteAllBytes(Path.Combine(into, Work.ShaderName), bytes);

            var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            Work.AddCompanionEffect(files, dir);
            Assert.True(files.ContainsKey(Work.ShaderPath), $"not planned from the {layout} layout");
            Assert.Equal(bytes, files[Work.ShaderPath]);
            Assert.True(Engine.Allowed.Contains(Work.ShaderPath),
                "the path has to be allowed or the transaction refuses it");
        }

        // A payload without one is skipped, not an error: manifests published before the effect
        // was installable have no shader component.
        var empty = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        Work.AddCompanionEffect(empty, Fixture.Temp("effect-none"));
        Assert.Empty(empty);

        // And both routes have to ask for the component, or there is nothing on disk to plan.
        var ui = File.ReadAllText(FindUp("src/AmdNr.App/Sheet/GameSheet.Actions.cs"));
        var asks = ui.Split("ShaderComponent").Length - 1;
        Assert.True(asks >= 2, $"ComponentsFor names ShaderComponent {asks} time(s); both routes need it");
    }

    [Fact]
    public void AManifestWithoutTheShaderComponentIsSkippedNotFatal()
    {
        // Naming ShaderComponent in the download list made it mandatory: a manifest published
        // before the companion effect existed answered 'The payload manifest has no shader
        // component' and the whole install stopped, three times over, for a file the add-on works
        // without. Has() is what the caller filters with so the component stays optional.
        var m = PayloadManifest.Parse(Minimal);
        Assert.False(m.Has(PayloadManifest.ShaderComponent));
        Assert.True(m.Has(PayloadManifest.AddonComponent));
        Assert.Throws<InstallException>(() => m.Component(PayloadManifest.ShaderComponent));

        var wanted = new[] { PayloadManifest.AddonComponent, PayloadManifest.RuntimeComponent,
                             PayloadManifest.ShaderComponent };
        Assert.Equal(2, wanted.Where(m.Has).Count());

        // And the pins still come out, with the effect simply absent.
        Assert.Equal(string.Empty, m.Pins().ShaderSha);
    }

    /// <summary>Walks up from the test binary to the repository root, because the working
    /// directory under `dotnet test` is bin/, not the checkout.</summary>
    private static string FindUp(string relative)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, relative.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException(relative);
    }
}
