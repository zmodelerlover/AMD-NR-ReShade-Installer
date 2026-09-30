// The OptiScaler route: the same network, run from inside the game's own upscaler call rather than
// from a ReShade add-on. On D3D12 an add-on is shown the finished frame and nothing else; OptiScaler
// sits where the game hands DLSS, FSR or XeSS its colour, depth and motion vectors, so the network
// gets all three. What it installs is the OptiScaler AMD neural rendering build
// (MatheusFerreiraS/neural-amd-opti), the runtime build that OptiScaler recognises, once per pass,
// and the same weights the add-on uses -- and, only when asked for, the mochizuki runtime
// (Work.Mochizuki.cs). Which danielblnc builds it runs in place of the download's is Work.Runtimes.cs.
//
// It goes through the same transaction as every other route (one manifest, a backup of whatever
// it displaces, uninstall putting that back), and it shares the 64-bit manifest with the ReShade
// routes, which is what makes them alternatives: Transaction.Apply refuses a second preset in a
// folder that already has one.

namespace AmdNr.Core;

public static partial class Work
{
    /// <summary>OptiScaler in the payload. It is installed under a proxy name, never under its own.</summary>
    public const string OptiScalerDllPayload = "OptiScaler.dll";

    public const string OptiScalerIni = "OptiScaler.ini";

    /// <summary>OptiScaler loads one copy of the runtime per pass, up to three, each its own module
    /// with its own history. The ReShade route retired these names; this route owns them.</summary>
    internal static readonly string[] OptiPasses =
        ["dlssnr_amd_pass1.dll", "dlssnr_amd_pass2.dll", "dlssnr_amd_pass3.dll"];

    /// <summary>Where the runtime came from when a person supplied it, as OwnRuntime names the others.</summary>
    private const string SuppliedFrom = "supplied";

    /// <summary>Folders this route creates, deepest first, so the uninstall can take each one back
    /// once it is empty and leave it alone when something else put files there too.</summary>
    private static readonly string[] OptiFolders =
    [
        "OptiScaler/D3D12_OptiScaler", "OptiScaler", "experimental_lighting",
        "lmxxf-modules", "native-game-tiled-assets", "shaders",
    ];

    /// <summary>The lmxxf runtime, which OptiScaler 0.2.0 and later install beside the danielblnc one.</summary>
    internal const string LmxxfRuntimeName = "LmxxfNrRuntime.dll";

    /// <summary>Where the lmxxf runtime keeps the shaders it compiled, next to their source. It is
    /// the runtime's, written while a game runs, so nothing records it; uninstall takes it when it
    /// holds nothing but those.</summary>
    private const string ShaderCache = "shaders/shader-cache";

    /// <summary>Files whose presence says the game has an upscaler for OptiScaler to take over. The
    /// detection reads the same list to decide whether a D3D11 and D3D12 game is recommended this route.</summary>
    internal static readonly string[] UpscalerFiles =
    [
        "nvngx_dlss.dll", "nvngx_dlssd.dll", "sl.dlss.dll", "libxess.dll", "amd_fidelityfx_dx12.dll",
        "amd_fidelityfx_upscaler_dx12.dll", "ffx_fsr3upscaler_x64.dll", "ffx_fsr2_api_x64.dll",
        "ffx_fsr2_api_dx12_x64.dll",
    ];

    /// <summary>Where one payload file goes in the game folder. OptiScaler takes the proxy name, and
    /// the Agility runtime sits one level deeper than a payload path may go.</summary>
    internal static string OptiDestination(string payloadPath, string proxyName) => payloadPath switch
    {
        OptiScalerDllPayload => proxyName,
        "D3D12_OptiScaler/D3D12Core.dll" => "OptiScaler/D3D12_OptiScaler/D3D12Core.dll",
        _ => payloadPath,
    };

    /// <summary>Launchers that sit beside the game they start and so load its proxy first. OptiScaler
    /// hooking inside Red Dead Redemption's PlayRDR.exe broke the Rockstar Games SDK (error 25D11006);
    /// TargetProcessName leaves it passive in every process but the game.</summary>
    private static readonly (string Launcher, string Game, string Why)[] Launchers =
        [("PlayRDR.exe", "rdr.exe", "the Rockstar Games SDK")];

