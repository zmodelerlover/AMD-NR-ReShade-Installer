// What happened the last time a game ran, read off the logs the add-on and the runtimes leave beside
// it. Installing tells nobody whether the network then ran; these files do, and they are already
// there -- this only reads them.
//
// The formats are the ones the add-on, danielblnc's runtime and mochizuki write today. None of them
// is a contract, so every rule below fails towards saying less: a line this does not recognise is
// skipped, and a session it cannot place is no session rather than a wrong one.

using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AmdNr.Core;

public enum SessionOutcome
{
    /// <summary>The network processed frames.</summary>
    Ran,

    /// <summary>The runtime caught the game going down, or the GPU was removed.</summary>
    Crashed,

    /// <summary>The add-on or the runtime said why it did not start.</summary>
    Failed,

    /// <summary>ReShade started and never loaded the add-on.</summary>
    NotLoaded,

    /// <summary>Everything loaded and no frame went through the network.</summary>
    NoFrames,
}

/// <param name="When">When the log was last written: the end of the session, give or take.</param>
/// <param name="Frames">Frames the network processed, when the log counts them.</param>
/// <param name="NetworkMs">The network's average time on the GPU per frame, when the log says it.</param>
/// <param name="Runtime">The runtime's version as it introduced itself, when it did.</param>
/// <param name="Line">The log's own sentence behind a crash or a failure, as written.</param>
public sealed record SessionResult(
    SessionOutcome Outcome, DateTime When, long? Frames, double? NetworkMs, string? Runtime, string? Line);

public static partial class SessionLog
{
    public const string AddonLog = "amd-nr.log";
    public const string AddonLog32 = "amd-nr-x86.log";
    public const string RuntimeLog = "dlssnr_on_amd.log";
    public const string MochizukiLog = "mochizuki_nr.log";
    public const string ReShadeLog = "ReShade.log";

    /// <summary>Every log this reads. Their newest write is when the game last ran with the mod in it,
    /// however it was started.</summary>
    public static readonly string[] Logs = [AddonLog, AddonLog32, RuntimeLog, MochizukiLog];

    /// <summary>How much of the end of a log is read. The runtime's grows by every session and never
    /// starts again; a session that wrote more than this is read from wherever this starts.</summary>
    private const int TailBytes = 1024 * 1024;

    /// <summary>Two logs written this far apart belong to different sessions.</summary>
    private static readonly TimeSpan SameSession = TimeSpan.FromMinutes(2);

    /// <summary>The newest write of any of the logs in these folders, or null when there are none.</summary>
    public static DateTime? LastWrite(IEnumerable<string> folders)
    {
        DateTime? newest = null;
        foreach (var folder in folders)
            foreach (var name in Logs)
                if (Written(Path.Combine(folder, name)) is { } when && (newest is null || when > newest))
                    newest = when;
        return newest;
    }

    /// <summary>The last session in this folder, or null when nothing in it says there was one.</summary>
    public static SessionResult? Read(string folder)
    {
        var addonPath = Path.Combine(folder, AddonLog);
        if (Written(addonPath) is null) addonPath = Path.Combine(folder, AddonLog32);
        var addonWhen = Written(addonPath);
        var runtimePath = Path.Combine(folder, RuntimeLog);
        var runtimeWhen = Written(runtimePath);
        var mochizukiPath = Path.Combine(folder, MochizukiLog);
        var mochizukiWhen = Written(mochizukiPath);
        var reshadeWhen = Written(Path.Combine(folder, ReShadeLog));

        var newest = new[] { addonWhen, runtimeWhen, mochizukiWhen }.Max();

        // ReShade writes its log afresh at every start, before anything of ours runs. One newer than
        // everything of ours is a session our add-on was never part of.
        if (reshadeWhen is { } started && (newest is null || started - newest > SameSession)
            && Tail(Path.Combine(folder, ReShadeLog)) is { } reshade
            && !reshade.Contains("Registered add-on \"AMD Neural Rendering", StringComparison.Ordinal))
        {
            // Only when this folder carries the add-on at all: ReShade on its own is somebody else's.
            if (File.Exists(Path.Combine(folder, Work.AddonName)) || File.Exists(Path.Combine(folder, Work.Addon32Name)))
                return new SessionResult(SessionOutcome.NotLoaded, started, null, null, null, null);
        }
        if (newest is null) return null;

        // mochizuki replaces danielblnc's runtime for a whole session, so whichever of the two logs is
        // current says which one ran.
        var engine = mochizukiWhen is { } mw && newest - mw <= SameSession
                                             && (runtimeWhen is null || mw - runtimeWhen > SameSession)
            ? Tail(mochizukiPath) is { } m ? Mochizuki(m, mw) : null
            : runtimeWhen is { } rw && newest - rw <= SameSession && Tail(runtimePath) is { } r
                ? Runtime(r, rw)
                : null;
        var addon = addonWhen is { } aw && newest - aw <= SameSession && Tail(addonPath) is { } a
            ? Addon(a, aw)
            : null;

        if (addon is null) return engine;
        if (engine is null) return addon;

        // Both: the add-on counts the frames and knows why it stood down; the runtime knows a crash,
        // its own version and how long the network took.
        var outcome = engine.Outcome == SessionOutcome.Crashed ? SessionOutcome.Crashed : addon.Outcome;
        return new SessionResult(outcome, Max(addon.When, engine.When), addon.Frames ?? engine.Frames,
            engine.NetworkMs, engine.Runtime,
            outcome == SessionOutcome.Crashed ? engine.Line : addon.Line ?? engine.Line);
    }

