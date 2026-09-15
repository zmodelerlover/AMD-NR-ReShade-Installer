// One file per install or uninstall, in %AppData%\AmdNrInstaller\logs\.
//
// The rolling log answers "what did it say"; this answers "why did it do that", which is the one a
// bug report needs. Half-Life is the case that earned it: ReShade was installed perfectly and the
// game never loaded it, because that build renders with OpenGL and the route came from a database
// row describing a build that no longer ships. Nothing in a report of copied files says any of
// that -- the machine, the detection evidence, the payload versions and the folder afterwards do.

using System.Text;
using AmdNr.Core;

namespace AmdNr.App;

public static class InstallLog
{
    /// <summary>Writes one log for an action and returns its path, or null if it could not be
    /// written. Never throws: a log that failed must not fail the install it describes.</summary>
    public static string? Write(
        string action,
        GameCard card,
        string target,
        Report report,
        PayloadManifest? manifest,
        PayloadPins pins,
        string? payloadFolder)
    {
        try
        {
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var name = $"{stamp}-{action}-{Safe(card.Name)}.log";
            var path = Path.Combine(AppPaths.Logs, name);

            var o = new StringBuilder();
            Head(o, "AMD-NR ReShade Installer");
            Line(o, "when", DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss zzz"));
            Line(o, "action", action);
            Line(o, "app", $"v{App.Version}  (language {App.CurrentLanguage})");
            Line(o, "result", report.Failed ? "FAILED" : "completed");

            var machine = GpuService.Read();
            Head(o, "This machine");
            Line(o, "os", Environment.OSVersion.VersionString);
            Line(o, "gpu", machine.Gpu);
            Line(o, "driver", machine.Driver);
            Line(o, "hip 7", GpuService.FindHip7() ?? "not found");
            Line(o, "radeon", machine.LooksLikeRadeon ? "yes" : "no");

            Head(o, "Target");
            Line(o, "game", card.Name);
            Line(o, "folder", card.Path);
            Line(o, "written to", target);
            Line(o, "platform", card.Platform);
            Line(o, "app id", card.Entry.AppId ?? "-");
            Line(o, "route", $"{card.Entry.Preset} ({card.Entry.Preset.ManifestPreset()})");
            Line(o, "route chosen by", card.Entry.PresetChosen ? "the user" : "detection");

            // The evidence behind the route. This is the field that explains a route that installed
            // cleanly and then did nothing in game.
            Head(o, "Detection");
            if (card.Graphics is { } g)
            {
                Line(o, "executable", g.Executable ?? "-");
                Line(o, "architecture", g.Width?.ToString() ?? "unknown");
                Line(o, "apis", g.All.Count == 0 ? "unknown" : string.Join(", ", g.All.Select(GraphicsDetection.Short)));
                Line(o, "source", g.Source ?? "the game's own files");
                Line(o, "recommended", GraphicsDetection.Short(g.Recommended));
                Line(o, "needs a renderer switch", g.NeedsRendererSwitch ? "yes" : "no");
                Line(o, "emulator", g.Emulator is { } e ? $"{e.Name} ({e.System})" : "-");
                Line(o, "why", g.Why);

                // Said plainly, because it is the difference between a broken install and a working
                // install of a route the game will never take.
                if (g.Source is not null && g.Api != GraphicsApi.Unknown && !g.All.Contains(g.Api))
                {
                    Line(o, "NOTE",
                        $"the database lists {string.Join(", ", g.All.Select(GraphicsDetection.Short))} but this copy's "
                        + $"files link {GraphicsDetection.Short(g.Api)}. If the chosen renderer is not in this build, "
                        + "nothing will load it.");
                }
            }
            else
            {
                Line(o, "detection", "had not run");
            }

            Head(o, "Payloads");
            Line(o, "manifest", manifest is null ? "not read (offline, or no local copy)" : "read");
            if (manifest is not null)
            {
                foreach (var (component, c) in manifest.Components)
                    Line(o, component, $"{c.Version}  ({string.Join(", ", c.Files.Select(f => f.Name))})");
            }
            Line(o, "staged from", payloadFolder ?? "-");
            Line(o, "addon sha", pins.AddonSha);
            Line(o, "runtime sha", pins.RuntimeSha);
            Line(o, "weights sha", pins.WeightsSha);

            Head(o, "What happened");
            foreach (var (level, text) in report.Lines)
            {
                var tag = level switch
                {
                    Level.Ok => "ok  ",
                    Level.Warn => "warn",
                    Level.Err => "ERR ",
                    _ => "    ",
                };
                o.AppendLine($"{tag} {text.Replace("\n", "\n     ")}");
            }

            Head(o, "The folder afterwards");
            FolderState(o, target);

            File.WriteAllText(path, o.ToString());
            Prune();
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The report is on screen either way; a log that could not be written is not a reason to
            // put a second error in front of someone.
            return null;
        }
    }

    /// <summary>Every file of ours that is in the folder now, with its size. "The add-on is not
    /// there" and "the add-on is there and never loaded" are different bugs and look identical in a
    /// report of what was copied.</summary>
    private static void FolderState(StringBuilder o, string target)
    {
        var dir = File.Exists(target) ? Path.GetDirectoryName(target)! : target;
        if (!Directory.Exists(dir))
        {
            Line(o, dir, "is not a folder");
            return;
        }

        string[] interesting =
        [
            .. Work.InstalledMarkers,
            "dlss5-neural.ini", "ReShade.ini", "ReShade.log",
            "dxgi.dll", "d3d11.dll", "d3d12.dll", "d3d9.dll", "d3d8.dll", "d3d8R.dll", "opengl32.dll",
            Route.X64.ManifestFileName(), Engine.ManifestName,
        ];

        foreach (var name in interesting.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var path = Path.Combine(dir, name);
            if (!File.Exists(path)) continue;
            var size = new FileInfo(path).Length;

            // A proxy DLL's filename says nothing about what it is: OptiScaler, DXVK and SpecialK all
            // install as dxgi.dll too. The version resource is what settles it.
            var what = "";
            if (name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(path);
                    var product = string.IsNullOrWhiteSpace(info.ProductName) ? info.FileDescription : info.ProductName;
                    if (!string.IsNullOrWhiteSpace(product)) what = $"  [{product.Trim()} {info.ProductVersion?.Trim()}]";
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // No version resource is itself informative; leave the column empty.
                }
            }
            o.AppendLine($"     {name,-32} {size,13:N0}{what}");
        }
    }

    /// <summary>Keeps the newest hundred. Logs exist to be read after something went wrong, and a
    /// folder nobody ever empties is its own small bug.</summary>
    private static void Prune(int keep = 100)
    {
        try
        {
            var files = new DirectoryInfo(AppPaths.Logs).GetFiles("*.log")
                .Where(f => f.Name != "amd-nr-installer.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(keep);
            foreach (var f in files) f.Delete();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not pruning costs disk, nothing else.
        }
    }

    private static void Head(StringBuilder o, string title)
    {
        o.AppendLine();
        o.AppendLine(title);
        o.AppendLine(new string('-', 74));
    }

    private static void Line(StringBuilder o, string key, string value) =>
        o.AppendLine($"  {key,-24} {value}");

    /// <summary>A game name as a filename. Everything awkward becomes a dash.</summary>
    private static string Safe(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) || c == ' ' ? '-' : c).ToArray());
        cleaned = cleaned.Trim('-');
        return cleaned.Length == 0 ? "game" : cleaned[..Math.Min(48, cleaned.Length)];
    }
}
