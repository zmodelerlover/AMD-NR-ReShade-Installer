// "This machine": the two things that stop the add-on from running -- not a Radeon, no HIP 7 -- and
// the files every install is made of.

using Avalonia.Controls;
using AmdNr.Core;

namespace AmdNr.App;

public partial class MachinePage : UserControl
{
    public MachinePage() => InitializeComponent();

    public void Attach(MainWindow shell) => Payloads.Attach(shell.Session, shell);

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
}
