// danielblnc's runtime builds: the ones this project has seen, the ones OptiScaler runs in place of the
// one it ships with, and the ones a person supplies themselves (UserRuntime) -- his supporter builds,
// which are never distributed. His own setup loads the runtime as version.dll; every route takes that
// file to the backup rather than run a second driver on one runtime, and uninstall puts it back. Loaded
// under another name, by a loader somebody else put there, it stops the ReShade routes instead.

namespace AmdNr.Core;

public static partial class Work
{
    /// <summary>The runtime builds this project has seen, by the start of their SHA-256: danielblnc's
    /// 0.2.14, 0.2.17, 0.3.0, 0.3.1, 0.3.3, 0.4.0, 0.4.1, 0.4.2, 0.4.3, 0.5.0, 0.5.1 and 0.6.0, and
    /// the 0.3.0, 0.4.0 and 0.4.1 the add-on pins. Any of them sitting in the game folder as version.dll is the
    /// author's own way of loading the runtime.</summary>
    private static readonly string[] KnownRuntimePrefixes =
    [
        "e145ff963b1ef614", "ddd82d313aa74c2e", "bc97f3b06718e190",
        "8321cae728d28cb7", "70af3fb757f83f71", "b108d6407eb7f094",
        "907b30a61644a6d7", "d62be3d8b9fbb3c6", "ff6feffa41abccce",
        "823063eb4c76b133", "c8808716c286a34f", "8aa2dcc5b6596aca", "d1e320862a8763ac",
        "cddfb09e01934795", "493b4a3b80a21f72", "195c4a891b6eac4c",
    ];

    /// <summary>The name the author's setup loads the runtime under.</summary>
    internal const string AuthorRuntimeName = "version.dll";

    /// <summary>The weights file beside the author's version.dll is the author's runtime's: it builds
    /// one there from the game's nvngx_dlssnr.dll, so it says nothing about an install of ours.</summary>
    internal static bool IsAuthorsWeights(string dir, string name) =>
        name == WeightsName && AuthorsVersionDllHere(dir);

    /// <summary>A version.dll here that is not OptiScaler, which goes in under that name too when it is picked:
    /// neither one an install of it recorded nor one that says it is OptiScaler.</summary>
    internal static bool AuthorsVersionDllHere(string dir)
    {
        var path = Path.Combine(dir, AuthorRuntimeName);
        return File.Exists(path)
               && !InstalledOptiProxies(InstalledManifest(dir)).Contains(AuthorRuntimeName)
               && Identify(path).Product?.Contains("OptiScaler", StringComparison.OrdinalIgnoreCase) != true;
    }

    /// <summary>The danielblnc runtimes OptiScaler runs in place of the one it ships with: SHA-256, runtime
    /// version, and the first OptiScaler release whose AmdLayout.h accepts that build (0.2.17 is in there too,
    /// but the release's own Setup refuses it).</summary>
    private static readonly (string Sha, Version Runtime, Version Since)[] AcceptedRuntimes =
    [
        ("8321cae728d28cb7632d0d58d3d913e91132bf7645c126505698fbe4cd5a0138", new(0, 3, 0), new(0, 1, 0)),
        ("b108d6407eb7f094a4f9111edd778eee7b978b648d413a9fc7aeedfdd914c154", new(0, 3, 1), new(0, 1, 0)),
        ("d62be3d8b9fbb3c6c81982c4ddb3dfa00eb9662e3206925cbe5b7e1bc6798b80", new(0, 4, 0), new(0, 4, 1)),
        ("823063eb4c76b1334fd1800c41798873ae61d4016af0406f1f0b9dce57b1d376", new(0, 4, 1), new(0, 4, 2)),
        ("8aa2dcc5b6596aca97995dbfd4e0a9790d8c15108495e0ed154dd15dbb5b465a", new(0, 4, 2), new(0, 4, 3)),
        ("d1e320862a8763ac39e7ce194536d4b6c55ba61bae9e8a92753cec32df67a457", new(0, 4, 3), new(0, 4, 3)),
        ("cddfb09e019347957bf7b96c95c0e900e8d3062dfaed697a8a96b0a039aec31a", new(0, 5, 0), new(0, 4, 4)),
        ("493b4a3b80a21f7255109172ab7bb01ba08d35f2941718f441768f1abfc48acd", new(0, 5, 1), new(0, 4, 5)),
        ("195c4a891b6eac4c1cb7671e10ff62bbbe2b17f1dfae1344dc5a6714e4775721", new(0, 6, 0), new(0, 4, 7)),
    ];

