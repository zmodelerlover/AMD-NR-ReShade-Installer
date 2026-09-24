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
    }
}
