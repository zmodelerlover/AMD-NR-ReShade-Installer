// One zip holding everything someone would otherwise be asked for, three messages at a time.
//
// Nothing is sent anywhere. The file lands in reports\ and the folder opens with it selected, so the
// person drags it into Discord themselves. That is the whole design: no webhook in a distributed
// binary for anyone to find, no rate limit to enforce, and nothing leaves the machine that the
// person did not hand over on purpose.
//
// What goes in is decided by what actually diagnosed the bugs this project has had. Half-Life 2 was
// solved by ReShade.log -- it said which folder ReShade searched for add-ons, which is the entire
// answer -- and that file is in the game folder, which is the one place nobody thinks to look.

using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using AmdNr.Core;

namespace AmdNr.App;

public static class SupportReport
{
    public static string Folder => Directory.CreateDirectory(Path.Combine(AppPaths.Root, "reports")).FullName;

    /// <summary>Files the add-on and ReShade write beside a game, which say what happened at run
    /// time rather than at install time.</summary>
    private static readonly string[] RuntimeEvidence =
    [
        "ReShade.log",
        // ReShade renames its log to .log1 when the game starts again, so the run that mattered is
        // the one this report was missing: a game that closes at once leaves a log of a few hundred
        // bytes, and the next launch -- the one people make before asking for help -- pushes it here.
        "ReShade.log1",
        "ReShade.ini", "dlss5-neural.log", "dlss5-neural.ini",
        "dlssnr_on_amd.log", "dlssnr_on_amd.ini", "dlss5-pass1.dll",
        // The 32-bit bridge does not use the name above: it writes one log per half of the pair,
        // and those two are what said which hotkey the add-on was actually running with.
        "dlss5-neural-x86.log", "dlss5-neural-x86-host.log",
    ];

    /// <summary>A single file this big is taken in full; past it, the tail is taken and the cut is
    /// noted. A ReShade log from a long session is the only thing here that ever grows.</summary>
    private const int MaxFileBytes = 2 * 1024 * 1024;

