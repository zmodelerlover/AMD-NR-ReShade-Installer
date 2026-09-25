// The mochizuki runtime, the third one OptiScaler can drive from 0.4.0 on: DLSS 5's NR network as
// Vulkan compute shaders (mochizuki0323/DLSSNR-AMD), run on a Vulkan device of its own beside the
// game's D3D12. It needs RDNA4's FP8 matrix instructions. Unlike the other two it goes in only when
// somebody asks for it -- it is experimental and its model is 141 MB -- and asking for it also makes
// it the NR runtime in OptiScaler.ini, the one key this app writes there. An install that does not
// ask for it takes out what an earlier one put in, so the choice is what the folder holds.
//
// Everything else is the OptiScaler route's own: the same transaction and manifest, every file
// pinned, and uninstall taking back what went in.

using System.Text;

namespace AmdNr.Core;

public static partial class Work
{
    /// <summary>The runtime itself, beside OptiScaler, which loads it by this name.</summary>
    internal const string MochizukiRuntimeName = "MochizukiNrRuntime.dll";

    /// <summary>What OptiScaler.ini names this runtime by: [DlssNr] NrBackend=mochizuki.</summary>
    internal const string MochizukiBackend = "mochizuki";

    /// <summary>The runtime's log, beside the DLL. Swept on uninstall with the others: see Droppings.</summary>
    internal const string MochizukiLog = "mochizuki_nr.log";

    /// <summary>The Vulkan pipeline cache the runtime writes into dlssnr-amd\ while a game runs.</summary>
    private const string MochizukiPipelineCache = "pipeline.cache";

    /// <summary>Where one mochizuki payload file goes in the game folder. A payload path may be two
    /// levels deep at most -- every app already out there refuses a whole manifest that has a deeper
    /// one -- and the runtime reads files three levels down, so the folder part of a payload path
    /// spells the folders with dots: dlssnr-amd.shaders.runtime/x.spv is dlssnr-amd\shaders\runtime\x.spv.
    /// The runtime and the model need no spelling: MochizukiNrRuntime.dll, dlssnr-amd/dlssnr.bin.</summary>
    internal static string MochizukiDestination(string payloadPath)
    {
        var slash = payloadPath.IndexOf('/');
        return slash < 0 ? payloadPath : payloadPath[..slash].Replace('.', '/') + payloadPath[slash..];
    }

    /// <summary>A name in the game folder that is the mochizuki runtime's: the runtime, or anything
    /// in its dlssnr-amd tree.</summary>
    private static bool IsMochizukiName(string name) =>
        name == MochizukiRuntimeName || name.StartsWith(Engine.MochizukiFolder + "/", StringComparison.Ordinal);

    /// <summary>What an install recorded of mochizuki in this folder. An install hands these to the
    /// transaction to take out when it does not write them again: all of them when mochizuki is left
    /// off, and those an older build had and this one has not when it is on.</summary>
    private static List<string> MochizukiRecorded(Manifest? manifest) =>
        manifest?.Entries.Where(e => !e.Configuration && IsMochizukiName(e.Name)).Select(e => e.Name).ToList() ?? [];

    /// <summary>Whether this app installed the mochizuki runtime in this game folder and it is still
    /// there: what the sheet goes by when nobody has said, on this machine, whether they want it.
    /// Without it, a game added again, or the app on another PC, would leave the box off and the next
    /// update would take mochizuki out.</summary>
    public static bool HasMochizuki(string gameDir)
    {
        var dir = ResolveSource(gameDir);
        if (dir.Length == 0 || !Directory.Exists(dir)) return false;
        return InstalledManifest(dir) is { } m && m.Preset == Preset.OptiScaler.ManifestPreset()
               && m.Entries.Any(e => e.Name == MochizukiRuntimeName)
               && File.Exists(Path.Combine(dir, MochizukiRuntimeName));
    }

