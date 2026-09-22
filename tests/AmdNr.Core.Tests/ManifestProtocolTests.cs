using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>A manifest is validated by re-encoding it and comparing byte for byte, so anything in
/// Encode that is fixed rather than carried orphans every manifest already on disk -- and an
/// orphaned manifest is an install that cannot be upgraded and cannot be uninstalled. The bridge
/// protocol number is the one field that moves, so these hold it to round-tripping.</summary>
public class ManifestProtocolTests
{
    private static readonly string WrittenByAnOlderInstall = """
        {
        "schema":1,
        "preset":"D3D9",
        "state":"installed",
        "bridge_protocol":2,
        "dgVoodoo":"none",
        "ReShade":"6.8.0.2156 Full Add-on Support",
        "files":[
        {"name":"amd-nr.addon32","sha256":"55e3769e53e50ea2eb98f31d7f6147d6061bfec67df0244b22e1bab60b4bf98a","backup":"","backup_sha256":"","owned":true,"configuration":false}
        ]
        }

        """.ReplaceLineEndings("\n");

    [Fact]
    public void AManifestFromTheReleaseBeforeThisOneStillDecodes()
    {
        var m = Manifest.Decode(WrittenByAnOlderInstall);
        Assert.Equal(2, m.BridgeProtocol);
        Assert.Equal("D3D9", m.Preset);
        Assert.Single(m.Entries);
        Assert.Equal(WrittenByAnOlderInstall, Manifest.Encode(m));
    }

    [Fact]
    public void AFreshManifestIsWrittenWithTheProtocolThisBuildSpeaks()
    {
        var m = new Manifest("D3D9", Route.X86);
        Assert.Equal(Manifest.Current, m.BridgeProtocol);
        Assert.Contains($"\"bridge_protocol\":{Manifest.Current},", Manifest.Encode(m), StringComparison.Ordinal);
    }

    /// <summary>Installing over a folder set up by an older release is the ordinary upgrade, and it
    /// is where the orphaned manifest would have shown up first: the install reads what is there
    /// before it writes, and a refusal there is an upgrade nobody can perform.</summary>
    [Fact]
    public void InstallingOverAnOlderManifestIsAnUpgradeNotARefusal()
    {
        var dir = Fixture.Temp("older-manifest");
        File.WriteAllText(Path.Combine(dir, Engine.ManifestName), WrittenByAnOlderInstall);
        var log = new List<string>();

        var desired = new SortedDictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["amd-nr.addon32"] = Encoding.UTF8.GetBytes("the rebuilt panel"),
        };
        Transaction.Apply(dir, "D3D9", Route.X86, desired, log);

        var after = Manifest.Decode(File.ReadAllText(Path.Combine(dir, Engine.ManifestName)));
        Assert.Equal(Manifest.Current, after.BridgeProtocol);
        Assert.Contains(after.Entries, e => e.Name == "amd-nr.addon32" && e.Owned);
    }

    /// <summary>And the other half of the same failure: the folder has to stay removable. The file
    /// carries the hash the manifest recorded, because uninstall deliberately keeps anything that
    /// has been changed since -- what is under test here is that the manifest is read at all.</summary>
    [Fact]
    public void AnOlderManifestCanStillBeUninstalled()
    {
        var dir = Fixture.Temp("older-uninstall");
        var addon = Path.Combine(dir, "amd-nr.addon32");
        const string content = "the add-on as this older install wrote it";
        File.WriteAllText(addon, content);
        File.WriteAllText(Path.Combine(dir, Engine.ManifestName),
            WrittenByAnOlderInstall.Replace(
                "55e3769e53e50ea2eb98f31d7f6147d6061bfec67df0244b22e1bab60b4bf98a",
                Engine.HashFile(addon), StringComparison.Ordinal));

        Transaction.Uninstall(dir, Route.X86, removeConfigs: true, []);

        Assert.False(File.Exists(addon), "an install written by an older release has to be removable");
    }
}
