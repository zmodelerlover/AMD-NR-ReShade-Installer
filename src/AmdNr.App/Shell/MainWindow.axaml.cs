// The main window: it owns what the pages share -- the session and the library -- starts the
// background work, switches pages, and says things in the corner. The pages do their own work.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AmdNr.Core;

namespace AmdNr.App;

public partial class MainWindow : Window
{
    public enum Page
    {
        Games,
        Machine,
        Settings,
    }

    public Session Session { get; } = new();
    public Library Library { get; }

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private DateTime _lastPrune = DateTime.MinValue;

    public MainWindow()
    {
        Library = new Library(Session);
        InitializeComponent();
        // Two short lines in the rail: "v0.7.5" and, on a hotfix, "hotfix 1".
        VersionText.Text = AppVersion.Hotfix(App.Version) is > 0 and var hotfix
            ? $"v{AppVersion.Release(App.Version)}{Environment.NewLine}{Ui.Format("Str.HotfixShort", hotfix)}"
            : $"v{App.Version}";
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBox.Classes.Set("show", false);
        };

        // What every tile compares its own install against, so a folder installed before the payload
        // moved on says so by itself. On every read of the list, not only the first: after a start
        // offline, the list read by Try again is the one that knows about the newer build. Ahead of
        // the pages, so the sheet's own refresh already sees it.
        Session.ManifestChanged += () =>
        {
            GameCard.Payload = Session.Manifest;
            foreach (var card in Library.Cards) card.RefreshInstalled();
        };
        GamesPage.Attach(this);
        SystemPage.Attach(this);
        SettingsPage.Attach(this);
        Sheet.Attach(this);
        Session.UpdateChanged += ShowUpdate;
        Activated += (_, _) => OnBack();

        // The previous executable, parked by an update. The process that held it has exited by now.
        if (Environment.ProcessPath is { } self) AppUpdater.SweepOld(self);

