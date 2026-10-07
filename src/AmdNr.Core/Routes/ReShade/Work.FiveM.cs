// The FiveM route. FiveM loads ReShade and its add-ons from FiveM.app\plugins, but runs GTA V as
// data\cache\subprocess\FiveM_b####_GTAProcess.exe, and the add-on reads its runtime and weights from
// beside the running executable. So one install writes both places, through one transaction and one
// manifest rooted at FiveM.app.
//
// plugins\ is usually somebody's working setup already: ReShade.ini, presets, shaders, other add-ons,
// ENB as d3d11.dll. Only ReShade itself may change, and only when the add-on cannot load in it (FitOf):
// then it is replaced under the name it already has, and Uninstall puts the original back.

namespace AmdNr.Core;

public static partial class Work
{
    public const string FiveMPlugins = "plugins";
    public const string FiveMGame = "data/cache/subprocess";

    private const string FiveMSwap =
        "It is replaced by ReShade 6.8.0 with full add-on support under the same name; yours goes to the "
        + "backup, and Uninstall puts it back. ReShade.ini, your presets, shaders and other add-ons are not touched.";

    public const string FiveMAntiCheat =
        "FiveM has its own anti-cheat. ReShade with full add-on support is unsigned: some servers block it and "
        + "some ban for client mods. Check your server's rules -- this is at your own risk.";

    /// <summary>FiveM.app, from whatever was given: FiveM.app itself, the FiveM folder above it (where
    /// FiveM.exe is), a folder inside it such as plugins or subprocess, or an executable in any of those.</summary>
    public static string? FiveMApp(string raw)
    {
        var p = ResolveSource(raw);
        if (p.Length == 0) return null;
        var candidates = new List<string> { Path.Combine(p, "FiveM.app") };
        // Three up reaches FiveM.app from data\cache\subprocess.
        for (var d = new DirectoryInfo(p); d is not null && candidates.Count <= 4; d = d.Parent) candidates.Add(d.FullName);
        return candidates.FirstOrDefault(d =>
            Path.GetFileName(d).Equals("FiveM.app", StringComparison.OrdinalIgnoreCase) && Directory.Exists(d));
    }

    internal static string NotFiveM(string path) =>
        $"{path} is not FiveM. Point at FiveM.app, usually %LOCALAPPDATA%\\FiveM\\FiveM.app.";

    private static Report PreflightFiveM(string gameDir, string payloadDir, PayloadPins pins,
        string? ownRuntime, UserRuntime? wantedRuntime)
    {
        var report = new Report();
        var src = ResolveSource(payloadDir);
        CheckPayloads(src, Preset.FiveM, pins, report);
        CheckSupplied(ownRuntime, wantedRuntime, pins, Preset.FiveM, report);

        if (ResolveSource(gameDir).Length == 0)
        {
            report.Info($"Waiting for the {Preset.FiveM.FolderLabel()}.");
            return report;
        }
        if (FiveMApp(gameDir) is not { } app)
        {
            report.Err(NotFiveM(gameDir));
            return report;
        }
        report.Warn(FiveMAntiCheat);
        CheckFiveM(app, src, report);
        if (!report.Failed) report.Ok("Nothing in the way.");
        return report;
    }

    private static Report InstallFiveM(string gameDir, string payloadDir, PayloadPins pins,
        string? ownRuntime, UserRuntime? wantedRuntime)
    {
        var report = new Report();
        var src = ResolveSource(payloadDir);
        if (FiveMApp(gameDir) is not { } app)
        {
            report.Err(NotFiveM(gameDir));
            return report;
        }
        if (src.Length == 0 || !Directory.Exists(src))
        {
            report.Err("No payload folder given, and every file this installs comes out of one.");
            return report;
        }
        report.Info($"target: {app}");
        report.Info($"preset: {Preset.FiveM.Label()}");

        var reshade = CheckFiveM(app, src, report);
        var payloads = PayloadDir(src);
        var plugins = Path.Combine(app, FiveMPlugins);
        var files = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);

