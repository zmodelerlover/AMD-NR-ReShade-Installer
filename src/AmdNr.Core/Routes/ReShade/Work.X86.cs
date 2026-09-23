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

    private static Report UninstallX86(string gameDir, bool force)
    {
        var report = new Report();
        var path = ResolveTarget(gameDir);
        if (path.Length == 0)
        {
            report.Err("No game folder given.");
            return report;
        }

        // Uninstall works off the manifest, so the folder is enough -- but accept an executable too,
        // because that is what the same field held during the install.
        string dir;
        if (File.Exists(path))
        {
            try { dir = Engine.InstallDirectory(path); }
            catch (InstallException e)
            {
                report.Err(e.Message);
                return report;
            }
        }
        else
        {
            dir = path;
        }
        report.Info($"target: {dir}");

        // No manifest is not a failure, it is the ordinary state of a folder nothing was installed
        // into -- and of one installed by a build from before the manifest existed. The x64 route
        // has always said so and swept by name; saying "No install manifest" in red here, for the
        // same situation, read as something having gone wrong.
        var hadManifest = File.Exists(Path.Combine(dir, Route.X86.ManifestFileName()));
        var gone = 0;
        if (!hadManifest)
        {
            foreach (var name in new[] { Addon32Name, Host64Name, RuntimeName, WeightsName })
                gone += RemoveFile(dir, name, report);
        }
        else
        {
            var log = new List<string>();
            try
            {
                Transaction.Uninstall(dir, Route.X86, false, log, force);
                foreach (var line in log) Narrate(line, report);
            }
            catch (InstallException e)
            {
                report.Err(e.Message);
            }
        }

        // Counted, the way the x64 branch counts it: the four files above can all be gone by hand
        // and the add-on's own droppings still be there, and saying "Nothing of ours was in that
        // folder" over the top of three "removed ..." lines reads as the tool being broken.
        gone += SweepDroppings(dir, report);
        if (!hadManifest && gone == 0) report.Warn("Nothing of ours was in that folder.");

        // Said only when it is true: when this route installed the pinned ReShade itself, uninstall
        // took it back, and telling someone it was left alone would send them looking for nothing.
        if (new[] { "d3d9.dll", "dxgi.dll", "d3d8.dll" }.Any(n => File.Exists(Path.Combine(dir, n))))
            report.Info("ReShade itself was left alone. Use its own installer to remove it.");
        return report;
    }
}
