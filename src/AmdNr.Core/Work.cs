// What the installer actually does to a folder. No UI -- so the whole of it can be reasoned about,
// and tested, without a window in the way. Ported from installer/src/work.rs.
//
// One deliberate difference from the Rust original: the add-on is not compiled into this assembly.
// The Rust installer embedded it so it could never hand out an add-on from a different release than
// the runtime it was built beside; an app that updates itself and downloads its payloads cannot
// carry that, so the same guarantee is kept by pinning every hash in PayloadPins, which comes from
// the payload manifest and is verified after download and again before a byte is copied.

using System.Text;

namespace AmdNr.Core;

/// <summary>The hashes and sizes a payload set has to match. Defaults are the pinned v0.5.0 values
/// from the add-on's own source; the app overrides them from payload.json.</summary>
public sealed class PayloadPins
{
    public required string AddonSha { get; init; }
    public required ulong AddonSize { get; init; }
    public string RuntimeSha { get; init; } = Engine.RuntimeSha;
    public ulong RuntimeSize { get; init; } = 7_248_384;
    public string WeightsSha { get; init; } = Engine.WeightsSha;
    public ulong WeightsSize { get; init; } = 147_689_451;
}

/// <summary>What the target says about which route applies. A folder can hold a 32-bit launcher
/// next to a 64-bit game, so this detects when the answer is unambiguous and says so when it is
/// not, rather than picking one and being confidently wrong.</summary>
public sealed class Detected
{
    public Route? Route { get; private init; }
    public string? Line { get; private init; }
    public bool IsMixed { get; private init; }

    public static readonly Detected Unknown = new();
    public static Detected On(Route route, string why) => new() { Route = route, Line = why };
    public static Detected Mixed(string why) => new() { IsMixed = true, Line = why };
}

public static class Work
{
    public const string AddonName = "dlss5-neural.addon64";
    public const string RuntimeName = "dlssnr_amd_pass1.dll";
    public const string WeightsName = "dlssnr_on_amd_weights.bin";

    /// <summary>Known-bad: the runtime this release replaced. Recognised by its first bytes so the
    /// message can be "you have the old one" instead of "this file is wrong".</summary>
    private const string RuntimeSha0214Prefix = "e145ff963b1ef614";

    /// <summary>A ReShade proxy, by the name it has to be loaded under.</summary>
    private static readonly string[] Proxies = ["d3d11.dll", "dxgi.dll", "d3d12.dll", "opengl32.dll"];

    /// <summary>Files an older layout left behind: one copy of the runtime per pass, which did not
    /// fit in VRAM and has not been used for several releases.</summary>
    private static IEnumerable<string> DeadFiles() =>
        Enumerable.Range(2, 9).Select(n => $"dlssnr_amd_pass{n}.dll");

    /// <summary>A release folder keeps its payloads in files\, and a folder holding just the
    /// unzipped files works too. Whichever was given, this is where the payloads are read from.</summary>
    public static string PayloadDir(string source)
    {
        var nested = Path.Combine(source, "files");
        return File.Exists(Path.Combine(nested, RuntimeName)) || File.Exists(Path.Combine(nested, WeightsName))
            ? nested
            : source;
    }

    /// <summary>A dropped executable becomes its folder, which is right for a flow that installs
    /// into one.</summary>
    private static string ResolveSource(string raw)
    {
        var p = raw.Trim().Trim('"');
        if (p.Length == 0) return string.Empty;
        return File.Exists(p) ? Path.GetDirectoryName(p) ?? p : p;
    }

    /// <summary>The path exactly as typed, file or folder -- the 32-bit route needs the executable
    /// itself, because it reads the PE header.</summary>
    private static string ResolveTarget(string raw) => raw.Trim().Trim('"');

    private static string NameOf(string p) => Path.GetFileName(p);

    /// <summary>Three names at most: the point is to show the evidence, not to list a folder.</summary>
    private static string Joined(IReadOnlyList<string> names)
    {
        var shown = names.Take(3).ToList();
        return names.Count > shown.Count
            ? $"{string.Join(", ", shown)} and {names.Count - shown.Count} more"
            : string.Join(", ", shown);
    }

