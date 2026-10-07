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
    internal static IEnumerable<string> OurNames => InstalledMarkers.Concat(FiveMMarkers).Append(ShaderPath).Concat(DeadFiles());

    /// <summary>The tuning files, by names only this project uses. Settings, so they are asked about
    /// rather than taken.</summary>
    private static readonly string[] OurSettings = ["amd-nr.ini", "dlss5-neural.ini", $"{FiveMGame}/amd-nr.ini"];

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
        if (Running(gameDir, preset) is { } running)
        {
            report.Err(running);
            report.Info("Nothing was taken out.");
            return report;
        }

        var builds = new HashSet<string>([Engine.ReShadeSha, Engine.ReShade64Sha, Engine.D3d8To9Sha, .. pinned ?? []],
            StringComparer.OrdinalIgnoreCase);
        var proxies = Proxies.Append("d3d8R.dll").ToArray();
        var recorded = Manifests(dir, migrate: true);
        // The weights beside the author's runtime, there now or coming back from the backup under any name
        // (an entry of no bytes is a file an install moved out), are his runtime's.
        var authors = AuthorsVersionDllHere(dir)
                      || recorded.Any(m => m.Entries.Any(e => e.Backup.Length > 0
                                                              && (e.Name == AuthorRuntimeName || e.Hash == Engine.Sha([]))));
        // A file the runtime rewrites on its own (the mochizuki prewarm list) is changed by design,
        // and still this app's.
        bool Ours(string name, string sha) => (OurNames.Contains(name) && !(authors && name == WeightsName))
                                              || Engine.IsRuntimeMaintained(name)
                                              || proxies.Contains(name) && builds.Contains(sha);

        var gone = 0;
        var opti = preset.IsOptiScaler() || recorded.Any(m => m.Preset == Preset.OptiScaler.ManifestPreset());
        // A record this build cannot read has nothing to undo by: everything of ours goes by name below,
        // and the record is set aside after, so it stops blocking every install and uninstall after this.
        var unreadable = recorded.Where(m => m.Preset.Length == 0).Select(m => m.Route).ToList();
        foreach (var route in recorded.Where(m => m.Preset.Length > 0).Select(m => m.Route))
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
        foreach (var name in OurNames.Where(n => !still.Contains(n) && !(authors && n == WeightsName)))
            gone += RemoveFile(dir, name, report);
        foreach (var name in proxies.Where(n => !before.Contains(n)))
        {
            var path = Path.Combine(dir, name);
            if (File.Exists(path) && builds.Contains(Engine.HashFile(path))) gone += RemoveFile(dir, name, report);
        }
        if (removeConfig)
            foreach (var name in OurSettings.Where(n => !still.Contains(n))) gone += RemoveFile(dir, name, report);

        gone += SwitchOffVulkanReShade(dir, recorded, report);
        gone += SweepDroppings(dir, report);
        // FiveM's add-on writes its runtime copies and logs beside the game process, not in FiveM.app.
        if (Directory.Exists(Path.Combine(dir, FiveMGame))) gone += SweepDroppings(Path.Combine(dir, FiveMGame), report);
        gone += RemovePinnedMochizuki(dir, builds, still, report);
        foreach (var route in unreadable) gone += SetAside(dir, route, report);
        PruneEmpty(dir, [Engine.BackupDir, Engine.LegacyBackupDir, ShaderFolder, "reshade-shaders"], report);
        AfterMochizukiUninstall(dir, report);
        if (opti) AfterOptiScalerUninstall(dir, recorded.Count > 0, report);

        if (gone == 0) report.Warn("Nothing of ours was in that folder.");
        if (KeptConfigurationIn(dir) is { Count: > 0 } kept)
            report.Info($"Kept your settings: {string.Join(", ", kept)}.");
        if (proxies.Any(n => File.Exists(Path.Combine(dir, n)) && Identify(Path.Combine(dir, n)).IsReShade))
            report.Info("A ReShade this app did not install was left alone. Use its own installer to remove it.");
        return report;
    }

    /// <summary>On Vulkan the ReShade.ini an install wrote is what turns the ReShade layer on for this program,
    /// so kept as a setting it would leave ReShade running here without the add-on. It is renamed instead, the
    /// settings in it kept; one that was here before the install is somebody's own ReShade and stays.</summary>
    private static int SwitchOffVulkanReShade(string dir, List<Manifest> recorded, Report report)
    {
        var vulkan = Enum.GetValues<Preset>().Where(p => p.IsVulkan()).Select(p => p.ManifestPreset()).ToHashSet();
        var ini = Path.Combine(dir, "ReShade.ini");
        if (!File.Exists(ini) || !recorded.Any(m => vulkan.Contains(m.Preset)
                                                     && m.Entries.Any(e => e.Name == "ReShade.ini" && e.Owned && e.Backup.Length == 0)))
            return 0;
        try
        {
            File.Move(ini, ini + ".off", overwrite: true);
            report.Info("ReShade.ini is now ReShade.ini.off: on Vulkan it is what turns ReShade on for this program. "
                        + "Your ReShade settings are in it; rename it back to use ReShade here again.");
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Warn($"ReShade.ini could not be switched off ({e.Message}), so ReShade still loads here. Delete it by hand.");
            return 0;
        }
    }

    /// <summary>Moves a record this build cannot read out of the way, kept beside it under
    /// <c>.unreadable</c> for whoever wants to see it. Everything of ours has gone by name by now.</summary>
    private static int SetAside(string dir, Route route, Report report)
    {
        var name = route.ManifestFileName();
        var path = Path.Combine(dir, name);
        string why;
        try
        {
            Manifest.Decode(Encoding.UTF8.GetString(Engine.Read(path)));
            return 0;
        }
        catch (InstallException e) { why = e.Message; }
        try
        {
            File.Move(path, path + ".unreadable", overwrite: true);
            report.Warn($"{name} could not be read ({why}), so everything of this app's came out by name instead. "
                        + $"It is kept as {name}.unreadable; a backup it pointed to stays in {Engine.BackupDir}.");
            return 1;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Err($"{name} could not be read ({why}), and could not be moved aside: {e.Message}");
            return 0;
        }
    }

    /// <summary>What stops an install here that the install can clear by itself, in words, or null. A
    /// record this build cannot read, one an install was cut off in the middle of, or another preset of
    /// the same route family: each used to fail the install, and send the person to Uninstall first --
    /// which, for the first, failed too. The other family is the sheet's to ask about, since that takes
    /// ReShade out or puts it in.</summary>
    internal static string? InTheWay(string dir, Preset preset)
    {
        foreach (var route in new[] { Route.X64, Route.X86 })
        {
            var path = Path.Combine(dir, route.ManifestFileName());
            if (!File.Exists(path)) continue;
            Manifest m;
            try { m = Manifest.Decode(Encoding.UTF8.GetString(Engine.Read(path))); }
            catch (InstallException e) { return $"The install record here ({route.ManifestFileName()}) cannot be read: {e.Message}."; }
            if (m.State != "installed") return "An earlier install here was cut off before it finished.";
            if (route != preset.Route() || m.Preset == preset.ManifestPreset()
                || (m.Preset == Preset.OptiScaler.ManifestPreset()) != preset.IsOptiScaler()) continue;
            if (m.Entries.Any(e => e.Owned && !e.Configuration && File.Exists(Path.Combine(dir, e.Name))))
                return $"This folder has the {m.Preset} install, and this one is {preset.ManifestPreset()}.";
        }
        return null;
    }

    /// <summary>What an install says it does about <see cref="InTheWay"/>.</summary>
    internal const string ClearsTheWay = "The install takes out what is this app's first, keeping your settings, and then goes in fresh.";

    /// <summary>Clears what <see cref="InTheWay"/> names, the way Uninstall would, keeping the settings.
    /// Null when nothing is in the way, or the folder cannot be found: the route says that itself.</summary>
    private static Report? ClearTheWay(string gameDir, Preset preset, PayloadPins pins)
    {
        string? dir;
        try { dir = InstallFolder(gameDir, preset); }
        catch (InstallException) { return null; }
        if (dir is null || InTheWay(dir, preset) is not { } why) return null;
        var report = new Report();
        report.Info($"{why} {ClearsTheWay}");
        report.Append(Uninstall(dir, preset, removeConfig: false, [pins.ReShade64Sha, pins.ReShade32Sha, pins.D3d8To9Sha, .. pins.MochizukiFiles.Values]));
        return report;
    }

    /// <summary>Where a route writes: beside the executable, or where ReShade.ini's BasePath points on
    /// the 32-bit route. Null when that is not known yet.</summary>
    private static string? InstallFolder(string gameDir, Preset preset)
    {
        if (preset == Preset.FiveM) return FiveMApp(gameDir);
        if (preset.Route() != Route.X86)
        {
            var dir = ResolveSource(gameDir);
            return Directory.Exists(dir) ? dir : null;
        }
        return X86Target(gameDir, new Report()) is { } target ? Engine.InstallDirectory(target) : null;
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
        if (preset == Preset.FiveM)
        {
            if (FiveMApp(path) is { } app) return app;
            report.Err(NotFiveM(path));
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

    /// <summary>The runtime copies the add-on writes beside the game, one per pass (neural.cpp's LoadExtraRuntime,
    /// and pass 1 re-pointed at a private D3D12): up to three, and some room above that. The 32-bit bridge's
    /// helper writes the same ones beside itself. Left behind they were read as danielblnc's standalone
    /// runtime loaded by something else, and stopped the next ReShade install (Rollout, 28/09).</summary>
    internal static readonly string[] RuntimeCopies = [.. Enumerable.Range(1, 9).Select(n => $"amd-nr-pass{n}.dll")];

    internal static readonly string[] Droppings =
    [
        .. RuntimeCopies, "amd-nr.log", "amd-nr-x86.log",
        "amd-nr-x86-host.log", "dlssnr_on_amd.log", "dlssnr_on_amd.ini",
        // What OptiScaler and its bridge into the runtime write while a game runs: from 0.5.0 on, NR without
        // upscaling loads its own copies of the runtime under these names.
        "OptiScaler.log", "amd_bridge.log", "amd_presr.log",
        "dlssnr_amd_present1.dll", "dlssnr_amd_present2.dll", "dlssnr_amd_present3.dll",
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

    /// <summary>The mochizuki runtime and its dlssnr-amd tree when nothing recorded them -- copied in by hand,
    /// or left by a record this build could not read -- taken the way a ReShade is: when the runtime is a build
    /// this app pins, the files that hash to one, and the prewarm list it rewrites. Not with OptiScaler here,
    /// whose package ships the very same files.</summary>
    private static int RemovePinnedMochizuki(string dir, HashSet<string> builds, HashSet<string> still, Report report)
    {
        var runtime = Path.Combine(dir, MochizukiRuntimeName);
        if (still.Contains(MochizukiRuntimeName) || File.Exists(Path.Combine(dir, OptiScalerIni))
            || !File.Exists(runtime) || !builds.Contains(Engine.HashFile(runtime))) return 0;
        var gone = RemoveFile(dir, MochizukiRuntimeName, report);
        var root = Path.Combine(dir, Engine.MochizukiFolder);
        if (!Directory.Exists(root)) return gone;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToList())
        {
            var name = Path.GetRelativePath(dir, file).Replace(Path.DirectorySeparatorChar, '/');
            if (!still.Contains(name) && Engine.IsAllowed(name)
                && (Engine.IsRuntimeMaintained(name) || builds.Contains(Engine.HashFile(file))))
                gone += RemoveFile(dir, name, report);
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
            // A log the game still writes is the game's, not a file of ours left installed: it goes next time.
            if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) && e is IOException or UnauthorizedAccessException)
                report.Warn($"{name} is still open, so it stays for now and goes with the next uninstall: {e.Message}");
            else
                report.Err($"could not remove {name}: {e.Message}");
            return 0;
        }
    }
}
