// Digimon World: Next Order (2026-10-07): a Unity D3D11 game, OptiScaler picked by hand over the leftovers of an
// earlier ReShade install -- ReShade.ini, a ReShade.log of nearly a megabyte, danielblnc's pass and weights -- and
// the app closed right after the install said it had completed. Driven the same way, with the sheet left open while
// everything that follows an install runs: the window has to stay up, and nothing may reach the dispatcher.

using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AmdNr.App;
using AmdNr.Core;

internal static class LeftoversFlow
{
    public static void Run(Action<bool, string> check, Func<Func<bool>, int, bool> until, Action<Button> click,
        Action<Window, string> save)
    {
        var game = Path.Combine(AppPaths.Root, "Digimon World Next Order");
        Directory.CreateDirectory(Path.Combine(game, "Digimon World Next Order_Data"));
        File.WriteAllBytes(Path.Combine(game, "Digimon World Next Order.exe"), Seed.PeImporting(true, "opengl32.dll"));
        File.WriteAllText(Path.Combine(game, "ReShade.ini"), "[GENERAL]\nEffectSearchPaths=.\\reshade-shaders\\Shaders\n");
        File.WriteAllText(Path.Combine(game, "ReShade.log"), string.Concat(Enumerable.Range(0, 9000).Select(i =>
            $"02:5{i % 10}:00:{i % 1000:000} [ 1234] | INFO  | Present {i} with an overlong line about the swap chain\n")));
        File.WriteAllText(Path.Combine(game, Work.RuntimeName), "an earlier route's runtime");
        File.WriteAllText(Path.Combine(game, Work.WeightsName), "an earlier route's weights");
        var entries = GameStore.Load();
        entries.Add(new GameEntry { Path = game, Name = "Digimon World: Next Order", Preset = Preset.OptiScaler, PresetChosen = true });
        GameStore.Save(entries);

        var main = new MainWindow { Width = 1240, Height = 820 };
        var escaped = new List<string>();
        Dispatcher.UIThread.UnhandledException += (_, e) =>
        {
            escaped.Add(e.Exception.ToString());
            e.Handled = true;
        };
        main.Show();
        check(until(() => main.Session.Manifest is not null && main.Library.Cards.Any(c => c.Path == game && c.Graphics is not null), 30),
            "the leftover game is read");
        var card = main.Library.Cards.First(c => c.Path == game);
        main.OpenSheet(card);
        var sheet = main.GetVisualDescendants().OfType<GameSheet>().First();
        check(until(() => sheet.IsOpen && sheet.FindControl<TextBlock>("VerdictTitle")!.Text is { Length: > 0 }, 15), "its sheet opens on OptiScaler");
        click(sheet.FindControl<Button>("InstallButton")!);
        // The leftovers read as the ReShade route installed, so Install asks to switch first, as it did there.
        check(until(() => main.OwnedWindows.Count > 0, 10), "the leftovers read as the ReShade route, and Install asks to switch");
        click(main.OwnedWindows[0].GetVisualDescendants().OfType<Button>().First(b => b.Tag as string == "switch"));
        check(until(() => !main.Session.Busy && card.InstalledVia == RouteFamily.OptiScaler, 30),
            $"OptiScaler installs over the leftovers ({sheet.FindControl<TextBlock>("ResultTitle")!.Text})");
        until(() => false, 3); // what follows an install: the routes, the check, the last session, the tile
        save(main, "flow-leftovers");
        check(main.IsVisible && escaped.Count == 0, $"and the window is still up with nothing escaped{(escaped.Count > 0 ? ": " + escaped[0] : "")}");
        main.Close();
    }
}