    /// <summary>danielblnc's runtime: "dlssnr_amd vX loaded into ..." starts a session, "network job N
    /// done" counts it, and CRASH, FAULT or a removed device ends it badly.</summary>
    internal static SessionResult Runtime(string text, DateTime when)
    {
        var lines = Lines(text);
        var start = lines.FindLastIndex(l => l.StartsWith("dlssnr_amd v", StringComparison.Ordinal));
        var session = start >= 0 ? lines.Skip(start).ToList() : lines;

        string? version = null;
        if (start >= 0 && RuntimeVersion().Match(session[0]) is { Success: true } v) version = v.Groups[1].Value;

        long? jobs = null;
        double? ms = null;
        string? crash = null, failure = null;
        foreach (var line in session)
        {
            if (Job().Match(line) is { Success: true } j && long.TryParse(j.Groups[1].Value, out var n))
                jobs = Math.Max(jobs ?? 0, n);
            else if (line.StartsWith("timing (", StringComparison.Ordinal) && Network().Match(line) is { Success: true } t
                     && double.TryParse(t.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var avg))
                ms = avg;
            // CRASH follows the FAULT it came from and says it in fewer words, so it is the line kept.
            else if (line.StartsWith("CRASH:", StringComparison.Ordinal))
                crash = line;
            else if (line.StartsWith("FAULT:", StringComparison.Ordinal)
                     || line.Contains("DEVICE_HUNG", StringComparison.Ordinal) || line.Contains("DEVICE REMOVED", StringComparison.Ordinal))
                crash ??= line;
            else if (failure is null && RuntimeFailures.Any(f => line.Contains(f, StringComparison.Ordinal)))
                failure = line;
        }

        var outcome = crash is not null ? SessionOutcome.Crashed
            : jobs > 0 ? SessionOutcome.Ran
            : failure is not null ? SessionOutcome.Failed
            : SessionOutcome.NoFrames;
        return new SessionResult(outcome, when, jobs, ms, version, crash ?? (outcome == SessionOutcome.Failed ? failure : null));
    }

    /// <summary>The runtime's own sentences for not starting.</summary>
    private static readonly string[] RuntimeFailures =
    [
        "setup failed", "HIP: no usable device", "hipSetDevice FAILED", "Weights missing",
        "FAILED (no dlssnr_on_amd_weights.bin)", "was not found in the game folder", "this is a different build",
        "Could not build the weights",
    ];

    /// <summary>The add-on writes its log afresh every session: "frame N processed" counts it, and its
    /// reasons for standing down are the lines that start the way these do.</summary>
    internal static SessionResult Addon(string text, DateTime when)
    {
        long? frames = null;
        string? failure = null;
        foreach (var line in Lines(text))
        {
            if (Frame().Match(line) is { Success: true } f && long.TryParse(f.Groups[1].Value, out var n))
                frames = Math.Max(frames ?? 0, n);
            else if (failure is null && AddonFailures.Any(p => line.StartsWith(p, StringComparison.Ordinal)))
                failure = line;
        }
        var outcome = frames > 0 ? SessionOutcome.Ran
            : failure is not null ? SessionOutcome.Failed
            : SessionOutcome.NoFrames;
        return new SessionResult(outcome, when, frames, null, null, outcome == SessionOutcome.Failed ? failure : null);
    }

    private static readonly string[] AddonFailures =
    [
        "off: ", "missing: ", "HIP: amdhip64_7.dll failed", "HIP: R0600", "HIP: no device", "LoadLibrary failed",
        "the helper stood down",
    ];

    /// <summary>mochizuki: "network ready" means it ran; "N frames in the session" is its count, said
    /// when the network is rebuilt at a new size and when the game closes.</summary>
    internal static SessionResult Mochizuki(string text, DateTime when)
    {
        var lines = Lines(text);
        var start = lines.FindLastIndex(l => l.Contains("[mochizuki] Vulkan device", StringComparison.Ordinal));
        var session = start >= 0 ? lines.Skip(start).ToList() : lines;
        var ready = session.Any(l => l.Contains("network ready", StringComparison.Ordinal));
        long? frames = null;
        foreach (var line in session)
            if (MochizukiFrames().Match(line) is { Success: true } m && long.TryParse(m.Groups[1].Value, out var n))
                frames = (frames ?? 0) + n;
        var outcome = ready || frames > 0 ? SessionOutcome.Ran : SessionOutcome.NoFrames;
        return new SessionResult(outcome, when, frames, null, "mochizuki", null);
    }

    private static DateTime? Written(string path)
    {
        try { return File.Exists(path) ? File.GetLastWriteTime(path) : null; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>The end of a log, shared with whoever is still writing it.</summary>
    private static string? Tail(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var skip = Math.Max(0, stream.Length - TailBytes);
            stream.Seek(skip, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            // Cut at a line: the first one read from the middle of a file is half of one.
            if (skip > 0 && text.IndexOf('\n') is var nl and >= 0) text = text[(nl + 1)..];
            return text;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> Lines(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    [GeneratedRegex(@"^dlssnr_amd v(\S+)")]
    private static partial Regex RuntimeVersion();

    [GeneratedRegex(@"^network job (\d+) done")]
    private static partial Regex Job();

    [GeneratedRegex(@"\+ network ([0-9.]+)")]
    private static partial Regex Network();

    [GeneratedRegex(@"^frame (\d+) processed")]
    private static partial Regex Frame();

    [GeneratedRegex(@"(\d+) frames in the session")]
    private static partial Regex MochizukiFrames();
}