    private const string MochizukiComesOut =
        "mochizuki is installed here and is left off this time, so installing takes out what this app put in "
        + "of it: MochizukiNrRuntime.dll and its dlssnr-amd folder.";

    /// <summary>After an install that took mochizuki out: what its runtime wrote beside it, its empty
    /// folders, and a word when the OptiScaler.ini left in place still names it.</summary>
    private static void AfterMochizukiRetired(string dir, Report report)
    {
        report.Info(File.Exists(Path.Combine(dir, MochizukiRuntimeName))
            ? $"mochizuki was left off. The {MochizukiRuntimeName} here stays: this app did not put it there, "
              + "or it was changed since."
            : "mochizuki was left off, so what this app had put in of it came out.");
        AfterMochizukiUninstall(dir, report);
        PruneEmpty(dir, [Engine.BackupDir], report);
        var ini = Path.Combine(dir, OptiScalerIni);
        if (File.Exists(ini) && !File.Exists(Path.Combine(dir, MochizukiRuntimeName))
            && string.Equals(Engine.Trim(Engine.GetIni(File.ReadAllText(ini), "DlssNr", "NrBackend")), MochizukiBackend,
                StringComparison.OrdinalIgnoreCase))
            report.Warn(
                $"{OptiScalerIni} holds your settings and still names mochizuki as the NR runtime, which is not "
                + "here any more. Pick daniel or lmxxf under NR runtime in OptiScaler's Neural tab, or tick mochizuki "
                + "and install again.");
    }

    /// <summary>The mochizuki files that are not yet in the game folder at their size, and how big.</summary>
    private static ulong MochizukiNeed(string src, string dir, PayloadPins pins) =>
        pins.MochizukiFiles.Keys
            .Select(p => (Size: Engine.SizeOf(Path.Combine(src, p)) ?? 0, Destination: MochizukiDestination(p)))
            .Where(f => Engine.SizeOf(Path.Combine(dir, f.Destination)) != f.Size)
            .Aggregate(0UL, (sum, f) => sum + f.Size);

    /// <summary>The names the running game would hold open, which fail the copy.</summary>
    private static IEnumerable<string> MochizukiHeld(string dir) =>
        new[] { MochizukiRuntimeName, Engine.MochizukiFolder + "/dlssnr.bin" }
            .Where(n => Engine.IsLocked(Path.Combine(dir, n)));

    /// <summary>What the pre-flight says about mochizuki once it has been asked for.</summary>
    private static void CheckMochizukiPayload(string src, PayloadPins pins, Report report)
    {
        if (pins.MochizukiFiles.Count == 0)
        {
            report.Err(NoMochizuki);
            return;
        }
        if (src.Length == 0 || !Directory.Exists(src)) return;
        var missing = pins.MochizukiFiles.Keys.Where(n => !File.Exists(Path.Combine(src, n))).ToList();
        if (missing.Count > 0)
            report.Err($"{Joined(missing)} {(missing.Count == 1 ? "is" : "are")} not in the payload folder.");
        else
            report.Ok("The mochizuki runtime, its shaders and its model are there. Installing verifies the SHA-256 of each one.");
    }

    private const string NoMochizuki =
        "The OptiScaler version chosen has no mochizuki runtime. Pick 0.4.0-amd-nr or newer, or leave mochizuki off.";

    /// <summary>Every mochizuki file, verified against its pin, under the name it takes in the game.</summary>
    private static void AddMochizuki(SortedDictionary<string, byte[]> files, string src, PayloadPins pins, Report report)
    {
        foreach (var (path, sha) in pins.MochizukiFiles)
            if (VerifiedPayload(src, path, sha, report) is { } bytes)
                files[MochizukiDestination(path)] = bytes;
    }

