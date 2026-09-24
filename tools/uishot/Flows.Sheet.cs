// A sheet opened while some other work holds the app busy -- a scan, Download all: the verdict on
// screen was the last game's, and the check never ran once that work was done. Run by the flows in
// Program.cs with the sheet open and nothing busy.

using Avalonia.Controls;
using AmdNr.App;

internal static class SheetFlow
{
    public static void Run(MainWindow main, GameSheet sheet, GameCard card, Action<bool, string> check,
        Func<Func<bool>, int, bool> until)
    {
        var banner = sheet.FindControl<Border>("ResultBanner")!;
        check(until(() => banner.IsVisible, 10), "the open sheet shows its verdict");

        sheet.Close();
        until(() => !sheet.IsOpen, 5);
        main.Session.Busy = true; // somebody else's work, as a scan is
        main.OpenSheet(card);
        check(until(() => sheet.IsOpen, 5) && !banner.IsVisible,
            "opened while other work runs, it shows no verdict rather than a stale one");

        main.Session.Busy = false;
        check(until(() => banner.IsVisible, 15), "and checks the game once that work is done");
    }
}
