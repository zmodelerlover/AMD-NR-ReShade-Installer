// The 32-bit bridge route. Ported from the Installer struct in installer/src/engine.rs.
//
// Unlike the x64 route, the payloads here are pinned by the release's own payload.sha256 rather
// than by constants: the bridge frontend and its 64-bit helper have to be the pair that shipped
// together, and coupling is proved by hash, not by embedding bytes.

using System.Text;

namespace AmdNr.Core;

public sealed class X86Installer(string release)
{
    public string Release { get; } = release;
    public uint Width { get; set; } = 1920;
    public uint Height { get; set; } = 1080;
    public List<string> Log { get; } = [];

    /// <summary>The pins this route checks, the engine's own unless a caller says otherwise. The same
    /// knob <see cref="PayloadPins"/> is for the 64-bit route: without it nothing but the genuine
    /// 150 MB of payloads could ever be installed, and the round trip went untested.</summary>
    public string RuntimeSha { get; init; } = Engine.RuntimeSha;
    public string WeightsSha { get; init; } = Engine.WeightsSha;
    public string ReShadeSha { get; init; } = Engine.ReShadeSha;
    public string D3d8To9Sha { get; init; } = Engine.D3d8To9Sha;

    public void Note(string s) => Log.Add(s);

    /// <summary>The release zip keeps every payload under files\; a staged folder built out of the
    /// cache keeps the runtime and weights at its root, beside payload.sha256. Both are the same
    /// bytes checked against the same hash, so both shapes are read.</summary>
    private byte[] Payload(string name, string expected)
    {
        var nested = Path.Combine(Release, "files", name);
        var bytes = Engine.Read(File.Exists(nested) ? nested : Path.Combine(Release, name));
        Engine.HashIs(bytes, expected, name);
        return bytes;
    }

    /// <summary>Reads the release's own checksum list so the bridge pair is pinned to the build it
    /// shipped with.</summary>
    internal static string BridgeSum(string sums, string name)
    {
        foreach (var line in sums.Split('\n'))
        {
            var at = line.IndexOf("  ", StringComparison.Ordinal);
            if (at < 0) continue;
            var hash = Engine.Lower(line[..at]);
            var file = line[(at + 2)..].Trim();
            if (file == name && Engine.IsHex(hash, 64)) return hash;
        }
        throw new InstallException("Missing bridge release checksum");
    }

