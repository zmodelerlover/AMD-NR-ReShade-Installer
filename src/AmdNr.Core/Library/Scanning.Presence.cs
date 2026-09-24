// Two questions asked about every folder already in the list, each time the app opens: is the game
// still there, and when something of ours is in it, which route put it there.

using System.Text;

namespace AmdNr.Core;

/// <summary>What became of a folder in the list. Unreachable is not Gone: a drive that is not
/// plugged in, or a network share that is down, says nothing about the game on it.</summary>
public enum Presence
{
    Here,
    Gone,
    Unreachable,
}

public static partial class GameScanner
{
    /// <summary>Which route the install in this folder came from, or null when nothing of ours is in
    /// it. The manifest says so when there is one this build can read and it still owns a file that
    /// is really there; a manifest kept only for the configuration it preserved is not an install
    /// (see <see cref="IsInstalled"/>) and does not get a say.
    ///
    /// Without one -- an install from before the manifest existed, or an OptiScaler another setup
    /// put there -- the files decide: the add-on's own modules are the ReShade route, and the runtime
    /// once per pass beside OptiScaler's ini is the OptiScaler one.</summary>
    public static RouteFamily? InstalledAs(string folder)
    {
        if (!IsInstalled(folder)) return null;

        foreach (var name in new[] { Engine.ManifestNameX64, Engine.ManifestName })
        {
            var path = Path.Combine(folder, name);
            if (!File.Exists(path)) continue;
            Manifest manifest;
            try { manifest = Manifest.Decode(Encoding.UTF8.GetString(Engine.Read(path))); }
            catch (InstallException) { continue; }
            if (!manifest.Entries.Any(e => e.Owned && !e.Configuration && File.Exists(Path.Combine(folder, e.Name))))
                continue;
            return manifest.Preset == Preset.OptiScaler.ManifestPreset() ? RouteFamily.OptiScaler : RouteFamily.ReShade;
        }

        if (new[] { Work.AddonName, Work.Addon32Name, Work.Host64Name }.Any(n => File.Exists(Path.Combine(folder, n))))
            return RouteFamily.ReShade;
        return File.Exists(Path.Combine(folder, Work.OptiScalerIni)) || File.Exists(Path.Combine(folder, Work.OptiPasses[1]))
            ? RouteFamily.OptiScaler
            : RouteFamily.ReShade;
    }

    /// <summary>Whether the game in this folder is still installed. A launcher that uninstalls a game
    /// deletes what it installed and nothing else, so what is left behind is exactly the files this
    /// app and ReShade put there -- a folder holding only those is a game that has gone, and so is a
    /// folder that is not there at all. A drive that is not there is <see cref="Presence.Unreachable"/>:
    /// the game may be fine, on a disk that is not plugged in right now.
    ///
    /// Anything this cannot read counts as Here. Taking a game out of the list on a guess is worse
    /// than leaving one that has gone.</summary>
    public static Presence PresenceOf(string folder)
    {
        string root;
        try { root = Path.GetPathRoot(Path.GetFullPath(folder)) ?? ""; }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or IOException
                                      or System.Security.SecurityException)
        {
            return Presence.Gone;
        }
        if (root.Length == 0) return Presence.Gone;
        if (!Directory.Exists(root)) return Presence.Unreachable;
        // Gone only when the folder it sat in is still there: an uninstall takes the game's own
        // folder and leaves its library. A drive letter taken by another disk -- the external SSD
        // unplugged, a USB stick given its letter -- has none of the path, and is the same as a
        // drive that is not there.
        if (!Directory.Exists(folder))
            return Path.GetDirectoryName(Path.GetFullPath(folder)) is { } parent && Directory.Exists(parent)
                ? Presence.Gone
                : Presence.Unreachable;

        try
        {
            return Directory.EnumerateFileSystemEntries(folder).All(e => IsLeftover(Path.GetFileName(e)))
                ? Presence.Gone
                : Presence.Here;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return Presence.Here;
        }
    }

    /// <summary>A name in a game's root that only this app, ReShade or OptiScaler put there. ReShade
    /// names its log and its preset after the name it was loaded under, so every proxy name brings a
    /// .log with it.</summary>
    private static bool IsLeftover(string name) =>
        Leftovers.Contains(name)
        || name.StartsWith("dlssnr_amd_pass", StringComparison.OrdinalIgnoreCase)
        || (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase)
            && Leftovers.Contains(Path.ChangeExtension(name, ".dll")));

    private static readonly HashSet<string> Leftovers = new(
        Engine.Allowed.Select(n => n.Split('/')[0])
            .Concat(Engine.Legacy)
            .Concat(Work.Droppings)
            .Concat(Work.DroppingFolders)
            .Concat([
                Engine.ManifestName, Engine.ManifestNameX64, Engine.BackupDir, Engine.LegacyBackupDir,
                "ReShade.log", "ReShadePreset.ini", "ReShade", ".amd-nr-installer-write-probe",
            ]),
        StringComparer.OrdinalIgnoreCase);
}