    /// <summary>The builds patched for the add-on that the add-on and its 32-bit pair run, by the first release
    /// that does: danielblnc's 0.4.3, 0.5.0, 0.5.1 and 0.6.0, the versions a person can pick.</summary>
    private static readonly (string Sha, Version Since)[] AddonAcceptedRuntimes =
    [
        ("f3d9f2e53b775e4870917572f1f87a28c73068a4dc97252d6fb52360ddf8597a", new(0, 7, 0)),
        ("c808cdb04b4cf99e806f2989bb5b258696a51c89c084c500c957b055a66479b6", new(0, 7, 0)),
        ("af67f066a250da5cabce87d8c70ddb148b5149eaaf0225279dd8489773bc79b0", new(0, 7, 2)),
        ("430be589020685d03f0ad92194457bafe1a186bf658c8cfe2fb7a0bdb7592cc4", new(0, 7, 6)),
    ];

    /// <summary>Whether a runtime version from the payload's list runs on this add-on (or bridge) version, or
    /// on this OptiScaler release.</summary>
    /// <summary>On an RX 9000 card, the danielblnc build that has not frozen a game there; every other one is
    /// unstable on those cards (single jobs held for seconds, then a locked PC).</summary>
    public const string Rdna4Recommended = "0.4.3";

    public enum RuntimeBadge { None, Recommended, Unstable }

    /// <summary>The versions offered, from those the add-on or OptiScaler version runs (newest first): all of them
    /// on an RX 9000 card, only the newest on any other.</summary>
    public static IReadOnlyList<ComponentRelease> RuntimeOffer(IReadOnlyList<ComponentRelease> runs, bool rdna4) =>
        rdna4 ? runs : runs.Take(1).ToList();

    /// <summary>The version that goes in with nothing picked: <see cref="Rdna4Recommended"/> on an RX 9000 card when
    /// it is offered, otherwise null, which keeps the one the release pins.</summary>
    public static string? RuntimeDefault(IReadOnlyList<ComponentRelease> offer, bool rdna4) =>
        rdna4 && offer.Any(r => r.Version == Rdna4Recommended) ? Rdna4Recommended : null;

    /// <summary>The version that goes in: the one picked when it is offered, otherwise the default.</summary>
    public static string? RuntimePick(IReadOnlyList<ComponentRelease> offer, string? picked, bool rdna4) =>
        picked is not null && offer.Any(r => r.Version == picked) ? picked : RuntimeDefault(offer, rdna4);

    public static RuntimeBadge BadgeFor(string version, bool rdna4) =>
        !rdna4 ? RuntimeBadge.None : version == Rdna4Recommended ? RuntimeBadge.Recommended : RuntimeBadge.Unstable;

    /// <summary>Said on the OptiScaler route when an RX 9000 card gets a build other than <see cref="Rdna4Recommended"/>:
    /// OptiScaler runs it inline, and danielblnc 0.6.0 there held the GPU for seconds at a time until the driver reset
    /// it (DEVICE_HUNG, RX 9070 XT). Null on any other card or build.</summary>
    public static string? UnstableOnRdna4(string version, bool rdna4) =>
        version.Length > 0 && BadgeFor(version, rdna4) == RuntimeBadge.Unstable
            ? $"danielblnc {version} is unstable on RX 9000 cards: OptiScaler runs it inline, and it can hold the GPU for "
              + "seconds at a time, stall the game and crash it with a driver reset (DEVICE_HUNG). "
              + $"{Rdna4Recommended} is the stable one there: pick it under danielblnc version."
            : null;

    public static bool RuntimeRunsOn(ComponentRelease runtime, bool optiScaler, string version)
    {
        var name = optiScaler ? PayloadManifest.OptiRuntimeComponent : PayloadManifest.RuntimeComponent;
        if (!runtime.Components.TryGetValue(name, out var component)
            || component.Files.FirstOrDefault(f => f.Name != WeightsName) is not { } file)
            return false;
        var sha = Engine.Lower(file.Sha256);
        if (optiScaler) return AcceptedRuntime(sha, version) is not null;
        return VersionOf(version) is { } v && AddonAcceptedRuntimes.Any(r => r.Sha == sha && v >= r.Since);
    }

