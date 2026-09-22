using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>An install records what it wrote; the payload says what is current. These are the four
/// answers that decision can have, and the one that matters is the third: a folder installed before
/// the bridge was rebuilt has to say so on its own, because nobody re-reads a changelog.</summary>
public class OutdatedTests
{
    private const string Payload = """
        {
          "schema": 1,
          "owner": "someone", "repo": "Extras", "tag": "payload-v1",
          "components": {
            "addon":  { "version": "0.6.5", "files": [
              { "name": "amd-nr.addon64", "size": 588288, "sha256": "aa11111111111111111111111111111111111111111111111111111111111111" } ] },
            "bridge": { "version": "0.6.6", "files": [
              { "name": "amd-nr.addon32", "path": "files/amd-nr.addon32", "size": 259584, "sha256": "bb22222222222222222222222222222222222222222222222222222222222222" },
              { "name": "amd-nr-host64.exe", "path": "files/amd-nr-host64.exe", "size": 468480, "sha256": "cc33333333333333333333333333333333333333333333333333333333333333" } ] }
          }
        }
        """;

    private static string Folder(string tag, params (string Name, string Hash, bool Config)[] entries)
    {
        var dir = Fixture.Temp(tag);
        var manifest = new Manifest("D3D9", Route.X86);
        foreach (var (name, hash, config) in entries)
            manifest.Entries.Add(new Entry
            {
                Name = name, Configuration = config, Hash = hash, Owned = true,
            });
        Manifest.WriteAtomic(dir, manifest);
        return dir;
    }

    [Fact]
    public void AnInstallOfTheCurrentPayloadIsNotOutdated()
    {
        var dir = Folder("current",
            ("amd-nr.addon32", "bb22222222222222222222222222222222222222222222222222222222222222", false),
            ("amd-nr-host64.exe", "cc33333333333333333333333333333333333333333333333333333333333333", false));
        Assert.False(Work.PayloadMovedOn(dir, PayloadManifest.Parse(Payload)));
    }

    [Fact]
    public void AnInstallFromBeforeTheBridgeWasRebuiltIsOutdated()
    {
        var dir = Folder("stale",
            ("amd-nr.addon32", "dd44444444444444444444444444444444444444444444444444444444444444", false),
            ("amd-nr-host64.exe", "cc33333333333333333333333333333333333333333333333333333333333333", false));
        Assert.True(Work.PayloadMovedOn(dir, PayloadManifest.Parse(Payload)));
    }

    /// <summary>ReShade goes in under the API's own name and the ini is the user's. Neither is
    /// something the payload pins, so neither may make a tile ask to be reinstalled.</summary>
    [Fact]
    public void FilesThePayloadDoesNotPinAreNotAskedAbout()
    {
        var dir = Folder("unpinned",
            ("d3d9.dll", "ee55555555555555555555555555555555555555555555555555555555555555", false),
            ("amd-nr.ini", "ff66666666666666666666666666666666666666666666666666666666666666", true));
        Assert.False(Work.PayloadMovedOn(dir, PayloadManifest.Parse(Payload)));
    }

    [Fact]
    public void AFolderWithNoManifestOfItsOwnIsNotOutdated() =>
        Assert.False(Work.PayloadMovedOn(Fixture.Temp("nothing"), PayloadManifest.Parse(Payload)));
}