    internal static (string Launcher, string Game, string Why)? LauncherBeside(string dir) =>
        Launchers.Where(l => File.Exists(Path.Combine(dir, l.Launcher)) && File.Exists(Path.Combine(dir, l.Game)))
            .Select(l => ((string Launcher, string Game, string Why)?)l).FirstOrDefault();

    private static byte[] OnlyInTheGame(string dir, byte[] ini, Report report)
    {
        if (LauncherBeside(dir) is not { } l) return ini;
        report.Info($"{OptiScalerIni} names {l.Game} as the only process to hook: {l.Launcher} loads OptiScaler first.");
        var text = System.Text.Encoding.UTF8.GetString(ini);
        return System.Text.Encoding.UTF8.GetBytes(Engine.SetIni(text, "ProcessFilter", "TargetProcessName", l.Game));
    }

    /// <summary>The name OptiScaler goes in as: the one picked, or else the first the OptiScaler wiki gives for
    /// the game, or else the one an install of it here already has, or else dxgi.dll. The wiki comes before the
    /// installed name: an install made before the app read the wiki went in as dxgi.dll, which the wiki can
    /// say the game refuses, and an update moves it the way picking a name does -- the old name is given back,
    /// so there is never a second OptiScaler beside the first.</summary>
    internal static string OptiProxyFor(string? wanted, Manifest? installed = null, string? suggested = null)
    {
        var choices = ProxyChoicesFor(Preset.OptiScaler);
        string? Named(string? name) => choices.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return Named(wanted) ?? Named(suggested) ?? InstalledOptiProxies(installed).FirstOrDefault() ?? choices[0];
    }

    /// <summary>The proxy names an OptiScaler install recorded writing. An entry of no bytes is a file the
    /// install moved out (the author's version.dll), not OptiScaler.</summary>
    private static IEnumerable<string> InstalledOptiProxies(Manifest? m) =>
        m is null || m.Preset != Preset.OptiScaler.ManifestPreset()
            ? []
            : m.Entries.Where(e => e.Owned && e.Hash != Engine.Sha([])
                                   && ProxyChoicesFor(Preset.OptiScaler).Contains(e.Name, StringComparer.OrdinalIgnoreCase))
                .Select(e => e.Name);

