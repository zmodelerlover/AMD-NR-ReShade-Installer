// After playing: the sheet reads the logs the add-on and the runtime leave beside the game and says how
// the last session went. Driven by writing those logs into the flow's game folder, the way a session
// would, and asking the sheet to look again as coming back to the window does.
//
// Also here, because they read the same state: the library's filter, and the notes dialog.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using AmdNr.App;
using AmdNr.Core;

internal static class SessionFlow
{
    public static void Run(MainWindow main, GameSheet sheet, GameCard card, string game,
        Action<bool, string> check, Func<Func<bool>, int, bool> until, Action<Window, string> save)
    {
        string S(string key) => main.FindResource(key) as string ?? key;
        // By name anywhere under the sheet: the session box and the left column are controls of their own.
        T Named<T>(string name) where T : Control => sheet.GetVisualDescendants().OfType<T>().First(c => c.Name == name);
        var section = Named<SessionBox>("LastSession");
        var title = Named<TextBlock>("SessionTitle");
        var detail = Named<TextBlock>("SessionDetail");
        var line = Named<SelectableTextBlock>("SessionLine");

        sheet.ShowSession(card);
        check(until(() => section.IsVisible && title.Text == S("Str.SessionNever"), 10),
            "an install nobody has played yet says so, where the last session goes");
        check(Named<Button>("TransferButton").IsVisible, "and offers its NR settings out and in");

        var addon = Path.Combine(game, SessionLog.AddonLog);
        var runtime = Path.Combine(game, SessionLog.RuntimeLog);
        File.WriteAllText(addon, "engine ready.\nframe 1200 processed (0 skipped)\n");
        File.WriteAllText(runtime, "dlssnr_amd v0.5.0 (build 1) loaded into Game.exe as version.dll from x\n"
                                   + "network job 1190 done in 9 ms\n"
                                   + "timing (avg of 200 jobs): worker wall 9.5 ms = queued 0.0 + network 6.5 + flag+sync 0.3\n");
        sheet.ShowSession(card);
        check(until(() => title.Text == S("Str.SessionRan"), 10) && detail.Text?.Contains("1,200") == true
              && detail.Text.Contains("6.5") && detail.Text.Contains("0.5.0") && !line.IsVisible,
            $"a session that ran says so, with its frames, the network's time and the runtime ({detail.Text})");
        save(main, "flow-session-ran");

        File.AppendAllText(runtime, "CRASH: exception 0xc0000005 at 0000 in Game.exe (thread 1, last job 1190, mode inline)\n");
        sheet.ShowSession(card);
        check(until(() => title.Text == S("Str.SessionCrashed"), 10) && line.IsVisible && line.Text!.StartsWith("CRASH:"),
            "a crash says so, with the runtime's own line for Discord");
        save(main, "flow-session-crash");
        // The longest of the languages, the one that reads right to left, and the one .NET has no culture for.
        foreach (var code in new[] { "de", "ar", "x-pirate" })
        {
            App.ChangeLanguage(code);
            // What ChangeLanguage does to every window of the desktop, which a headless run has none of.
            main.FlowDirection = App.Flow;
            main.Relabel();
            sheet.Show(card);
            until(() => title.Text == S("Str.SessionCrashed"), 10);
            save(main, $"flow-session-crash-{code}");
        }
        App.ChangeLanguage("en");
        main.FlowDirection = App.Flow;
        main.Relabel();
        sheet.Show(card);

        // The library's filter reads the same install state the sheet does.
        var page = main.GetVisualDescendants().OfType<LibraryPage>().First();
        var grid = page.FindControl<ItemsControl>("CardList")!;
        MenuItem Item(string name) => page.FindControl<MenuItem>(name)!;
        Item("FilterNotInstalled").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check(until(() => grid.ItemCount == 0, 5), "filtering to games not installed hides the installed one");
        Item("FilterInstalled").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check(until(() => grid.ItemCount == 1, 5) && page.FindControl<Button>("FilterButton")!.Classes.Contains("on"),
            "filtering to installed games shows it, and the filter button is lit while it hides any");
        // Coming back to the window reads the logs' dates again, as MainWindow.OnBack does.
        card.RefreshInstalled();
        check(card.LastPlayed is not null, "the logs count as the game having been played");
        Item("SortRecent").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Item("FilterAll").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        Item("SortName").RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
        check(until(() => grid.ItemCount == main.Library.Cards.Count, 5), "and All shows every game again");

        // What's new, with notes in the shape GitHub gives them, over the page in the sheet's frame.
        main.ShowNotes("dlss5-neural-amd v0.7.2",
            Task.FromResult<string?>("""
                ### danielblnc's 0.5.1 supporter build.

                - **The add-on runs danielblnc's 0.5.1**, the newest build he gives his supporters, beside 0.5.0, v0.4.3, v0.4.2 and v0.4.1. Like 0.5.0 it is **not distributed**: the installer takes your own `version.dll` or `dlssnr_on_amd_setup.exe`.
                - Its addresses were mapped from 0.5.0 by aligned instructions, and pass `tools/runtime_offsets_check.py` against the patched file.
                - **Your settings stay.** Nothing else changed from v0.7.1.

                The installer offers it as an update. Full notes in [CHANGELOG.md](https://example.invalid).
                """),
            "https://github.com/zmodelerlover/dlss5-neural-amd/releases/tag/v0.7.2");
        var notes = main.GetVisualDescendants().OfType<NotesSheet>().First();
        check(until(() => notes.IsOpen && notes.FindControl<StackPanel>("NotesBody")!.Children.Count == 5, 10),
            "What's new opens over the page with the notes drawn as a heading, bullets and a paragraph");
        until(() => false, 1);
        save(main, "flow-whats-new");
        main.ShowPage(MainWindow.Page.Settings);
        check(until(() => !notes.IsVisible, 5), "and closes when another page is picked");
        main.ShowPage(MainWindow.Page.Games);
        until(() => false, 1);
        check(!notes.IsVisible && !notes.IsOpen, "staying closed back on the games page");
        main.OpenSheet(card); // The page switch closed the game's sheet too, as it always has.
        until(() => sheet.IsOpen, 5);

        File.Delete(addon);
        File.Delete(runtime);
    }
}
