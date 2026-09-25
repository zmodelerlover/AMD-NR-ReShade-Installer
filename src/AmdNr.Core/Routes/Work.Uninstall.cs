// Taking an install back, on every route: through its manifest when there is one, and then by name
// and by pinned hash, because what counts a folder as installed is the files in it, not the record.

using System.Text;

namespace AmdNr.Core;

public static partial class Work
{
    // -- Uninstall -------------------------------------------------------------------------------

    /// <summary>Every name only this project writes: the markers detection reads, the companion
    /// effect, and what older layouts left. Whatever is under one of these is ours, whether a manifest
    /// lists it or somebody copied it in by hand, and uninstall takes it. Nothing with a name anybody
    /// else uses is here.</summary>
    internal static IEnumerable<string> OurNames => InstalledMarkers.Append(ShaderPath).Concat(DeadFiles());

    /// <summary>The tuning files, by names only this project uses. Settings, so they are asked about
    /// rather than taken.</summary>
    private static readonly string[] OurSettings = ["amd-nr.ini", "dlss5-neural.ini"];

    /// <summary>Takes back everything of this app's in the folder, and puts back what it displaced.
    /// The settings -- a configuration entry in a manifest, or the tuning by name -- stay unless
    /// <paramref name="removeConfig"/>; <see cref="KeptConfiguration"/> says which stayed, so the
    /// person can be asked.
    ///
    /// Every manifest in the folder is undone, whichever route the preset names, and then every
    /// file under <see cref="OurNames"/> goes by name, and a proxy goes when it is a build this app
    /// pins: the engine's own, or one in <paramref name="pinned"/>. That half is the fix for a folder
    /// whose binaries were copied in by hand beside a manifest listing only settings, which counted
    /// as installed and could not be uninstalled at all. A proxy a manifest records as the person's
    /// own, or put back from a backup, stays whatever it hashes to.</summary>
    public static Report Uninstall(string gameDir, Preset preset, bool removeConfig = false, IEnumerable<string>? pinned = null)
    {
        var report = new Report();
        if (UninstallFolder(gameDir, preset, report) is not { } dir) return report;
        report.Info($"target: {dir}");

        var builds = new HashSet<string>([Engine.ReShadeSha, Engine.ReShade64Sha, Engine.D3d8To9Sha, .. pinned ?? []],
            StringComparer.OrdinalIgnoreCase);
        var proxies = Proxies.Append("d3d8R.dll").ToArray();
        // A file the runtime rewrites on its own (the mochizuki prewarm list) is changed by design,
        // and still this app's.
        bool Ours(string name, string sha) => OurNames.Contains(name) || Engine.IsRuntimeMaintained(name)
                                              || proxies.Contains(name) && builds.Contains(sha);

        var gone = 0;
        var recorded = Manifests(dir, migrate: true);
        var opti = preset.IsOptiScaler() || recorded.Any(m => m.Preset == Preset.OptiScaler.ManifestPreset());
        foreach (var route in recorded.Select(m => m.Route))
        {
            var log = new List<string>();
            try
            {
                Transaction.Uninstall(dir, route, removeConfig, log, Ours);
                gone++;
            }
            catch (InstallException e)
            {
                report.Err($"could not undo the recorded install: {e.Message}");
            }
            foreach (var line in log) Narrate(line, report);
        }

        // What a manifest still holds is something it kept on purpose -- a file the game has open, one
        // somebody else changed -- and it has already said why. What it held before is the person's
        // or put back from a backup, and a proxy under one of those names is not taken on its hash.
        var before = recorded.SelectMany(m => m.Entries).Select(e => e.Name).ToHashSet();
        var still = Manifests(dir, migrate: false).SelectMany(m => m.Entries).Select(e => e.Name).ToHashSet();
        foreach (var name in OurNames.Where(n => !still.Contains(n))) gone += RemoveFile(dir, name, report);
        foreach (var name in proxies.Where(n => !before.Contains(n)))
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && builds.Contains(Engine.HashFile(path))) gone += RemoveFile(dir, name, report);
        }
        if (removeConfig)
            foreach (var name in OurSettings.Where(n => !still.Contains(n))) gone += RemoveFile(dir, name, report);

        gone += SweepDroppings(dir, report);
        PruneEmpty(dir, [Engine.BackupDir, Engine.LegacyBackupDir, ShaderFolder, "reshade-shaders"], report);
        if (opti) AfterOptiScalerUninstall(dir, recorded.Count > 0, report);

        if (gone == 0) report.Warn("Nothing of ours was in that folder.");
        if (KeptConfigurationIn(dir) is { Count: > 0 } kept)
            report.Info($"Kept your settings: {string.Join(", ", kept)}.");
        if (proxies.Any(n => File.Exists(Path.Combine(dir, n)) && Identify(Path.Combine(dir, n)).IsReShade))
            report.Info("A ReShade this app did not install was left alone. Use its own installer to remove it.");
        return report;
    }

    /// <summary>The settings files an uninstall left in the folder: what a manifest still records as
    /// configuration, and the tuning by name. Empty when nothing of the person's is left to ask about.</summary>
    public static IReadOnlyList<string> KeptConfiguration(string gameDir, Preset preset) =>
        UninstallFolder(gameDir, preset, new Report()) is { } dir ? KeptConfigurationIn(dir) : [];

    private static List<string> KeptConfigurationIn(string dir) =>
        Manifests(dir, migrate: false).SelectMany(m => m.Entries).Where(e => e.Owned && e.Configuration)
            .Select(e => e.Name).Concat(OurSettings)
            .Where(n => File.Exists(Path.Combine(dir, n))).Distinct().ToList();

    /// <summary>Where the install being taken back was written. The 32-bit route installs where
    /// ReShade.ini's BasePath points, the others beside the executable; a folder is itself.</summary>
    private static string? UninstallFolder(string gameDir, Preset preset, Report report)
    {
        var path = ResolveTarget(gameDir);
        if (path.Length == 0)
        {
            report.Err("No game folder given.");
            return null;
        }
        if (File.Exists(path))
        {
            if (preset.Route() != Route.X86) return ResolveSource(path);
            try { return Engine.InstallDirectory(path); }
            catch (InstallException e)
            {
                report.Err(e.Message);
                return null;
            }
        }
        if (Directory.Exists(path)) return path;
        report.Err($"{path} is not a folder.");
        return null;
    }

    /// <summary>The manifests in this folder that this build can read, the 64-bit one first. One it
    /// cannot read is left for <see cref="Transaction.Uninstall"/> to refuse in words.</summary>
    private static List<Manifest> Manifests(string dir, bool migrate)
    {
        var found = new List<Manifest>();
        foreach (var route in new[] { Route.X64, Route.X86 })
        {
            try
            {
                if (migrate) Transaction.MigrateLegacyManifest(dir, route);
                var path = Path.Combine(dir, route.ManifestFileName());
                if (File.Exists(path)) found.Add(Manifest.Decode(Encoding.UTF8.GetString(Engine.Read(path))));
            }
            catch (InstallException)
            {
                if (File.Exists(Path.Combine(dir, route.ManifestFileName()))) found.Add(new Manifest("", route));
            }
        }
        return found;
    }

    private const string ShaderFolder = "reshade-shaders/Shaders";

    /// <summary>Folders an install made or filled, once nothing is left in them: the backups whose
    /// every original went back, and where the companion effect went. One that still holds anything
    /// is left, because that is somebody else's.</summary>
    private static void PruneEmpty(string dir, IEnumerable<string> folders, Report report)
    {
        foreach (var folder in folders)
        {
            var path = Path.Combine(dir, folder);
            try
            {
                if (!Directory.Exists(path)) continue;
                // A backup folder holds one folder per install, each empty once its originals are back.
                if (folder is Engine.BackupDir or Engine.LegacyBackupDir)
                    foreach (var stamp in Directory.GetDirectories(path))
                        if (!Directory.EnumerateFileSystemEntries(stamp, "*", SearchOption.AllDirectories).Any(File.Exists))
                            Directory.Delete(stamp, recursive: true);
                if (Directory.EnumerateFileSystemEntries(path).Any()) continue;
                Directory.Delete(path);
                if (folder is not (Engine.BackupDir or Engine.LegacyBackupDir)) report.Ok($"removed {folder.Replace('/', '\\')}\\");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                report.Warn($"could not remove {folder}: {e.Message}");
            }
        }
    }

    internal static readonly string[] Droppings =
    [
        "amd-nr-pass1.dll", "amd-nr.log", "amd-nr-x86.log",
        "amd-nr-x86-host.log", "dlssnr_on_amd.log", "dlssnr_on_amd.ini",
        // What OptiScaler and its bridge into the runtime write while a game runs.
        "OptiScaler.log", "amd_bridge.log", "amd_presr.log",
        // And the mochizuki runtime, beside itself.
        MochizukiLog,
    ];

    internal static readonly string[] DroppingFolders = ["amd-nr-runtime", "dlss5-runtime", "amd-nr-captures", "dlss5-captures"];

    /// <summary>What the add-on itself writes while a game runs: the unpacked runtime, its logs,
    /// its capture folder. They are never in a manifest, because nothing here put them there, so
    /// they are swept by name -- and by both routes. The x86 uninstall used to skip this entirely
    /// and left 7 MB of runtime plus a folder behind every time.</summary>
    private static int SweepDroppings(string dir, Report report)
    {
        var gone = 0;
        foreach (var name in Droppings) gone += RemoveFile(dir, name, report);

        foreach (var folder in DroppingFolders)
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
            Engine.Writable(p);
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
