// Taking an install back, on every route: through its manifest when there is one, by name when the
// install predates the manifest, and the add-on's own droppings either way.

namespace AmdNr.Core;

public static partial class Work
{
    // -- Uninstall -------------------------------------------------------------------------------

    /// <summary>The words a retained-because-it-changed line carries. The window reads it to know
    /// whether offering to remove the file anyway would achieve anything: forcing gets past a file
    /// somebody edited, and gets nowhere against one the running game still has open.</summary>
    public const string ModifiedMarker = "modified after install";

    /// <summary><paramref name="force"/> takes back the files that changed after the install as
    /// well. Off by default, because a file that is not the one written here belongs to whatever
    /// changed it; on only when somebody has been shown that list and asked for it anyway.</summary>
    public static Report Uninstall(string gameDir, Preset preset, bool force = false)
    {
        if (preset.Route() == Route.X86) return UninstallX86(gameDir, force);

        var report = new Report();
        var dir = ResolveSource(gameDir);
        if (dir.Length == 0)
        {
            report.Err("No game folder given.");
            return report;
        }
        if (!Directory.Exists(dir))
        {
            report.Err($"{dir} is not a folder.");
            return report;
        }
        report.Info($"target: {dir}");

        var gone = 0;

        // An install written by this version has a manifest, so it knows what it owned, what it
        // displaced and what the user has changed since. Installs from before the manifest existed
        // have none, and the name sweep below is the only way to take those back.
        var manifest = Path.Combine(dir, Route.X64.ManifestFileName());
        if (File.Exists(manifest))
        {
            var log = new List<string>();
            try
            {
                Transaction.Uninstall(dir, Route.X64, false, log, force);
                foreach (var line in log) Narrate(line, report);
                gone++;
            }
            catch (InstallException e)
            {
                report.Err($"could not undo the recorded install: {e.Message}");
            }
        }
        else if (preset.IsOptiScaler())
        {
            // No manifest means this app never installed OptiScaler here. The by-name sweep below
            // would take the runtime passes and the weights out from under an OptiScaler that some
            // other installer put there, and leave it broken.
            report.Warn(
                "No install record for OptiScaler here, so it was not installed by this app and nothing is "
                + "removed. Use the uninstaller that came with it: the release package puts "
                + "Uninstall_OptiScaler_NR.bat in the game folder.");
            return report;
        }
        else
        {
            // Everything the add-on installs. The ini is deliberately not in this list.
            var names = new List<string> { AddonName, RuntimeName, WeightsName };
            names.AddRange(DeadFiles());
            foreach (var name in names) gone += RemoveFile(dir, name, report);
        }

        gone += SweepDroppings(dir, report);
        if (preset.IsOptiScaler()) AfterOptiScalerUninstall(dir, report);

        if (gone == 0) report.Warn("Nothing of ours was in that folder.");
        if (File.Exists(Path.Combine(dir, "amd-nr.ini")))
        {
            report.Info(
                "amd-nr.ini was left in place: it is your tuning, not ours. Delete it by hand if "
                + "you want a clean slate.");
        }

        // Only true when a ReShade proxy is still there: one this app installed came back out above.
        if (Proxies.Any(n => File.Exists(Path.Combine(dir, n)) && Identify(Path.Combine(dir, n)).IsReShade))
            report.Info("ReShade itself was left alone. Use its own installer to remove it.");
        return report;
    }

    /// <summary>What the add-on itself writes while a game runs: the unpacked runtime, its logs,
    /// its capture folder. They are never in a manifest, because nothing here put them there, so
    /// they are swept by name -- and by both routes. The x86 uninstall used to skip this entirely
    /// and left 7 MB of runtime plus a folder behind every time.</summary>
    private static int SweepDroppings(string dir, Report report)
    {
        var gone = 0;
        foreach (var name in new[]
                 {
                     "amd-nr-pass1.dll", "amd-nr.log", "amd-nr-x86.log",
                     "amd-nr-x86-host.log", "dlssnr_on_amd.log", "dlssnr_on_amd.ini",
                     // What OptiScaler and its bridge into the runtime write while a game runs.
                     "OptiScaler.log", "amd_bridge.log", "amd_presr.log",
                 })
            gone += RemoveFile(dir, name, report);

        foreach (var folder in new[] { "amd-nr-runtime", "dlss5-runtime", "amd-nr-captures", "dlss5-captures" })
        {
            var p = Path.Combine(dir, folder);
            if (!Directory.Exists(p)) continue;
            try
            {
                Directory.Delete(p, recursive: true);
                gone++;
                report.Ok($"removed {folder}\\");
            }
            catch (Exception e)
            {
                report.Err($"could not remove {folder}: {e.Message}");
            }
        }
        return gone;
    }

    private static int RemoveFile(string dir, string name, Report report)
    {
        var p = Path.Combine(dir, name);
        if (!File.Exists(p)) return 0;
        try
        {
            File.Delete(p);
            report.Ok($"removed {name}");
            return 1;
        }
        catch (Exception e)
        {
            report.Err($"could not remove {name}: {e.Message}");
            return 0;
        }
    }
}