    /// <summary>"0.4.3-amd-nr" or "0.4.2" as a version, or null.</summary>
    private static Version? VersionOf(string text) =>
        Version.TryParse(text.Split('-')[0], out var v) ? v : null;

    /// <summary>The runtime version of a build the given OptiScaler release runs, or null.</summary>
    internal static Version? AcceptedRuntime(string sha, string optiScalerVersion) =>
        VersionOf(optiScalerVersion) is { } opti
            ? AcceptedRuntimes.FirstOrDefault(r => r.Sha == sha && opti >= r.Since).Runtime
            : null;

    /// <summary>The runtime an install takes from the game folder in place of the payload's: the author's
    /// version.dll when the chosen OptiScaler runs that build, or else the pass 1 an earlier install put in,
    /// when it is a build that OptiScaler runs and newer than the payload's (a build of the author's kept
    /// across updates). Null when the payload's is the one.</summary>
    private static (byte[] Bytes, Version Runtime, string From)? OwnRuntime(string dir, PayloadPins pins)
    {
        if (pins.RuntimeChosen) return null;
        foreach (var name in new[] { AuthorRuntimeName, OptiPasses[0] })
        {
            var path = Path.Combine(dir, name);
            if (Engine.SizeOf(path) is not (> 7_000_000 and < 80_000_000)) continue;
            var bytes = Engine.Read(path);
            if (AcceptedRuntime(Engine.Sha(bytes), pins.OptiScalerVersion) is not { } runtime) continue;
            if (name == AuthorRuntimeName || VersionOf(pins.OptiRuntimeVersion) is { } shipped && runtime > shipped)
                return (bytes, runtime, name);
        }
        return null;
    }

    /// <summary>Whether a runtime pass an install recorded is current although the newest payload pins
    /// another build: one that release runs and newer than the one it ships (<see cref="OwnRuntime"/>).</summary>
    internal static bool OwnRuntimeIsCurrent(PayloadManifest payload, string name, string sha)
    {
        if (!OptiPasses.Contains(name) || payload.RuntimeChoice is not null) return false;
        payload = payload.Newest(PayloadManifest.OptiScalerComponent);
        return payload.Has(PayloadManifest.OptiScalerComponent) && payload.Has(PayloadManifest.OptiRuntimeComponent)
               && AcceptedRuntime(sha, payload.Component(PayloadManifest.OptiScalerComponent).Version) is { } own
               && VersionOf(payload.Component(PayloadManifest.OptiRuntimeComponent).Version) is { } shipped
               && own > shipped;
    }

    private static void CheckRuntimeAsVersionDll(string dir, PayloadPins pins, Report report)
    {
        var path = Path.Combine(dir, AuthorRuntimeName);
        // Size first, so this stays a stat() for every version.dll that is something else.
        if (Engine.SizeOf(path) is not (> 7_000_000 and < 80_000_000)) return;
        var sha = Engine.HashFile(path);
        if (AcceptedRuntime(sha, pins.OptiScalerVersion) is { } runtime)
        {
            report.Info(
                $"version.dll here is danielblnc's runtime {runtime}, loaded the way its author's setup loads it. "
                + "OptiScaler takes it as its runtime (dlssnr_amd_pass1-3.dll) in place of the download, and "
                + "version.dll goes to the backup, since both at once would be two drivers on one runtime. "
                + "Uninstall puts it back.");
            return;
        }
        if (!KnownRuntimePrefixes.Any(p => sha.StartsWith(p, StringComparison.Ordinal))) return;
        report.Info(
            "version.dll here is the DLSS-NR-on-AMD runtime itself, loaded the way its author's setup "
            + $"loads it, in a build OptiScaler {pins.OptiScalerVersion} does not run. Beside OptiScaler it "
            + "would be two drivers on one runtime, so version.dll goes to the backup, the download's runtime "
            + "goes in, and uninstall puts version.dll back.");
    }

    // -- Builds a person supplies ------------------------------------------------------------------

    /// <summary>The builds a person can supply that an install of this route, at the version these pins
    /// carry, runs: patched, by an add-on (or bridge) from the build's addon_since on, or as it is, by an
    /// OptiScaler whose <see cref="AcceptedRuntimes"/> has it.</summary>
    public static IReadOnlyList<UserRuntime> OfferedRuntimes(PayloadPins pins, Preset preset) =>
        pins.UserRuntimes.Where(b => Runs(pins, preset, b)).ToList();