    private static void CheckReShade(string dir, Preset preset, Report report)
    {
        var found = Proxies.Where(n => File.Exists(Path.Combine(dir, n))).ToList();

        if (preset.IsVulkan())
        {
            if (found.Count == 0)
            {
                report.Info(
                    "No ReShade proxy DLL here, which is correct for Vulkan: ReShade loads as a global "
                    + $"layer instead. Make sure you ran its installer against {preset.ExpectedExe() ?? "the game's own .exe"} "
                    + "and picked Vulkan.");
            }
            else
            {
                report.Warn(
                    $"Found {string.Join(", ", found)} here. On Vulkan ReShade loads as a global layer, "
                    + "and a proxy DLL as well means two ReShade instances in one process. Remove it if "
                    + "Vulkan is what you run.");
            }
            return;
        }

        switch (found.Count)
        {
            case 0:
                report.Warn(
                    "No ReShade proxy DLL found here (d3d11.dll, dxgi.dll, d3d12.dll). The add-on cannot "
                    + "load without ReShade, and it has to be the build with full add-on support. Files "
                    + "were still copied, so installing ReShade afterwards is enough.");
                break;
            case 1:
                report.Ok($"ReShade found: {found[0]}");
                break;
            default:
                report.Warn(
                    $"More than one ReShade proxy here ({string.Join(", ", found)}). Only one is loaded, "
                    + "and which one depends on the game. Keep the one that matches the renderer.");
                break;
        }
    }

    private static void CheckExe(string dir, Preset preset, Report report)
    {
        if (preset.ExpectedExe() is not { } exe) return;
        if (File.Exists(Path.Combine(dir, exe)))
            report.Ok($"{exe} is here, so this is the right folder.");
        else
            report.Warn(
                $"{exe} is not in this folder. That is only a warning -- nothing checks which program "
                + "it is -- but it is usually a sign the path is wrong.");
    }

