// The causes the logs beside a game already name, read off them for the top of the problem report: each rule is a
// line one of the logs writes and what it means, with the line itself as the evidence. Every rule here was the
// answer to a report once; a log line no rule knows says nothing, and the logs are in the zip anyway.

using System.Text.RegularExpressions;

namespace AmdNr.Core;

/// <param name="Cause">What it means and what to do, in a sentence or two.</param>
/// <param name="Evidence">The log and its line that says so.</param>
public sealed record Finding(string Cause, string Evidence);

public static partial class Diagnosis
{
    private sealed record Rule(string[] Logs, Func<string, bool> Matches, string Cause, int AtLeast = 1);

    private static readonly Rule[] Rules =
    [
        new([SessionLog.ReShadeLog], l => l.Contains("limited add-on functionality", StringComparison.OrdinalIgnoreCase),
            "ReShade here is a build with limited add-on functionality, so it left the add-on out: the normal build, or "
            + "a global ReShade Vulkan layer older than 6.8.0. Install ReShade 6.8.0 with full add-on support (on Vulkan, "
            + "run its setup as administrator to update the global layer, or uninstall that one)."),
        new([SessionLog.ReShadeLog], l => l.Contains("Another ReShade instance was already loaded", StringComparison.OrdinalIgnoreCase),
            "Two ReShades in one process: another proxy DLL, an .asi through an ASI loader, or a global Vulkan layer. "
            + "Remove every ReShade but the one this app installed."),
        new([SessionLog.MochizukiLog], l => l.Contains("insufficient VRAM", StringComparison.OrdinalIgnoreCase),
            "mochizuki refused the network: not enough VRAM. Lower Scale or Passes, or use danielblnc's runtime."),
        new([SessionLog.RuntimeLog, SessionLog.AddonLog, "OptiScaler.log", "amd_bridge.log"],
            l => GpuReset().IsMatch(l),
            "The GPU was reset while the network ran (HIP error 719 or DEVICE_HUNG). On an RX 9000 card, use danielblnc "
            + Work.Rdna4Recommended + "; otherwise lower the resolution the network runs at, and check the driver is current."),
        new(["amd_presr.log", SessionLog.AddonLog], l => l.Contains("HIP device enumeration failed", StringComparison.OrdinalIgnoreCase),
            "HIP found no GPU, so the network never ran. Usually an amdhip64_7.dll copied into the game folder that does "
            + "not match the driver: move it out (the driver has its own in System32), or reinstall the AMD driver."),
        new([SessionLog.RuntimeLog], l => l.StartsWith("CRASH:", StringComparison.Ordinal),
            "danielblnc's runtime caught the game crashing in several sessions: the first CRASH line is the evidence, and "
            + "the report's dlssnr_on_amd.log has them all.", AtLeast: 3),
    ];

    /// <summary>What the logs in <paramref name="dir"/> say went wrong, with the line each comes from, plus danielblnc's
    /// runtime inline on an RX 9000 from OptiScaler.ini (<see cref="InstallState.OptiNr"/>).</summary>
    public static IReadOnlyList<Finding> Of(string dir, PayloadManifest? payload, bool? rdna4)
    {
        var found = new List<Finding>();
        var texts = new Dictionary<string, List<string>?>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in Rules)
        {
            foreach (var log in rule.Logs)
            {
                if (!texts.TryGetValue(log, out var lines))
                    texts[log] = lines = SessionLog.Tail(Path.Combine(dir, log))?.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
                var hits = lines?.Where(rule.Matches).ToList() ?? [];
                if (hits.Count < rule.AtLeast) continue;
                found.Add(new Finding(rule.Cause, $"{log}: {hits[0].Trim()}{(hits.Count > 1 ? $" ({hits.Count} lines)" : "")}"));
                break;
            }
        }
        if (InstallState.OptiNr(dir, InstallState.RuntimeIn(dir, payload), rdna4) is { } opti && opti.Contains("unstable runtime inline"))
            found.Add(new Finding(
                $"danielblnc's runtime runs inline on an RX 9000 card at a build that stalls there. Pick {Work.Rdna4Recommended} "
                + "under danielblnc version and install again.", $"{Work.OptiScalerIni}: {opti}"));
        return found;
    }

    /// <summary>DEVICE_HUNG, hipErrorLaunchFailure, or 719 after "error", "code" or "returned" on a line a HIP call or
    /// "HIP" wrote, in any case: whole words, so "chip" and "relationship" next to some 719 are not a reset, and a HIP
    /// call is still told by its camel case.</summary>
    [GeneratedRegex(@"\b(DEVICE_HUNG|hipErrorLaunchFailure)\b|\b(HIP|(?-i:hip[A-Z]\w*))\b.*\b(error|code|returned)\s*[:=#]?\s*719\b",
        RegexOptions.IgnoreCase)]
    private static partial Regex GpuReset();
}