        Run("startup", StartAsync);
    }

    /// <summary>Everything the window needs from the disk and the network, in the order the screen
    /// can use it: the list first, then what the tiles say about themselves, then the rest.</summary>
    private async Task StartAsync()
    {
        var gone = await Library.LoadAsync();
        if (gone.Count > 0) Toast(GoneMessage(gone), Level.Info);
        if (GameStore.SetAside is { } aside) Toast(Ui.Format("Str.GamesUnreadable", Path.GetFileName(aside)), Level.Warn);
        _lastPrune = DateTime.Now;

        ShowWhatsNewOnce();

        var machine = await Session.ReadMachineAsync();
        GamesPage.ShowMachine(machine);
        SystemPage.Show(machine);

        await Session.LoadManifestAsync();
        await Task.WhenAll(Session.LoadReleasesAsync(), Session.LoadApiDbAsync());
        await Library.DetectAsync();
        _ = Library.LoadCoversAsync();
        _ = Session.CheckForUpdateAsync();
        await AutoUpdateAsync();

        // A first run has nothing in the list, and a scan is what it wants. Reading the launchers is
        // read-only -- nothing is installed or changed by looking -- so it just happens, unless the
        // wizard was told not to: skipping that step and then watching it scan anyway is the app
        // ignoring the only answer it asked for.
        if (Library.Cards.Count == 0 && Library.Away == 0 && !Settings.Load().ScanDeclined)
            await GamesPage.ScanAsync();
    }

    private static string GoneMessage(IReadOnlyList<string> gone) =>
        gone.Count == 1 ? Ui.Format("Str.GameGone", gone[0]) : Ui.Count("Str.GamesGone", gone.Count);

    /// <summary>A release's notes over the page, in the game sheet's frame, under "What's new" and the
    /// release's name.</summary>
    public void ShowNotes(string title, Task<string?> notes, string? url) => Notes.Show(title, notes, url);

    private void OnSupport(object? sender, RoutedEventArgs e) => Support.Show();

    /// <summary>This app's release notes for one version: the ones the update check already read, or
    /// GitHub's.</summary>
    public void ShowWhatsNew(string version, string? known = null)
    {
        var repo = Session.Config.App;
        var tag = $"v{version}";
        ShowNotes($"AMD-NR ReShade Installer v{version}",
            known is not null ? Task.FromResult<string?>(known) : ReleaseNotes.GetAsync(Session.Http, repo.Owner, repo.Repo, tag),
            $"https://github.com/{repo.Owner}/{repo.Repo}/releases/tag/{tag}");
    }

    /// <summary>After this app updated itself, once: what the new version brought. Not on a first run,
    /// where nothing was there before it.</summary>
    private void ShowWhatsNewOnce()
    {
        var settings = Settings.Load();
        if (settings.SeenVersion == App.Version) return;
        var updated = settings.SetupDone;
        settings.SeenVersion = App.Version;
        settings.Save();
        if (updated && WhatsNewOnUpdate) ShowWhatsNew(App.Version);
    }

    /// <summary>Off in the headless renders, where notes nobody closes would sit over every capture.</summary>
    public static bool WhatsNewOnUpdate { get; set; } = true;

    /// <summary>Back to the front after being away: a game uninstalled meanwhile goes from the list
    /// now, not on the next start. Not more often than every few seconds -- alt-tabbing back and
    /// forth should not walk a few hundred folders each time.</summary>

    private void OnBack()
    {
        if (DateTime.Now - _lastPrune < TimeSpan.FromSeconds(10) || Session.Busy) return;
        _lastPrune = DateTime.Now;
        Run("recheck", async () =>
        {
            var gone = await Library.PruneAsync(Sheet.Card);
            if (gone.Count > 0) Toast(GoneMessage(gone), Level.Info);
            // Games back from a drive that returned have not been read yet; nothing to do otherwise.
            await Library.DetectAsync();
            // Back from a game, most likely: when each game last ran, and how the open one's session went.
            foreach (var card in Library.Cards) card.RefreshInstalled();
            GamesPage.Refresh();
            if (Sheet.IsOpen && Sheet.Card is { } open) Sheet.ShowSession(open);
        });
    }

    /// <summary>The games whose automatic update is on and that the payload list has moved past, updated
    /// one after another through their sheet, once the list has been read. Anything else running first wins.</summary>
    private async Task AutoUpdateAsync()
    {
        var due = Library.Cards.Where(c => c.Entry.AutoUpdate && c.Outdated).ToList();
        var done = 0;
        foreach (var card in due)
        {
            if (Session.Busy) break;
            if (await Sheet.UpdateAsync(card)) done++;
        }
        if (done > 0) Toast(Ui.Count("Str.AutoUpdated", done), Level.Ok);
    }

    public void ShowPage(Page page)
    {
        (page switch { Page.Machine => TabSystem, Page.Settings => TabSettings, _ => TabGames }).IsChecked = true;
    }

    private void OnTabChanged(object? sender, RoutedEventArgs e)
    {
        if (GamesPage is null) return; // Fires once while the window is still being built.
        // Decided by the button just checked: the one it replaces is not unchecked yet when this runs.
        GamesPage.IsVisible = sender == TabGames;
        SystemPage.IsVisible = sender == TabSystem;
        SettingsPage.IsVisible = sender == TabSettings;
        // Docked, the sheet is part of the games page and goes out of sight with it.
        if (sender != TabGames && Sheet.IsOpen && !Sheet.Docked) Sheet.Close();
        // The notes belong to the moment they were opened in, not to a page: any switch closes them.
        if (Notes.IsOpen) Notes.Close();
        if (Support.IsOpen) Support.Close();
        if (sender == TabSystem) Run("files", SystemPage.RefreshAsync);
    }

    /// <summary>Opens a game's sheet. While something is running the sheet belongs to the game it
    /// is running for -- closed or not -- and switching it away would put that result, and another
    /// game's route and version, on it.</summary>
    public void OpenSheet(GameCard card)
    {
        if (Session.Busy && Sheet.Card is { } working && working != card)
        {
            Toast(Ui.Text("Str.Working"), Level.Info);
            return;
        }
        ShowPage(Page.Games);
        Sheet.Open(card);
        GamesPage.Select(card);
    }

    /// <summary>Moves the sheet into <paramref name="host"/> -- the list view's page column -- or, with
    /// null, back over the pages where it floats over the grid. The control itself moves, so what it
    /// shows, and an install it is running, carry over.</summary>
    public void DockSheet(Panel? host)
    {
        var target = host ?? Pages;
        if (Sheet.Parent == target) return;
        (Sheet.Parent as Panel)?.Children.Remove(Sheet);
        // Over the pages it goes under the toast, where it was built.
        if (host is null) Pages.Children.Insert(Pages.Children.IndexOf(ToastBox), Sheet);
        else host.Children.Add(Sheet);
        Sheet.SetDocked(host is not null);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Notes.IsOpen)
        {
            Notes.Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && Support.IsOpen)
        {
            Support.Close();
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Escape && Sheet.IsOpen && !Sheet.Docked)
        {
            Sheet.Close();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowFit.ToScreen(this);
    }

    /// <summary>Not while something writes to a game. Closing ends the process, and an install or an
    /// uninstall cut off inside its transaction leaves a folder that can be neither reinstalled nor
    /// uninstalled without deleting the manifest by hand. It takes seconds; the corner says to wait.
    /// A download can be closed on at any point, and Windows shutting down is not stopped: that is
    /// not a question this app gets to answer.</summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (Session.Writing && e.CloseReason != WindowCloseReason.OSShutdown)
        {
            e.Cancel = true;
            Toast(Ui.Text("Str.CloseWhileBusy"), Level.Warn);
        }
        base.OnClosing(e);
    }

    /// <summary>Everything written in code in the old language, written again in the new one.</summary>
    public void Relabel()
    {
        GamesPage.Refresh();
        SettingsPage.ShowUpdate();
        ShowUpdate();
        if (Session.Machine is { } machine) SystemPage.Show(machine);
        Run("files", SystemPage.RefreshAsync);
        if (Sheet.Card is { } card) Sheet.Show(card);
    }

    public void Toast(string text, Level level)
    {
        ToastText.Text = text;
        ToastIcon.Data = Ui.Glyph(level);
        ToastIcon.Foreground = Ui.LevelBrush(level);
        ToastBox.Classes.Set("show", true);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>Runs an action from a click. Every one of these is async void underneath, and an
    /// exception escaping one is the window closing: so nothing escapes. What does get this far is
    /// logged with its type, and said.</summary>
    public async void Run(string what, Func<Task> work)
    {
        try { await work(); }
        catch (Exception e)
        {
            InstallLog.Append($"{DateTime.Now:s} {what}  {e.GetType().Name}: {e.Message}\n{e.StackTrace}");
            Toast(Ui.Text("Str.Unexpected"), Level.Err);
        }
    }

    // -- Updates -----------------------------------------------------------------------------------

    private void ShowUpdate()
    {
        var release = Session.Update is { State: UpdateState.Available, Release: { } found } ? found : null;
        UpdateDot.IsVisible = release is not null;
        if (release is null) UpdateBanner.IsVisible = false;
        else if (!_dismissed)
        {
            UpdateText.Text = App.UpdateOut(release.Version);
            UpdateButton.Content = Ui.Text(release.ActionKey);
            UpdateBanner.IsVisible = true;
        }
    }

    private bool _dismissed;

    private void OnDismissUpdate(object? sender, RoutedEventArgs e)
    {
        _dismissed = true;
        UpdateBanner.IsVisible = false;
    }

    private void OnUpdate(object? sender, RoutedEventArgs e) => Run("update", async () =>
    {
        UpdateButton.IsEnabled = false;
        try { await ApplyUpdateAsync(p => UpdateText.Text = $"{Ui.Text("Str.UpdateWorking")} {p * 100:0}%"); }
        finally { UpdateButton.IsEnabled = true; }
    });

    /// <summary>Installs the newer release in place, or opens its page when it does not publish what
    /// that needs. Nothing is replaced unless it hashes to what the release publishes; a failure
    /// leaves this executable exactly where it was, and says why.</summary>
    public async Task ApplyUpdateAsync(Action<double> report)
    {
        if (Session.Busy)
        {
            Toast(Ui.Text("Str.Working"), Level.Info);
            return;
        }
        try
        {
            if (await Session.ApplyUpdateAsync(new Progress<double>(report))) Close();
        }
        catch (Exception e) when (e is HttpRequestException or InstallException or IOException
                                      or TaskCanceledException or UnauthorizedAccessException)
        {
            Toast(e.Message, Level.Warn);
            ShowUpdate();
        }
    }
}
