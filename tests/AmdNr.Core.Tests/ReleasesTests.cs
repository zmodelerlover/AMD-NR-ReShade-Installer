using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class ReleasesTests
{
    /// <summary>The shape GitHub actually answers with, cut down to the fields that are read. The
    /// v0.5.0 entry is the real one: an add-on published loose, the pair only inside the archive.
    /// </summary>
    private const string ReleasesJson = """
    [
      {
        "tag_name": "v0.5.1", "name": "v0.5.1", "draft": false, "prerelease": false,
        "published_at": "2026-09-16T00:00:00Z",
        "assets": [
          { "name": "dlss5-neural.addon64", "size": 550400,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.5.1/dlss5-neural.addon64" },
          { "name": "dlss5-neural.addon32", "size": 257536,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.5.1/dlss5-neural.addon32" },
          { "name": "dlss5-neural-host64.exe", "size": 442368,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.5.1/dlss5-neural-host64.exe" },
          { "name": "payload.sha256", "size": 179,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.5.1/payload.sha256" },
          { "name": "SHA256SUMS.txt", "size": 400,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.5.1/SHA256SUMS.txt" }
        ]
      },
      {
        "tag_name": "v0.5.0", "name": "v0.5.0", "draft": false, "prerelease": false,
        "published_at": "2026-09-15T00:24:02Z",
        "assets": [
          { "name": "dlss5-neural-amd-v0.5.0.zip", "size": 1121878,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.5.0/dlss5-neural-amd-v0.5.0.zip" },
          { "name": "dlss5-neural.addon64", "size": 550400,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.5.0/dlss5-neural.addon64" },
          { "name": "SHA256SUMS.txt", "size": 181,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.5.0/SHA256SUMS.txt" }
        ]
      },
      {
        "tag_name": "v0.6.0", "name": "unfinished", "draft": true, "prerelease": false,
        "published_at": "2026-09-20T00:00:00Z",
        "assets": [
          { "name": "SHA256SUMS.txt", "size": 100,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.6.0/SHA256SUMS.txt" }
        ]
      },
      {
        "tag_name": "v0.4.2", "name": "too old", "draft": false, "prerelease": false,
        "published_at": "2026-09-12T02:09:20Z",
        "assets": [
          { "name": "dlss5-neural.addon64", "size": 550400,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.4.2/dlss5-neural.addon64" },
          { "name": "SHA256SUMS.txt", "size": 100,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.4.2/SHA256SUMS.txt" }
        ]
      },
      {
        "tag_name": "v0.5.2", "name": "unpinned", "draft": false, "prerelease": false,
        "published_at": "2026-09-18T00:00:00Z",
        "assets": [
          { "name": "dlss5-neural.addon64", "size": 550400,
            "browser_download_url": "https://github.com/o/r/releases/download/v0.5.2/dlss5-neural.addon64" }
        ]
      },
      { "tag_name": "media-v1", "name": "media", "draft": false, "prerelease": false, "assets": [] }
    ]
    """;

    private static string Hash(char c) => new(c, 64);

    private static AddonRelease Release(string version, params string[] files) => new()
    {
        Version = Version.Parse(version),
        Tag = $"v{version}",
        Title = $"v{version}",
        Published = DateTimeOffset.Parse("2026-09-16T00:00:00Z"),
        Assets = files.ToDictionary(f => f,
            f => new ReleaseAsset(f, $"https://github.com/o/r/releases/download/v{version}/{f}", 1024),
            StringComparer.OrdinalIgnoreCase),
        Sums = files.ToDictionary(f => f, f => Hash('a'), StringComparer.OrdinalIgnoreCase),
    };

    [Fact]
    public void OnlyReleasesThatAreOldEnoughFinishedAndPinnedAreOffered()
    {
        var found = AddonReleases.Parse(ReleasesJson);

        // Newest first, and by version rather than by the order GitHub listed them in.
        Assert.Equal(["v0.5.1", "v0.5.0"], found.Select(f => f.Bare.Tag));

        // v0.6.0 is a draft, v0.4.2 is older than the first installable release, v0.5.2 publishes
        // no SHA256SUMS.txt so nothing pins it, and media-v1 is not a version at all.
        Assert.DoesNotContain(found, f => f.Bare.Tag is "v0.6.0" or "v0.4.2" or "v0.5.2" or "media-v1");

        Assert.EndsWith("/v0.5.1/SHA256SUMS.txt", found[0].SumsUrl);
        Assert.Equal(550400ul, found[0].Bare.Assets["dlss5-neural.addon64"].Size);
    }

    /// <summary>Publishing a release has to be the whole of making it the default. This is the
    /// rule the version menu opens on, and the reason it is here rather than in the window is that
    /// "are you sure it will come up as 0.5.1" is not a question anybody should have to answer by
    /// reading the UI code.</summary>
    [Fact]
    public void ANewerReleaseBecomesTheDefaultUnlessThisGameWasPinnedToAnother()
    {
        // As the menu builds it: every offered version, newest first.
        List<Version> offered = [new(0, 5, 1), new(0, 5, 0)];

        // Nothing remembered, nothing chosen this session: the newest, which is the whole point.
        Assert.Equal(0, AddonReleases.Preferred(offered, null, null));
        Assert.Equal(new Version(0, 5, 1), offered[AddonReleases.Preferred(offered, null, null)]);

        // A game installed at 0.5.0 opens at 0.5.0, however many releases land after it.
        Assert.Equal(1, AddonReleases.Preferred(offered, "0.5.0", null));

        // Nothing remembered for this game, but the app is set to one: that one.
        Assert.Equal(1, AddonReleases.Preferred(offered, null, new Version(0, 5, 0)));

        // What the game remembers beats what the app is set to.
        Assert.Equal(1, AddonReleases.Preferred(offered, "0.5.0", new Version(0, 5, 1)));

        // A remembered version that is no longer published falls through to the newest rather
        // than leaving the sheet on nothing.
        Assert.Equal(0, AddonReleases.Preferred(offered, "0.4.9", null));
        Assert.Equal(0, AddonReleases.Preferred(offered, "not a version", null));

        // And with nothing to offer there is no selection to make.
        Assert.Equal(-1, AddonReleases.Preferred([], "0.5.1", null));
    }

    /// <summary>The same thing end to end, from the JSON GitHub answers with: a v0.5.1 that
    /// publishes the four files loose plus its sums is offered to both routes and sorts first, so
    /// it is what Preferred lands on.</summary>
    [Fact]
    public void AReleasePublishingEverythingLooseIsOfferedToBothRoutesAndComesFirst()
    {
        var found = AddonReleases.Parse(ReleasesJson);
        Assert.Equal("v0.5.1", found[0].Bare.Tag);

        var sums = new[] { Work.AddonName, Work.Addon32Name, Work.Host64Name, AddonReleases.BridgeSums }
            .ToDictionary(f => f, _ => Hash('a'), StringComparer.OrdinalIgnoreCase);
        var newest = new AddonRelease
        {
            Version = found[0].Bare.Version,
            Tag = found[0].Bare.Tag,
            Title = found[0].Bare.Title,
            Published = found[0].Bare.Published,
            PreRelease = found[0].Bare.PreRelease,
            Assets = found[0].Bare.Assets,
            Sums = sums,
        };

        Assert.True(newest.Covers(Route.X64), "the 64-bit route has to be offered it");
        Assert.True(newest.Covers(Route.X86), "and so does the bridge");
        Assert.Equal(new Version(0, 5, 1), newest.Version);
        Assert.Equal(0, AddonReleases.Preferred([newest.Version, new Version(0, 5, 0)], null, null));
    }

    [Fact]
    public void ATagThatIsNotAVersionIsNotOne()
    {
        Assert.Equal(new Version(0, 5, 1), AddonReleases.Version("v0.5.1"));
        Assert.Equal(new Version(0, 5, 1), AddonReleases.Version("0.5.1"));
        Assert.Equal(new Version(0, 5, 1), AddonReleases.Version("v0.5.1-rc1"));
        Assert.Null(AddonReleases.Version("media-v1"));
        Assert.Null(AddonReleases.Version(""));
    }

    [Fact]
    public void SumsAreReadTheWayShaTwoFiveSixSumWritesThem()
    {
        var sums = AddonReleases.ParseSums(
            $"{Hash('a')} *dlss5-neural.addon64\n" +
            $"{Hash('b')}  files/dlss5-neural.addon32\r\n" +
            "not a hash at all\n" +
            $"{Hash('c')} *payload.sha256\n");

        Assert.Equal(Hash('a'), sums["dlss5-neural.addon64"]);
        // Listed with the folder it sits in inside the archive; installed under its own name.
        Assert.Equal(Hash('b'), sums["dlss5-neural.addon32"]);
        Assert.Equal(Hash('c'), sums["payload.sha256"]);
        Assert.Equal(3, sums.Count);
    }

    [Fact]
    public void ARouteIsOnlyOfferedAVersionThatPublishesEverythingItInstalls()
    {
        var full = Release("0.5.1", Work.AddonName, Work.Addon32Name, Work.Host64Name, AddonReleases.BridgeSums);
        Assert.True(full.Covers(Route.X64));
        Assert.True(full.Covers(Route.X86));

        // v0.5.0's shape: the add-on loose, the bridge pair only inside the archive.
        var addonOnly = Release("0.5.0", Work.AddonName);
        Assert.True(addonOnly.Covers(Route.X64));
        Assert.False(addonOnly.Covers(Route.X86));
    }

    [Fact]
    public void ChoosingAVersionSwapsTheAddonAndTheBridgeAndLeavesEverythingElseAlone()
    {
        var manifest = PayloadManifest.Parse($$"""
        {
          "schema": 1,
          "components": {
            "addon":   { "version": "0.5.0", "files": [
              { "name": "{{Work.AddonName}}", "size": 550400, "sha256": "{{Hash('0')}}",
                "url": "https://example.invalid/addon64" } ] },
            "bridge":  { "version": "0.5.0", "files": [
              { "name": "{{Work.Addon32Name}}", "path": "files/{{Work.Addon32Name}}", "size": 257536,
                "sha256": "{{Hash('1')}}", "url": "https://example.invalid/addon32" } ] },
            "runtime": { "version": "0.2.17", "files": [
              { "name": "{{Work.RuntimeName}}", "size": 7248384, "sha256": "{{Hash('2')}}",
                "url": "https://example.invalid/runtime" },
              { "name": "{{Work.WeightsName}}", "size": 147689451, "sha256": "{{Hash('3')}}",
                "url": "https://example.invalid/weights" } ] }
          }
        }
        """);

        var release = Release("0.5.1", Work.AddonName, Work.Addon32Name, Work.Host64Name, AddonReleases.BridgeSums);
        var chosen = AddonReleases.With(manifest, release);

        var addon = chosen.Component(PayloadManifest.AddonComponent);
        Assert.Equal("0.5.1", addon.Version);
        Assert.Equal(Hash('a'), addon.Files[0].Sha256);
        Assert.Equal(1024ul, addon.Files[0].Size);
        Assert.StartsWith("https://github.com/o/r/releases/download/v0.5.1/", chosen.DownloadUrl(
            PayloadManifest.AddonComponent, addon.Files[0]).ToString());

        // The bridge pair still lands under files\, which is the shape the x86 installer reads.
        var bridge = chosen.Component(PayloadManifest.BridgeComponent);
        Assert.Equal([$"files/{Work.Addon32Name}", $"files/{Work.Host64Name}", AddonReleases.BridgeSums],
            bridge.Files.Select(f => f.RelativePath));

        // The runtime and the weights are not versioned with the add-on and are left exactly alone.
        var runtime = chosen.Component(PayloadManifest.RuntimeComponent);
        Assert.Equal("0.2.17", runtime.Version);
        Assert.Equal(Hash('2'), runtime.Files[0].Sha256);

        // And the pre-flight now judges the folder against the version that was chosen.
        Assert.Equal(Hash('a'), chosen.Pins().AddonSha);
        Assert.Equal(Hash('0'), manifest.Pins().AddonSha);

        // A release that does not publish the bridge pair leaves the manifest's own bridge in place,
        // so v0.5.0 stays installable on a 32-bit game from the files it did publish.
        var addonOnly = AddonReleases.With(manifest, Release("0.5.2", Work.AddonName));
        Assert.Equal("0.5.0", addonOnly.Component(PayloadManifest.BridgeComponent).Version);
        Assert.Equal("0.5.2", addonOnly.Component(PayloadManifest.AddonComponent).Version);
    }
}