    /// <summary>ReShade writes DisabledAddons= into its own ini the first time anyone unticks an
    /// add-on, and from then on it never loads it again and says nothing anywhere. It is the one
    /// failure in this project that looks exactly like a broken install.</summary>
    private static void CheckDisabledAddons(string dir, Report report)
    {
        var ini = Path.Combine(dir, "ReShade.ini");
        string text;
        try { text = File.ReadAllText(ini); }
        catch { return; }

        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("DisabledAddons=", StringComparison.Ordinal)) continue;
            var list = line["DisabledAddons=".Length..];
            if (list.Contains(AddonName, StringComparison.Ordinal)
                || list.Contains("dlss5", StringComparison.OrdinalIgnoreCase))
            {
                report.Err(
                    "ReShade.ini has this add-on in DisabledAddons=. ReShade writes that line if the "
                    + "add-on is ever unticked, and then it never loads it again, with no error anywhere. "
                    + "Clear that line before blaming the install.");
            }
            else if (list.Length > 0)
            {
                report.Info($"ReShade.ini disables other add-ons: {list}");
            }
        }
    }

    /// <summary>Verify a payload in the folder given and hand back its bytes. Whether it then gets
    /// written is <see cref="Transaction.Apply"/>'s decision, because that is what records ownership
    /// and takes the backup.</summary>
    private static byte[]? VerifiedPayload(string srcDir, string name, string wantSha, Report report)
    {
        var src = Path.Combine(srcDir, name);
        if (!File.Exists(src))
        {
            report.Err($"{name} is not in the payload folder.");
            return null;
        }

        byte[] bytes;
        try { bytes = File.ReadAllBytes(src); }
        catch (Exception e)
        {
            report.Err($"could not read {name}: {e.Message}");
            return null;
        }

        var got = Engine.Sha(bytes);
        if (got == wantSha) return bytes;

        if (name == RuntimeName && got.StartsWith(RuntimeSha0214Prefix, StringComparison.Ordinal))
        {
            report.Err(
                $"{name} is the old v0.2.14 runtime. This release requires v0.2.17 and the add-on "
                + "refuses anything else. Let the app download the current one.");
        }
        else
        {
            report.Err(
                $"{name} does not match the expected SHA-256.\n      expected {wantSha}\n      "
                + $"got      {got}\n      The add-on hashes the runtime at load and will refuse it.");
        }
        return null;
    }

    /// <summary>The engine speaks the x86 installer's vocabulary; this turns it into the sentences
    /// a person reads.</summary>
    private static void Narrate(string line, Report report)
    {
        if (Strip(line, "IDENTICAL: ") is { } identical) report.Ok($"{identical} already correct, left alone.");
        else if (Strip(line, "CREATE: ") is { } created) report.Ok($"{created} copied and verified.");
        else if (Strip(line, "EXTERNAL backed up: ") is { } backed) report.Ok($"{backed} replaced; the previous file was backed up.");
        else if (Strip(line, "RESTORED: ") is { } restored) report.Ok($"restored {restored} from its backup");
        else if (Strip(line, "REMOVED: ") is { } removed) report.Ok($"removed {removed}");
        else if (Strip(line, "WARNING ") is { } warning) report.Warn(warning);
        else report.Info(line); // PRESERVED lines and anything the engine adds later read fine as they are.

        static string? Strip(string s, string prefix) =>
            s.StartsWith(prefix, StringComparison.Ordinal) ? s[prefix.Length..] : null;
    }

    private static void SweepDead(string dir, Report report)
    {
        var removed = new List<string>();
        foreach (var name in DeadFiles())
        {
            var p = Path.Combine(dir, name);
            if (!File.Exists(p)) continue;
            try
            {
                File.Delete(p);
                removed.Add(name);
            }
            catch (Exception e)
            {
                report.Warn($"could not remove {name}: {e.Message}");
            }
        }
        if (removed.Count > 0)
            report.Ok($"removed {removed.Count} unused file(s) from the old per-pass layout: {string.Join(", ", removed)}");
    }

    // -- Pre-flight ------------------------------------------------------------------------------
    // Everything that can be known before a single byte is written, and cheap enough to redo while
    // a path is still being pasted: metadata, one open(), one free-space call.

    public static Report Preflight(string gameDir, string payloadDir, Preset preset, PayloadPins pins)
    {
        var report = new Report();
        var dir = ResolveSource(gameDir);
        var src = ResolveSource(payloadDir);

        // --- the payloads, which is where someone starts ---------------------------------------
        if (src.Length == 0)
        {
            report.Info("Waiting for the payload folder: the add-on, the runtime and the weights.");
        }
        else if (!Directory.Exists(src))
        {
            report.Err($"{src} is not a folder.");
        }
        else
        {
            var allThere = true;
            var payloads = PayloadDir(src);
            foreach (var (name, want) in new[]
                     {
                         (AddonName, pins.AddonSize),
                         (RuntimeName, pins.RuntimeSize),
                         (WeightsName, pins.WeightsSize),
                     })
            {
                switch (Engine.SizeOf(Path.Combine(payloads, name)))
                {
                    case null:
                        report.Err($"{name} is not in that folder.");
                        allThere = false;
                        break;
                    case { } got when got != want:
                        report.Err(
                            $"{name} is {got} bytes, and this release expects {want}. That is a different "
                            + "build, and the add-on refuses anything but the one it was compiled against.");
                        allThere = false;
                        break;
                }
            }
            if (allThere) report.Ok("Every payload is there and the right size. Installing verifies the SHA-256 too.");
        }

        // --- the target -------------------------------------------------------------------------
        if (dir.Length == 0)
        {
            report.Info($"Waiting for the {preset.FolderLabel().ToLowerInvariant()}.");
            return report;
        }
        if (!Directory.Exists(dir))
        {
            report.Err($"{dir} is not a folder.");
            return report;
        }

        if (!Engine.FolderIsWritable(dir))
        {
            report.Err(
                "That folder cannot be written to. It is either read-only or somewhere that needs "
                + "administrator rights -- run this installer as administrator, or move the game.");
        }

        // Anything already there and held open will fail the copy, so name the files rather than let
        // the copy come back with "access denied" halfway through.
        var held = new[] { AddonName, RuntimeName, WeightsName }
            .Where(n => Engine.IsLocked(Path.Combine(dir, n))).ToList();
        if (held.Count > 0)
        {
            report.Err(
                $"{string.Join(", ", held)} {(held.Count == 1 ? "is" : "are")} open by another program. "
                + "The game or emulator is almost certainly still running -- close it and this line goes away.");
        }

        // --- room for the weights ----------------------------------------------------------------
        ulong need = 0;
        foreach (var (name, size) in new[]
                 {
                     (AddonName, pins.AddonSize),
                     (RuntimeName, pins.RuntimeSize),
                     (WeightsName, pins.WeightsSize),
                 })
        {
            if (Engine.SizeOf(Path.Combine(dir, name)) != size) need += size;
        }
        if (Engine.FreeBytes(dir) is { } free && need > 0 && free < need)
        {
            report.Err(
                $"Not enough room: {free / 1_048_576} MB free, and this needs {need / 1_048_576} MB. "
                + $"The weights alone are {pins.WeightsSize / 1_048_576} MB.");
        }

        CheckExe(dir, preset, report);
        CheckReShade(dir, preset, report);
        CheckDisabledAddons(dir, report);

        var dead = DeadFiles().Where(n => File.Exists(Path.Combine(dir, n))).ToList();
        if (dead.Count > 0)
        {
            report.Info(
                $"{dead.Count} file(s) from the old per-pass layout are here and will be removed: "
                + string.Join(", ", dead));
        }

        if (!report.Failed) report.Ok("Nothing in the way.");
        return report;
    }

    // -- Install ---------------------------------------------------------------------------------

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

    private static Report InstallX86(string gameDir, string releaseDir, Preset preset)
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

        var app = new X86Installer(release);
        try
        {
            app.Install(target, preset.ManifestPreset());
            foreach (var line in app.Log) Narrate(line, report);
            report.Info(preset.Note());
            report.Info(
                "It starts switched off. Open the overlay with Home, or press Ctrl+End. StartOn=1 in "
                + "dlss5-neural.ini makes it come up enabled.");
        }
        catch (InstallException e)
        {
            foreach (var line in app.Log) Narrate(line, report);
            report.Err($"{e.Message}. Nothing was left half-written: the install rolled itself back.");
        }
        return report;
    }

    public static Report Install(string gameDir, string payloadDir, Preset preset, PayloadPins pins)
    {
        if (preset.Route() == Route.X86) return InstallX86(gameDir, payloadDir, preset);

        var report = new Report();
        var dir = ResolveSource(gameDir);
        var src = ResolveSource(payloadDir);

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
        report.Info($"preset: {preset.Label()}");

        CheckExe(dir, preset, report);
        CheckReShade(dir, preset, report);

        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        if (src.Length == 0)
        {
            report.Err("No payload folder given, and every file this installs comes out of one.");
            return report;
        }
        if (!Directory.Exists(src))
        {
            report.Err($"{src} is not a folder.");
            return report;
        }

        var payloads = PayloadDir(src);
        foreach (var (name, want) in new[]
                 {
                     (AddonName, pins.AddonSha),
                     (RuntimeName, pins.RuntimeSha),
                     (WeightsName, pins.WeightsSha),
                 })
        {
            if (VerifiedPayload(payloads, name, want, report) is { } bytes) files[name] = bytes;
        }

        // A refused payload stops the whole install rather than leaving the add-on behind on its own.
        // The transaction is all-or-nothing, which is the point of routing through the engine.
        if (report.Failed)
        {
            report.Info("Nothing was written: fix the problem above and run it again.");
            return report;
        }

        var log = new List<string>();
        try
        {
            Transaction.Apply(dir, preset.ManifestPreset(), Route.X64, files, log);
            foreach (var line in log) Narrate(line, report);
        }
        catch (InstallException e)
        {
            foreach (var line in log) Narrate(line, report);
            report.Err($"{e.Message}. Nothing was left half-written: the install rolled itself back.");
            return report;
        }

        SweepDead(dir, report);

        if (!report.Failed)
        {
            report.Info(preset.Note());
            report.Info(
                "It starts switched off. Open the overlay with Home, or press Ctrl+End. StartOn=1 in "
                + "dlss5-neural.ini makes it come up enabled.");
        }
        return report;
    }

    // -- Uninstall -------------------------------------------------------------------------------

    private static Report UninstallX86(string gameDir)
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

        var log = new List<string>();
        try
        {
            Transaction.Uninstall(dir, Route.X86, false, log);
            foreach (var line in log) Narrate(line, report);
        }
        catch (InstallException e)
        {
            report.Err(e.Message);
        }
        report.Info("ReShade itself was left alone. Use its own installer to remove it.");
        return report;
    }

    public static Report Uninstall(string gameDir, Preset preset)
    {
        if (preset.Route() == Route.X86) return UninstallX86(gameDir);

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
                Transaction.Uninstall(dir, Route.X64, false, log);
                foreach (var line in log) Narrate(line, report);
                gone++;
            }
            catch (InstallException e)
            {
                report.Err($"could not undo the recorded install: {e.Message}");
            }
        }
        else
        {
            // Everything the add-on installs. The ini is deliberately not in this list.
            var names = new List<string> { AddonName, RuntimeName, WeightsName };
            names.AddRange(DeadFiles());
            foreach (var name in names) gone += RemoveFile(dir, name, report);
        }

        // Written by the add-on itself at run time, so they are never in a manifest and are swept the
        // same way whichever branch ran above.
        foreach (var name in new[] { "dlss5-pass1.dll", "dlss5-neural.log", "dlssnr_on_amd.log", "dlssnr_on_amd.ini" })
            gone += RemoveFile(dir, name, report);

        foreach (var folder in new[] { "dlss5-runtime", "dlss5-captures" })
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

        if (gone == 0) report.Warn("Nothing of ours was in that folder.");
        if (File.Exists(Path.Combine(dir, "dlss5-neural.ini")))
        {
            report.Info(
                "dlss5-neural.ini was left in place: it is your tuning, not ours. Delete it by hand if "
                + "you want a clean slate.");
        }
        report.Info("ReShade itself was left alone. Use its own installer to remove it.");
        return report;
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

    // -- Detection -------------------------------------------------------------------------------

    private static Route? RouteOf(ushort? machine) => machine switch
    {
        Engine.MachineX86 => Route.X86,
        Engine.MachineX64 => Route.X64,
        _ => null,
    };

    /// <summary>Read the target -- an executable, or the executables sitting in a folder -- and
    /// decide.</summary>
    public static Detected Detect(string target)
    {
        var path = ResolveTarget(target);
        if (path.Length == 0) return Detected.Unknown;

        if (File.Exists(path))
        {
            return RouteOf(Engine.MachineOfFile(path)) switch
            {
                Route.X86 => Detected.On(Route.X86, $"{NameOf(path)} is a 32-bit executable, so this is the bridge route."),
                Route.X64 => Detected.On(Route.X64, $"{NameOf(path)} is a 64-bit executable."),
                _ => Detected.Unknown,
            };
        }
        if (!Directory.Exists(path)) return Detected.Unknown;

        var x86 = new List<string>();
        var x64 = new List<string>();
        try
        {
            foreach (var p in Directory.EnumerateFiles(path, "*.exe"))
            {
                switch (RouteOf(Engine.MachineOfFile(p)))
                {
                    case Route.X86: x86.Add(NameOf(p)); break;
                    case Route.X64: x64.Add(NameOf(p)); break;
                }
            }
        }
        catch
        {
            return Detected.Unknown;
        }

        return (x86.Count > 0, x64.Count > 0) switch
        {
            (false, false) => Detected.Unknown,
            (true, false) => Detected.On(Route.X86,
                $"{Joined(x86)} here {(x86.Count == 1 ? "is" : "are")} 32-bit, so this is the bridge route."),
            (false, true) => Detected.On(Route.X64,
                $"{Joined(x64)} here {(x64.Count == 1 ? "is" : "are")} 64-bit."),
            (true, true) => Detected.Mixed(
                $"Both widths are here: {Joined(x86)} is 32-bit and {Joined(x64)} is 64-bit. A 32-bit "
                + "launcher beside a 64-bit game is normal -- pick the one the game actually runs as."),
        };
    }
}