    private static bool Runs(PayloadPins pins, Preset preset, UserRuntime build) =>
        preset.IsOptiScaler()
            ? AcceptedRuntime(build.OriginalSha256, pins.OptiScalerVersion) is not null
            : build.RunsOn(AddonVersionFor(pins, preset));

    /// <summary>The first OptiScaler release that runs a build as it is, or null when none does.</summary>
    public static Version? OptiScalerSince(string sha) => AcceptedRuntimes.FirstOrDefault(r => r.Sha == sha).Since;

    private static string AddonVersionFor(PayloadPins pins, Preset preset) =>
        preset.Route() == Route.X86 ? pins.BridgeVersion : pins.AddonVersion;

    /// <summary>What an install puts in place of the download's runtime when a person supplied a build: read
    /// from <paramref name="source"/> (see <see cref="UserRuntime.Read"/>), and patched for the add-on on the
    /// ReShade routes. Null is the download's: nothing supplied, or a version that does not run that build.
    /// Whenever a build was chosen -- <paramref name="wanted"/>, or the one the file holds -- and is not the one
    /// that goes in, the report says why. A file that is not a listed build, or does not patch to its listed
    /// hash, is an error, and nothing is written.</summary>
    private static (UserRuntime Build, byte[] Bytes)? Supplied(string? source, UserRuntime? wanted, PayloadPins pins,
        Preset preset, Report report)
    {
        if (string.IsNullOrWhiteSpace(source))
        {
            if (wanted is not null)
                report.Warn(NotUsed(wanted, "the build chosen for this game", Runs(pins, preset, wanted)
                    ? "no copy of it is kept on this machine, and none was supplied"
                    : NotRunBecause(wanted, pins, preset), preset));
            return null;
        }
        try
        {
            var (build, original) = UserRuntime.Read(source, pins.UserRuntimes);
            if (Runs(pins, preset, build)) return (build, preset.IsOptiScaler() ? original : build.Patched(original));
            report.Warn(NotUsed(build, "the build you supplied", NotRunBecause(build, pins, preset), preset));
        }
        catch (InstallException e)
        {
            report.Err(e.Message);
        }
        return null;
    }

    private static string NotRunBecause(UserRuntime build, PayloadPins pins, Preset preset) =>
        preset.IsOptiScaler() ? $"OptiScaler {pins.OptiScalerVersion} does not run it"
        : !build.Patchable ? "the payload list has no patch for it yet, so the add-on cannot drive it"
        : $"add-on v{AddonVersionFor(pins, preset)} does not run it (v{build.AddonSince} and later do)";

    private static string NotUsed(UserRuntime build, string what, string why, Preset preset) =>
        $"danielblnc's runtime {build.Name}, {what}, is not used: {why}. "
        + (preset.IsOptiScaler()
            ? "The runtime is the download's instead, or a build of his already in this folder that OptiScaler runs."
            : "The download's runtime goes in instead.");

    /// <summary>What the pre-flight says about a build a person supplied or chose.</summary>
    private static void CheckSupplied(string? source, UserRuntime? wanted, PayloadPins pins, Preset preset, Report report)
    {
        if (Supplied(source, wanted, pins, preset, report) is { } own)
            report.Ok($"danielblnc's runtime {own.Build.Name}, from your own file, is checked"
                      + (preset.IsOptiScaler() ? "" : " and patched for the add-on") + ": it goes in place of the download's.");
    }

    /// <summary>The build a person supplied that this game folder runs: the one an install of this app put
    /// in (patched beside the add-on, as it is beside OptiScaler), or else danielblnc's own version.dll. What
    /// the sheet goes by until somebody picks, the way <see cref="HasMochizuki"/> is; null is the download.</summary>
    public static UserRuntime? UserRuntimeIn(string gameDir, IReadOnlyList<UserRuntime> builds)
    {
        var dir = ResolveSource(gameDir);
        if (builds.Count == 0 || dir.Length == 0 || !Directory.Exists(dir)) return null;
        foreach (var route in new[] { Route.X64, Route.X86 })
            if (InstalledManifest(dir, route)?.Entries.FirstOrDefault(e => e.Name == RuntimeName) is { } entry
                && File.Exists(Path.Combine(dir, RuntimeName))
                && builds.FirstOrDefault(b => entry.Hash == b.OriginalSha256 || entry.Hash == b.PatchedSha256) is { } build)
                return build;
        var loader = Path.Combine(dir, AuthorRuntimeName);
        return builds.FirstOrDefault(b => PayloadCache.Verified(loader, b.OriginalSize, b.OriginalSha256));
    }

