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
        "lmxxf-modules", "lmxxf-modules-gfx1200", "native-game-tiled-assets", "shaders",
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
        "nvngx_dlss.dll", "nvngx_dlssd.dll", "sl.dlss.dll", "libxess.dll", "libxess_dx11.dll", "amd_fidelityfx_dx12.dll",
        "amd_fidelityfx_upscaler_dx12.dll", "amd_fidelityfx_vk.dll", "ffx_fsr3upscaler_x64.dll", "ffx_fsr2_api_x64.dll",
        "ffx_fsr2_api_dx12_x64.dll", "ffx_fsr2_api_vk_x64.dll",
    ];

    /// <summary>The first OptiScaler release that also runs the network on the finished frame of a game
    /// without an upscaler (NR without upscaling), on every API in <see cref="GraphicsDetection.OptiEverywhereApis"/>.</summary>
    public static readonly Version OptiEverywhere = new(0, 5, 0);

    /// <summary>Whether this OptiScaler version ("0.5.0-amd-nr") runs on games without an upscaler too.</summary>
    public static bool OptiRunsEverywhere(string? version) =>
        version is not null && AddonReleases.Version(version) is { } v && v >= OptiEverywhere;

    /// <summary>The name OptiScaler loads under in a game on this API when nothing else names one:
    /// opengl32.dll and d3d9.dll take the API's own place, winmm.dll is loaded early by Vulkan games
    /// that never touch DXGI, and dxgi.dll is what D3D11 and D3D12 games load.</summary>
    internal static string OptiProxyForApi(GraphicsApi? api) => api switch
    {
        GraphicsApi.OpenGL => "opengl32.dll",
        GraphicsApi.D3D9 => "d3d9.dll",
        GraphicsApi.Vulkan => "winmm.dll",
        _ => "dxgi.dll",
    };

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

    /// <summary>Whether this OptiScaler carries lmxxf with the kernels of both RX 9000 series (9070 and 9060).</summary>
    private static bool LmxxfEverywhere(PayloadPins pins) =>
        pins.OptiFiles.ContainsKey(LmxxfRuntimeName)
        && pins.OptiFiles.Keys.Any(k => k.StartsWith("lmxxf-modules-gfx1200/", StringComparison.Ordinal));

    /// <summary>On an RX 9000 card, the one mochizuki goes in on, a fresh OptiScaler.ini runs lmxxf.</summary>
    private static byte[] RunsLmxxf(byte[] ini, bool wanted) =>
        wanted
            ? System.Text.Encoding.UTF8.GetBytes(
                Engine.SetIni(System.Text.Encoding.UTF8.GetString(ini), "DlssNr", "NrBackend", "lmxxf"))
            : ini;

    /// <summary>From OptiScaler 0.5.0 on, a fresh OptiScaler.ini runs the network on the finished frame of a
    /// game with no upscaler running. It stands aside by itself while the game's upscaler runs.</summary>
    private static byte[] WithoutUpscaler(byte[] ini, bool everywhere) =>
        everywhere
            ? System.Text.Encoding.UTF8.GetBytes(
                Engine.SetIni(System.Text.Encoding.UTF8.GetString(ini), "DlssNr", "PresentWithoutUpscaler", "true"))
            : ini;

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
    /// <param name="api">The API the game runs OptiScaler on, which names the last fallback (<see cref="OptiProxyForApi"/>).</param>
    internal static string OptiProxyFor(string? wanted, Manifest? installed = null, string? suggested = null,
        GraphicsApi? api = null)
    {
        var choices = ProxyChoicesFor(Preset.OptiScaler);
        string? Named(string? name) => choices.FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
        return Named(wanted) ?? Named(suggested) ?? InstalledOptiProxies(installed).FirstOrDefault() ?? OptiProxyForApi(api);
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
        foreach (var name in new[] { PayloadManifest.OptiScalerComponent, PayloadManifest.LmxxfWeightsComponent,
                                     PayloadManifest.LmxxfGfx1200Component })
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

    private static IEnumerable<(string Name, ulong Size)> OptiPayloadSizes(string src, PayloadPins pins) =>
        pins.OptiFiles.Keys.Select(p => (p, Engine.SizeOf(Path.Combine(src, p)) ?? 0))
            .Concat(OptiPasses.Select(p => (p, pins.OptiRuntimeSize)))
            .Append((WeightsName, pins.WeightsSize));

    private static Report PreflightOptiScaler(string gameDir, string payloadDir, PayloadPins pins, string? proxy,
        bool mochizuki, string? ownRuntime, UserRuntime? wantedRuntime, string? suggestedProxy, GraphicsApi? api)
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
        var everywhere = OptiRunsEverywhere(pins.OptiScalerVersion);
        var proxyName = OptiProxyFor(proxy, manifest, suggestedProxy, everywhere ? api : null);
        NoteProxy(proxyName, proxy, manifest, suggestedProxy, report);
        var retiring = !mochizuki && MochizukiRecorded(manifest).Count > 0;
        var held = new[] { proxyName, WeightsName }.Concat(OptiPasses).Concat(AuthorsRuntimesHere(dir, pins).Select(f => f.Name))
            .Where(n => Engine.IsLocked(Path.Combine(dir, n)))
            .Concat(mochizuki || retiring ? MochizukiHeld(dir) : []).ToList();
        if (held.Count > 0) report.Err(Engine.OpenElsewhere(dir, held));

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
        CheckForeignMod(dir, manifest, report);
        CheckRuntimeAsVersionDll(dir, pins, report);
        CheckAuthorsRuntime(dir, pins, Preset.OptiScaler, report);
        CheckUpscaler(dir, report, everywhere, GraphicsDetector.Detect(dir).Executable);
        CheckOptiRouteIsReachable(dir, report, everywhere);
        if (!mochizuki && manifest?.Entries.Any(e => e.Name == MochizukiRuntimeName && e.Owned) == true)
            report.Info(MochizukiComesOut);

        if (File.Exists(Path.Combine(dir, OptiScalerIni)) && manifest?.Entries.Any(e => e.Name == OptiScalerIni) != true
            && !ForeignIni(dir, ForeignNrMod(dir, manifest).Count > 0))
            report.Info($"{OptiScalerIni} is already here and stays as it is: it is your configuration.");

        if (!report.Failed) report.Ok("Nothing in the way.");
        return report;
    }

    private static Report InstallOptiScaler(string gameDir, string payloadDir, PayloadPins pins, string? proxy,
        bool mochizuki, string? ownRuntime, UserRuntime? wantedRuntime, string? suggestedProxy, GraphicsApi? api)
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
        report.Info($"preset: {Preset.OptiScaler.Label()}{(api is { } a ? $", {GraphicsDetection.Short(a)} game" : "")}");

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
        var everywhere = OptiRunsEverywhere(pins.OptiScalerVersion);
        var proxyName = OptiProxyFor(proxy, manifest, suggestedProxy, everywhere ? api : null);
        CheckOptiInTheWay(dir, proxyName, manifest, report);
        var foreign = ForeignNrMod(dir, manifest);
        CheckForeignMod(dir, manifest, report);
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
        var iniIsForeign = !iniIsOurs && ForeignIni(dir, foreign.Count > 0);
        if (iniIsForeign)
            report.Info($"The {OptiScalerIni} here is another build's: it is backed up and this one's goes in, and "
                        + "uninstall puts it back.");
        // From OptiScaler 0.5.0 on, a game with no upscaler gets the network on the finished frame, and that
        // runs on danielblnc's runtime only: lmxxf is the default only where the game has an upscaler.
        IReadOnlyList<string> upscalers = everywhere ? GraphicsDetector.UpscalersDeep(dir, null, GraphicsDetector.Detect(dir).Executable) : [];
        var lmxxf = mochizuki && LmxxfEverywhere(pins) && (!everywhere || upscalers.Count > 0);
        if (everywhere && upscalers.Count == 0)
            report.Info("No DLSS, FSR or XeSS found in this game: OptiScaler runs the network on its finished frame (NR without upscaling).");
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, sha) in pins.OptiFiles)
        {
            var destination = OptiDestination(path, proxyName);
            if (destination == OptiScalerIni && File.Exists(Path.Combine(dir, OptiScalerIni)) && !iniIsOurs && !iniIsForeign)
            {
                report.Info($"{OptiScalerIni} is already here and stays as it is: it is your configuration. "
                            + "Delete it before installing to start from the package's.");
                continue;
            }
            if (VerifiedPayload(src, path, sha, report) is { } bytes)
                files[destination] = destination == OptiScalerIni
                    ? WithoutUpscaler(RunsLmxxf(OnlyInTheGame(dir, bytes, report), lmxxf), everywhere)
                    : bytes;
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
            report.Err(RolledBack(e));
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
        if (files.ContainsKey(OptiScalerIni) && everywhere)
            report.Info($"{OptiScalerIni} has NR without upscaling on: with no upscaler running, OptiScaler runs the network "
                        + "on the finished frame, HUD included, on danielblnc's runtime. Where the game's DLSS, FSR or XeSS "
                        + "is on, the network runs inside it instead.");
        if (files.ContainsKey(OptiScalerIni) && lmxxf)
            report.Info($"{OptiScalerIni} runs lmxxf (NrBackend=lmxxf), the default on RX 9000 cards. danielblnc and "
                        + "mochizuki are beside it: pick another under NR runtime in OptiScaler's Neural tab.");
        else if (pins.OptiFiles.ContainsKey(LmxxfRuntimeName))
            report.Info(
                "The lmxxf runtime went in too, with its weights. It runs on "
                + (pins.OptiFiles.Keys.Any(k => k.StartsWith("lmxxf-modules-gfx1200/", StringComparison.Ordinal))
                    ? "RX 9070 and RX 9060 series cards: "
                    : "RX 9070 series cards only: ")
                + "to use it, pick lmxxf under NR runtime in OptiScaler's Neural tab and restart the game.");
        if (mochizuki)
            report.Info(MochizukiInstalled
                        + (files.ContainsKey(OptiScalerIni) && lmxxf
                            ? ""
                            : $" {OptiScalerIni} keeps the NR runtime it names: to use mochizuki, pick it under NR runtime in "
                              + "OptiScaler's Neural tab and restart the game."));
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
