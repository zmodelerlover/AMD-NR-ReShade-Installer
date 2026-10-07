// The manifest format is a compatibility surface, not an internal detail. Installs performed by
// installer-x86 and by the Rust installer already exist on disk and read what this writes, so Encode
// keeps their layout byte for byte; the round-trip test against a literal captured from the C++
// writer guards that. Decode reads it as JSON and checks every field, so a manifest that says the
// same thing in another layout -- reformatted by an editor, CRLF, a byte order mark -- still reads.
// Requiring the exact bytes turned all of those into "modified or unsupported manifest", a folder
// that could be neither installed over nor uninstalled (Need for Speed, 28/09).

using System.Text;
using System.Text.Json;

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
    /// and written back as it was read, never as the number this build happens to use: the older
    /// installers read these files too. A fresh install writes Current; an upgrade moves it to
    /// Current, because it has just written the current pair.</summary>
    public const int Current = 3;
    public int BridgeProtocol { get; set; } = Current;

    /// <summary>The ReShade this install runs with: the pinned build unless it is somebody else's Vulkan layer,
    /// which is named with its version and path. Read back as written; nothing decides anything on it.</summary>
    public const string PinnedReShade = "6.8.0.2156 Full Add-on Support";
    public string ReShade { get; set; } = PinnedReShade;

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
            // Escaped, because a layer's path has backslashes; the pinned one comes out as it always did. Not
            // JsonSerializer.Serialize: the published exe is trimmed with reflection off, and that threw
            // InvalidOperationException on every install in v0.8.1.
            .Append("\"dgVoodoo\":\"none\",\n\"ReShade\":\"").Append(JsonEncodedText.Encode(m.ReShade).ToString())
            .Append("\",\n\"files\":[\n");
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
    /// existed, and Current is then the answer.</summary>
    private static int Protocol(JsonElement root) =>
        root.TryGetProperty("bridge_protocol", out var p) && p.ValueKind == JsonValueKind.Number
        && p.TryGetInt32(out var value) && value is > 0 and < 100
            ? value
            : Current;

    /// <summary>A string field, empty when it is not there. Anything but a string there is refused.</summary>
    private static string Text(JsonElement o, string key)
    {
        if (!o.TryGetProperty(key, out var v)) return string.Empty;
        Engine.Require(v.ValueKind == JsonValueKind.String, $"Bad manifest field: {key}");
        return v.GetString() ?? string.Empty;
    }

    private static bool Flag(JsonElement o, string key)
    {
        if (!o.TryGetProperty(key, out var v)) return false;
        Engine.Require(v.ValueKind is JsonValueKind.True or JsonValueKind.False, $"Bad manifest field: {key}");
        return v.GetBoolean();
    }

    /// <summary>Read as JSON, whatever its layout, and every field checked: the names against
    /// <see cref="Engine.Allowed"/>, the hashes as hashes, a backup only inside the backup folder and
    /// under its own name. Those checks are what stop a tampered manifest from reaching any file this
    /// app does not own; the layout never did.</summary>
    public static Manifest Decode(string s)
    {
        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(Engine.Trim(s), new JsonDocumentOptions
            {
                AllowTrailingCommas = true,
                CommentHandling = JsonCommentHandling.Skip,
            });
            root = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new InstallException("Unreadable install manifest: it is not complete JSON");
        }
        Engine.Require(root.ValueKind == JsonValueKind.Object, "Unreadable install manifest: it is not complete JSON");
        Engine.Require(root.TryGetProperty("schema", out var schema) && schema.ValueKind == JsonValueKind.Number
                       && schema.TryGetInt32(out var version) && version == 1, "Unknown manifest schema");

        var preset = Text(root, "preset");
        var routeName = Text(root, "route");
        Engine.Require(routeName is "" or "x64", "Bad manifest route");
        var route = routeName == "x64" ? Route.X64 : Route.X86;
        // Adding a name here is backward compatible in the direction that matters: every manifest
        // already on disk still decodes, and one written by a newer build is refused by an older
        // one -- which is the honest answer, since an older build has no route to uninstall it
        // with. "FiveM" is the newest.
        var known = route == Route.X86
            ? preset is "D3D11" or "D3D9" or "D3D8" or "OpenGL"
            : preset is "PCSX2" or "RPCS3" or "D3D11" or "D3D12" or "Vulkan" or "OpenGL" or "OptiScaler" or "FiveM";
        Engine.Require(known, "Bad manifest preset");

        var state = Text(root, "state");
        Engine.Require(state is "installed" or "installing", "Bad manifest state");

        var m = new Manifest(preset, route) { State = state, BridgeProtocol = Protocol(root) };
        if (Text(root, "ReShade") is { Length: > 0 } reShade) m.ReShade = reShade;
        var seen = new HashSet<string>(StringComparer.Ordinal);

        List<JsonElement> rows = [];
        if (root.TryGetProperty("files", out var files))
        {
            Engine.Require(files.ValueKind == JsonValueKind.Array, "Malformed manifest entries");
            rows = [.. files.EnumerateArray()];
        }
        Engine.Require(rows.Count <= Engine.MaxManifestEntries, "Malformed manifest entries");
        foreach (var row in rows)
        {
            Engine.Require(row.ValueKind == JsonValueKind.Object, "Malformed manifest entries");
            var e = new Entry
            {
                Name = Text(row, "name"),
                Hash = Text(row, "sha256"),
                Backup = Text(row, "backup"),
                BackupHash = Text(row, "backup_sha256"),
                Owned = Flag(row, "owned"),
                Configuration = Flag(row, "configuration"),
            };

            Engine.Require(Engine.IsHex(e.Hash, 64), "Unsafe/duplicate manifest entry");
            // Anybody's DLL an install moved to the backup, under its own name: only ever put back.
            var displaced = Engine.IsPlainDll(e.Name) && e.Hash == Engine.Sha([]) && e.Backup.Length > 0;
            Engine.Require((Engine.IsAllowed(e.Name) || displaced) && seen.Add(e.Name),
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
        // "dgVoodoo" and "ReShade" are read by nobody: the original fork's "2.87.4" there is still an
        // install to take back, and this build never writes anything but "none".
        return m;
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
