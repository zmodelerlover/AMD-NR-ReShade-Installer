// "This machine": the two things that stop the add-on from running -- not a Radeon, no HIP 7 -- the
// files every install is made of, and the logs for when those files would not download.

using Avalonia.Controls;
using Avalonia.Interactivity;
using AmdNr.Core;

namespace AmdNr.App;

public partial class MachinePage : UserControl
{
    private MainWindow _shell = null!;

    public MachinePage() => InitializeComponent();

    public void Attach(MainWindow shell)
    {
        _shell = shell;
        Payloads.Attach(shell.Session, shell);
    }

    public Task RefreshAsync() => Payloads.RefreshAsync();

    public void Show(SystemState state)
    {
        GpuText.Text = state.Gpu;
        DriverText.Text = state.Driver;
        SetStatus(GpuStatus, GpuStatusIcon, state.LooksLikeRadeon);
        Ui.Localize(GpuStatusText, state.LooksLikeRadeon ? "Str.GpuOk" : "Str.GpuBad");
        SetStatus(HipStatus, HipStatusIcon, state.Hip7);
        Ui.Localize(HipText, state.Hip7 ? "Str.Ready" : "Str.Missing");

        // One sentence on top that says whether anything on this page needs doing.
        Ui.SetLevel(Verdict, state.Ready ? Level.Ok : Level.Err);
        Ui.SetLevel(VerdictTile, state.Ready ? Level.Ok : Level.Err);
        VerdictIcon.Data = Ui.Icon(state.Ready ? "IconOk" : "IconErr");
        Ui.Localize(VerdictTitle, state.Ready ? "Str.SystemReady" : "Str.SystemNotReady");

        Warning.Text = !state.Hip7 ? Ui.Text("Str.HipMissing") : !state.LooksLikeRadeon ? Ui.Text("Str.NotRadeon") : "";
        Warning.IsVisible = Warning.Text.Length > 0;
    }

    private static void SetStatus(Border pill, PathIcon icon, bool ok)
    {
        Ui.SetLevel(pill, ok ? Level.Ok : Level.Err);
        icon.Data = Ui.Icon(ok ? "IconCheck" : "IconErr");
    }

    /// <summary>Every download log, and every address checked again now, in one zip. Nothing is
    /// sent: the file is saved and Explorer opens with it selected. Not refused while a download
    /// runs, unlike the game's report: it only reads, and a download that hangs is when it is wanted.</summary>
    private void OnDownloadReport(object? sender, RoutedEventArgs e) => _shell.Run("download report", async () =>
    {
        DownloadReportButton.IsEnabled = false;
        Ui.Localize(DownloadReportLabel, "Str.ReportWorking");
        try
        {
            // The checks wait on the network and the zip on the disk; neither belongs on the UI thread.
            var session = _shell.Session;
            var path = await Task.Run(async () => SupportReport.SaveDownloads(await DownloadLog.DiagnoseAsync(session)));
            if (path is null)
            {
                _shell.Toast(Ui.Format("Str.ReportFailed", AppPaths.Logs), Level.Err);
                return;
            }
            _shell.Toast(Ui.Format("Str.ReportSaved", Path.GetFileName(path)), Level.Ok);
        }
        finally
        {
            DownloadReportButton.IsEnabled = true;
            Ui.Localize(DownloadReportLabel, "Str.DownloadReport");
        }
    });
}