    /// <summary>Where this machine has a build for an install to read: the copy this app kept, or else
    /// danielblnc's version.dll or the runtime an install put in the game folder, kept on the way so the next
    /// game does not ask. Null when the person has to supply it.</summary>
    public static string? FindUserRuntime(UserRuntime build, string gameDir)
    {
        if (build.Kept() is { } kept) return kept;
        var dir = ResolveSource(gameDir);
        if (dir.Length == 0) return null;
        // Under any name: his version.dll, the runtime an install put in, or his runtime a loader renamed.
        foreach (var path in TopLevelDlls(dir).Where(p => Engine.SizeOf(p) == build.OriginalSize))
        {
            try
            {
                UserRuntime.Keep(path, [build]);
                return build.Kept();
            }
            catch (InstallException)
            {
                // Somebody else's file under that name, or another build: not a source.
            }
        }
        return null;
    }

    /// <summary>Whether the runtime a ReShade install recorded is current although the payload pins another:
    /// a build a person supplied, patched, that the add-on (or bridge) the payload pins runs.</summary>
    internal static bool UserRuntimeIsCurrent(PayloadManifest payload, Route route, string name, string sha)
    {
        var own = route == Route.X86 ? PayloadManifest.BridgeComponent : PayloadManifest.AddonComponent;
        return name == RuntimeName && payload.Has(own)
               && (payload.UserRuntimes ?? []).Any(b => b.PatchedSha256 == sha && b.RunsOn(payload.Component(own).Version));
    }

    // -- His standalone runtime in the game folder ---------------------------------------------------

