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
        VersionText.Text = $"v{App.Version}";
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBox.Classes.Set("show", false);
        };

        GamesPage.Attach(this);
        SystemPage.Attach(this);
        SettingsPage.Attach(this);
        Sheet.Attach(this);
        Session.UpdateChanged += ShowUpdate;
        Activated += (_, _) => OnBack();

        // The previous executable, parked by an update. The process that held it has exited by now.
        if (Environment.ProcessPath is { } self && Path.GetDirectoryName(self) is { } home)
            AppUpdater.SweepOld(home);

        Run("startup", StartAsync);
    }

    /// <summary>Everything the window needs from the disk and the network, in the order the screen
    /// can use it: the list first, then what the tiles say about themselves, then the rest.</summary>
    private async Task StartAsync()
    {
        var gone = await Library.LoadAsync();
        if (gone.Count > 0) Toast(GoneMessage(gone), Level.Info);
        _lastPrune = DateTime.Now;

        var machine = await Session.ReadMachineAsync();
        GamesPage.ShowMachine(machine);
        SystemPage.Show(machine);

        await Session.LoadManifestAsync();
        // What every tile compares its own install against, so a folder installed before the payload
        // moved on says so by itself.
        GameCard.Payload = Session.Manifest;
        foreach (var card in Library.Cards) card.RefreshInstalled();

        await Task.WhenAll(Session.LoadReleasesAsync(), Session.LoadApiDbAsync());
        await Library.DetectAsync();
        _ = Library.LoadCoversAsync();
        _ = Session.CheckForUpdateAsync();

        // A first run has nothing in the list, and a scan is what it wants. Reading the launchers is
        // read-only -- nothing is installed or changed by looking -- so it just happens, unless the
        // wizard was told not to: skipping that step and then watching it scan anyway is the app
        // ignoring the only answer it asked for.
        if (Library.Cards.Count == 0 && Library.Away == 0 && !Settings.Load().ScanDeclined)
            await GamesPage.ScanAsync();
    }

    private static string GoneMessage(IReadOnlyList<string> gone) =>
        gone.Count == 1 ? Ui.Format("Str.GameGone", gone[0]) : Ui.Count("Str.GamesGone", gone.Count);

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
        });
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
        if (sender != TabGames && Sheet.IsOpen) Sheet.Close();
        if (sender == TabSystem) Run("files", SystemPage.RefreshAsync);
    }

    /// <summary>Opens a game's sheet. While something is running the sheet belongs to the game it
    /// is running for: switching it away would put that result on another game.</summary>
    public void OpenSheet(GameCard card)
    {
        if (Session.Busy && Sheet.IsOpen && Sheet.Card != card)
        {
            Toast(Ui.Text("Str.Working"), Level.Info);
            return;
        }
        ShowPage(Page.Games);
        Sheet.Open(card);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Sheet.IsOpen)
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
        var available = Session.Update is { State: UpdateState.Available, Release: { } release };
        UpdateDot.IsVisible = available;
        if (!available) UpdateBanner.IsVisible = false;
        else if (!_dismissed)
        {
            UpdateText.Text = Ui.Format("Str.UpdateOut", Session.Update.Release!.Version, App.Version);
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
        if (Session.Busy) return;
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
