// The download that failed, from the side of whoever has to work out why: the log it left, and the
// button on "This machine" that gathers every such log with a check of each address. Run by the flows
// in Program.cs right after the files panel has failed to download anything.

using System.IO.Compression;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AmdNr.App;
using AmdNr.Core;

internal static class DownloadFlow
{
    public static void Run(MainWindow main, Action<bool, string> check, Func<Func<bool>, int, bool> until,
        Action<Button> click)
    {
        var log = Directory.GetFiles(AppPaths.Logs, "*-download-addon.log").OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
        var text = log is null ? "" : File.ReadAllText(log);
        check(text.Contains("FAILED: refused") && text.Contains("https://127.0.0.1:1/amd-nr.addon64")
              && text.Contains("SocketException"),
            $"the failed download left a log naming the address, that it refused, and the exception ({Path.GetFileName(log)})");

        // Nobody is watching a headless run, and an Explorer window would open on the real desktop.
        SupportReport.Reveal = false;
        var button = main.GetVisualDescendants().OfType<MachinePage>().First().FindControl<Button>("DownloadReportButton")!;
        var before = Directory.GetFiles(SupportReport.Folder, "amd-nr-downloads-*.zip");
        click(button);
        check(until(() => button.IsEnabled && Directory.GetFiles(SupportReport.Folder, "amd-nr-downloads-*.zip").Length > before.Length, 30),
            "Download logs on This machine saves a zip");

        var saved = Directory.GetFiles(SupportReport.Folder, "amd-nr-downloads-*.zip").Except(before).FirstOrDefault();
        if (saved is null) return;
        using var zip = ZipFile.OpenRead(saved);
        string Read(string name) => zip.GetEntry(name) is { } e ? new StreamReader(e.Open()).ReadToEnd() : "";
        check(Read("addresses.txt").Contains("127.0.0.1") && Read("addresses.txt").Contains("FAILED after"),
            "with each address checked again, and this one failing");
        check(log is not null && Read($"logs/{Path.GetFileName(log)}").Contains("FAILED: refused"),
            "and the download's own log inside it");
    }
}
