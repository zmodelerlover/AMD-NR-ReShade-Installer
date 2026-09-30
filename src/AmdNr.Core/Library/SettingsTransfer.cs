// A game's NR settings in one file, to keep or to hand to somebody else: a zip with a note saying which
// route they are for, and the settings themselves.
//
// The ReShade route's are whole files the add-on and the runtime own. The OptiScaler route's live inside
// OptiScaler.ini, among the upscaler's and the game's own, so only the fork's sections travel, and they
// are written back key by key: importing somebody's NR tuning must not bring their frame generation or
// their spoofing with it.

using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace AmdNr.Core;

public sealed class SettingsTransferException(string message) : Exception(message);

public static class SettingsTransfer
{
    private const string NoteName = "amd-nr-settings.json";
    private const string OptiPart = "OptiScaler.nr.ini";
    public const string Extension = ".amdnr.zip";

    /// <summary>Whole files, for the ReShade route: the add-on's settings and the runtime's.</summary>
    private static readonly string[] ReShadeFiles = ["amd-nr.ini", "dlssnr_on_amd.ini"];

    /// <summary>The runtime's own file travels with the OptiScaler route too.</summary>
    private static readonly string[] OptiFiles = ["dlssnr_on_amd.ini"];

    /// <summary>The fork's sections of OptiScaler.ini: the network, its lighting and look passes, and FSR-RR.</summary>
    public static readonly string[] OptiSections = ["DlssNr", "AmdRtgi", "AmdLook", "FSR-RR"];

    /// <summary>Writes this folder's settings to <paramref name="zipPath"/>. Returns false, writing nothing,
    /// when there are none yet: the add-on and OptiScaler write theirs the first time the game runs.</summary>
    public static bool Export(string folder, RouteFamily route, string game, string appVersion, string zipPath)
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in route == RouteFamily.OptiScaler ? OptiFiles : ReShadeFiles)
            if (ReadText(Path.Combine(folder, name)) is { } text && text.Trim().Length > 0)
                files[name] = text;
        if (route == RouteFamily.OptiScaler && ReadText(Path.Combine(folder, Work.OptiScalerIni)) is { } ini
                                           && Sections(ini) is { Length: > 0 } part)
            files[OptiPart] = part;
        if (files.Count == 0) return false;

        var temp = zipPath + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            var note = JsonSerializer.Serialize(new Dictionary<string, string>
            {
                ["format"] = "1",
                ["route"] = route.ToString(),
                ["game"] = game,
                ["app"] = appVersion,
                ["made"] = DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"),
            }, CoreJson.Default.DictionaryStringString);
            Write(zip, NoteName, note);
            foreach (var (name, text) in files) Write(zip, name, text);
        }
        File.Move(temp, zipPath, overwrite: true);
        return true;
    }

    /// <summary>The route and game an export says it came from. Throws when the file is not one.</summary>
    public static (RouteFamily Route, string Game) Describe(string zipPath)
    {
        using var zip = Open(zipPath);
        var note = ReadNote(zip);
        return (note.Route, note.Game);
    }

    /// <summary>Puts an export's settings into this folder, after copying what they replace into
    /// <paramref name="backup"/>. Returns the files written. Throws when the file is not an export, or is
    /// one for the other route: an OptiScaler tuning means nothing to the add-on, and the other way round.</summary>
    public static IReadOnlyList<string> Import(string zipPath, string folder, RouteFamily route, string backup)
    {
        using var zip = Open(zipPath);
        var note = ReadNote(zip);
        if (note.Route != route) throw new WrongRouteException(note.Route);

        var allowed = route == RouteFamily.OptiScaler ? [.. OptiFiles, OptiPart] : ReShadeFiles;
        var incoming = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in zip.Entries)
            if (allowed.Contains(entry.FullName, StringComparer.OrdinalIgnoreCase))
                incoming[entry.FullName] = Read(entry);
        if (incoming.Count == 0) throw new SettingsTransferException("empty");

        // What each file becomes, worked out before anything is written.
        var writes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, text) in incoming)
        {
            if (!name.Equals(OptiPart, StringComparison.OrdinalIgnoreCase))
            {
                writes[name] = text;
                continue;
            }
            var target = Path.Combine(folder, Work.OptiScalerIni);
            var ini = ReadText(target) ?? throw new SettingsTransferException(Work.OptiScalerIni);
            writes[Work.OptiScalerIni] = Merge(ini, text);
        }

        Directory.CreateDirectory(backup);
        foreach (var name in writes.Keys)
        {
            var current = Path.Combine(folder, name);
            if (File.Exists(current)) File.Copy(current, Path.Combine(backup, name), overwrite: true);
        }
        foreach (var (name, text) in writes)
            File.WriteAllText(Path.Combine(folder, name), text, new UTF8Encoding(false));
        return [.. writes.Keys];
    }

    public sealed class WrongRouteException(RouteFamily route) : Exception(route.ToString())
    {
        public RouteFamily Route { get; } = route;
    }

    /// <summary>The fork's sections of an OptiScaler.ini, keys and values only. Comments stay behind: the
    /// ini they land in has its own, written for the version installed there.</summary>
    internal static string Sections(string ini)
    {
        var o = new StringBuilder();
        var keep = false;
        foreach (var raw in ini.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 1 && line[0] == '[' && line[^1] == ']')
            {
                keep = OptiSections.Contains(line[1..^1], StringComparer.OrdinalIgnoreCase);
                if (keep) o.Append('\n').Append(line).Append('\n');
            }
            else if (keep && line.Length > 0 && line[0] != ';' && line[0] != '#' && line.Contains('='))
            {
                o.Append(line).Append('\n');
            }
        }
        return o.ToString().Trim();
    }

    /// <summary>Each key of the exported sections set in <paramref name="ini"/>, in place, and nothing else
    /// touched. A section this ini does not have is a fork newer or older than the one installed; its keys
    /// are added all the same, and OptiScaler ignores what it does not know.</summary>
    internal static string Merge(string ini, string part)
    {
        var section = "";
        foreach (var raw in part.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length > 1 && line[0] == '[' && line[^1] == ']') section = line[1..^1];
            else if (section.Length > 0 && OptiSections.Contains(section, StringComparer.OrdinalIgnoreCase)
                     && line.IndexOf('=') is var eq and > 0)
                ini = Engine.SetIni(ini, section, line[..eq].Trim(), line[(eq + 1)..].Trim());
        }
        return ini;
    }

    private static ZipArchive Open(string zipPath)
    {
        try { return ZipFile.OpenRead(zipPath); }
        catch (InvalidDataException) { throw new SettingsTransferException("not a zip"); }
    }

    private static (RouteFamily Route, string Game) ReadNote(ZipArchive zip)
    {
        var entry = zip.GetEntry(NoteName) ?? throw new SettingsTransferException("no note");
        try
        {
            var note = JsonSerializer.Deserialize(Read(entry), CoreJson.Default.DictionaryStringString);
            if (note is null || !note.TryGetValue("route", out var route) || !Enum.TryParse<RouteFamily>(route, out var family))
                throw new SettingsTransferException("no route");
            return (family, note.TryGetValue("game", out var game) ? game : "");
        }
        catch (JsonException) { throw new SettingsTransferException("bad note"); }
    }

    private static string Read(ZipArchiveEntry entry)
    {
        // A settings file is a few kilobytes; anything past a megabyte is not one.
        if (entry.Length > 1024 * 1024) throw new SettingsTransferException("too large");
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static void Write(ZipArchive zip, string name, string text)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(text));
    }

    private static string? ReadText(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
}