        var supplied = Supplied(ownRuntime, wantedRuntime, pins, Preset.FiveM, report);
        if (VerifiedPayload(payloads, AddonName, pins.AddonSha, report) is { } addon)
            files[$"{FiveMPlugins}/{AddonName}"] = addon;
        if (supplied is { } own) files[$"{FiveMGame}/{RuntimeName}"] = own.Bytes;
        else if (VerifiedPayload(payloads, RuntimeName, pins.RuntimeSha, report) is { } runtime)
            files[$"{FiveMGame}/{RuntimeName}"] = runtime;
        if (VerifiedPayload(payloads, WeightsName, pins.WeightsSha, report) is { } weights)
            files[$"{FiveMGame}/{WeightsName}"] = weights;

        if (reshade is not null && VerifiedPayload(payloads, "ReShade64.dll", pins.ReShade64Sha, report) is { } reShade)
            files[$"{FiveMPlugins}/{reshade}"] = reShade;

        // Only the keys the add-on needs, in an ini that is otherwise left exactly as it was.
        var iniPath = Path.Combine(plugins, "ReShade.ini");
        if (reshade is not null || File.Exists(iniPath))
        {
            var before = File.Exists(iniPath) ? File.ReadAllText(iniPath) : "";
            var after = ReadyReShadeIni(before);
            if (after != before) files[$"{FiveMPlugins}/ReShade.ini"] = System.Text.Encoding.UTF8.GetBytes(after);
        }

        if (pins.ShaderSha.Length > 0)
        {
            var effect = new Dictionary<string, byte[]>();
            if (AddCompanionEffect(effect, payloads, plugins))
                files[$"{FiveMPlugins}/{ShaderPath}"] = effect[ShaderPath];
            else
                report.Info(EffectLeftOut);
        }

        if (report.Failed)
        {
            report.Info("Nothing was written: fix the problem above and run it again.");
            return report;
        }

        var log = new List<string>();
        try
        {
            Transaction.Apply(app, Preset.FiveM.ManifestPreset(), Route.X64, files, log);
            foreach (var line in log) Narrate(line, report);
        }
        catch (InstallException e)
        {
            foreach (var line in log) Narrate(line, report);
            report.Err(RolledBack(e));
            return report;
        }

