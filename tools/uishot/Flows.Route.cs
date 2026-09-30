// A game whose graphics API could not be read, as the person meets it: no route is picked for them, the
// sheet says why and which route suits which game, Install waits, and once they pick ReShade it installs
// and the log says the route was chosen by hand. The flow game's executable imports nothing, which is
// exactly the NBA 2K27 case. Run first by the flows in Program.cs; it leaves the ReShade route installed.

using Avalonia.Controls;
using AmdNr.App;
using AmdNr.Core;

internal static class RouteFlow
{
    public static void Run(MainWindow main, GameSheet sheet, GameCard card, Action<bool, string> check,
        Func<Func<bool>, int, bool> until, Action<Button> click, Action<Window, string> save)
    {
        string S(string key) => main.FindResource(key) as string ?? key;
        T Named<T>(string name) where T : Control => sheet.FindControl<T>(name)!;
        var reshade = Named<RadioButton>("RouteReShade");
        var install = Named<Button>("InstallButton");

        check(card.Graphics is { All.Count: 0 } && !card.Entry.PresetChosen, "the flow game's API is unknown, and no route was chosen");
        check(reshade.IsChecked != true && Named<RadioButton>("RouteOpti").IsChecked != true,
            "so neither route is picked for it");
        check(Named<Border>("PickRouteBox").IsVisible && !Named<Border>("ReShadePanel").IsVisible
              && Named<TextBlock>("PickRouteText").Text?.Contains("OptiScaler") == true,
            "the sheet asks for one and says which suits which game");
        check(until(() => Named<TextBlock>("VerdictTitle").Text == S("Str.PickRouteTitle"), 5) && !install.IsEnabled
              && !Named<Border>("ReportEmpty").IsVisible,
            "and Install waits for it");
        save(main, "flow-0-pick-route");

        reshade.IsChecked = true;
        check(card.Entry.PresetChosen && card.Entry.Preset == Preset.Dx11 && install.IsEnabled
              && Named<Border>("ReShadePanel").IsVisible, "picking ReShade chooses it by hand and lets Install run");
        click(install);
        check(until(() => !main.Session.Busy, 30), "Install finishes");
        var log = new DirectoryInfo(AppPaths.Logs).GetFiles("*-install-*.log").MaxBy(f => f.LastWriteTimeUtc);
        check(log is not null && File.ReadAllLines(log.FullName).Any(l => l.Contains("route chosen by") && l.TrimEnd().EndsWith("hand")),
            "and its log says the route was chosen by hand");
    }
}
