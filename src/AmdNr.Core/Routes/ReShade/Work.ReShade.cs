// The ReShade route: ReShade with full add-on support and the add-on beside a 64-bit game, through
// the same transaction every route goes through. The 32-bit bridge is Work.X86.cs, and what both of
// them check before a byte is written is Work.ReShade.Checks.cs.

namespace AmdNr.Core;

public static partial class Work
{
    private static Report PreflightReShade(string gameDir, string payloadDir, Preset preset, PayloadPins pins,
        string? proxy)
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

            // The 32-bit route installs its own pair, pinned by payload.sha256, and never the 64-bit
            // add-on -- asking for that file there is asking for something that is not supposed to exist.
            if (preset.Route() == Route.X86)
            {
                foreach (var name in new[] { "payload.sha256", @"files\amd-nr.addon32", @"files\amd-nr-host64.exe" })
                {
                    if (File.Exists(Path.Combine(src, name))) continue;
                    report.Err($"{Path.GetFileName(name)} is not in the payload folder; the 32-bit bridge cannot be installed without it.");
                    allThere = false;
                }
            }

            var expected = preset.Route() == Route.X86
                ? new[] { (RuntimeName, pins.RuntimeSize), (WeightsName, pins.WeightsSize) }
                : [(AddonName, pins.AddonSize), (RuntimeName, pins.RuntimeSize), (WeightsName, pins.WeightsSize)];

            foreach (var (name, want) in expected)
            {
                var nested = Path.Combine(src, "files", name);
                switch (Engine.SizeOf(File.Exists(nested) ? nested : Path.Combine(payloads, name)))
                {
                    case null:
                        report.Err($"{name} is not in that folder.");
                        allThere = false;
                        break;
                    // want == 0 means the size is not known, not that the file should be empty: the
                    // add-on's own size only comes from the payload manifest, and without one the
                    // pins fall back to a zero there. Judging a real file against it said "that is a
                    // different build" about the right file.
                    case { } got when want > 0 && got != want:
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
        CheckRouteIsReachable(dir, preset, report);

        // The 32-bit route installs the pinned ReShade build itself when the payload carries it, so
        // "no ReShade here" is not a problem to report there -- it is the state before installing.
        var shipped = ShippedReShade(src, preset);
        if (shipped is not null)
            report.Ok(preset.Route() == Route.X86
                ? $"The pinned 32-bit ReShade 6.8.0 is part of this install, as {ProxyNameFor(preset, dir, proxy)}; nothing to install by hand."
                : $"ReShade 6.8.0 with full add-on support is part of this install, as {ReShadeProxyFor(preset, dir, proxy)}; nothing to install by hand.");
        else
            CheckReShade(dir, preset, report);

        // Never inside the else: shipping ReShade is exactly when a second one is most likely, and
        // skipping the whole check there is what let a folder with two of them install cleanly.
        CheckDoubleReShade(dir, preset, report, ProxyNameFor(preset, dir, proxy), shipped);

        CheckDisabledAddons(dir, report);

        var dead = DeadFiles().Where(n => File.Exists(Path.Combine(dir, n))).ToList();
        if (dead.Count > 0)
        {
            report.Info(
                $"{dead.Count} file(s) from an older layout are here and will be removed: "
                + string.Join(", ", dead));
        }

        if (!report.Failed) report.Ok("Nothing in the way.");
        return report;
    }

    private static Report InstallReShade(string gameDir, string payloadDir, Preset preset, PayloadPins pins,
        string? proxy)
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
        report.Info($"preset: {preset.Label()}");

        CheckExe(dir, preset, report);
        if (src.Length == 0 || !File.Exists(Path.Combine(PayloadDir(src), "ReShade64.dll")) || preset.IsVulkan())
            CheckReShade(dir, preset, report);
        CheckDoubleReShade(dir, preset, report, ReShadeProxyFor(preset, dir, proxy), ShippedReShade(src, preset));

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

        // The companion effect. Shared with the 32-bit route; see AddCompanionEffect.
        if (pins.ShaderSha.Length > 0)
        {
            if (!AddCompanionEffect(files, payloads, dir))
            {
                if (!HasStandardShaders(dir)) LeaveOutEffect(dir, report);
            }
            else
                report.Info(
                    $"{ShaderName} goes in reshade-shaders\\Shaders. Enable it in ReShade, under a "
                    + "motion-vector shader such as iMMERSE Launchpad, and the add-on gets real "
                    + "motion vectors in a game that has none of its own.");
        }

        // ReShade itself, when the payload carries it: the add-on does nothing without it, and asking
        // someone to run a second installer and pick the right API is the step people get wrong.
        if (File.Exists(Path.Combine(payloads, "ReShade64.dll"))
            && ReShadeProxyFor(preset, dir, proxy) is { } proxyName
            && VerifiedPayload(payloads, "ReShade64.dll", pins.ReShade64Sha, report) is { } reShade)
        {
            files[proxyName] = reShade;
            var iniPath = Path.Combine(dir, "ReShade.ini");
            var before = File.Exists(iniPath) ? File.ReadAllText(iniPath) : "";
            var after = ReadyReShadeIni(before);
            if (after != before) files["ReShade.ini"] = System.Text.Encoding.UTF8.GetBytes(after);
            report.Info($"ReShade 6.8.0 with full add-on support goes in as {proxyName}.");
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

        if (SweepDead(dir, report.Warn) is { Count: > 0 } swept)
            report.Ok($"removed {swept.Count} file(s) an older install left behind: {string.Join(", ", swept)}");

        if (!report.Failed)
        {
            report.Info(preset.Note());
            report.Info(
                "It starts switched off. Open the overlay with Home, or press Ctrl+End. StartOn=1 in "
                + "amd-nr.ini makes it come up enabled.");
        }
        return report;
    }
}
