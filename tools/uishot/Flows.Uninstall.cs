// Uninstall as a person sees it: everything of this app's goes, the tile loses its badge and the
// sheet says Install again -- never Reinstall -- and the one question, about the settings left
// behind, comes only when some were. Run by the flows in Program.cs with OptiScaler installed,
// selected, and its OptiScaler.ini recorded as configuration.

using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AmdNr.App;
using AmdNr.Core;

internal static class UninstallFlow
{
    public static void Run(MainWindow main, GameSheet sheet, GameCard card, string game, Func<string, bool> tileSays,
        Action<bool, string> check, Func<Func<bool>, int, bool> until, Action<Button> click, Action<Window, string> save)
    {
        string S(string key) => main.FindResource(key) as string ?? key;
        T Named<T>(string name) where T : Control => sheet.FindControl<T>(name)!;
        var install = Named<Button>("InstallButton");
        var uninstall = Named<Button>("UninstallButton");
        var label = Named<TextBlock>("InstallLabel");
        var verdict = Named<TextBlock>("ResultTitle");
        var ini = Path.Combine(game, Work.OptiScalerIni);
        bool NoBadge() => !tileSays("OptiScaler") && !tileSays("ReShade") && card.InstalledVia is null;
        Button Choice(string id) => main.OwnedWindows[0].GetVisualDescendants().OfType<Button>().First(b => b.Tag as string == id);

        click(uninstall);
        check(until(() => main.OwnedWindows.Count > 0, 10), "Uninstall asks about the settings it left");
        var dialog = main.OwnedWindows[0];
        until(() => dialog.Bounds.Height > 0, 5);
        check(dialog.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == Work.OptiScalerIni),
            "naming them in the question");
        check(NoBadge(), "asked only once everything of the app's is out: the tile has no badge already");
        check(Fits(dialog), "and the question fits its window");
        save(dialog, "flow-6-uninstall-settings-en");
        click(Choice("keep"));
        check(until(() => !main.Session.Busy, 30) && File.Exists(ini), "Keep my settings keeps them");
        check(NoBadge() && !uninstall.IsVisible && label.Text == S("Str.InstallOpti"),
            $"and the game is not installed: no badge, Install, no Uninstall ({label.Text})");
        check(verdict.Text == string.Format(S("Str.UninstallKeptSettings"), card.Name, Work.OptiScalerIni),
            $"the result says what stayed ({verdict.Text})");
        save(main, "flow-6-uninstalled-kept");

        // The kept settings ride along into the next install, and the next uninstall asks again.
        sheet.FindControl<RadioButton>("RouteReShade")!.IsChecked = true;
        until(() => label.Text == S("Str.Install"), 5);
        click(install);
        check(until(() => !main.Session.Busy && card.InstalledVia == RouteFamily.ReShade, 30), "ReShade installs over the kept settings");
        click(uninstall);
        check(until(() => main.OwnedWindows.Count > 0, 10), "and its uninstall asks about them again");
        click(Choice("remove"));
        check(until(() => !main.Session.Busy, 30) && !File.Exists(ini)
              && !File.Exists(Path.Combine(game, Engine.ManifestNameX64)), "Remove them too leaves nothing, not even the manifest");
        check(NoBadge() && label.Text == S("Str.Install") && verdict.Text == string.Format(S("Str.UninstallClean"), card.Name),
            $"and says so ({verdict.Text})");

        // Nothing left to keep: nothing asked.
        click(install);
        check(until(() => !main.Session.Busy && card.InstalledVia == RouteFamily.ReShade, 30), "ReShade installs on a clean folder");
        click(uninstall);
        check(until(() => !main.Session.Busy, 30) && main.OwnedWindows.Count == 0, "and its uninstall asks nothing");
        check(NoBadge() && label.Text == S("Str.Install"), $"leaving Install, not Reinstall ({label.Text})");

        // The same question in Portuguese, which runs longer, with more files than a real folder has.
        App.ChangeLanguage("pt-BR");
        main.Relabel();
        var (pt, _) = GameSheet.SettingsQuestion(main, "Some Game With A Long Title 01", ["ReShade.ini", "amd-nr.ini", "OptiScaler.ini"]);
        pt.Show(main);
        until(() => pt.Bounds.Height > 0, 5);
        check(Fits(pt), "the question fits its window in Portuguese too");
        save(pt, "flow-6-uninstall-settings-pt");
        pt.Close();
        App.ChangeLanguage("en");
        main.Relabel();
    }

    /// <summary>Every line of text inside the window's width, and nothing scrolled out of sight.</summary>
    private static bool Fits(Window window) =>
        window.GetVisualDescendants().OfType<TextBlock>().Where(t => t.IsEffectivelyVisible && t.Bounds.Width > 0)
            .All(t => t.TranslatePoint(new Point(t.Bounds.Width, t.Bounds.Height), window) is { } p
                      && p.X <= window.Bounds.Width + 0.5 && p.Y <= window.Bounds.Height + 0.5)
        && window.GetVisualDescendants().OfType<ScrollViewer>().All(s => s.Extent.Height <= s.Viewport.Height + 1);
}