    /// <summary>Makes mochizuki the NR runtime in the OptiScaler.ini this install writes: one key,
    /// [DlssNr] NrBackend, set in place the way ReShade.ini is edited, every other byte as it was.
    /// An ini that is the person's -- one that was here before OptiScaler was installed from this
    /// app, or one OptiScaler has saved settings into since -- is not written by the install at all,
    /// so it is not changed here either: the report says where to pick mochizuki instead.</summary>
    private static void PickMochizukiInIni(SortedDictionary<string, byte[]> files, string dir, Manifest? manifest,
        Report report)
    {
        var path = Path.Combine(dir, OptiScalerIni);
        var recorded = manifest?.Entries.FirstOrDefault(e => e.Name == OptiScalerIni);
        var theirs = File.Exists(path) && (recorded is null || Engine.HashFile(path) != recorded.Hash);
        if (theirs || !files.TryGetValue(OptiScalerIni, out var ini))
        {
            var now = File.Exists(path) ? Engine.Trim(Engine.GetIni(File.ReadAllText(path), "DlssNr", "NrBackend")) : "";
            if (!string.Equals(now, MochizukiBackend, StringComparison.OrdinalIgnoreCase))
                report.Info(
                    $"{OptiScalerIni} holds your settings and stays as it is, so the NR runtime it names is unchanged. "
                    + "Pick mochizuki under NR runtime in OptiScaler's Neural tab and restart the game.");
            return;
        }
        files[OptiScalerIni] = Encoding.UTF8.GetBytes(
            Engine.SetIni(Encoding.UTF8.GetString(ini), "DlssNr", "NrBackend", MochizukiBackend));
        report.Ok($"{OptiScalerIni} names mochizuki as the NR runtime (NrBackend=mochizuki under [DlssNr]).");
    }

    private const string MochizukiInstalled =
        "The mochizuki runtime went in too: MochizukiNrRuntime.dll, and dlssnr-amd\\ with its shaders, its prewarm "
        + "list and its model. It is experimental and runs on RDNA4 cards only (RX 9000 series, AMD driver 25.10 or "
        + "newer). The first start in each game compiles its network for a few seconds before NR shows; its log is "
        + MochizukiLog + ".";

    /// <summary>What the mochizuki runtime writes beside its files while a game runs -- the pipeline
    /// cache, and the temporary file a write cut short leaves -- once the runtime itself is gone, and
    /// then its folders, deepest first, each only when nothing is left in it. With the runtime still
    /// here (the game has it open, or it was not this app's) nothing of it is touched.</summary>
    private static void AfterMochizukiUninstall(string dir, Report report)
    {
        var root = Path.Combine(dir, Engine.MochizukiFolder);
        if (!Directory.Exists(root) || File.Exists(Path.Combine(dir, MochizukiRuntimeName))) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(root).Concat(Leaves(Path.Combine(root, "prewarm"))).ToList())
            {
                var name = Path.GetFileName(file);
                var written = name == MochizukiPipelineCache
                              || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)
                              && (name.StartsWith(MochizukiPipelineCache + ".", StringComparison.Ordinal)
                                  || name.StartsWith("manifest.txt.", StringComparison.Ordinal));
                if (!written) continue;
                Engine.Writable(file);
                File.Delete(file);
                report.Ok($"removed {Engine.MochizukiFolder}\\{Path.GetRelativePath(root, file)}");
            }

            // Two levels below dlssnr-amd at most, which is as deep as anything of ours goes.
            var folders = Directory.GetDirectories(root)
                .SelectMany(d => Directory.GetDirectories(d).Append(d))
                .Append(root);
            foreach (var folder in folders)
            {
                if (!Directory.Exists(folder) || Directory.EnumerateFileSystemEntries(folder).Any()) continue;
                Directory.Delete(folder);
                if (folder == root) report.Ok($"removed {Engine.MochizukiFolder}\\");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Warn($"could not remove everything in {Engine.MochizukiFolder}: {e.Message}");
        }

        static IEnumerable<string> Leaves(string folder) =>
            Directory.Exists(folder) ? Directory.EnumerateFiles(folder) : [];
    }
}