    /// <summary>Builds the zip and returns its path. Throws nothing the caller has to handle beyond
    /// IO: a report that cannot be written reports that.</summary>
    public static string Build(GameCard? card, string? installFolder)
    {
        var path = Path.Combine(Folder, $"amd-nr-report-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);

        Text(zip, "report.txt", Summary(card, installFolder));
        Text(zip, "system.txt", Machine());

        // Every per-action log, newest first, plus the rolling one. These carry the route, who chose
        // it, the detection evidence and the payload hashes.
        //
        // Counted per kind rather than "the twelve newest": a burst of uninstalls pushed every
        // install out of the zip, and the pair is what a report gets read against -- what went in,
        // and what came back out. "*-install-*" does not match "-uninstall-", which is why the two
        // patterns can be counted separately at all.
        var logs = Recent(AppPaths.Logs, "*-install-*.log", 8)
            .Concat(Recent(AppPaths.Logs, "*uninstall*.log", 8))
            .Concat(Recent(AppPaths.Logs, "amd-nr-installer.log", 1))
            .DistinctBy(f => f.FullName);
        foreach (var log in logs)
            Copy(zip, log, $"logs/{log.Name}");

        // What the app is configured with, which is how a wrong manifest or a stale config shows up.
        foreach (var name in new[] { "games.json", "settings.json", "config.json", "payload.json", "api-db.json" })
        {
            var beside = Path.Combine(AppPaths.Root, name);
            var shipped = Path.Combine(AppContext.BaseDirectory, name);
            if (File.Exists(beside)) Copy(zip, new FileInfo(beside), $"config/{name}");
            else if (File.Exists(shipped)) Copy(zip, new FileInfo(shipped), $"config/shipped-{name}");
        }

        // The game folder itself: what is in it, and what the add-on and ReShade wrote there.
        if (installFolder is { Length: > 0 })
        {
            var dir = File.Exists(installFolder) ? Path.GetDirectoryName(installFolder)! : installFolder;
            Text(zip, "game/listing.txt", Listing(dir));
            foreach (var name in RuntimeEvidence)
            {
                var file = Path.Combine(dir, name);
                if (File.Exists(file)) Copy(zip, new FileInfo(file), $"game/{name}");
            }
            foreach (var manifest in new[] { Route.X64.ManifestFileName(), Engine.ManifestName })
            {
                var file = Path.Combine(dir, manifest);
                if (File.Exists(file)) Copy(zip, new FileInfo(file), $"game/{manifest}");
            }

            // A Source or Unreal game installs into a subfolder; the root is where someone looking by
            // hand would go, and its listing says whether an older install is still sitting there.
            if (card is not null && !Engine.SamePath(dir, card.Path) && Directory.Exists(card.Path))
                Text(zip, "game/listing-root.txt", Listing(card.Path));
        }

        return path;
    }

    /// <summary>Saves the report and shows it in Explorer, selected. Returns the path, or null when
    /// it could not be written.</summary>
    public static string? Save(GameCard? card, string? installFolder)
    {
        string path;
        try { path = Build(card, installFolder); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        try { Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // The path is on screen either way.
        }
        return path;
    }

    // -- What goes in ------------------------------------------------------------------------------

    private static string Summary(GameCard? card, string? installFolder)
    {
        var o = new StringBuilder();
        o.AppendLine("AMD-NR ReShade Installer -- problem report");
        o.AppendLine(new string('=', 74));
        o.AppendLine($"made            {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}");
        o.AppendLine($"app             v{App.Version}  (language {App.CurrentLanguage})");
        o.AppendLine();

        if (card is null)
        {
            o.AppendLine("No game was selected when this was made.");
        }
        else
        {
            o.AppendLine($"game            {card.Name}");
            o.AppendLine($"folder          {card.Path}");
            o.AppendLine($"installed to    {installFolder ?? "-"}");
            o.AppendLine($"platform        {card.Platform}");
            o.AppendLine($"app id          {card.Entry.AppId ?? "-"}");
            o.AppendLine($"route           {card.Entry.Preset} ({card.Entry.Preset.ManifestPreset()})");
            o.AppendLine($"route chosen by {(card.Entry.PresetChosen ? "the user" : "detection")}");
            o.AppendLine($"reads installed {card.Installed}");
            o.AppendLine();

            if (card.Graphics is { } g)
            {
                o.AppendLine("detection");
                o.AppendLine($"  executable    {g.Executable ?? "-"}");
                o.AppendLine($"  installs into {g.Target ?? "-"}");
                o.AppendLine($"  architecture  {g.Width?.ToString() ?? "unknown"}");
                o.AppendLine($"  apis          {(g.All.Count == 0 ? "unknown" : string.Join(", ", g.All.Select(GraphicsDetection.Short)))}");
                o.AppendLine($"  source        {g.Source ?? "the game's own files"}");
                o.AppendLine($"  emulator      {(g.Emulator is { } e ? $"{e.Name} ({e.System})" : "-")}");
                o.AppendLine($"  why           {g.Why}");
            }
        }

        o.AppendLine();
        o.AppendLine("What is in here");
        o.AppendLine("  report.txt      this file");
        o.AppendLine("  system.txt      the machine, and whether it can run the add-on at all");
        o.AppendLine("  logs/           one file per install or uninstall, plus the rolling log");
        o.AppendLine("  config/         what the app is configured with and what it downloaded");
        o.AppendLine("  game/           the game folder's listing, and what ReShade and the add-on");
        o.AppendLine("                  wrote there at run time -- ReShade.log is usually the answer");
        return o.ToString();
    }

    private static string Machine()
    {
        var state = GpuService.Read();
        var o = new StringBuilder();
        o.AppendLine($"os              {Environment.OSVersion.VersionString}");
        o.AppendLine($"64-bit          {Environment.Is64BitOperatingSystem}");
        o.AppendLine($"gpu             {state.Gpu}");
        o.AppendLine($"driver          {state.Driver}");
        o.AppendLine($"hip 7           {GpuService.FindHip7() ?? "not found"}");
        o.AppendLine($"looks radeon    {state.LooksLikeRadeon}");
        o.AppendLine($"ready           {state.Ready}");
        o.AppendLine();
        o.AppendLine($"data folder     {AppPaths.Root}");
        o.AppendLine($"cache           {AppPaths.Cache}");
        o.AppendLine($"app folder      {AppContext.BaseDirectory}");
        o.AppendLine();
        o.AppendLine("cache contents");
        try
        {
            foreach (var f in new DirectoryInfo(AppPaths.Cache).GetFiles("*", SearchOption.AllDirectories)
                         .Where(f => f.Directory?.Name != "covers")
                         .OrderBy(f => f.FullName))
                o.AppendLine($"  {Path.GetRelativePath(AppPaths.Cache, f.FullName),-62} {f.Length,13:N0}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            o.AppendLine("  (could not be read)");
        }
        return o.ToString();
    }

    /// <summary>Every file in the folder with its size, and what each DLL says it is. The filename of
    /// a proxy says nothing -- OptiScaler, DXVK and SpecialK all install as dxgi.dll -- so the version
    /// resource is what tells one from another.</summary>
    private static string Listing(string dir)
    {
        var o = new StringBuilder();
        o.AppendLine(dir);
        o.AppendLine(new string('-', 74));
        try
        {
            foreach (var f in new DirectoryInfo(dir).GetFiles().OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var what = "";
                if (f.Extension.Equals(".dll", StringComparison.OrdinalIgnoreCase)
                    || f.Extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    try
                    {
                        var info = FileVersionInfo.GetVersionInfo(f.FullName);
                        var product = string.IsNullOrWhiteSpace(info.ProductName) ? info.FileDescription : info.ProductName;
                        if (!string.IsNullOrWhiteSpace(product)) what = $"  [{product.Trim()} {info.ProductVersion?.Trim()}]";
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        // No version resource is itself informative.
                    }
                }
                o.AppendLine($"  {f.Name,-46} {f.Length,13:N0}  {f.LastWriteTime:yyyy-MM-dd HH:mm}{what}");
            }

            foreach (var d in new DirectoryInfo(dir).GetDirectories().OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                o.AppendLine($"  {d.Name + "\\",-46} {"<dir>",13}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            o.AppendLine($"  could not be read: {e.Message}");
        }
        return o.ToString();
    }

    // -- Zip plumbing ------------------------------------------------------------------------------

    private static IEnumerable<FileInfo> Recent(string dir, string pattern, int count)
    {
        try
        {
            return new DirectoryInfo(dir).GetFiles(pattern)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Take(count)
                .ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or DirectoryNotFoundException)
        {
            return [];
        }
    }

    private static void Text(ZipArchive zip, string name, string content)
    {
        using var stream = zip.CreateEntry(name).Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }

    private static void Copy(ZipArchive zip, FileInfo file, string name)
    {
        try
        {
            // Shared read, because ReShade holds its own log open while the game runs.
            using var source = new FileStream(file.FullName, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);

            using var target = zip.CreateEntry(name).Open();
            if (source.Length > MaxFileBytes)
            {
                target.Write(Encoding.UTF8.GetBytes(
                    $"[the first {source.Length - MaxFileBytes:N0} bytes were cut; the end is what matters here]\n\n"));
                source.Seek(-MaxFileBytes, SeekOrigin.End);
            }
            source.CopyTo(target);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Text(zip, name + ".unreadable.txt", $"{file.FullName}\n{e.Message}\n");
        }
    }
}
