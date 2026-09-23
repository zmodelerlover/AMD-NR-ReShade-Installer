// The manifest format is a compatibility surface, not an internal detail. Installs performed by
// installer-x86 and by the Rust installer already exist on disk, and Decode accepts a manifest only
// when re-encoding reproduces the file byte for byte. Any change to spacing, key order or
// punctuation in Encode makes every existing install unreadable, which reads as "modified or
// unsupported manifest" to the user. The round-trip test against a literal captured from the C++
// writer is what guards that.

using System.Text;

namespace AmdNr.Core;

public sealed class Entry
{
    public required string Name { get; init; }
    public string Hash { get; set; } = string.Empty;
    public string Backup { get; set; } = string.Empty;
    public string BackupHash { get; set; } = string.Empty;
    public bool Owned { get; set; }
    public bool Configuration { get; init; }

    public Entry Clone() => new()
    {
        Name = Name,
        Hash = Hash,
        Backup = Backup,
        BackupHash = BackupHash,
        Owned = Owned,
        Configuration = Configuration,
    };

    public override bool Equals(object? o) =>
        o is Entry e && e.Name == Name && e.Hash == Hash && e.Backup == Backup
        && e.BackupHash == BackupHash && e.Owned == Owned && e.Configuration == Configuration;

    public override int GetHashCode() => HashCode.Combine(Name, Hash, Backup, BackupHash, Owned, Configuration);
}

public sealed class Manifest(string preset, Route route)
{
    public string Preset { get; set; } = preset;
    public string State { get; set; } = "installed";
    public Route Route { get; set; } = route;
    public List<Entry> Entries { get; set; } = [];

    /// <summary>What the bridge protocol was when this install was written. Carried on the object
    /// and written back as it was read, never as the number this build happens to use: Decode
    /// accepts a manifest only when Encode reproduces it byte for byte, so emitting today's number
    /// over one an older install wrote makes every manifest on disk unreadable -- and unreadable
    /// means uninstall refuses and an upgrade cannot see what it owns. A fresh install writes
    /// Current; an upgrade moves it to Current, because it has just written the current pair.</summary>
    public const int Current = 3;
    public int BridgeProtocol { get; set; } = Current;

    public override bool Equals(object? o) =>
        o is Manifest m && m.Preset == Preset && m.State == State && m.Route == Route
        && m.Entries.SequenceEqual(Entries);

    public override int GetHashCode() => HashCode.Combine(Preset, State, Route, Entries.Count);

    /// <summary>Byte for byte the layout installer-x86/core.h writes. See the file comment:
    /// changing any character here orphans every manifest already on disk.</summary>
    public static string Encode(Manifest m)
    {
        var o = new StringBuilder();
        o.Append("{\n\"schema\":1,\n\"preset\":\"").Append(m.Preset)
            .Append("\",\n\"state\":\"").Append(m.State).Append("\",\n");
        // Emitted only for x64, so every x86 manifest already on disk still round-trips byte for byte.
        if (m.Route == Route.X64) o.Append("\"route\":\"x64\",\n");
        o.Append($"\"bridge_protocol\":{m.BridgeProtocol},\n")
            .Append("\"dgVoodoo\":\"none\",\n\"ReShade\":\"6.8.0.2156 Full Add-on Support\",\n\"files\":[\n");
        for (var i = 0; i < m.Entries.Count; i++)
        {
            var e = m.Entries[i];
            o.Append($"{{\"name\":\"{e.Name}\",\"sha256\":\"{e.Hash}\",\"backup\":\"{e.Backup}\",")
                .Append($"\"backup_sha256\":\"{e.BackupHash}\",\"owned\":{Bool(e.Owned)},")
                .Append($"\"configuration\":{Bool(e.Configuration)}}}")
                .Append(i + 1 == m.Entries.Count ? "" : ",").Append('\n');
        }
        o.Append("]\n}\n");
        return o.ToString();

        // Rust prints booleans lowercase; C# prints them capitalised. That difference alone would
        // orphan every manifest on disk.
        static string Bool(bool b) => b ? "true" : "false";
    }

    /// <summary>The bridge protocol as written. Missing means a manifest from before the field
    /// existed, and Current is then the only answer that can round-trip.</summary>
    private static int Protocol(string s)
    {
        const string needle = "\"bridge_protocol\":";
        var at = s.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return Current;
        var rest = s[(at + needle.Length)..];
        var end = 0;
        while (end < rest.Length && char.IsAsciiDigit(rest[end])) end++;
        return end > 0 && int.TryParse(rest[..end], out var value) && value is > 0 and < 100
            ? value
            : Current;
    }

