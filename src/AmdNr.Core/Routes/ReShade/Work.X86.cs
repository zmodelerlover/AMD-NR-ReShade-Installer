// The ReShade route for a 32-bit game: the bridge pair -- a 32-bit frontend inside the game and a
// 64-bit helper beside it -- installed and taken back by the engine installer-x86 defined.

namespace AmdNr.Core;

public static partial class Work
{
    /// <summary>The 32-bit route reads the PE header to be certain, so it needs the executable and
    /// not just the folder. When a folder was given and exactly one 32-bit executable is in it, that
    /// is unambiguous and gets used; anything else is a question only the person can answer.</summary>
    private static string? X86Target(string gameDir, Report report)
    {
        var path = ResolveTarget(gameDir);
        if (File.Exists(path)) return path;
        if (!Directory.Exists(path))
        {
            report.Err($"{path} is not a folder.");
            return null;
        }

        var found = Directory.EnumerateFiles(path, "*.exe")
            .Where(p => Engine.MachineOfFile(p) == Engine.MachineX86)
            .ToList();

        switch (found.Count)
        {
            case 1:
                return found[0];
            case 0:
                report.Err(
                    "No 32-bit executable in that folder. The bridge route needs the game's own .exe: "
                    + "point it straight at the executable.");
                return null;
            default:
                report.Err(
                    $"More than one 32-bit executable here ({string.Join(", ", found.Select(NameOf))}). "
                    + "Point it at the one the game actually runs, rather than at the folder.");
                return null;
        }
    }

    private static Report InstallX86(string gameDir, string releaseDir, Preset preset, string? proxy)
    {
        var report = new Report();
        if (X86Target(gameDir, report) is not { } target) return report;

        var release = ResolveSource(releaseDir);
        if (release.Length == 0)
        {
            report.Err(
                "The 32-bit bridge needs the unpacked x86 release -- the folder holding files\\ and "
                + "payload.sha256. The bridge ships as separate files, so nothing can be installed without it.");
            return report;
        }
        if (!File.Exists(Path.Combine(release, "payload.sha256")))
        {
            report.Err($"{release} does not look like the x86 release: payload.sha256 is not in it.");
            return report;
        }

        report.Info($"target: {target}");
        report.Info($"preset: {preset.Label()}");

        var installDir = Engine.InstallDirectory(target);
        var proxyName = ProxyNameFor(preset, installDir, proxy);
        CheckDoubleReShade(installDir, preset, report, proxyName, ShippedReShade(release, preset));
        if (report.Failed)
        {
            report.Info("Nothing was written: fix the problem above and run it again.");
            return report;
        }

        var app = new X86Installer(release);
        try
        {
            app.Install(target, preset.ManifestPreset(), proxyName);
            foreach (var line in app.Log) Narrate(line, report);
            report.Info(preset.Note());
            report.Info(
                "It starts switched off. Open the overlay with Home, or press Ctrl+End. StartOn=1 in "
                + "amd-nr.ini makes it come up enabled.");
        }
        catch (InstallException e)
        {
            foreach (var line in app.Log) Narrate(line, report);
            report.Err($"{e.Message}. Nothing was left half-written: the install rolled itself back.");
        }
        return report;
    }
}