    /// <summary>Everything that would be written, with nothing written. Every payload hash, the PE
    /// machine type and the chaining rule are decided here, so a refusal happens before any file
    /// moves.</summary>
    public SortedDictionary<string, byte[]> Plan(string target, string preset, string? proxyName = null)
    {
        Engine.Require(preset is "D3D11" or "D3D9" or "D3D8", "Unsupported x86 preset");
        Engine.SafePath(target);
        Engine.Require(Engine.Machine(Engine.Read(target)) == Engine.MachineX86,
            "Target must be PE32/x86; x64 targets are not supported");
        var dir = Engine.InstallDirectory(target);
        var p = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        // Say what the folder is, not that a file could not be read. Getting field 1 wrong is the
        // ordinary mistake here, and "Cannot read ...\payload.sha256" tells nobody what to do.
        var manifest = Path.Combine(Release, "payload.sha256");
        Engine.Require(File.Exists(manifest),
            $"{Release} does not look like the unpacked download: it has no payload.sha256 beside a "
            + "files folder. Point it at the folder you unzipped.");
        var sums = Encoding.UTF8.GetString(Engine.Read(manifest));

        foreach (var name in new[] { "amd-nr.addon32", "amd-nr-host64.exe" })
        {
            var bytes = Payload(name, BridgeSum(sums, name));
            var want = name.Contains("addon32", StringComparison.Ordinal)
                ? Engine.MachineX86
                : Engine.MachineX64;
            Engine.Require(Engine.Machine(bytes) == want, "Wrong bridge architecture");
            p[name] = bytes;
        }

        p["dlssnr_amd_pass1.dll"] = Payload("dlssnr_amd_pass1.dll", RuntimeSha);
        p["dlssnr_on_amd_weights.bin"] = Payload("dlssnr_on_amd_weights.bin", WeightsSha);

        if (preset == "D3D8")
        {
            var translator = Payload("d3d8to9.dll", D3d8To9Sha);
            Engine.Require(Engine.Machine(translator) == Engine.MachineX86, "d3d8to9 must be x86");
            var name = "d3d8.dll";
            var existing = Path.Combine(dir, name);
            Engine.SafePath(existing);
            if (File.Exists(existing) && Engine.HashFile(existing) != D3d8To9Sha)
            {
                Engine.Require(Engine.AdvertisesD3d8Sidecar(Engine.Read(existing)),
                    "Existing d3d8.dll does not advertise d3d8R.dll chaining; preserved");
                name = "d3d8R.dll";
            }
            p[name] = translator;
        }

        // The name asked for when the caller resolved one, and the API's own name otherwise. This
        // route used to hardcode the second, so the "ReShade loads as" menu was drawn for a 32-bit
        // game, accepted a choice, and then wrote the other name anyway.
        var reShadeName = proxyName is { Length: > 0 } ? proxyName : Engine.X86ProxyName(preset);
        Engine.Require(Engine.Allowed.Contains(reShadeName), $"Unsupported ReShade proxy name: {reShadeName}");
        byte[] reShade;
        if (File.Exists(Path.Combine(Release, "files", "dxgi.dll")))
        {
            reShade = Payload("dxgi.dll", ReShadeSha);
        }
        else
        {
            var existing = Path.Combine(dir, reShadeName);
            Engine.Require(File.Exists(existing),
                $"ReShade is not installed for this API: there is no {reShadeName} in the game folder. "
                + "Install ReShade 6.8.0.2156 with full add-on support, 32-bit, against the game's own "
                + "executable and pick the API it uses.");
            reShade = Engine.Read(existing);
            // Having *a* ReShade is not the same as having the one this was tested against, and the
            // difference is invisible unless it is said out loud.
            Engine.Require(Engine.Sha(reShade) == ReShadeSha,
                $"The {reShadeName} already in the game folder is a different build from the one this "
                + "was tested with. It has to be ReShade 6.8.0.2156 with full add-on support, 32-bit "
                + "-- a newer version is refused too, not just an older one.");
        }
        Engine.Require(Engine.Machine(reShade) == Engine.MachineX86, "ReShade must be x86");
        p[reShadeName] = reShade;

        var tuning = Path.Combine(dir, "amd-nr.ini");
        Engine.SafePath(tuning);
        if (!File.Exists(tuning)) p["amd-nr.ini"] = Encoding.UTF8.GetBytes(Engine.FreshIni());

        // The companion effect, the same way the other route does it.
        Work.AddCompanionEffect(p, Release, dir);

        var ini = Path.Combine(dir, "ReShade.ini");
        Engine.SafePath(ini);
        var before = File.Exists(ini) ? Encoding.UTF8.GetString(Engine.Read(ini)) : string.Empty;
        // The same preparation the x64 route does, through the same function. This one used to dock
        // the panel and stop there, so an add-on the person had unticked in ReShade's Add-ons tab
        // stayed unticked: the install reported success, every file was correct, and nothing loaded.
        var after = Work.ReadyReShadeIni(before, Width, Height, Engine.PanelTitle32);
        if (before != after) p["ReShade.ini"] = Encoding.UTF8.GetBytes(after);

        return p;
    }

    public void Install(string target, string preset, string? proxyName = null)
    {
        var dir = Engine.InstallDirectory(target);
        Engine.SafePath(dir);
        var desired = Plan(Engine.Absolute(target), preset, proxyName);
        Transaction.Apply(dir, preset, Route.X86, desired, Log);
        // The same sweep the x64 route has done since the rename, and this route needs it more:
        // a 32-bit folder set up before v0.6.5 still has dlss5-neural.addon32 in it, ReShade loads
        // every .addon32 it finds, and two add-ons on one present is two overlays and two helpers.
        if (Work.SweepDead(dir, Note) is { Count: > 0 } swept)
            Note($"removed {swept.Count} file(s) an older install left behind: {string.Join(", ", swept)}");
        var how = preset == "D3D8"
            ? $"d3d8to9 {Engine.D3d8To9Version} -> native D3D9 frontend"
            : "native frontend";
        Note($"Installed {preset} x86; {how}; same-frame protocol v3");
    }

    public void Uninstall(string directory, bool removeConfigs) =>
        Transaction.Uninstall(directory, Route.X86, removeConfigs, Log);
}
