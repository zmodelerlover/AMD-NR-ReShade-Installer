// What a game folder says it runs, for the problem report: the install record, which danielblnc build is in
// the runtime passes, what OptiScaler.ini sets for the network, and which ReShade the game actually loaded.
// Every line is read off a file already there, and a file that is missing or unreadable is a "-", never a guess.
//
// The OptiScaler line is the one that diagnosed a stall: danielblnc 0.6.0 run inline (AmdAsync=auto) on an
// RX 9070 XT held the GPU for seconds at a time until the driver reset it (DEVICE_HUNG).

using System.Text;

namespace AmdNr.Core;

public static class InstallState
{
    /// <summary>Lines of "key  value" about the install in <paramref name="dir"/>. <paramref name="payload"/> names
    /// the runtime builds by their hashes; <paramref name="rdna4"/> is whether the card is an RX 9000.</summary>
    public static IReadOnlyList<string> Lines(string dir, PayloadManifest? payload, bool? rdna4)
    {
        var lines = new List<string>();
        var record = Path.Combine(dir, Route.X64.ManifestFileName());
        try
        {
            var m = File.Exists(record) ? Manifest.Decode(File.ReadAllText(record)) : null;
            lines.Add($"installed     {(m is null ? "-" : $"{m.Preset}, ReShade {m.ReShade}")}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstallException)
        {
            lines.Add($"installed     unreadable record: {e.Message}");
        }

        var version = RuntimeIn(dir, payload);
        lines.Add($"runtime       {version ?? "-"}");
        if (OptiNr(dir, version, rdna4) is { } opti) lines.Add($"optiscaler    {opti}");
        lines.Add($"reshade log   {FirstLine(Path.Combine(dir, "ReShade.log")) ?? "-"}");
        return lines;
    }

    /// <summary>The danielblnc version in the first runtime pass, by its hash against every build the payload
    /// list names, or "unknown build" with the start of its hash.</summary>
    internal static string? RuntimeIn(string dir, PayloadManifest? payload)
    {
        var pass = new[] { Work.RuntimeName, Work.OptiPasses[0] }.Select(n => Path.Combine(dir, n)).FirstOrDefault(File.Exists);
        if (pass is null) return null;
        string sha;
        try { sha = Engine.HashFile(pass); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return "unreadable"; }
        var builds = (payload?.RuntimeVersions() ?? []).SelectMany(r => r.Components.Values.Select(c => (r.Version, c)))
            .Concat(payload?.Components.Where(c => c.Key is PayloadManifest.RuntimeComponent or PayloadManifest.OptiRuntimeComponent)
                .Select(c => (c.Value.Version, c.Value)) ?? []);
        foreach (var (v, component) in builds)
            if (component.Files.Any(f => Engine.Lower(f.Sha256) == sha)) return v;
        return $"unknown build {sha[..12]}";
    }

    /// <summary>[DlssNr] NrBackend and AmdAsync from OptiScaler.ini, and a flag when danielblnc runs inline on an
    /// RX 9000 at a version other than <see cref="Work.Rdna4Recommended"/>. Null without an OptiScaler.ini.</summary>
    internal static string? OptiNr(string dir, string? runtime, bool? rdna4)
    {
        string text;
        try { text = File.ReadAllText(Path.Combine(dir, Work.OptiScalerIni)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var backend = Engine.Trim(Engine.GetIni(text, "DlssNr", "NrBackend"));
        var async = Engine.Trim(Engine.GetIni(text, "DlssNr", "AmdAsync"));
        var inline = async is not ("true" or "1");
        var daniel = backend is not ("lmxxf" or Work.MochizukiBackend);
        var line = $"NrBackend={(backend.Length == 0 ? "-" : backend)}, AmdAsync={(async.Length == 0 ? "-" : async)}"
                   + (inline ? " (inline)" : " (async)");
        if (rdna4 == true && daniel && inline && runtime is { } v && v != Work.Rdna4Recommended && !v.StartsWith("unknown"))
            line += $" -- unstable runtime inline: danielblnc {v} on an RX 9000 can stall the GPU until the driver resets it";
        return line;
    }

    /// <summary>The first line of a log: ReShade's says which version loaded and from where.</summary>
    private static string? FirstLine(string path)
    {
        try
        {
            using var reader = new StreamReader(new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete), Encoding.UTF8);
            return reader.ReadLine()?.Trim();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
