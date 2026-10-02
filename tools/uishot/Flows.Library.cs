// A search is also when the list is checked against the disk again: a game uninstalled since the app
// opened goes with it. The check refused to run while any work was going on, and the search itself is
// work, so it never ran. Driven through "Search a folder", which never leaves the given folder.

using Avalonia.VisualTree;
using AmdNr.App;
using AmdNr.Core;

internal static class LibraryFlow
{
    public static void Run(MainWindow main, byte[] exe, Action<bool, string> check, Func<Func<bool>, int, bool> until)
    {
        var page = main.GetVisualDescendants().OfType<LibraryPage>().First();
        var lib = Path.Combine(AppPaths.Root, "flow-library");
        foreach (var name in new[] { "Kept Game", "Gone Game" })
        {
            Directory.CreateDirectory(Path.Combine(lib, name));
            File.WriteAllBytes(Path.Combine(lib, name, name.Replace(" ", "") + ".exe"), exe);
        }
        bool Listed(string name) => main.Library.Cards.Any(c => c.Name == name);

        var first = page.SearchFolderAsync(lib);
        check(until(() => first.IsCompleted && Listed("Kept Game") && Listed("Gone Game"), 20), "searching a folder adds its games");

        Directory.Delete(Path.Combine(lib, "Gone Game"), recursive: true);
        var again = page.SearchFolderAsync(lib);
        check(until(() => again.IsCompleted && !main.Session.Busy, 20) && !Listed("Gone Game") && Listed("Kept Game"),
            "searching again takes out the game that was uninstalled, and keeps the rest");

        // A game taken out by hand stays out: the next search passes over it, and Settings brings it back.
        var kept = main.Library.Cards.First(c => c.Name == "Kept Game");
        main.Library.Remove(kept);
        check(main.Library.Ignored.Any(i => i.Name == "Kept Game"), "a game taken out of the list is ignored");
        var third = page.SearchFolderAsync(lib);
        check(until(() => third.IsCompleted && !main.Session.Busy, 20) && !Listed("Kept Game"),
            "and searching again leaves it out");
        check(main.Library.Restore(main.Library.Ignored.First(i => i.Name == "Kept Game")) && Listed("Kept Game")
              && !main.Library.Ignored.Any(i => i.Name == "Kept Game"),
            "until it is brought back, which takes it off the ignored list");

        // The default order: favourites, then the games with NR installed, then the rest, by name in each.
        foreach (var name in new[] { "Alpha Pin", "Bravo Pin", "Charlie Pin" })
        {
            Directory.CreateDirectory(Path.Combine(lib, name));
            File.WriteAllBytes(Path.Combine(lib, name, name.Replace(" ", "") + ".exe"), exe);
        }
        var fourth = page.SearchFolderAsync(lib);
        check(until(() => fourth.IsCompleted && !main.Session.Busy && Listed("Charlie Pin"), 20), "three more games to order");
        GameCard Card(string name) => main.Library.Cards.First(c => c.Name == name);
        main.Library.SetFavorite(Card("Charlie Pin"), true);
        Card("Bravo Pin").Installed = true;
        var shown = (System.Collections.Generic.IList<GameCard>)page.GetVisualDescendants().OfType<Avalonia.Controls.ItemsControl>().First(i => i.Name == "CardList").ItemsSource!;
        List<string> Order() => shown.Select(c => c.Name).Where(n => n.EndsWith(" Pin")).ToList();
        check(until(() => Order().SequenceEqual(["Charlie Pin", "Bravo Pin", "Alpha Pin"]), 5),
            "a favourite comes first and an installed game before the rest: " + string.Join(", ", Order()));
        check(GameStore.Load().Any(e => e.Favorite && e.Path.EndsWith("Charlie Pin")), "the favourite is saved with the game");
        main.Library.SetFavorite(Card("Charlie Pin"), false);
        Card("Bravo Pin").Installed = false;
        check(until(() => Order().SequenceEqual(["Alpha Pin", "Bravo Pin", "Charlie Pin"]), 5),
            "unpinned and uninstalled, they go back to the name order");
    }
}
