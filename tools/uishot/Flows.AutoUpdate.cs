// Auto-update runs as the window opens, before anyone has opened a game's page: the sheet it drives has
// never been laid out. The left column's switch found its sheet by walking up the visual tree, which is
// not there yet, and every start with an auto-update due ended in the startup's catch. Driven the way a
// player hits it: a game with the switch on, out of date, and a window opened fresh.

using AmdNr.App;
using AmdNr.Core;

internal static class AutoUpdateFlow
{
    public static void Run(string game, Action<bool, string> check, Func<Func<bool>, int, bool> until)
    {
        var entries = GameStore.Load();
        var entry = entries.First(e => Engine.SamePath(e.Path, game));
        entry.AutoUpdate = true;
        GameStore.Save(entries);
        Seed.SeedPayload("1.0.9");

        int Installs() => Directory.GetFiles(AppPaths.Logs, "*-install-Flow-Game.log").Length;
        var installs = Installs();
        var log = Path.Combine(AppPaths.Logs, "amd-nr-installer.log");
        var before = File.Exists(log) ? File.ReadAllText(log).Length : 0;
        string Since() => File.Exists(log) ? File.ReadAllText(log)[before..] : "";

        var main = new MainWindow { Width = 1240, Height = 820 };
        main.Show();
        // Either an install ran or the start gave up; before the payload list lands, nothing reads as out of date.
        until(() => (Installs() > installs && !main.Session.Busy) || Since().Contains("startup "), 60);
        check(!Since().Contains("startup "), "a start with an auto-update due does not fail on the way");
        check(Installs() > installs && main.Library.Cards.First(c => Engine.SamePath(c.Path, game)) is { Installed: true, Outdated: false },
            "and brings the game up to date as the window opens");
        main.Close();
    }
}
