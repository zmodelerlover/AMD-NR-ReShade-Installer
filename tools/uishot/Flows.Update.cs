// The update card on the Settings page in each of its states: checking, up to date, a release with no sums
// (Download from GitHub), one that can update in place, a failed check, and a hotfix of the release running,
// said as one. The gear's dot shows only while there is an update. Leaves the check failed.

using Avalonia.Controls;
using AmdNr.App;
using AmdNr.Core;

internal static class UpdateFlow
{
    public static void Run(MainWindow main, Action<bool, string> check, Func<Func<bool>, int, bool> until,
        Action<int> settle, Action<Window, string> save)
    {
        var session = main.Session;
        string S(string key) => main.FindResource(key) as string ?? key;
        main.ShowPage(MainWindow.Page.Settings);
        var update = typeof(Session).GetProperty(nameof(Session.Update))!;
        check(until(() => session.Update.State == UpdateState.Failed, 10), "an update check with nowhere to ask fails");
        foreach (var state in new UpdateCheck[]
                 {
                     new(UpdateState.Checking), new(UpdateState.UpToDate, When: DateTimeOffset.Now),
                     new(UpdateState.Available, new AppRelease("9.9.9", "", "https://example.invalid", new Dictionary<string, string>())),
                     new(UpdateState.Available, new AppRelease("9.9.9", "", "https://example.invalid",
                         new Dictionary<string, string> { [AppUpdate.ExeAsset] = "", [AppUpdate.SumsAsset] = "" })),
                     new(UpdateState.Failed),
                 })
        {
            update.SetValue(session, state);
            main.Relabel();
            settle(6);
            save(main, $"flow-4-update-{state.State.ToString().ToLowerInvariant()}{(state.Release?.CanSelfUpdate == true ? "-self" : "")}");
            if (state.Release is { } r)
                check(main.FindControl<Button>("UpdateButton")!.Content as string == S(r.ActionKey) && S(r.ActionKey) != r.ActionKey,
                    $"the update button says what it does: \"{S(r.ActionKey)}\"");
            check(main.FindControl<Control>("UpdateDot")!.IsVisible == (state.State == UpdateState.Available),
                $"update {state.State}: the dot on the gear only when there is one");
        }
        // A hotfix of the release running is said as one, not as a version of its own.
        var hotfix = $"{AppVersion.Release(App.Version)}.{AppVersion.Hotfix(App.Version) + 1}";
        update.SetValue(session, new UpdateCheck(UpdateState.Available, new AppRelease(hotfix, "", "https://example.invalid",
            new Dictionary<string, string> { [AppUpdate.ExeAsset] = "", [AppUpdate.SumsAsset] = "" })));
        main.Relabel();
        settle(6);
        save(main, "flow-4-update-hotfix");
        check(main.FindControl<TextBlock>("UpdateText")!.Text == App.UpdateOut(hotfix) && !App.UpdateOut(hotfix).Contains("Str."),
            $"a hotfix is offered as one: \"{App.UpdateOut(hotfix)}\"");
        update.SetValue(session, new UpdateCheck(UpdateState.Failed));
    }
}