    /// <summary>What an OptiScaler install is compared against: the newest version the payload
    /// offers, the one an install gets when nobody picks another. Keyed by the path in the game
    /// folder, because the lmxxf weights and shaders share file names across folders.</summary>
    private static Dictionary<string, string> PinnedForOptiScaler(PayloadManifest payload)
    {
        payload = payload.Newest(PayloadManifest.OptiScalerComponent);
        var pinned = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { PayloadManifest.OptiScalerComponent, PayloadManifest.LmxxfWeightsComponent })
            if (payload.Has(name))
                foreach (var file in payload.Component(name).Installed)
                    pinned[OptiDestination(file.RelativePath, OptiScalerDllPayload)] = Engine.Lower(file.Sha256);
        if (payload.Has(PayloadManifest.OptiRuntimeComponent)
            && payload.Component(PayloadManifest.OptiRuntimeComponent).Files.FirstOrDefault() is { } runtime)
            foreach (var pass in OptiPasses)
                pinned[pass] = Engine.Lower(runtime.Sha256);
        if (payload.Has(PayloadManifest.RuntimeComponent)
            && payload.Component(PayloadManifest.RuntimeComponent).Files.FirstOrDefault(f => f.Name == WeightsName) is { } weights)
            pinned[WeightsName] = Engine.Lower(weights.Sha256);
        // Asked about only where an install has them: a folder installed without mochizuki has none
        // of these names in its manifest.
        if (payload.Has(PayloadManifest.MochizukiComponent) && payload.Has(PayloadManifest.MochizukiModelComponent))
            foreach (var name in new[] { PayloadManifest.MochizukiComponent, PayloadManifest.MochizukiModelComponent })
                foreach (var file in payload.Component(name).Installed)
                    pinned[MochizukiDestination(file.RelativePath)] = Engine.Lower(file.Sha256);
        return pinned;
    }

    /// <summary>The route's manifest in this folder (the 64-bit one unless told), when this build can read one.</summary>
    private static Manifest? InstalledManifest(string dir, Route route = Route.X64)
    {
        var path = Path.Combine(dir, route.ManifestFileName());
        if (!File.Exists(path)) return null;
        try { return Manifest.Decode(System.Text.Encoding.UTF8.GetString(Engine.Read(path))); }
        catch (InstallException) { return null; }
    }

    /// <summary>Whether another route's install is still in this folder. What is left once a
    /// ReShade install has been taken back is its manifest holding only the configuration it keeps
    /// on purpose, and that is not an install: Transaction.Apply carries it over.</summary>
    private static bool OtherRouteInstalled(Manifest? m, string dir) =>
        m is not null && m.Preset != Preset.OptiScaler.ManifestPreset()
        && m.Entries.Any(e => e.Owned && !e.Configuration && File.Exists(Path.Combine(dir, e.Name)));

    /// <summary>Where the name OptiScaler goes in as came from, when it is not simply the one picked.</summary>
    private static void NoteProxy(string proxyName, string? wanted, Manifest? m, string? suggested, Report report)
    {
        var had = InstalledOptiProxies(m).FirstOrDefault();
        var fromWiki = !ProxyAllowed(Preset.OptiScaler, wanted) && ProxyAllowed(Preset.OptiScaler, suggested)
                       && string.Equals(suggested, proxyName, StringComparison.OrdinalIgnoreCase);
        var why = fromWiki ? ", the name the OptiScaler wiki gives for this game," : "";
        if (had is not null && had != proxyName)
            report.Info($"OptiScaler is here as {had}. This install puts it in as {proxyName}{why} instead, and {had} goes back to what it was.");
        else if (had is null && fromWiki)
            report.Info($"OptiScaler goes in as {proxyName}, the name the OptiScaler wiki gives for this game.");
    }

    /// <param name="preflight">Said before Install is pressed: the sheet asks to switch and takes the ReShade
    /// route out first, so there it is what Install does, not a problem.</param>
    private static void CheckOptiInTheWay(string dir, string proxyName, Manifest? m, Report report, bool preflight = false)
    {
        if (OtherRouteInstalled(m, dir))
        {
            if (preflight)
                report.Info($"This folder has the {m!.Preset} ReShade route installed. The two routes are alternatives: "
                            + "Install asks, then takes that one out before this goes in.");
            else
                report.Err(
                    $"This folder has the {m!.Preset} ReShade route installed. The two routes are alternatives: "
                    + "uninstall that one here first, then install this.");
            return;
        }

        var existing = Path.Combine(dir, proxyName);
        if (!File.Exists(existing) || m?.Entries.Any(e => e.Name == proxyName && e.Owned) == true) return;

        var (isReShade, product, version) = Identify(existing);
        var named = product is null ? "a file with no version information" : $"{product}{(version is null ? "" : $" {version}")}";
        if (product?.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) == true)
            report.Info($"{proxyName} here is already {named}. It is backed up and replaced, and uninstall puts it back.");
        else if (isReShade)
            report.Warn(
                $"{proxyName} here is ReShade. OptiScaler takes that name, so this ReShade is backed up and stops "
                + "loading until you uninstall. ReShade can run beside OptiScaler under another name, such as d3d12.dll.");
        else
            report.Warn(
                $"{proxyName} here is {named}. OptiScaler replaces it, the original is backed up and uninstall "
                + "puts it back. Pick winmm.dll as the name to keep it loading.");
    }

    private static void CheckUpscaler(string dir, Report report)
    {
        var found = GraphicsDetector.Upscalers(dir);
        if (found.Count > 0)
            report.Ok(
                $"{Joined(found)} {(found.Count == 1 ? "is" : "are")} here, so the game has an upscaler for "
                + "OptiScaler to take over. Switch it on in the game's settings.");
        else
            report.Warn(
                "No DLSS, FSR or XeSS files next to the game. OptiScaler only runs the network where the game "
                + "calls one of those upscalers, so a game without any has nothing for it to take over. The "
                + "D3D12 ReShade route is the one for that. Some engines keep these files elsewhere, so this is "
                + "only a warning.");
    }

    private static void CheckOptiRouteIsReachable(string dir, Report report)
    {
        var local = GraphicsDetector.Detect(dir);
        if (local.All.Count == 0 || local.All.Contains(GraphicsApi.D3D12)) return;
        report.Warn(
            $"The files here link {string.Join(", ", local.All.Select(GraphicsDetection.Short))}, not D3D12. "
            + $"{local.Why} OptiScaler's neural pass is built for D3D12; a D3D11 or Vulkan game reaches it only "
            + "through OptiScaler's D3D12-bridged upscalers.");
    }

    private static IEnumerable<(string Name, ulong Size)> OptiPayloadSizes(string src, PayloadPins pins) =>
        pins.OptiFiles.Keys.Select(p => (p, Engine.SizeOf(Path.Combine(src, p)) ?? 0))
            .Concat(OptiPasses.Select(p => (p, pins.OptiRuntimeSize)))
            .Append((WeightsName, pins.WeightsSize));

    private static Report PreflightOptiScaler(string gameDir, string payloadDir, PayloadPins pins, string? proxy,
        bool mochizuki, string? ownRuntime, UserRuntime? wantedRuntime, string? suggestedProxy)
    {
        var report = new Report();
        var dir = ResolveSource(gameDir);
        var src = ResolveSource(payloadDir);

        if (pins.OptiFiles.Count == 0 || pins.OptiRuntimeName.Length == 0)
        {
            report.Err("The payload list this app read has no OptiScaler route. Update the app, or try again once the payload carries it.");
        }
        else if (src.Length == 0)
        {
            report.Info("Waiting for the payload folder: OptiScaler, the runtime and the weights.");
        }
        else if (!Directory.Exists(src))
        {
            report.Err($"{src} is not a folder.");
        }
        else
        {
            var missing = pins.OptiFiles.Keys.Append(pins.OptiRuntimeName).Append(WeightsName)
                .Where(n => !File.Exists(Path.Combine(src, n))).ToList();
            if (missing.Count > 0)
                report.Err($"{Joined(missing)} {(missing.Count == 1 ? "is" : "are")} not in the payload folder.");
            else
                report.Ok("Every OptiScaler file is there. Installing verifies the SHA-256 of each one.");
        }
        if (mochizuki) CheckMochizukiPayload(src, pins, report);
        CheckSupplied(ownRuntime, wantedRuntime, pins, Preset.OptiScaler, report);

        if (dir.Length == 0)
        {
            report.Info($"Waiting for the {Preset.OptiScaler.FolderLabel().ToLowerInvariant()}.");
            return report;
        }
        if (!Directory.Exists(dir))
        {
            report.Err($"{dir} is not a folder.");
            return report;
        }
        if (!Engine.FolderIsWritable(dir))
            report.Err(
                "That folder cannot be written to. It is either read-only or somewhere that needs "
                + "administrator rights. Run this installer as administrator, or move the game.");

        var manifest = InstalledManifest(dir);
        var proxyName = OptiProxyFor(proxy, manifest, suggestedProxy);
        NoteProxy(proxyName, proxy, manifest, suggestedProxy, report);
        var retiring = !mochizuki && MochizukiRecorded(manifest).Count > 0;
        var held = new[] { proxyName, WeightsName }.Concat(OptiPasses).Concat(AuthorsRuntimesHere(dir, pins).Select(f => f.Name))
            .Where(n => Engine.IsLocked(Path.Combine(dir, n)))
            .Concat(mochizuki || retiring ? MochizukiHeld(dir) : []).ToList();
        if (held.Count > 0)
            report.Err(
                $"{string.Join(", ", held)} {(held.Count == 1 ? "is" : "are")} open by another program. "
                + "The game is almost certainly still running. Close it and this line goes away.");

        if (src.Length > 0 && Directory.Exists(src))
        {
            var need = OptiPayloadSizes(src, pins)
                .Where(f => Engine.SizeOf(Path.Combine(dir, OptiDestination(f.Name, proxyName))) != f.Size)
                .Aggregate(0UL, (sum, f) => sum + f.Size)
                + (mochizuki ? MochizukiNeed(src, dir, pins) : 0);
            if (Engine.FreeBytes(dir) is { } free && need > 0 && free < need)
                report.Err($"Not enough room: {free / 1_048_576} MB free, and this needs {need / 1_048_576} MB.");
        }

        CheckOptiInTheWay(dir, proxyName, manifest, report, preflight: true);
        CheckRuntimeAsVersionDll(dir, pins, report);
        CheckAuthorsRuntime(dir, pins, Preset.OptiScaler, report);
        CheckUpscaler(dir, report);
        CheckOptiRouteIsReachable(dir, report);
        if (!mochizuki && manifest?.Entries.Any(e => e.Name == MochizukiRuntimeName && e.Owned) == true)
            report.Info(MochizukiComesOut);

        if (File.Exists(Path.Combine(dir, OptiScalerIni)) && manifest?.Entries.Any(e => e.Name == OptiScalerIni) != true)
            report.Info($"{OptiScalerIni} is already here and stays as it is: it is your configuration.");

        if (!report.Failed) report.Ok("Nothing in the way.");
        return report;
    }

    private static Report InstallOptiScaler(string gameDir, string payloadDir, PayloadPins pins, string? proxy,
        bool mochizuki, string? ownRuntime, UserRuntime? wantedRuntime, string? suggestedProxy)
    {
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
        report.Info($"preset: {Preset.OptiScaler.Label()}");

        if (pins.OptiFiles.Count == 0 || pins.OptiRuntimeName.Length == 0)
        {
            report.Err("The payload list this app read has no OptiScaler route. Update the app, or try again once the payload carries it.");
            return report;
        }
        if (src.Length == 0 || !Directory.Exists(src))
        {
            report.Err("No payload folder given, and every file this installs comes out of one.");
            return report;
        }
        if (mochizuki && pins.MochizukiFiles.Count == 0)
        {
            report.Err(NoMochizuki);
            report.Info("Nothing was written: fix the problem above and run it again.");
            return report;
        }

        var manifest = InstalledManifest(dir);
        var proxyName = OptiProxyFor(proxy, manifest, suggestedProxy);
        CheckOptiInTheWay(dir, proxyName, manifest, report);
        CheckRuntimeAsVersionDll(dir, pins, report);
        var moves = CheckAuthorsRuntime(dir, pins, Preset.OptiScaler, report);
        if (report.Failed)
        {
            report.Info("Nothing was written: fix the problem above and run it again.");
            return report;
        }

        // Somebody's configuration is kept: OptiScaler rewrites its ini whenever a setting is saved,
        // so one that is here and was not written by this route is the settings somebody chose.
        var iniIsOurs = manifest?.Entries.Any(e => e.Name == OptiScalerIni) == true;
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, sha) in pins.OptiFiles)
        {
            var destination = OptiDestination(path, proxyName);
            if (destination == OptiScalerIni && File.Exists(Path.Combine(dir, OptiScalerIni)) && !iniIsOurs)
            {
                report.Info($"{OptiScalerIni} is already here and stays as it is: it is your configuration. "
                            + "Delete it before installing to start from the package's.");
                continue;
            }
            if (VerifiedPayload(src, path, sha, report) is { } bytes)
                files[destination] = destination == OptiScalerIni ? OnlyInTheGame(dir, bytes, report) : bytes;
        }
        if (!files.ContainsKey(OptiScalerIni) && LauncherBeside(dir) is { } kept)
            report.Warn($"{kept.Launcher} sits beside the game and loads OptiScaler first, which breaks {kept.Why}. "
                        + $"Set TargetProcessName={kept.Game} under [ProcessFilter] in your {OptiScalerIni}.");
        // A build the person supplied comes first; the author's version.dll goes to the backup either way.
        var author = OwnRuntime(dir, pins);
        var own = Supplied(ownRuntime, wantedRuntime, pins, Preset.OptiScaler, report) is { } supplied
            ? (supplied.Bytes, AcceptedRuntime(supplied.Build.OriginalSha256, pins.OptiScalerVersion)!, From: SuppliedFrom)
            : author;
        if ((own?.Bytes ?? VerifiedPayload(src, pins.OptiRuntimeName, pins.OptiRuntimeSha, report)) is { } runtime)
            foreach (var pass in OptiPasses)
                files[pass] = runtime;
        if (VerifiedPayload(src, WeightsName, pins.WeightsSha, report) is { } weights)
            files[WeightsName] = weights;
        if (mochizuki) AddMochizuki(files, src, pins, report);

        if (report.Failed)
        {
            report.Info("Nothing was written: fix the problem above and run it again.");
            return report;
        }

        // What an earlier install put in of mochizuki and this one does not write again comes out in
        // the same transaction: all of it when it was left off, what an older build had when it is on.
        // So does OptiScaler under the name it had, when it now goes in under another: two of it would
        // both load, and whatever that name displaced comes back.
        var recorded = MochizukiRecorded(manifest).Concat(InstalledOptiProxies(manifest).Where(n => n != proxyName)).ToList();
        var log = new List<string>();
        try
        {
            Transaction.Apply(dir, Preset.OptiScaler.ManifestPreset(), Route.X64, files, log, recorded, moves);
            foreach (var line in log) Narrate(line, report);
        }
        catch (InstallException e)
        {
            foreach (var line in log) Narrate(line, report);
            report.Err($"{e.Message}. Nothing was left half-written: the install rolled itself back.");
            return report;
        }

        report.Info($"OptiScaler goes in as {proxyName}.");
        if (own is { } o)
            report.Info(o.From switch
            {
                AuthorRuntimeName => $"The runtime is danielblnc's {o.Runtime} from the version.dll that was here, in place of the "
                                     + $"download's {pins.OptiRuntimeVersion}. version.dll is in the backup, and uninstall puts it back.",
                SuppliedFrom => $"The runtime is danielblnc's {o.Runtime} from your own file, in place of the download's "
                                + $"{pins.OptiRuntimeVersion}."
                                + (author?.From == AuthorRuntimeName ? " The version.dll that was here is in the backup, and uninstall puts it back." : ""),
                _ => $"The runtime stays danielblnc's {o.Runtime}, already here and newer than the "
                     + $"{pins.OptiRuntimeVersion} the download carries.",
            });
        if (pins.OptiFiles.ContainsKey(LmxxfRuntimeName))
            report.Info(
                "The lmxxf runtime went in too, with its weights. It runs on RDNA4 (gfx1201) cards only: "
                + "to use it, pick lmxxf under NR runtime in OptiScaler's Neural tab and restart the game.");
        if (mochizuki) report.Info(MochizukiInstalled + " " + MochizukiPickInOpti);
        else if (recorded.Count > 0) AfterMochizukiRetired(dir, report);
        report.Info(Preset.OptiScaler.Note());
        return report;
    }

    /// <summary>The folders this route created, once uninstall has emptied them, and a word about an
    /// OptiScaler this app has no record of: its runtime passes and weights went by name, and the
    /// rest of it is its own installer's to take.</summary>
    private static void AfterOptiScalerUninstall(string dir, bool recorded, Report report)
    {
        var cache = Path.Combine(dir, ShaderCache);
        try
        {
            if (Directory.Exists(cache) && Directory.EnumerateFileSystemEntries(cache)
                    .All(e => File.Exists(e) && e.EndsWith(".dxbc", StringComparison.OrdinalIgnoreCase)))
            {
                Directory.Delete(cache, recursive: true);
                report.Ok($"removed {ShaderCache.Replace('/', '\\')}\\");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            report.Warn($"could not remove {ShaderCache}: {e.Message}");
        }

        foreach (var folder in OptiFolders)
        {
            var path = Path.Combine(dir, folder);
            try
            {
                if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any())
                {
                    Directory.Delete(path);
                    report.Ok($"removed {folder.Replace('/', '\\')}\\");
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                report.Warn($"could not remove {folder}: {e.Message}");
            }
        }
        if (!recorded && File.Exists(Path.Combine(dir, OptiScalerIni)))
            report.Info(
                "This OptiScaler was not installed by this app, so only this project's files came out of it. "
                + "Its own uninstaller removes the rest: the release package puts Uninstall_OptiScaler_NR.bat in the game folder.");
    }
}