        if (supplied is { } kept) report.Info(SuppliedInstalled(kept.Build));
        report.Info(Preset.FiveM.Note());
        report.Info(
            "It starts switched off. In FiveM press Home, find AMD Neural Rendering under Add-ons and turn it on, "
            + "or press Ctrl+End. After a FiveM update, run this again if it stops.");
        return report;
    }

    /// <summary>What the pre-flight and the install both check, and the name ReShade has to be written
    /// under -- null when the ReShade already in plugins stays, or none can go in.</summary>
    private static string? CheckFiveM(string app, string src, Report report)
    {
        var plugins = Path.Combine(app, FiveMPlugins);
        var game = Path.Combine(app, FiveMGame);

        if (!Engine.FolderIsWritable(app))
            report.Err($"{app} cannot be written to. Run this installer as administrator.");

        if (!Directory.Exists(game))
        {
            report.Err(
                "data\\cache\\subprocess is not there yet: FiveM makes it the first time it starts GTA V. Start "
                + "FiveM once, let it load, close it, and run this again.");
        }
        else
        {
            var processes = Directory.EnumerateFiles(game, "FiveM_b*_GTAProcess.exe").Select(Path.GetFileName).ToList();
            if (processes.Count > 0)
                report.Ok($"FiveM runs GTA V as {Joined(processes!)}; the runtime and the weights go beside it, in data\\cache\\subprocess.");
            else
                report.Warn("No FiveM_b####_GTAProcess.exe in data\\cache\\subprocess yet. The files still go there; start FiveM once if it does nothing.");
        }

        var held = new[] { $"{FiveMPlugins}/{AddonName}", $"{FiveMGame}/{RuntimeName}", $"{FiveMGame}/{WeightsName}" }
            .Where(n => Engine.IsLocked(Path.Combine(app, n))).ToList();
        if (held.Count > 0)
            report.Err($"{string.Join(", ", held)} {(held.Count == 1 ? "is" : "are")} open: FiveM is still running. Close it and this line goes away.");

        var shipped = ShippedReShade(src, Preset.FiveM);
        var reshade = PlanFiveMReShade(plugins, shipped, report);
        if (reshade is not null && shipped is null && src.Length > 0)
            report.Err("ReShade64.dll is not in the payload folder, and the ReShade in plugins has to be replaced for the add-on to load.");
        if (Directory.Exists(plugins)) CheckDisabledAddons(plugins, report);
        CheckGtaFolder(app, report);
        return reshade;
    }

    /// <summary>The ReShade plugins\ loads, and what becomes of it: kept when the add-on loads in it,
    /// replaced under its own name when not, dxgi.dll when there is none. Somebody else's dxgi.dll is
    /// never replaced, and two ReShades are refused, as on every other route.</summary>
    private static string? PlanFiveMReShade(string plugins, string? shipped, Report report)
    {
        var found = Proxies.Where(n => File.Exists(Path.Combine(plugins, n)) && IsReShadeFile(Path.Combine(plugins, n), shipped)).ToList();
        if (found.Count > 1)
        {
            report.Err($"There are two ReShades in plugins: {Joined(found)}. A process that loads two ReShades does not "
                       + "start at all. Keep the one you use and run this again.");
            return null;
        }
        if (found.Count == 1)
        {
            var name = found[0];
            var path = Path.Combine(plugins, name);
            var version = Identify(path).Version;
            switch (FitOf(version, Engine.IsSignedFile(path)))
            {
                case ReShadeFit.Fits:
                    report.Ok($"Your ReShade {version} (plugins\\{name}) stays exactly as it is: it has full add-on support "
                              + "and is new enough for the add-on.");
                    return null;
                case ReShadeFit.Signed:
                    report.Info($"plugins\\{name} is ReShade {version} without full add-on support (the signed build), "
                                + "which switches every add-on off in an online game. " + FiveMSwap);
                    break;
                default:
                    report.Info($"plugins\\{name} is ReShade {version ?? "of an unknown version"}, and the add-on needs "
                                + $"{MinReShade} or newer (ReShade API 20). " + FiveMSwap);
                    break;
            }
            if (Engine.IsAllowed($"{FiveMPlugins}/{name}")) return name;
            report.Err($"ReShade in plugins is loaded as {name}, and this route only replaces it as dxgi.dll, d3d11.dll "
                       + "or dinput8.dll. Install ReShade 6.8.0 with full add-on support there yourself, then run this again.");
            return null;
        }

        var dxgi = Path.Combine(plugins, "dxgi.dll");
        if (File.Exists(dxgi) && Identify(dxgi).Product is { } other)
        {
            report.Err($"plugins\\dxgi.dll is {other}, not ReShade, and this app does not replace it. ReShade loads in "
                       + "FiveM as plugins\\dxgi.dll, so the two cannot both be there.");
            return null;
        }
        report.Info("There is no ReShade in plugins yet: ReShade 6.8.0 with full add-on support goes in as plugins\\dxgi.dll.");
        return "dxgi.dll";
    }

    /// <summary>FiveM ignores graphics mods in the GTA V folder ("Ignored graphics mod" in its F8
    /// console), so an install of ours there does nothing in FiveM. Said, never touched: story mode
    /// uses it.</summary>
    private static void CheckGtaFolder(string app, Report report)
    {
        string ini;
        try { ini = File.ReadAllText(Path.Combine(app, "CitizenFX.ini")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return; }
        var gta = Engine.GetIni(ini, "Game", "IVPath");
        if (gta.Length == 0 || !File.Exists(Path.Combine(gta, AddonName))) return;
        report.Info($"The GTA V folder ({gta}) has AMD-NR as well. FiveM ignores graphics mods there -- that is the "
                    + "\"Ignored graphics mod\" line in its F8 console -- so it stays for story mode and is not touched.");
    }
}