    /// <summary>danielblnc's runtime wherever it sits in this folder: every top-level DLL but the names this
    /// app writes, of a size his builds come in, that hashes to a build this project knows. His setup loads it
    /// as version.dll; a chain loader somebody added can load it under a name of its own -- NBA 2K27 had a
    /// loader as version.dll and his 0.3.0 as dlssnr_ver.dll. Each file is hashed once (PayloadCache.HashOf),
    /// so the pre-flight stays cheap.</summary>
    private static List<(string Name, string Sha)> AuthorsRuntimesHere(string dir, PayloadPins pins)
    {
        // Ours by name: what an install writes and the copies the add-on makes of it, one per pass.
        var ours = OptiPasses.Concat(DeadFiles()).Concat(RuntimeCopies).Append(LmxxfRuntimeName).Append(MochizukiRuntimeName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // And ours by content, under any name: a build patched for the add-on is one his setups never load.
        var patched = new HashSet<string>([Engine.RuntimeSha, .. Engine.EarlierRuntimeShas, pins.RuntimeSha, .. pins.UserRuntimes.Select(b => b.PatchedSha256)],
            StringComparer.OrdinalIgnoreCase);
        var found = new List<(string Name, string Sha)>();
        foreach (var path in TopLevelDlls(dir))
        {
            var name = Path.GetFileName(path);
            if (ours.Contains(name) || Engine.SizeOf(path) is not { } size
                || !(size is > 7_000_000 and < 80_000_000 || pins.UserRuntimes.Any(b => b.OriginalSize == size)))
                continue;
            if (PayloadCache.HashOf(path) is { } sha && !patched.Contains(sha)
                && (KnownRuntimePrefixes.Any(p => sha.StartsWith(p, StringComparison.Ordinal))
                    || AcceptedRuntimes.Any(r => r.Sha == sha) || pins.UserRuntimes.Any(b => b.OriginalSha256 == sha)))
                found.Add((name, sha));
        }
        return found;
    }

    private static IEnumerable<string> TopLevelDlls(string dir)
    {
        try
        {
            return Directory.GetFiles(dir, "*.dll")
                .Where(p => Path.GetExtension(p).Equals(".dll", StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    /// <summary>The DLL here that names <paramref name="runtime"/>: the loader that loads his runtime under a
    /// name of its own. Only small ones are read; a loader is a shim.</summary>
    private static string? LoaderOf(string dir, string runtime)
    {
        foreach (var path in TopLevelDlls(dir))
        {
            if (Path.GetFileName(path).Equals(runtime, StringComparison.OrdinalIgnoreCase)
                || Engine.SizeOf(path) is not < 8_000_000) continue;
            try
            {
                if (Engine.Mentions(File.ReadAllBytes(path), runtime)) return Path.GetFileName(path);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Held open or refused: not the one this can name.
            }
        }
        return null;
    }

    /// <summary>What his setup leaves beside the game. His runtime writes dlssnr_on_amd.ini wherever it runs,
    /// ours included, so that one says something only where no runtime of ours is.</summary>
    private static List<string> AuthorsSetupHere(string dir) =>
        new[] { "dlssnr_on_amd_setup.exe", "dlssnr_on_amd.ini" }
            .Where(n => File.Exists(Path.Combine(dir, n)))
            .Where(n => n != "dlssnr_on_amd.ini" || !File.Exists(Path.Combine(dir, RuntimeName)))
            .ToList();

    /// <summary>What danielblnc's runtime in this folder means for an install, in the report, and the files the
    /// install moves to the backup for it -- every one of them, on every route: his version.dll, and his runtime
    /// under any other name a loader gives it. Beside the add-on or OptiScaler it would be two drivers on one
    /// runtime, and beside ReShade it hooks the same DXGI and D3D12 calls, which can keep the game from starting.
    /// Uninstall puts each back. A loader that names it stays where it is: it may load other mods too, and with
    /// the runtime gone it has nothing of his to load. Stopping the install here instead -- "remove his setup
    /// first" -- was a red line on a folder the install could put right itself (NBA 2K27).</summary>
    private static List<string> CheckAuthorsRuntime(string dir, PayloadPins pins, Preset preset, Report report)
    {
        var found = AuthorsRuntimesHere(dir, pins);
        var host = preset.IsOptiScaler() ? "OptiScaler" : "ReShade and the add-on";
        foreach (var (name, sha) in found.Where(f => !f.Name.Equals(AuthorRuntimeName, StringComparison.OrdinalIgnoreCase)))
        {
            var by = LoaderOf(dir, name);
            var pick = pins.UserRuntimes.FirstOrDefault(b => b.OriginalSha256 == sha) is { } build
                ? $" It is his {build.Name} supporter build: pick it as your supporter files in this app and it goes in as the runtime."
                : "";
            report.Info($"danielblnc's standalone runtime{BuildOf(sha, pins)} is loaded here as {name}"
                        + (by is null ? "" : $", by {by}")
                        + $". Beside {host} that would be two drivers on one runtime, and it can keep the game from starting, "
                        + $"so the install moves {name} to the backup"
                        + (by is null ? "" : $" ({by} stays, with nothing of his left to load)")
                        + ", and uninstall puts it back." + pick);
        }
        var moves = found.Select(f => f.Name).ToList();
        if (preset.IsOptiScaler()) return moves;
        if (moves.Contains(AuthorRuntimeName, StringComparer.OrdinalIgnoreCase)) report.Info(AuthorsLoaderMoves);
        else if (found.Count == 0 && AuthorsSetupHere(dir) is { Count: > 0 } setup)
            report.Warn(
                $"{Joined(setup)} {(setup.Count == 1 ? "is" : "are")} here: danielblnc's own setup has been in this folder. "
                + "None of his runtime was found loaded here, so this goes on; if the game does not start with ReShade, "
                + "remove his setup with his own setup or uninstaller.");
        return moves;
    }

    /// <summary>" 0.5.0" when the build is one this project can name, and nothing otherwise.</summary>
    private static string BuildOf(string sha, PayloadPins pins) =>
        pins.UserRuntimes.FirstOrDefault(b => b.OriginalSha256 == sha) is { } build ? " " + build.Name
        : AcceptedRuntimes.FirstOrDefault(r => r.Sha == sha).Runtime is { } version ? $" {version}"
        : "";

    private static string SuppliedInstalled(UserRuntime build) =>
        $"The runtime is danielblnc's {build.Name} from your own file, patched for the add-on, in place of the download's.";

    private const string AuthorsLoaderMoves =
        "version.dll here is danielblnc's own loader for his runtime, the way his setup installs it. Beside the "
        + "add-on it would be two drivers on one runtime, so an install moves it to the backup, and uninstall puts it back.";
}
