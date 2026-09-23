// The library: the grid of games, the search over it, and the ways a game gets into it -- a launcher
// scan, a folder searched for games, one game's folder, or an emulator's.

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmdNr.Core;

namespace AmdNr.App;

public partial class LibraryPage : UserControl
{
    private readonly ObservableCollection<GameCard> _shown = [];
    private MainWindow _shell = null!;

    public LibraryPage()
    {
        InitializeComponent();
        CardList.ItemsSource = _shown;
    }

    private Session Session => _shell.Session;
    private Library Library => _shell.Library;

    public void Attach(MainWindow shell)
    {
        _shell = shell;
        Library.Changed += Refresh;
        Session.BusyChanged += () => ScanButton.IsEnabled = !Session.Busy;
        Refresh();
    }

    /// <summary>The grid filtered by the search, and the two different empties: nothing in the list
    /// wants the ways to add something, a search that matched nothing only wants to be told so.</summary>
    public void Refresh()
    {
        var needle = SearchBox.Text?.Trim() ?? "";
        _shown.Clear();
        foreach (var card in Library.Cards.Where(c => needle.Length == 0
                                                      || c.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)))
            _shown.Add(card);

        var all = Library.Cards.Count;
        var noMatch = all > 0 && _shown.Count == 0;
        EmptyHint.IsVisible = all == 0 || noMatch;
        EmptyActions.IsVisible = !noMatch;
        Ui.Localize(EmptyTitle, noMatch ? "Str.NoMatchTitle" : "Str.EmptyTitle");
        EmptyIcon.Data = Ui.Icon(noMatch ? "IconSearch" : "IconGames");
        if (noMatch) EmptyBody.Text = Ui.Format("Str.NoMatch", needle);
        else Ui.Localize(EmptyBody, "Str.NoGames");
        Foot(all == 0 ? "" : Ui.Format("Str.GameCount", all)
                             + (Library.Away > 0 ? " · " + Ui.Format("Str.GamesAway", Library.Away) : ""));
        if (Library.Away > 0) ToolTip.SetTip(FootText, Ui.Text("Str.GamesAwayHint"));
    }

    public void ShowMachine(SystemState state)
    {
        GpuPillText.Text = state.Gpu;
        GpuDot.Fill = Ui.Brush(state.Ready ? "Ok" : "Err");
    }

    private void Foot(string text) => FootText.Text = text;

    private void OnSearch(object? sender, TextChangedEventArgs e) => Refresh();

    private void OnGpuPill(object? sender, RoutedEventArgs e) => _shell.ShowPage(MainWindow.Page.Machine);

    private void OnCardClick(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: GameCard card }) _shell.OpenSheet(card);
    }

    private void OnScan(object? sender, RoutedEventArgs e) => _shell.Run("scan", ScanAsync);

    /// <summary>Reads every launcher. Read-only -- nothing is installed or changed by looking -- and
    /// the least predictable thing this app does, so it runs off the UI thread and whatever escapes
    /// it is a message, never a closed window.</summary>
    public async Task ScanAsync()
    {
        if (Session.Busy) return;
        Session.Busy = true;
        ScanSpinner.IsVisible = true;
        ScanGlyph.IsVisible = false;
        ScanSpin.IsVisible = true;
        EmptyActions.IsEnabled = false;
        Ui.Localize(ScanLabel, "Str.ScanningShort");
        Foot(Ui.Text("Str.Scanning"));
        try
        {
            var found = await Task.Run(GameScanner.ScanAll);
            await MergeAsync(found);
        }
        finally
        {
            ScanSpinner.IsVisible = false;
            ScanGlyph.IsVisible = true;
            ScanSpin.IsVisible = false;
            EmptyActions.IsEnabled = true;
            Ui.Localize(ScanLabel, "Str.Scan");
            Session.Busy = false;
            Refresh();
        }
    }

    /// <summary>Everything a scan or a folder search turned up, folded into the list: counted, sorted,
    /// detected and reported the same way whichever it was. A scan is also when the list is checked
    /// against the disk again, so a game uninstalled since the app opened goes with it.</summary>
    private async Task MergeAsync(IReadOnlyList<ScannedGame> found)
    {
        var added = Library.Merge(found);
        var gone = await Library.PruneAsync(null);
        await Library.DetectAsync();
        _ = Library.LoadCoversAsync();
        _shell.Toast(Ui.Format("Str.ScanDone", found.Count, added)
                     + (gone.Count > 0 ? " " + Ui.Count("Str.GamesGone", gone.Count) : ""), Level.Ok);
    }

    /// <summary>Every game under one folder: a Games drive, an old library, a copy off another
    /// machine. The search reads a PE header per candidate, so it runs off the UI thread.</summary>
    private void OnSearchFolder(object? sender, RoutedEventArgs e) => _shell.Run("search a folder", async () =>
    {
        if (Session.Busy) return;
        var path = await PickFolderAsync(Ui.Text("Str.AddGamePickLibrary"));
        if (path is null) return;

        Session.Busy = true;
        ScanSpinner.IsVisible = true;
        Foot(Ui.Text("Str.Scanning"));
        try
        {
            var found = await Task.Run(() => GameScanner.UnderFolder(path));
            if (found.Count == 0)
            {
                _shell.Toast(Ui.Format("Str.AddGameNone", Path.GetFileName(path.TrimEnd('\\', '/'))), Level.Warn);
                return;
            }
            await MergeAsync(found);
        }
        finally
        {
            ScanSpinner.IsVisible = false;
            Session.Busy = false;
            Refresh();
        }
    });

    /// <summary>The folder one game runs from.</summary>
    private void OnAddOne(object? sender, RoutedEventArgs e) => _shell.Run("add a game", async () =>
    {
        if (Session.Busy) return;
        var path = await PickFolderAsync(Ui.Text("Str.PickFolder"));
        if (path is null) return;
        if (Library.Find(path) is { } existing)
        {
            _shell.OpenSheet(existing);
            return;
        }

        var entry = new GameEntry
        {
            Path = path,
            Name = Path.GetFileName(path.TrimEnd('\\', '/')),
            Preset = GameScanner.GuessPreset(path),
        };
        var card = Library.Add(entry);
        Library.Redetect(card);
        _shell.OpenSheet(card);
    });

    /// <summary>Adding an emulator, which is the one case where the folder someone picks is usually
    /// the wrong one: the files go beside the emulator, not beside the ROMs. So the flow names the
    /// emulator first and then asks for that emulator's folder, rather than asking for "a folder"
    /// and working out what it was afterwards.</summary>
    private void OnAddEmulator(object? sender, RoutedEventArgs e) => _shell.Run("add an emulator", async () =>
    {
        if (Session.Busy) return;
        var choice = await AskWhichEmulatorAsync();
        if (choice is null) return;
        var wanted = choice == "other" ? null : Emulators.ById(choice);

        var path = await PickFolderAsync(wanted is null
            ? Ui.Text("Str.EmulatorPickOther")
            : Ui.Format("Str.EmulatorPick", wanted.Name));
        if (path is null) return;
        if (Library.Find(path) is { } existing)
        {
            _shell.OpenSheet(existing);
            return;
        }

        // What is actually in there decides, not what was picked from the list: someone who chose
        // PCSX2 and pointed at RPCS3 should get RPCS3, not a wrong route and a silent failure.
        var found = Emulators.Identify(path);
        var card = Library.Add(new GameEntry
        {
            Path = path,
            Name = found?.Name ?? Path.GetFileName(path.TrimEnd('\\', '/')),
            Preset = GameScanner.GuessPreset(path),
        });
        Library.Redetect(card);
        _shell.OpenSheet(card);

        // Say what was actually found, including when it disagrees with what was asked for.
        if (found is not null && (wanted is null || found.Id == wanted.Id))
            _shell.Toast(Ui.Format("Str.EmulatorFound", found.Name, found.System), Level.Ok);
        else if (wanted is not null)
            _shell.Toast(Ui.Format("Str.EmulatorWrong", wanted.Name, string.Join(", ", wanted.Executables.Take(2))), Level.Warn);
        else
            _shell.Toast(Ui.Format("Str.EmulatorUnknown", card.Graphics?.Tag ?? "?"), Level.Info);
    });

    /// <summary>The two with a route of their own, then everything else. Returns an emulator id,
    /// "other", or null when it was dismissed.</summary>
    private Task<string?> AskWhichEmulatorAsync()
    {
        var named = new[] { "pcsx2", "rpcs3" }.Select(Emulators.ById).OfType<EmulatorInfo>().ToList();
        var options = named.Select(e => (e.Id, e.Name, e.System)).ToList();
        // Everything else this app recognises, so "other" is a real list and not a shrug.
        var rest = Emulators.Known.Where(e => named.All(n => n.Id != e.Id)).ToList();
        options.Add(("other", Ui.Text("Str.EmulatorOther"),
            $"{Ui.Text("Str.EmulatorOtherBody")} ({string.Join(", ", rest.Take(6).Select(e => e.Name))}…)"));
        return ChoiceDialog.ShowAsync(_shell, Ui.Text("Str.EmulatorTitle"), Ui.Text("Str.EmulatorBody"), options);
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var picked = await _shell.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });
        var path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        return string.IsNullOrWhiteSpace(path) ? null : path;
    }

    /// <summary>Tiles stretch a little to fill the row, so the grid runs edge to edge with the same
    /// gap everywhere. Wider windows get wider tiles as well as more of them: with a fixed minimum,
    /// a tile at 2560 came out smaller than one at 980, thirteen to a row with their names cut short.
    /// A narrow window gets narrower ones, so more than one row fits on the screen.</summary>
    private void OnGridResized(object? sender, SizeChangedEventArgs e)
    {
        if (CardList.ItemsPanelRoot is not WrapPanel panel) return;
        const double gap = 18, gutter = 24;
        var width = e.NewSize.Width - gutter * 2;
        var minTile = width < 1100 ? 150 : Math.Clamp(width / 10, 168, 220);
        var columns = Math.Max(1, Math.Floor((width + gap) / (minTile + gap)));
        var tile = Math.Floor(Math.Min(minTile + 44, (width - gap * (columns - 1)) / columns));
        panel.ItemWidth = tile + gap;
        // The last column's gap hangs past the edge; the right margin gives it back.
        CardList.Width = columns * (tile + gap);
        CardList.Margin = new Thickness(gutter, 6, gutter - gap, gutter);
        CardList.Resources["TileWidth"] = tile;
        CardList.Resources["CoverHeight"] = Math.Round(tile * 1.5);

        // Below this the top bar does not fit its own buttons, and the GPU's name is the one that
        // can go: the dot stays, and so does the tooltip that says what it is.
        GpuPillText.IsVisible = e.NewSize.Width >= 900;
    }
}