    private static string? Field(string s, string key)
    {
        var needle = $"\"{key}\":\"";
        var at = s.IndexOf(needle, StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = s[(at + needle.Length)..];
        var end = rest.IndexOf('"');
        return end < 0 ? null : rest[..end];
    }

    /// <summary>Parsed structurally, then validated by re-encoding: a manifest is accepted only
    /// when Encode reproduces it exactly. That single check is what makes hand-editing detectable
    /// without having to enumerate every way a file could be tampered with.</summary>
    public static Manifest Decode(string s)
    {
        Engine.Require(s.Contains("\"schema\":1,", StringComparison.Ordinal), "Unknown manifest schema");
        var preset = Field(s, "preset") ?? string.Empty;
        var route = s.Contains("\"route\":\"x64\",", StringComparison.Ordinal) ? Route.X64 : Route.X86;
        // Adding a name here is backward compatible in the direction that matters: every manifest
        // already on disk still decodes, and one written by a newer build is refused by an older
        // one -- which is the honest answer, since an older build has no route to uninstall it
        // with. "OpenGL" is the newest.
        var known = route == Route.X86
            ? preset is "D3D11" or "D3D9" or "D3D8"
            : preset is "PCSX2" or "RPCS3" or "D3D11" or "D3D12" or "Vulkan" or "OpenGL";
        Engine.Require(known, "Bad manifest preset");

        var state = Field(s, "state") ?? string.Empty;
        Engine.Require(state is "installed" or "installing", "Bad manifest state");

        var m = new Manifest(preset, route) { State = state, BridgeProtocol = Protocol(s) };
        var seen = new HashSet<string>(StringComparer.Ordinal);

        var rows = s.Split("{\"name\":\"");
        for (var i = 1; i < rows.Length; i++)
        {
            var brace = rows[i].IndexOf('}');
            if (brace < 0) continue;
            var row = "{\"name\":\"" + rows[i][..(brace + 1)];

            var name = Field(row, "name") ?? string.Empty;
            var e = new Entry
            {
                Name = name,
                Hash = Field(row, "sha256") ?? string.Empty,
                Backup = Field(row, "backup") ?? string.Empty,
                BackupHash = Field(row, "backup_sha256") ?? string.Empty,
                Owned = row.Contains("\"owned\":true", StringComparison.Ordinal),
                Configuration = row.Contains("\"configuration\":true", StringComparison.Ordinal),
            };

            Engine.Require(Engine.IsHex(e.Hash, 64), "Unsafe/duplicate manifest entry");
            Engine.Require(Engine.Allowed.Contains(e.Name) && seen.Add(e.Name),
                "Unsafe/duplicate manifest entry");
            Engine.Require(e.Configuration == Engine.IsConfig(e.Name), "Manifest config mismatch");

            if (e.Backup.Length > 0)
            {
                // Either directory: the current one, or the one installs before v0.6.5 used.
                var prefix = $"{Engine.BackupDir}/";
                var legacy = $"{Engine.LegacyBackupDir}/";
                if (!e.Backup.StartsWith(prefix, StringComparison.Ordinal)
                    && e.Backup.StartsWith(legacy, StringComparison.Ordinal))
                    prefix = legacy;
                var stamped = false;
                if (e.Backup.StartsWith(prefix, StringComparison.Ordinal))
                {
                    var tail = e.Backup[prefix.Length..];
                    var slash = tail.IndexOf('/');
                    if (slash > 0)
                    {
                        var stamp = tail[..slash];
                        stamped = stamp.All(char.IsAsciiDigit) && tail[(slash + 1)..] == e.Name;
                    }
                }
                Engine.Require(stamped && Engine.IsHex(e.BackupHash, 64) && e.Owned, "Unsafe backup entry");
            }

            m.Entries.Add(e);
        }

        var nameCount = Count(s, "\"name\":");
        Engine.Require(nameCount == m.Entries.Count && nameCount <= Engine.Allowed.Count,
            "Malformed manifest entries");

        var canonical = Encode(m);
        var supported = canonical == s;
        // The original fork used dgVoodoo for both translated presets. Preserve its manifests for
        // uninstall/recovery, but never create another one or carry that wrapper into a new install.
        if (!supported && m.Preset is "D3D8" or "D3D9")
        {
            var index = canonical.IndexOf("\"dgVoodoo\":\"none\"", StringComparison.Ordinal);
            if (index >= 0)
            {
                var legacy = canonical[..index] + "\"dgVoodoo\":\"2.87.4\""
                             + canonical[(index + "\"dgVoodoo\":\"none\"".Length)..];
                supported = legacy == s;
            }
        }
        Engine.Require(supported, "Modified or unsupported install manifest");
        return m;
    }

    private static int Count(string haystack, string needle)
    {
        var n = 0;
        for (var i = haystack.IndexOf(needle, StringComparison.Ordinal);
             i >= 0;
             i = haystack.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            n++;
        return n;
    }

    /// <summary>The journal only means anything if it lands before the writes it describes, so the
    /// manifest is written to a temporary file and moved over the old one through the filesystem's
    /// own replace.</summary>
    public static void WriteAtomic(string dir, Manifest m)
    {
        var name = m.Route.ManifestFileName();
        var finalPath = Path.Combine(dir, name);
        var tmp = Path.Combine(dir, $"{name}.tmp");
        Engine.SafePath(tmp);
        Engine.Write(tmp, Encoding.UTF8.GetBytes(Encode(m)));
        Engine.CommitRename(tmp, finalPath);
    }
}
