using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>An install records what it wrote; the payload says what is current. These are the
/// answers that decision can have, and two of them matter: an install from before the bridge was
/// rebuilt has to say so on its own, and an install that is perfectly current must never say it --
/// a badge that is always on is a badge nobody reads.</summary>
public class OutdatedTests
{
    private const string Addon32 = "bb22222222222222222222222222222222222222222222222222222222222222";
    private const string Host64 = "cc33333333333333333333333333333333333333333333333333333333333333";
    private const string Addon64 = "aa11111111111111111111111111111111111111111111111111111111111111";
    private const string ReShade32 = "dd44444444444444444444444444444444444444444444444444444444444444";

    /// <summary>The shape of the real manifest, including the trap: dxgi.dll is in x86-extras and
    /// is ReShade 32-bit, while a 64-bit install writes ReShade 64-bit under that same name.</summary>
    private const string Payload = """
        {
          "schema": 1,
          "owner": "someone", "repo": "Extras", "tag": "payload-v1",
          "components": {
            "addon":  { "version": "0.6.5", "files": [
              { "name": "amd-nr.addon64", "size": 588288, "sha256": "aa11111111111111111111111111111111111111111111111111111111111111" } ] },
            "bridge": { "version": "0.6.6", "files": [
              { "name": "amd-nr.addon32", "path": "files/amd-nr.addon32", "size": 259584, "sha256": "bb22222222222222222222222222222222222222222222222222222222222222" },
              { "name": "amd-nr-host64.exe", "path": "files/amd-nr-host64.exe", "size": 468480, "sha256": "cc33333333333333333333333333333333333333333333333333333333333333" } ] },
            "x86-extras": { "version": "1", "files": [
              { "name": "dxgi.dll", "path": "files/dxgi.dll", "size": 4398080, "sha256": "dd44444444444444444444444444444444444444444444444444444444444444" } ] }
          }
        }
        """;

    private static string Folder(string tag, Route route,
                                 params (string Name, string Hash, bool Config)[] entries)
    {
        var dir = Fixture.Temp(tag);
        var manifest = new Manifest(route == Route.X64 ? "D3D12" : "D3D9", route);
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
        var dir = Folder("current", Route.X86,
            ("amd-nr.addon32", Addon32, false), ("amd-nr-host64.exe", Host64, false));
        Assert.False(Work.PayloadMovedOn(dir, PayloadManifest.Parse(Payload)));
    }

    [Fact]
    public void AnInstallFromBeforeTheBridgeWasRebuiltIsOutdated()
    {
        var dir = Folder("stale", Route.X86,
            ("amd-nr.addon32", "ee55555555555555555555555555555555555555555555555555555555555555", false),
            ("amd-nr-host64.exe", Host64, false));
        Assert.True(Work.PayloadMovedOn(dir, PayloadManifest.Parse(Payload)));
    }

    /// <summary>The one that would have shouted at everybody: a 64-bit install writes ReShade 64-bit
    /// as dxgi.dll, the payload has a 32-bit ReShade under that same name for the other route, and
    /// a flat name-to-hash comparison marks a perfectly current install as out of date.</summary>
    [Fact]
    public void AnX64InstallIsNotJudgedAgainstThe32BitReShade()
    {
        var dir = Folder("x64-reshade", Route.X64,
            ("amd-nr.addon64", Addon64, false),
            ("dxgi.dll", "ff66666666666666666666666666666666666666666666666666666666666666", false));
        Assert.NotEqual(ReShade32, "ff66666666666666666666666666666666666666666666666666666666666666");
        Assert.False(Work.PayloadMovedOn(dir, PayloadManifest.Parse(Payload)));
    }

    /// <summary>And the 32-bit route does compare it, because there that name really is the payload's
    /// file -- so a ReShade the payload repins is noticed.</summary>
    [Fact]
    public void AnX86InstallIsJudgedAgainstTheOneItActuallyTook()
    {
        var current = Folder("x86-reshade-ok", Route.X86, ("dxgi.dll", ReShade32, false));
        Assert.False(Work.PayloadMovedOn(current, PayloadManifest.Parse(Payload)));

        var stale = Folder("x86-reshade-old", Route.X86,
            ("dxgi.dll", "ff66666666666666666666666666666666666666666666666666666666666666", false));
        Assert.True(Work.PayloadMovedOn(stale, PayloadManifest.Parse(Payload)));
    }

    /// <summary>ReShade under the API's own name and the ini are not the payload's to judge.</summary>
    [Fact]
    public void FilesThePayloadDoesNotPinAreNotAskedAbout()
    {
        var dir = Folder("unpinned", Route.X86,
            ("d3d9.dll", "ff66666666666666666666666666666666666666666666666666666666666666", false),
            ("amd-nr.ini", "ff66666666666666666666666666666666666666666666666666666666666666", true));
        Assert.False(Work.PayloadMovedOn(dir, PayloadManifest.Parse(Payload)));
    }

    [Fact]
    public void AFolderWithNoManifestOfItsOwnIsNotOutdated() =>
        Assert.False(Work.PayloadMovedOn(Fixture.Temp("nothing"), PayloadManifest.Parse(Payload)));

    /// <summary>A manifest this build cannot read is not an answer either way. It must not throw:
    /// this runs on the thread that draws the window, for every tile.</summary>
    [Fact]
    public void AManifestThatCannotBeReadIsNotOutdatedAndDoesNotThrow()
    {
        var dir = Fixture.Temp("mangled");
        File.WriteAllText(Path.Combine(dir, Engine.ManifestName), "{not a manifest at all");
        Assert.False(Work.PayloadMovedOn(dir, PayloadManifest.Parse(Payload)));
    }
}
