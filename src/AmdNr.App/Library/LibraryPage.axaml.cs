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
        GameList.ItemsSource = _shown;
    }

    /// <summary>The list view: games down the left, the open one's page beside them.</summary>
    private bool _list;

    /// <summary>Set while the code moves the list's selection, which is not a person picking a game.</summary>
    private bool _selecting;

    private Session Session => _shell.Session;
    private Library Library => _shell.Library;

    public void Attach(MainWindow shell)
    {
        _shell = shell;
        Library.Changed += Refresh;
        Session.BusyChanged += () => ScanButton.IsEnabled = !Session.Busy;
        var settings = Settings.Load();
        _list = settings.LibraryView == "list";
        _filter = settings.LibraryFilter ?? "all";
        _sort = settings.LibrarySort ?? "name";
        ShowFilter();
        Refresh();
        ApplyView();
    }

    private string _filter = "all";
    private string _sort = "name";

    private void OnFilter(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string filter }) return;
        _filter = filter;
        var settings = Settings.Load();
        settings.LibraryFilter = filter == "all" ? null : filter;
        settings.Save();
        ShowFilter();
        Refresh();
    }

    private void OnSort(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string sort }) return;
        _sort = sort;
        var settings = Settings.Load();
        settings.LibrarySort = sort == "name" ? null : sort;
        settings.Save();
        ShowFilter();
        Refresh();
    }

    private void ShowFilter()
    {
        foreach (var item in new[] { FilterAll, FilterInstalled, FilterUpdates, FilterNotInstalled, FilterEmulators })
            item.IsChecked = (string?)item.Tag == _filter;
        foreach (var item in new[] { SortName, SortRecent, SortAdded })
            item.IsChecked = (string?)item.Tag == _sort;
        // Lit while games are hidden, so an empty-looking library never keeps a filter secret.
        FilterButton.Classes.Set("on", _filter != "all");
    }

    private bool Shows(GameCard card) => _filter switch
    {
        "installed" => card.Installed,
        "updates" => card.Outdated,
        "notinstalled" => !card.Installed,
        "emulators" => card.Graphics?.Emulator is not null,
        _ => true,
    };

    /// <summary>The library already keeps its games by name, so that order needs nothing; the others sort a
    /// copy, with the name settling ties and games that never ran, or came before the date was kept, last.</summary>
    private IEnumerable<GameCard> Ordered(IEnumerable<GameCard> cards) => _sort switch
    {
        "recent" => cards.OrderByDescending(c => c.LastPlayed ?? DateTime.MinValue),
        "added" => cards.OrderByDescending(c => c.Entry.Added ?? DateTime.MinValue),
        _ => cards,
    };

    private void OnGridView(object? sender, RoutedEventArgs e) => SetView(list: false);

    private void OnListView(object? sender, RoutedEventArgs e) => SetView(list: true);

    private void SetView(bool list)
    {
        if (_list == list) return;
        _list = list;
        var settings = Settings.Load();
        settings.LibraryView = list ? "list" : null;
        settings.Save();
        // The sheet of a game open over the grid is a page of the list now, and the other way round
        // a floating sheet nobody asked for: it closes when the grid comes back.
        if (!list && _shell.Sheet.IsOpen && !Session.Busy) _shell.Sheet.Close();
        ApplyView();
    }

    private void ApplyView()
    {
        GridScroll.IsVisible = !_list;
        ListLayout.IsVisible = _list;
        GridViewButton.Classes.Set("on", !_list);
        ListViewButton.Classes.Set("on", _list);
        _shell.DockSheet(_list ? DockHost : null);
        if (_list) KeepSelection();
    }

    /// <summary>In the list view a game is always open: the one the sheet shows, or else the first.</summary>
    private void KeepSelection()
    {
        if (!_list || _shown.Count == 0) return;
        var open = _shell.Sheet.Card is { } card && _shown.Contains(card) ? card : null;
        if (open is null || !_shell.Sheet.IsOpen) _shell.OpenSheet(open ?? _shown[0]);
        else Select(open);
    }

    /// <summary>The list's selection brought in line with the game whose sheet is open.</summary>
    public void Select(GameCard card)
    {
        if (!_list || GameList.SelectedItem == card) return;
        _selecting = true;
        GameList.SelectedItem = card;
        GameList.ScrollIntoView(card);
        _selecting = false;
    }

    private void OnListSelection(object? sender, SelectionChangedEventArgs e)
    {
        if (_selecting || GameList.SelectedItem is not GameCard card || card == _shell.Sheet.Card) return;
        _shell.OpenSheet(card);
        // Refused while another game's install runs: the list goes back to the game that has the sheet.
        if (_shell.Sheet.Card is { } open && open != card) Select(open);
    }

    /// <summary>The grid filtered by the search, and the two different empties: nothing in the list
    /// wants the ways to add something, a search that matched nothing only wants to be told so.</summary>
    public void Refresh()
    {
        var needle = SearchBox.Text?.Trim() ?? "";
        var wanted = Ordered(Library.Cards.Where(c => Shows(c) && (needle.Length == 0
                                              || c.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)))).ToList();

        // Brought in line, not rebuilt. A tile is a few controls deep, and clearing the grid built
        // every one of them again: over a second for each key typed in the search with a few hundred
        // games, and the same again on every language switch. Only the tiles that come or go are.
        var keep = wanted.ToHashSet();
        for (var i = _shown.Count - 1; i >= 0; i--)
            if (!keep.Contains(_shown[i])) _shown.RemoveAt(i);
        for (var i = 0; i < wanted.Count; i++)
            if (i >= _shown.Count || _shown[i] != wanted[i]) _shown.Insert(i, wanted[i]);
        while (_shown.Count > wanted.Count) _shown.RemoveAt(_shown.Count - 1);
        if (_shell is not null) KeepSelection();

        var all = Library.Cards.Count;
        var noMatch = all > 0 && _shown.Count == 0;
        EmptyHint.IsVisible = all == 0 || noMatch;
        EmptyActions.IsVisible = !noMatch;
        Ui.Localize(EmptyTitle, noMatch ? "Str.NoMatchTitle" : "Str.EmptyTitle");
        EmptyIcon.Data = Ui.Icon(noMatch ? "IconSearch" : "IconGames");
        if (noMatch && needle.Length == 0) Ui.Localize(EmptyBody, "Str.NoFilterMatch");
        else if (noMatch) EmptyBody.Text = Ui.Format("Str.NoMatch", needle);
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

    // The buttons a tile shows under the pointer: Play and Configure once the mod is in, Install before.

    private void OnTilePlay(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Session.Busy || sender is not Button { Tag: GameCard card }) return;
        if (card.Graphics is null) Library.Apply(card, Library.Detect(card));
        GameSheet.Play(card, _shell);
    }

    private void OnTileConfigure(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (sender is Button { Tag: GameCard card }) _shell.OpenSheet(card);
    }

    private void OnTileInstall(object? sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (Session.Busy || sender is not Button { Tag: GameCard card }) return;
        _shell.OpenSheet(card);
        if (_shell.Sheet.Card == card) _shell.Sheet.Install();
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
        var gone = await Library.PruneAsync(_shell.Sheet.Card, scanning: true);
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
        if (path is not null) await SearchFolderAsync(path);
    });

    public async Task SearchFolderAsync(string path)
    {
        if (Session.Busy) return;
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
    }

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

    /// <summary>The two with a route of their own and the PlayStation 4 and 5 ones, then everything
    /// else. Returns an emulator id, "other", or null when it was dismissed.</summary>
    private Task<string?> AskWhichEmulatorAsync()
    {
        var named = new[] { "pcsx2", "rpcs3", "shadps4", "kyty" }.Select(Emulators.ById).OfType<EmulatorInfo>().ToList();
        var options = named.Select(e => (e.Id, e.Name, e.System)).ToList();
        // Everything else this app recognises, so "other" is a real list and not a shrug. FiveM is not
        // an emulator, and Scan finds it where its installer puts it.
        var rest = Emulators.Known.Where(e => named.All(n => n.Id != e.Id) && e.Route != Preset.FiveM).ToList();
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
