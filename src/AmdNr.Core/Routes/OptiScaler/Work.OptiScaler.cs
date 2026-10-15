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
        // A 32-bit game's, after what is inside it (Work.Mochizuki takes dlssnr-amd).
        "OptiScaler64/OptiScaler/D3D12_OptiScaler", "OptiScaler64/OptiScaler", "OptiScaler64/experimental_lighting",
        "OptiScaler64/dlssnr-amd", "OptiScaler64",
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
                                     PayloadManifest.LmxxfGfx1200Component, PayloadManifest.OptiX86Component })
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
        // A 32-bit game's install keeps all of it in OptiScaler64\ with the host; its proxies are left out of the
        // comparison, as OptiScaler under its proxy name is on a 64-bit game.
        foreach (var (name, sha) in pinned.ToList())
        {
            pinned.Remove(name);
            if (name.StartsWith(OptiX86Payload, StringComparison.Ordinal))
            {
                if (name == OptiX86Payload + OptiHostName) pinned[$"{OptiHostFolder}/{OptiHostName}"] = sha;
                continue;
            }
            pinned[name] = sha;
            pinned[$"{OptiHostFolder}/{name}"] = sha;
        }
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

        // A 32-bit game: everything goes in OptiScaler64\ for the 64-bit host, which goes there too, and the
        // 32-bit proxy for the game's API goes beside the game, under the 32-bit route's record.
        var detected = GraphicsDetector.Detect(dir);
        var x86 = detected.Width == Route.X86;
        if (x86 && !OptiRunsBridged(pins.OptiScalerVersion))
        {
            report.Err($"This is a 32-bit game. OptiScaler runs 32-bit games from {OptiBridges} on, through its 32-bit "
                       + "proxy: pick that version or a later one, or the ReShade route.");
            return report;
        }
        var route = x86 ? Route.X86 : Route.X64;
        var home = x86 ? OptiHostFolder + "/" : "";
        var homeDir = x86 ? Path.Combine(dir, OptiHostFolder) : dir;
        var iniName = home + OptiScalerIni;
        var manifest = InstalledManifest(dir, route);
        var everywhere = OptiRunsEverywhere(pins.OptiScalerVersion);
        var proxyName = x86 ? OptiX86Proxy(api ?? detected.OptiApi(true, true))
            : OptiProxyFor(proxy, manifest, suggestedProxy, everywhere ? api : null);
        CheckOptiInTheWay(dir, proxyName, manifest, report);
        var foreign = ForeignNrMod(dir, manifest);
        CheckForeignMod(dir, manifest, report);
        CheckRuntimeAsVersionDll(dir, pins, report);
        CheckLocalHip(dir, report);
        CheckRenamedStreamline(dir, report);
        var moves = CheckAuthorsRuntime(dir, pins, Preset.OptiScaler, report);
        if (report.Failed)
        {
            report.Info("Nothing was written: fix the problem above and run it again.");
            return report;
        }

        // Somebody's configuration is kept: OptiScaler rewrites its ini whenever a setting is saved,
        // so one that is here and was not written by this route is the settings somebody chose.
        var iniIsOurs = manifest?.Entries.Any(e => e.Name == iniName) == true;
        var iniIsForeign = !iniIsOurs && ForeignIni(homeDir, foreign.Count > 0);
        if (iniIsForeign)
            report.Info($"The {OptiScalerIni} here is another build's: it is backed up and this one's goes in, and "
                        + "uninstall puts it back.");
        // From OptiScaler 0.5.0 on, a game with no upscaler gets the network on the finished frame, and up to 0.5.1
        // that runs on danielblnc's runtime only: lmxxf is the default only where the game has an upscaler. From 0.5.2
        // on mochizuki runs there too, the fastest of the three on an RX 9000 (the card mochizuki goes in on), so a
        // fresh ini runs it there with or without an upscaler; other cards keep danielblnc.
        IReadOnlyList<string> upscalers = everywhere ? GraphicsDetector.UpscalersDeep(dir, null, GraphicsDetector.Detect(dir).Executable) : [];
        var runsMochizuki = mochizuki && OptiRunsBridged(pins.OptiScalerVersion);
        var lmxxf = !runsMochizuki && mochizuki && LmxxfEverywhere(pins) && (!everywhere || upscalers.Count > 0);
        if (everywhere && upscalers.Count == 0)
            report.Info("No DLSS, FSR or XeSS found in this game: OptiScaler runs the network on its finished frame (NR without upscaling).");
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (path, sha) in pins.OptiFiles)
        {
            // The package's 32-bit proxies and their host: for a 32-bit game, the host and the proxy for its API.
            if (path.StartsWith(OptiX86Payload, StringComparison.Ordinal))
            {
                var name = path[OptiX86Payload.Length..];
                var to = !x86 ? null : name == OptiHostName ? home + name : name == proxyName ? proxyName : null;
                if (to is not null && VerifiedPayload(src, path, sha, report) is { } x86Bytes)
                    files[to] = x86Bytes;
                continue;
            }
            if (x86 && LmxxfOnly(path)) continue;
            var destination = home + OptiDestination(path, x86 ? OptiScalerDllPayload : proxyName);
            if (destination == iniName && File.Exists(Path.Combine(dir, iniName)) && !iniIsOurs && !iniIsForeign)
            {
                report.Info($"{OptiScalerIni} is already here and stays as it is: it is your configuration. "
                            + "Delete it before installing to start from the package's.");
                continue;
            }
            if (VerifiedPayload(src, path, sha, report) is { } bytes)
                files[destination] = destination == iniName
                    ? WithoutUpscaler(RunsMochizuki(RunsLmxxf(OnlyInTheGame(dir, bytes, report), lmxxf, !LmxxfWeighted(pins)),
                        runsMochizuki), everywhere)
                    : bytes;
        }
        if (x86 && !files.ContainsKey(proxyName))
            report.Err($"This OptiScaler package has no 32-bit {proxyName}: a 32-bit game needs {OptiBridges} or a later one.");
        if (!files.ContainsKey(iniName) && LauncherBeside(dir) is { } kept)
            report.Warn($"{kept.Launcher} sits beside the game and loads OptiScaler first, which breaks {kept.Why}. "
                        + $"Set TargetProcessName={kept.Game} under [ProcessFilter] in your {OptiScalerIni}.");
        // A build the person supplied comes first; the author's version.dll goes to the backup either way.
        var author = OwnRuntime(dir, pins);
        var own = Supplied(ownRuntime, wantedRuntime, pins, Preset.OptiScaler, report) is { } supplied
            ? (supplied.Bytes, AcceptedRuntime(supplied.Build.OriginalSha256, pins.OptiScalerVersion)!, From: SuppliedFrom)
            : author;
        if ((own?.Bytes ?? VerifiedPayload(src, pins.OptiRuntimeName, pins.OptiRuntimeSha, report)) is { } runtime)
            foreach (var pass in OptiPasses)
                files[home + pass] = runtime;
        if (VerifiedPayload(src, WeightsName, pins.WeightsSha, report) is { } weights)
            files[home + WeightsName] = weights;
        if (mochizuki)
        {
            var mochizukiFiles = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            AddMochizuki(mochizukiFiles, src, pins, report);
            foreach (var (name, bytes) in mochizukiFiles) files[home + name] = bytes;
        }

        if (report.Failed)
        {
            report.Info("Nothing was written: fix the problem above and run it again.");
            return report;
        }

        // What an earlier install put in of mochizuki and this one does not write again comes out in
        // the same transaction: all of it when it was left off, what an older build had when it is on.
        // So does OptiScaler under the name it had, when it now goes in under another: two of it would
        // both load, and whatever that name displaced comes back.
        var recorded = MochizukiRecorded(manifest).Concat(InstalledOptiProxies(manifest).Where(n => n != proxyName))
            // lmxxf's weights and RX 9060 kernels an earlier install put in, on a card that has no use for them.
            .Concat(LmxxfWeighted(pins) ? [] : manifest?.Entries.Where(e => e.Owned && !files.ContainsKey(e.Name)
                    && (e.Name.StartsWith(LmxxfWeightsFolder, StringComparison.Ordinal)
                        || e.Name.StartsWith("lmxxf-modules-gfx1200/", StringComparison.Ordinal))).Select(e => e.Name) ?? [])
            .ToList();
        var log = new List<string>();
        try
        {
            Transaction.Apply(dir, Preset.OptiScaler.ManifestPreset(), route, files, log, recorded, x86 ? [] : moves);
            foreach (var line in log) Narrate(line, report);
        }
        catch (InstallException e)
        {
            foreach (var line in log) Narrate(line, report);
            report.Err(RolledBack(e));
            return report;
        }

        report.Info(x86
            ? $"OptiScaler goes in {OptiHostFolder}\\ with its 64-bit host, and the 32-bit {proxyName} beside the game hands "
              + "the host every frame: NR, frame generation and the menu run there."
            : $"OptiScaler goes in as {proxyName}.");
        NoPreExposure(dir, own?.Runtime ?? VersionOf(pins.OptiRuntimeVersion), report);
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
        if (files.ContainsKey(iniName) && everywhere)
            report.Info($"{OptiScalerIni} has NR without upscaling on: with no upscaler running, OptiScaler runs the network "
                        + $"on the finished frame, HUD included, on {(runsMochizuki ? "mochizuki" : "danielblnc's runtime")}. "
                        + "Where the game's DLSS, FSR or XeSS is on, the network runs inside it instead.");
        if (files.ContainsKey(iniName) && runsMochizuki)
            report.Info($"{OptiScalerIni} runs mochizuki (NrBackend=mochizuki), the default on RX 9000 cards, with ACO on. "
                        + "danielblnc and lmxxf are beside it: pick another under NR runtime in OptiScaler's Neural tab.");
        else if (files.ContainsKey(iniName) && lmxxf)
            report.Info($"{OptiScalerIni} runs lmxxf (NrBackend=lmxxf), the default on RX 9000 cards. danielblnc and "
                        + "mochizuki are beside it: pick another under NR runtime in OptiScaler's Neural tab.");
        else if (!LmxxfWeighted(pins))
        {
            if (!files.ContainsKey(iniName) && KeptIniNames(homeDir) == "lmxxf")
                report.Warn($"The {OptiScalerIni} kept here names lmxxf as the NR runtime, which runs on RX 9070 and RX 9060 "
                            + "series cards only and is not installed on this one: pick daniel under NR runtime in OptiScaler's "
                            + $"Neural tab, or delete {OptiScalerIni} and install again.");
        }
        else if (pins.OptiFiles.ContainsKey(LmxxfRuntimeName))
            report.Info(
                "The lmxxf runtime went in too, with its weights. It runs on "
                + (pins.OptiFiles.Keys.Any(k => k.StartsWith("lmxxf-modules-gfx1200/", StringComparison.Ordinal))
                    ? "RX 9070 and RX 9060 series cards: "
                    : "RX 9070 series cards only: ")
                + "to use it, pick lmxxf under NR runtime in OptiScaler's Neural tab and restart the game.");
        if (mochizuki)
            report.Info(MochizukiInstalled
                        + (files.ContainsKey(iniName) && (lmxxf || runsMochizuki)
                            ? ""
                            : $" {OptiScalerIni} keeps the NR runtime it names: to use mochizuki, pick it under NR runtime in "
                              + "OptiScaler's Neural tab and restart the game."));
        else if (recorded.Count > 0) AfterMochizukiRetired(dir, report);
        report.Info(Preset.OptiScaler.Note());
        return report;
    }

    /// <summary>The NR runtime the OptiScaler.ini kept here names, or null. Read after the install has committed, so a
    /// file that cannot be read now is only not said about, never a failed install.</summary>
    private static string? KeptIniNames(string dir)
    {
        try { return Engine.Trim(Engine.GetIni(File.ReadAllText(Path.Combine(dir, OptiScalerIni)), "DlssNr", "NrBackend")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
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
