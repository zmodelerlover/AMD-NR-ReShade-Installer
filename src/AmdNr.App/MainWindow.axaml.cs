using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AmdNr.Core;

namespace AmdNr.App;

/// <summary>One line of a report, with the colour it reads in. The engine already decided the
/// level; this only chooses how it looks.</summary>
public sealed record ReportLine(Geometry? Glyph, string Text, IBrush Brush);

/// <summary>One downloadable component as the System page lists it.</summary>
public sealed record PayloadRow(string Name, string Version, string Size, bool Ready);

/// <summary>One installable version of the add-on, as the sheet offers it. A null release means
/// the version the payload manifest pins, which is the one this build was published with.</summary>
public sealed record VersionChoice(Version Version, string Label, AddonRelease? Release);

public partial class MainWindow : Window
{
    private readonly AppConfig _config = AppConfig.Load();
    private readonly HttpClient _http = PayloadCache.DefaultClient(App.Version);
    private readonly List<GameCard> _all = [];
    private readonly ObservableCollection<GameCard> _shown = [];
    private readonly ObservableCollection<ReportLine> _report = [];
    private readonly List<Preset> _offered = [];

    /// <summary>What the width detection said about the selected game, kept so the route note can
    /// say when the chosen route disagrees with it.</summary>
    private Detected _detected = Detected.Unknown;

    private PayloadManifest? _manifest;
    private IReadOnlyList<AddonRelease> _releases = [];
    private readonly List<VersionChoice> _versions = [];
    private VersionChoice? _version;
    private bool _settingVersion;
    private string? _proxy;
    private bool _settingProxy;
    private ApiDatabase? _apiDb;
    private AppRelease? _update;
    private GameCard? _selected;
    private bool _busy;
    private bool _settingPreset;

    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    // The install steps, in the order the chips sit in the drawer.
    private const int StepDownload = 0, StepVerify = 1, StepInstall = 2, StepDone = 3;

    public MainWindow()
    {
        InitializeComponent();

        CardList.ItemsSource = _shown;
        ReportList.ItemsSource = _report;
        _report.CollectionChanged += (_, _) =>
        {
            ReportEmpty.IsVisible = _report.Count == 0;
            DetailsCount.Text = _report.Count == 0 ? "" : string.Format(Text("Str.DetailsCount"), _report.Count);
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Classes.Set("show", false);
        };
        VersionText.Text = $"v{App.Version}";
        AboutVersion.Text = $"v{App.Version}";
        DataFolderText.Text = AppPaths.Root;
        ToolTip.SetTip(DataFolderText, AppPaths.Root);
        ToolTip.SetTip(CacheFolder, AppPaths.Cache);
        CacheFolder.Text = AppPaths.Cache;

        LanguageBox.ItemsSource = App.Languages.Select(l => l.Name).ToList();
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(App.Languages, l => l.Code == App.CurrentLanguage));

        foreach (var game in GameStore.Load())
        {
            // The install state is read from the folder, never stored: something could have been
            // installed or removed by hand since the last run.
            var card = new GameCard(game);
            card.RefreshInstalled();
            _all.Add(card);
        }
        RefreshGrid();
        ShowSystem();

        // The previous executable, parked by an update. The process that held it has exited by now.
        if (Environment.ProcessPath is { } self && Path.GetDirectoryName(self) is { } home)
            AppUpdater.SweepOld(home);

        _ = StartBackgroundWorkAsync();
    }

    // -- Startup ---------------------------------------------------------------------------------

    private async Task StartBackgroundWorkAsync()
    {
        var cache = new PayloadCache(_http);
        try
        {
            _manifest = await cache.FetchManifestAsync(
                _config.Payload.Owner, _config.Payload.Repo, _config.Payload.Branch, _config.Payload.File,
                _config.Payload.ManifestUrl);
        }
        catch (Exception e) when (e is HttpRequestException or InstallException or TaskCanceledException)
        {
            // Offline, or the content repository is not up yet: the copy beside the executable pins
            // the same hashes, so everything still works against whatever is already cached.
            _manifest = PayloadCache.LoadLocalManifest();
        }

        await ShowPayloadStateAsync();

        // Which versions of the add-on can be installed. One call to GitHub, kept in the cache,
        // and the sheet falls back to the version the manifest pins when it comes back empty.
        _releases = await AddonReleases.ListAsync(_http, _config.Addon.Owner, _config.Addon.Repo);

        // Which API each game supports: the database the content repository publishes, then the
        // last copy fetched, then the one shipped beside the exe. Detection runs either way; the
        // database only corrects what a game's own files get wrong.
        _apiDb = await ApiDatabase.LoadAsync(_http, _config.Payload.Owner, _config.Payload.Repo,
            _config.Payload.Branch, _config.Payload.ApiDbUrl);
        await DetectAllAsync();
        await LoadCoversAsync();

        _update = await AppUpdate.CheckAsync(_http, _config.App, App.Version);
        if (_update is not null)
        {
            UpdateText.Text = $"{Text("Str.UpdateAvailable")} — v{_update.Version}";
            UpdateBanner.IsVisible = true;
        }

        // A first run has nothing in the list, and a scan is what it wants. Reading the launchers
        // is read-only -- nothing is installed or changed by looking -- so it just happens, unless
        // the wizard was told not to: skipping that step and then watching it scan anyway is the
        // app ignoring the only answer it asked for.
        if (_all.Count == 0 && !Settings.Load().ScanDeclined) OnScan(null, new RoutedEventArgs());
    }

    private void ShowSystem()
    {
        var state = GpuService.Read();
        GpuText.Text = state.Gpu;
        DriverText.Text = state.Driver;
        SetStatus(GpuStatus, GpuStatusIcon, state.LooksLikeRadeon);
        Localize(GpuStatusText, state.LooksLikeRadeon ? "Str.GpuOk" : "Str.GpuBad");
        SetStatus(HipStatus, HipStatusIcon, state.Hip7);
        Localize(HipText, state.Hip7 ? "Str.Ready" : "Str.Missing");

        GpuPillText.Text = state.Gpu;
        GpuDot.Fill = state.Ready ? Brush("Ok") : Brush("Err");

        // One sentence on top that says whether anything on this page needs doing.
        SystemVerdict.Classes.Set("ok", state.Ready);
        SystemVerdict.Classes.Set("err", !state.Ready);
        SystemVerdictTile.Classes.Set("ok", state.Ready);
        SystemVerdictTile.Classes.Set("err", !state.Ready);
        SystemVerdictIcon.Data = Vector(state.Ready ? "IconOk" : "IconErr");
        Localize(SystemVerdictTitle, state.Ready ? "Str.SystemReady" : "Str.SystemNotReady");

        if (!state.Hip7) ShowWarning(Text("Str.HipMissing"));
        else if (!state.LooksLikeRadeon) ShowWarning(Text("Str.NotRadeon"));
        else SystemWarning.IsVisible = false;
    }

    private static void SetStatus(Border pill, PathIcon icon, bool ok)
    {
        pill.Classes.Set("ok", ok);
        pill.Classes.Set("err", !ok);
        icon.Data = Vector(ok ? "IconCheck" : "IconErr");
    }

    private void ShowWarning(string text)
    {
        SystemWarning.Text = text;
        SystemWarning.IsVisible = true;
    }

    /// <summary>What is in the cache, and what it would still cost to fill it. Deciding that means
    /// hashing 141 MB of weights, which is about a second, so it runs off the UI thread -- on the
    /// thread it used to run on, switching to this page froze the window for that second.</summary>
    private async Task ShowPayloadStateAsync()
    {
        var manifest = Selected();
        PayloadState.IsVisible = manifest is null;
        PrefetchButton.IsEnabled = manifest is not null && !_busy;
        if (manifest is null)
        {
            PayloadState.Text = Text("Str.ManifestFailed");
            PayloadSummary.IsVisible = false;
            PayloadList.ItemsSource = null;
            return;
        }

        var rows = await Task.Run(() => manifest.Components.Select(pair =>
        {
            var complete = false;
            try { complete = PayloadCache.IsComplete(manifest, pair.Key); }
            catch (Exception e) when (e is InstallException or IOException)
            {
                // A component this build does not understand is not a reason to show nothing.
            }
            return new PayloadRow(pair.Key, pair.Value.Version, Megabytes(Bytes(pair.Value)), complete);
        }).ToList());

        PayloadList.ItemsSource = rows;

        var ready = rows.Count(r => r.Ready);
        var cached = manifest.Components
            .Where(pair => rows.Any(r => r.Name == pair.Key && r.Ready))
            .Aggregate(0UL, (sum, pair) => sum + Bytes(pair.Value));
        var missing = manifest.Components
            .Where(pair => rows.Any(r => r.Name == pair.Key && !r.Ready))
            .Aggregate(0UL, (sum, pair) => sum + Bytes(pair.Value));

        PayloadSummary.Text = ready == rows.Count
            ? string.Format(Text("Str.PayloadsAllReady"), ready, rows.Count, Megabytes(cached))
            : string.Format(Text("Str.PayloadsMissing"), ready, rows.Count, Megabytes(missing));
        PayloadSummary.IsVisible = rows.Count > 0;
    }

    private static ulong Bytes(PayloadComponent component) =>
        component.Files.Aggregate(0UL, (sum, file) => sum + file.Size);

    /// <summary>A size someone can compare against their connection. Whole megabytes truncate every
    /// component but the weights to "0 MB", which reads as "nothing to download".</summary>
    private static string Megabytes(ulong bytes) => bytes switch
    {
        >= 10 * 1_048_576 => $"{bytes / 1_048_576} MB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    /// <summary>Fetch every component now rather than at the first install that needs one. The cache
    /// is keyed by component and version and shared by every game, so this is paid once: the 141 MB
    /// of weights is the same file for all of them, and an install afterwards only verifies and
    /// copies. Anything already there and already hashing correctly is not fetched again, so this is
    /// also how a half-finished download is finished.</summary>
    private async void OnPrefetch(object? sender, RoutedEventArgs e)
    {
        var manifest = Selected();
        if (_busy || manifest is null) return;

        Busy(true);
        PrefetchButton.IsEnabled = false;
        PayloadProgressBox.IsVisible = true;
        PayloadProgress.IsIndeterminate = true;
        PayloadProgressText.Text = Text("Str.Verifying");

        var cache = new PayloadCache(_http);
        try
        {
            // What is actually missing, hashed off the UI thread. Clicking with a full cache is a
            // re-check of every byte, and it says so rather than looking like it did nothing.
            var pending = await Task.Run(() => manifest.Components.Keys
                .Where(name =>
                {
                    try { return !PayloadCache.IsComplete(manifest, name); }
                    catch (Exception e2) when (e2 is InstallException or IOException) { return false; }
                })
                .ToList());

            if (pending.Count == 0)
            {
                ShowToast(Text("Str.PayloadsAlreadyThere"), Level.Ok);
                Status(Text("Str.PayloadsReady"));
                return;
            }

            // One bar for the whole set, not one per file: six files of wildly different sizes each
            // running 0 to 100 tells nobody how much of the download is left.
            var sizes = pending.SelectMany(name => manifest.Component(name).Files)
                .GroupBy(file => file.Name)
                .ToDictionary(group => group.Key, group => group.First().Size);
            var total = sizes.Values.Aggregate(0UL, (sum, size) => sum + size);
            var finished = 0UL;
            var current = "";

            PayloadProgress.IsIndeterminate = false;
            var progress = new Progress<DownloadProgress>(p => Dispatcher.UIThread.Post(() =>
            {
                if (p.File != current)
                {
                    // A file only stops being reported when it is done, or when a mirror restarts
                    // it under the same name -- which leaves this untouched, as it should.
                    if (current.Length > 0 && sizes.TryGetValue(current, out var done)) finished += done;
                    current = p.File;
                }
                var received = finished + (ulong)Math.Max(0, p.Received);
                PayloadProgress.Value = total == 0 ? 0 : Math.Min(100, 100.0 * received / total);
                PayloadProgressText.Text =
                    $"{p.File} — {Megabytes(received)} / {Megabytes(total)}";
                Status($"{Text("Str.Downloading")} {p.File}");
            }));

            foreach (var component in pending)
                await cache.EnsureAsync(manifest, component, progress);

            PayloadProgress.Value = 100;
            ShowToast(Text("Str.PayloadsReady"), Level.Ok);
            Status(Text("Str.PayloadsReady"));
        }
        catch (Exception ex) when (ex is InstallException or HttpRequestException or IOException)
        {
            // The rows below say which ones did land, so the message is the reason and not a list.
            PayloadProgressText.Text = ex.Message;
            ShowToast(Text("Str.DownloadFailed"), Level.Err);
            Status(ex.Message);
        }
        finally
        {
            PayloadProgressBox.IsVisible = false;
            Busy(false);
            // Re-read rather than assume: a component that failed has to keep saying so.
            await ShowPayloadStateAsync();
        }
    }

    // -- The library -----------------------------------------------------------------------------

    private void RefreshGrid()
    {
        var needle = SearchBox.Text?.Trim() ?? "";
        _shown.Clear();
        foreach (var card in _all.Where(c => needle.Length == 0
                                             || c.Name.Contains(needle, StringComparison.CurrentCultureIgnoreCase)))
            _shown.Add(card);

        // Two different empties: nothing scanned yet wants the actions, a search that matched
        // nothing only wants to be told so.
        var noMatch = _all.Count > 0 && _shown.Count == 0;
        EmptyHint.IsVisible = _all.Count == 0 || noMatch;
        EmptyActions.IsVisible = !noMatch;
        Localize(EmptyTitle, noMatch ? "Str.NoMatchTitle" : "Str.EmptyTitle");
        EmptyIcon.Data = Vector(noMatch ? "IconSearch" : "IconGames");
        if (noMatch) EmptyBody.Text = string.Format(Text("Str.NoMatch"), needle);
        else Localize(EmptyBody, "Str.NoGames");
        Foot(_all.Count == 0 ? "" : string.Format(Text("Str.GameCount"), _all.Count));
    }

    private void OnSearch(object? sender, TextChangedEventArgs e) => RefreshGrid();

    private async void OnScan(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ScanSpinner.IsVisible = true;
        Foot(Text("Str.Scanning"));
        ScanButton.IsEnabled = false;
        EmptyActions.IsEnabled = false;
        ScanGlyph.IsVisible = false;
        ScanSpin.IsVisible = true;
        Localize(ScanLabel, "Str.ScanningShort");
        try
        {
            IReadOnlyList<ScannedGame> found;
            try { found = await Task.Run(GameScanner.ScanAll); }
            catch (Exception ex)
            {
                // Reading someone else's launcher data is the least predictable thing this app does,
                // and this is an async void handler: an escape here is a dead window, not an error.
                WriteLog(Failure(ex), "scan");
                ShowToast(Text("Str.Unexpected"), Level.Err);
                return;
            }

            await MergeFoundAsync(found);
        }
        finally
        {
            ScanSpinner.IsVisible = false;
            ScanButton.IsEnabled = !_busy;
            EmptyActions.IsEnabled = true;
            ScanGlyph.IsVisible = true;
            ScanSpin.IsVisible = false;
            Localize(ScanLabel, "Str.Scan");
        }
    }

    /// <summary>Everything a scan turned up, folded into the list. Shared by the launcher scan and
    /// by searching one folder, so both count, sort, detect and report the same way.</summary>
    private async Task MergeFoundAsync(IReadOnlyList<ScannedGame> found)
    {
        var added = 0;
        foreach (var game in found)
        {
            // A folder that is already in the list keeps the route the person chose for it.
            if (_all.Any(c => Engine.SamePath(c.Path, game.InstallPath))) continue;
            _all.Add(new GameCard(GameEntry.From(game)));
            added++;
        }

        _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        Save();
        RefreshGrid();
        foreach (var card in _all) card.RefreshInstalled();
        Foot($"{found.Count} {Text("Str.Found")} · {added} {Text("Str.Added")}");
        await DetectAllAsync();
        await LoadCoversAsync();
        // Said once the button stops spinning, so the toast and the button agree it is over.
        ShowToast(string.Format(Text("Str.ScanDone"), found.Count, added), Level.Ok);
    }

    /// <summary>Reads every game's graphics API off the UI thread. It only opens files -- headers and
    /// import tables -- so a library of a few hundred games takes seconds, not minutes, and it works
    /// offline.</summary>
    private async Task DetectAllAsync()
    {
        var pending = _all.Where(c => c.Graphics is null).ToList();
        foreach (var card in pending)
        {
            var db = _apiDb;
            var detection = await Task.Run(() =>
                GraphicsDetector.Detect(card.Path, card.Entry.Name, card.Entry.Executable)
                    .With(db?.Lookup(card.Entry.AppId, card.Entry.Name)));
            card.Graphics = detection;
            if (!card.Entry.PresetChosen && detection.Preset is { } preset) card.Entry.Preset = preset;
        }
        if (pending.Count > 0) Save();
        if (_selected is not null && pending.Contains(_selected)) Select(_selected);
    }

    /// <summary>Cover art, a few at a time, and only for what has none yet. Steam publishes it on
    /// its own CDN keyed by the app id the scan already read; everything else keeps its tile.</summary>
    private async Task LoadCoversAsync()
    {
        var covers = new CoverCache(_http);
        foreach (var card in _all.Where(c => c.Cover is null && c.Entry.AppId is not null).ToList())
        {
            try
            {
                var file = await covers.EnsureAsync(card.Entry.AppId);
                if (file is null) continue;
                await using var stream = File.OpenRead(file);
                var bitmap = new Bitmap(stream);
                Dispatcher.UIThread.Post(() => card.Cover = bitmap);
            }
            catch (Exception e) when (e is IOException or HttpRequestException or ArgumentException)
            {
                // A cover that will not load is a tile, which is what it already is.
            }
        }
    }

    /// <summary>Two different things wear one button. Pointing at a game's own folder is what this
    /// always did; pointing at the folder games are kept in -- a Games drive, a library copied off
    /// another machine -- was only ever possible one game at a time, which nobody does for forty of
    /// them. Asked the same way Add emulator asks, because it is the same shape of question.</summary>
    private async void OnAddGame(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var how = await EmulatorPicker.ShowAsync(this, Text("Str.AddGameTitle"), Text("Str.AddGameBody"),
        [
            ("many", Text("Str.AddGameMany"), Text("Str.AddGameManyBody")),
            ("one", Text("Str.AddGameOne"), Text("Str.AddGameOneBody")),
        ]);
        if (how is null) return;
        if (how == "many")
        {
            await AddFromLibraryAsync();
            return;
        }

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Text("Str.PickFolder"),
            AllowMultiple = false,
        });
        if (picked.Count == 0) return;

        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var existing = _all.FirstOrDefault(c => Engine.SamePath(c.Path, path));
        if (existing is not null)
        {
            Select(existing);
            return;
        }

        var card = new GameCard(new GameEntry
        {
            Path = path,
            Name = Path.GetFileName(path.TrimEnd('\\', '/')),
            Preset = GameScanner.GuessPreset(path),
        });
        card.Graphics = GraphicsDetector.Detect(path, card.Entry.Name).With(_apiDb?.Lookup(null, card.Entry.Name));
        if (card.Graphics.Preset is { } detected) card.Entry.Preset = detected;
        _all.Add(card);
        _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        card.RefreshInstalled();
        Save();
        RefreshGrid();
        Select(card);
    }

    /// <summary>Every game under one folder. The search is the slow part -- it reads a PE header per
    /// candidate -- so it runs off the UI thread with the same spinner the launcher scan uses.
    /// </summary>
    private async Task AddFromLibraryAsync()
    {
        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Text("Str.AddGamePickLibrary"),
            AllowMultiple = false,
        });
        if (picked.Count == 0) return;
        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        Busy(true);
        ScanSpinner.IsVisible = true;
        Foot(Text("Str.Scanning"));
        try
        {
            IReadOnlyList<ScannedGame> found;
            // Somebody else's folder tree, read on a background thread: an escape here is a dead
            // window, exactly as it is for the launcher scan.
            try { found = await Task.Run(() => GameScanner.UnderFolder(path)); }
            catch (Exception ex)
            {
                WriteLog(Failure(ex), $"search {path}");
                ShowToast(Text("Str.Unexpected"), Level.Err);
                return;
            }

            if (found.Count == 0)
            {
                Foot("");
                ShowToast(string.Format(Text("Str.AddGameNone"), Path.GetFileName(path.TrimEnd('\\', '/'))),
                    Level.Warn);
                return;
            }
            await MergeFoundAsync(found);
        }
        finally
        {
            ScanSpinner.IsVisible = false;
            Busy(false);
        }
    }

    /// <summary>Adding an emulator, which is the one case where the folder someone picks is usually
    /// the wrong one: the files go beside the emulator, not beside the ROMs. So the flow names the
    /// emulator first and then asks for that emulator's folder, rather than asking for "a folder"
    /// and working out what it was afterwards.</summary>
    private async void OnAddEmulator(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var choice = await AskWhichEmulator();
        if (choice is null) return;
        var wanted = choice == "other" ? null : Emulators.ById(choice);

        var picked = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = wanted is null
                ? Text("Str.EmulatorPickOther")
                : string.Format(Text("Str.EmulatorPick"), wanted.Name),
            AllowMultiple = false,
        });
        if (picked.Count == 0) return;

        var path = picked[0].TryGetLocalPath();
        if (string.IsNullOrWhiteSpace(path)) return;

        var existing = _all.FirstOrDefault(c => Engine.SamePath(c.Path, path));
        if (existing is not null)
        {
            Select(existing);
            return;
        }

        // What is actually in there decides, not what was picked from the list: someone who chose
        // PCSX2 and pointed at RPCS3 should get RPCS3, not a wrong route and a silent failure.
        var found = Emulators.Identify(path);
        var detection = GraphicsDetector.Detect(path);

        var card = new GameCard(new GameEntry
        {
            Path = path,
            Name = found?.Name ?? Path.GetFileName(path.TrimEnd('\\', '/')),
            Preset = GameScanner.GuessPreset(path),
        })
        {
            Graphics = detection,
        };
        if (detection.Preset is { } route) card.Entry.Preset = route;

        _all.Add(card);
        _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        card.RefreshInstalled();
        Save();
        RefreshGrid();
        Select(card);

        // Say what was actually found, including when it disagrees with what was asked for.
        if (found is not null && (wanted is null || found.Id == wanted.Id))
            ShowToast(string.Format(Text("Str.EmulatorFound"), found.Name, found.System), Level.Ok);
        else if (wanted is not null)
            ShowToast(string.Format(Text("Str.EmulatorWrong"), wanted.Name,
                string.Join(", ", wanted.Executables.Take(2))), Level.Warn);
        else
            ShowToast(string.Format(Text("Str.EmulatorUnknown"), detection.Tag), Level.Info);
    }

    /// <summary>The two with a route of their own, then everything else. Returns an emulator id,
    /// "other", or null when it was dismissed.</summary>
    private async Task<string?> AskWhichEmulator()
    {
        var named = new[] { "pcsx2", "rpcs3" }
            .Select(Emulators.ById)
            .Where(e => e is not null)
            .Select(e => e!)
            .ToList();

        var options = new List<(string Id, string Title, string Detail)>();
        foreach (var emulator in named)
            options.Add((emulator.Id, emulator.Name, emulator.System));
        // Everything else this app recognises, so "other" is a real list and not a shrug.
        var rest = Emulators.Known.Where(e => named.All(n => n.Id != e.Id)).ToList();
        options.Add(("other", Text("Str.EmulatorOther"),
            $"{Text("Str.EmulatorOtherBody")} ({string.Join(", ", rest.Take(6).Select(e => e.Name))}…)"));

        return await EmulatorPicker.ShowAsync(this, Text("Str.EmulatorTitle"), Text("Str.EmulatorBody"), options);
    }

    /// <summary>Takes a game out of the list. It removes a row and nothing else: whatever is
    /// installed in that folder stays installed, which is why the message says so when there is
    /// something there. This is for the folder that was picked by mistake and for the game nobody
    /// is going to use this on -- the detection is per folder, so a wrong folder is otherwise a row
    /// that keeps offering a route for a game that is not in it.</summary>
    private void OnRemoveGame(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } card || _busy) return;

        // Read before the drawer closes, because closing it clears the selection.
        var installed = card.Installed;
        _all.Remove(card);
        Save();
        CloseDrawer();
        RefreshGrid();
        ShowToast(string.Format(Text(installed ? "Str.RemovedGameInstalled" : "Str.RemovedGame"), card.Name),
            installed ? Level.Warn : Level.Ok);
    }

    /// <summary>The game's page on PCGamingWiki, which is where the API question is actually
    /// settled. Detection reads the files, and files are honest about what they link and silent
    /// about what the game offers: Control ships a D3D11 and a D3D12 executable in one folder, and
    /// Crysis starts in D3D10 from an executable that will also do D3D9. Both answers here are
    /// true and neither is the whole of it, so the route is worth a second opinion.</summary>
    private void OnOpenWiki(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } card) return;
        AppUpdate.OpenInBrowser(
            "https://www.pcgamingwiki.com/w/index.php?search=" + Uri.EscapeDataString(card.Name));
    }

    /// <summary>Starts the game. Through Steam when it has an app id, because that is the path the
    /// game expects -- its own launcher, its DRM, its overlay -- and running the executable straight
    /// is what breaks that. Otherwise the executable the detection already found.</summary>
    private void OnPlay(object? sender, RoutedEventArgs e)
    {
        // Not just consistency with every other handler: the game opens the very DLLs an install
        // is in the middle of writing, Windows locks them, and the transaction fails halfway. The
        // button stays lit because what it can do depends on the card, not on this, so the guard
        // is here rather than in SetButtons.
        if (_busy) return;
        if (_selected is not { } card) return;

        var what = card.Entry.Platform == GamePlatform.Steam && card.Entry.AppId is { Length: > 0 } id
            ? $"steam://rungameid/{id}"
            : card.Graphics?.Executable;

        if (what is null || (!what.StartsWith("steam://", StringComparison.Ordinal) && !File.Exists(what)))
        {
            ShowToast(Text("Str.PlayFailed"), Level.Warn);
            return;
        }

        try { Process.Start(new ProcessStartInfo(what) { UseShellExecute = true, WorkingDirectory = card.Path }); }
        catch (Exception e2) when (e2 is System.ComponentModel.Win32Exception or FileNotFoundException
                                       or System.ComponentModel.InvalidEnumArgumentException)
        {
            ShowToast(Text("Str.PlayFailed"), Level.Warn);
        }
    }

    /// <summary>Everything someone would otherwise be asked for, three messages at a time, in one
    /// zip. Nothing is sent: the file is saved and Explorer opens with it selected, so what leaves
    /// the machine is whatever the person chooses to hand over.</summary>
    private async void OnReport(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        ReportButton.IsEnabled = false;
        Status(Text("Str.ReportWorking"));
        try
        {
            var card = _selected;
            var target = card is null ? null : TargetFor(card);
            // Reading a whole game folder and a ReShade log is disk work, not UI work.
            var path = await Task.Run(() => SupportReport.Save(card, target));

            if (path is null)
            {
                Status(string.Format(Text("Str.ReportFailed"), AppPaths.Logs));
                ShowToast(string.Format(Text("Str.ReportFailed"), AppPaths.Logs), Level.Err);
                return;
            }
            Status(string.Format(Text("Str.ReportSaved"), path));
            ShowToast(string.Format(Text("Str.ReportSaved"), Path.GetFileName(path)), Level.Ok);
        }
        finally
        {
            ReportButton.IsEnabled = true;
        }
    }

    private void Save() => GameStore.Save(_all.Select(c => c.Entry));

    // -- The drawer ------------------------------------------------------------------------------

    private void OnCardClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: GameCard card }) return;

        // The drawer belongs to the game being installed until the work finishes: switching it
        // away mid-install would put that game's result banner on another game's drawer.
        if (_busy && card != _selected)
        {
            ShowToast(Text("Str.Working"), Level.Info);
            return;
        }
        Select(card);
    }

    private async void Select(GameCard card)
    {
        if (_selected is not null && _selected != card) _selected.IsSelected = false;
        _selected = card;
        card.IsSelected = true;
        DrawerHeader.DataContext = card;
        OpenDrawer();

        var graphics = card.Graphics ?? GraphicsDetector.Detect(card.Path, card.Entry.Name, card.Entry.Executable);
        ApiTag.Text = graphics.Tag;
        DetectedLine.Text = graphics.Why;
        ApiCheckBody.Text = string.Format(Text("Str.CheckApiBody"), graphics.Tag);
        ApiHint.Text = graphics switch
        {
            { Preset: null, All.Count: > 0 } => Text("Str.NoRoute"),
            { NeedsRendererSwitch: true } => string.Format(Text("Str.SwitchRenderer"),
                    GraphicsDetection.Short(graphics.Recommended),
                    string.Join(", ", graphics.All.Select(GraphicsDetection.Short)))
                + (graphics.Executable?.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase) == true
                    ? " " + string.Format(Text("Str.SwitchRendererUnreal"), GraphicsDetection.Short(graphics.Recommended).ToLowerInvariant())
                    : ""),
            _ => "",
        };
        // An emulator's renderer is a setting inside it, and that sentence is the whole difference
        // between working and not, so it replaces the generic "switch the renderer" hint.
        if (graphics.Emulator is { } emulator)
            ApiHint.Text = $"{Text("Str.EmulatorSetting")}: "
                           + Translated($"Str.Emulator.{emulator.Id}", emulator.Setting);
        ApiHintBox.IsVisible = ApiHint.Text.Length > 0;

        // Steam can always start it; anything else needs an executable we actually found.
        var canPlay = (card.Entry.Platform == GamePlatform.Steam && card.Entry.AppId is { Length: > 0 })
                      || (graphics.Executable is { } exe2 && File.Exists(exe2));
        PlayButton.IsEnabled = canPlay;
        Localize(PlayLabel, graphics.Emulator is not null ? "Str.Launch" : "Str.Play");

        // The width comes from the executable the detection picked, so a folder holding a 32-bit
        // launcher beside a 64-bit game is decided by the game and not by the launcher.
        var detected = graphics.Width is { } width
            ? Detected.On(width, Path.GetFileName(graphics.Executable ?? card.Path))
            : Work.Detect(card.Path);

        ShowExecutable(graphics, card.Entry);

        // The routes the detected width says can work come first; the rest stay reachable, because
        // a wrong width is wrong about which file is the game, and a list that hides every working
        // route leaves nowhere to go. PresetNote_ says so when the selected one does not match.
        _offered.Clear();
        _offered.AddRange(Presets.Offered(detected));
        _detected = detected;
        _settingPreset = true;
        PresetBox.ItemsSource = _offered.Select(PresetLabel).ToList();
        var index = _offered.IndexOf(card.Entry.Preset);
        PresetBox.SelectedIndex = index >= 0 ? index : 0;
        _settingPreset = false;

        card.Entry.Preset = _offered[PresetBox.SelectedIndex];
        card.RefreshRoute();
        PresetNote.Text = PresetNote_(card.Entry.Preset);
        ShowVersions(card.Entry.Preset);
        ShowProxies(card.Entry.Preset);

        await RefreshAsync();
    }

    private void OnCloseDrawer(object? sender, RoutedEventArgs e) => CloseDrawer();

    private void OpenDrawer()
    {
        if (Drawer.Classes.Contains("open")) return;
        Drawer.IsVisible = true;
        // One layout pass while visible before the class lands, or the transition has no starting
        // frame and the drawer just appears.
        Dispatcher.UIThread.Post(() =>
        {
            Drawer.Classes.Set("open", true);
            // Focus goes into the sheet, so Tab walks its controls rather than the grid behind it.
            CloseButton.Focus();
        }, DispatcherPriority.Render);
    }

    /// <summary>A click on the dimmed grid around the sheet closes it, as it would any dialog.</summary>
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e) => CloseDrawer();

    private void OnGpuPill(object? sender, RoutedEventArgs e) => TabSystem.IsChecked = true;

    /// <summary>Empties the download cache. Nothing in it is unrecoverable -- the payloads, the
    /// covers and the release list all come back -- but it is about 165 MB of coming back, so it
    /// asks first. What is installed in a game is not in here and is not touched.</summary>
    private async void OnClearCache(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        var answer = await EmulatorPicker.ShowAsync(this, Text("Str.ClearCache"), Text("Str.ClearCacheBody"),
        [
            ("clear", Text("Str.ClearCacheYes"), Text("Str.ClearCacheYesBody")),
            ("keep", Text("Str.Dismiss"), Text("Str.ClearCacheNoBody")),
        ]);
        if (answer != "clear") return;

        Busy(true);
        Status(Text("Str.Working"));
        try
        {
            var freed = await Task.Run(PayloadCache.Clear);
            Status(string.Format(Text("Str.CacheCleared"), Megabytes(freed)));
            ShowToast(string.Format(Text("Str.CacheCleared"), Megabytes(freed)), Level.Ok);
        }
        finally
        {
            Busy(false);
            // Re-read rather than assume: a file something else had open is still there.
            await ShowPayloadStateAsync();
        }
    }

    private void OnOpenCache(object? sender, RoutedEventArgs e)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.Cache);
            Process.Start(new ProcessStartInfo(AppPaths.Cache) { UseShellExecute = true });
        }
        catch (Exception e2) when (e2 is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            // No shell association for a folder is not something to interrupt anyone over.
        }
    }

    private async void CloseDrawer()
    {
        if (_selected is not null) _selected.IsSelected = false;
        _selected = null;
        Drawer.Classes.Set("open", false);
        await Task.Delay(220);
        // Reopened while it was sliding out: leave it be.
        if (!Drawer.Classes.Contains("open")) Drawer.IsVisible = false;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowFit.ToScreen(this);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.Key == Key.Escape && Drawer.Classes.Contains("open"))
        {
            CloseDrawer();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }

    /// <summary>Tiles stretch a little to fill the row, so the grid runs edge to edge with the same
    /// gap everywhere instead of piling leftover width between tiles. Past the widest tile another
    /// column is added.</summary>
    private void OnGridResized(object? sender, SizeChangedEventArgs e)
    {
        if (CardList.ItemsPanelRoot is not WrapPanel panel) return;
        const double gap = 18, minTile = 168, maxTile = 212;
        var width = e.NewSize.Width - GridScroll.Padding.Left - GridScroll.Padding.Right;
        var columns = Math.Max(1, Math.Floor((width + gap) / (minTile + gap)));
        var tile = Math.Floor(Math.Min(maxTile, (width - gap * (columns - 1)) / columns));
        panel.ItemWidth = tile + gap;
        // The last column's gap hangs past the right edge; a matching negative margin keeps the
        // centred rows lined up with the top bar.
        CardList.Width = columns * (tile + gap);
        CardList.Margin = new Thickness(0, 0, -gap, 0);
        CardList.Resources["TileWidth"] = tile;
        CardList.Resources["CoverHeight"] = Math.Round(tile * 1.5);
    }

    private async void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingPreset || _selected is null) return;
        if (PresetBox.SelectedIndex < 0 || PresetBox.SelectedIndex >= _offered.Count) return;

        _selected.Entry.Preset = _offered[PresetBox.SelectedIndex];
        _selected.Entry.PresetChosen = true;
        _selected.RefreshRoute();
        PresetNote.Text = PresetNote_(_selected.Entry.Preset);
        ShowVersions(_selected.Entry.Preset);
        ShowProxies(_selected.Entry.Preset);
        Save();
        await RefreshAsync();
    }

    /// <summary>Which file the width and the API were read off, and the way to change it.
    ///
    /// It is shown because it is the one input everything else is derived from and the one the app
    /// can get wrong on its own: a folder with a launcher in the root and the game in Bin64 is read
    /// as the launcher, and every route offered after that is for the wrong architecture.</summary>
    private void ShowExecutable(GraphicsDetection graphics, GameEntry entry)
    {
        var chosen = entry.Executable is { Length: > 0 };
        var exe = graphics.Executable;
        var width = exe is null ? null : Engine.MachineOfFile(exe) switch
        {
            Engine.MachineX86 => "32-bit",
            Engine.MachineX64 => "64-bit",
            _ => null,
        };

        ExeLine.Text = exe is null
            ? Text("Str.ExeNone")
            : Path.GetFileName(exe) + (width is null ? "" : $"  ·  {width}");
        ExeNote.Text = chosen ? Text("Str.ExeNoteChosen") : Text("Str.ExeNote");
        ExeButton.Content = chosen ? Text("Str.ExeUseDetected") : Text("Str.ExeChoose");
    }

    /// <summary>Point the detection at a file, or put it back on its own answer. The route list is
    /// rebuilt from it, because the width it reads is what orders that list.</summary>
    private async void OnChooseExecutable(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } card || _busy) return;

        if (card.Entry.Executable is { Length: > 0 })
        {
            card.Entry.Executable = null;
        }
        else
        {
            var picked = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Text("Str.ExeSection"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("*.exe") { Patterns = ["*.exe"] }],
                SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(card.Path),
            });
            if (picked.Count == 0) return;

            var path = picked[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path)) return;

            // Inside this game's folder, and a PE this can read. Both are refusals with a sentence
            // rather than a silent fallback to detection, which would look like the pick was taken.
            var root = Engine.WeaklyCanonical(card.Path);
            if (!Engine.IsInside(root, Engine.WeaklyCanonical(path)))
            {
                ShowToast(Text("Str.ExeNotHere"), Level.Err);
                return;
            }
            if (Engine.MachineOfFile(path) is null)
            {
                ShowToast(Text("Str.ExeUnreadable"), Level.Err);
                return;
            }
            card.Entry.Executable = path;
        }

        // Detection has to run again: the width, the API and the folder the install writes into all
        // come off the executable, and so does the order of the route list.
        card.Graphics = GraphicsDetector.Detect(card.Path, card.Entry.Name, card.Entry.Executable)
            .With(_apiDb?.Lookup(card.Entry.AppId, card.Entry.Name));
        if (!card.Entry.PresetChosen && card.Graphics.Preset is { } preset) card.Entry.Preset = preset;
        Save();
        Select(card);
    }

    private void OnTabChanged(object? sender, RoutedEventArgs e)
    {
        if (GamesPage is null) return; // Fires once while the window is still being built.
        // Decided by the button just checked: the one it replaces is not unchecked yet when this
        // runs, so reading every IsChecked left two pages showing on top of each other.
        GamesPage.IsVisible = sender == TabGames;
        SystemPage.IsVisible = sender == TabSystem;
        SettingsPage.IsVisible = sender == TabSettings;
        if (sender == TabSystem) _ = ShowPayloadStateAsync();
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = LanguageBox.SelectedIndex;
        if (index < 0 || index >= App.Languages.Length) return;
        var code = App.Languages[index].Code;
        if (code == App.CurrentLanguage) return;

        App.ChangeLanguage(code);
        var settings = Settings.Load();
        settings.Language = code;
        settings.Save();

        // Labels bound with DynamicResource follow the swap on their own; the ones written in code
        // -- the route name, its note, the verdict -- were written in the old language, so the open
        // sheet is filled again.
        RefreshGrid();
        if (_selected is { } card) Select(card);
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        try { Process.Start(new ProcessStartInfo(_selected.Path) { UseShellExecute = true }); }
        catch (Exception e2) when (e2 is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            // No shell association for a folder is not something to interrupt anyone over.
        }
    }

    // -- Pre-flight, install, uninstall -----------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (_selected is not { } card || _busy) return;
        var pins = Pins();

        // A result belongs to the action that produced it; a new check replaces it.
        Steps.IsVisible = false;
        ResultBanner.IsVisible = false;
        _report.Clear();

        // Off the UI thread: deciding whether the cache is complete means hashing 141 MB of
        // weights, which is about a second and would be a second of frozen window.
        var (report, payloads) = await Task.Run(() =>
        {
            var staged = CachedPayloadFolder(card.Entry.Preset);
            return (Work.Preflight(TargetFor(card), staged ?? "", card.Entry.Preset, pins, _proxy), staged);
        });

        Show(report);
        card.RefreshInstalled();
        ShowVerdict(report, card);
        SetButtons(true);
        Status(payloads is null ? Text("Str.WillDownload") : Text("Str.PayloadsReady"));
    }

    /// <summary>The versions this route can be installed at: every GitHub release that publishes
    /// what the route needs, plus the one the payload manifest pins. That last one is not
    /// decoration -- v0.5.0 published its 32-bit pair inside the archive rather than beside it,
    /// so for a bridge route the manifest is the only place those two files can be pinned from.
    /// </summary>
    /// <summary>The name ReShade goes in as. Automatic is the first entry and what almost every
    /// game wants; the list under it is only the names the pinned ReShade can actually be loaded
    /// under for this API, because a name it does not export is a file nothing opens.</summary>
    private void ShowProxies(Preset preset)
    {
        var choices = Work.ProxyChoicesFor(preset);
        ProxySection.IsVisible = choices.Length > 0;
        if (choices.Length == 0)
        {
            // Vulkan: ReShade is a layer there, so there is no name to choose.
            _proxy = null;
            return;
        }

        // A name chosen for one API is not carried into another, where it would be ignored anyway.
        if (!Work.ProxyAllowed(preset, _proxy)) _proxy = null;

        _settingProxy = true;
        ProxyBox.ItemsSource = new[] { Text("Str.ProxyAuto") }.Concat(choices).ToList();
        ProxyBox.SelectedIndex = _proxy is null ? 0 : Array.IndexOf(choices, _proxy) + 1;
        _settingProxy = false;
        ProxyNote.Text = Text("Str.ProxyNote");
    }

    private void OnProxyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingProxy || _selected is null) return;
        var choices = Work.ProxyChoicesFor(_selected.Entry.Preset);
        var i = ProxyBox.SelectedIndex;
        _proxy = i >= 1 && i - 1 < choices.Length ? choices[i - 1] : null;
        _ = RefreshAsync();
    }

    private void ShowVersions(Preset preset)
    {
        var route = preset.Route();
        _versions.Clear();
        foreach (var release in _releases.Where(r => r.Covers(route)))
            _versions.Add(new VersionChoice(release.Version, release.Label, release));

        if (_manifest is not null)
        {
            var component = route == Route.X86
                ? PayloadManifest.BridgeComponent
                : PayloadManifest.AddonComponent;
            try
            {
                if (AddonReleases.Version(_manifest.Component(component).Version) is { } pinned &&
                    _versions.All(v => v.Version != pinned))
                    _versions.Add(new VersionChoice(pinned, $"v{pinned} - {Text("Str.VersionShipped")}", null));
            }
            catch (InstallException)
            {
                // A manifest without that component adds nothing; the releases still stand.
            }
        }

        _versions.Sort((a, b) => b.Version.CompareTo(a.Version));

        // What this game was last installed with, then whatever the app is currently set to, then
        // the newest. The rule lives in Core so it can be asserted against: "it never remembers
        // which one I chose" is the loudest complaint about the tool this one is modelled on, and
        // "does a new release actually become the default" is not a question to answer by reading.
        var index = AddonReleases.Preferred(_versions.Select(v => v.Version).ToList(),
            _selected?.Entry.AddonVersion, _version?.Version);
        _settingVersion = true;
        AddonVersionBox.ItemsSource = _versions.Select(v => v.Label).ToList();
        AddonVersionBox.SelectedIndex = index;
        _settingVersion = false;

        _version = AddonVersionBox.SelectedIndex >= 0 ? _versions[AddonVersionBox.SelectedIndex] : null;
        VersionSection.IsVisible = _versions.Count > 0;
        VersionNote.Text = VersionNoteText();
    }

    private string VersionNoteText() => _version is null
        ? string.Empty
        : _version.Release is null
            ? Text("Str.VersionNoteShipped")
            : string.Format(Text("Str.VersionNoteRelease"), _version.Version);

    private async void OnAddonVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingVersion || _selected is null) return;
        if (AddonVersionBox.SelectedIndex < 0 || AddonVersionBox.SelectedIndex >= _versions.Count) return;

        _version = _versions[AddonVersionBox.SelectedIndex];
        _selected.Entry.AddonVersion = _version.Version.ToString();
        Save();
        VersionNote.Text = VersionNoteText();
        // A different version is a different pair of hashes, so the pre-flight has to be redone:
        // "already installed" is only true of the version that is actually in the folder.
        await RefreshAsync();
    }

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } card || _busy) return;
        Busy(true, InstallButton);
        ResultBanner.IsVisible = false;
        ShowStep(StepDownload);
        try
        {
            var folder = await EnsurePayloadsAsync(card.Entry.Preset);
            if (folder is null)
            {
                ShowStep(StepDownload, failed: true);
                // The reason is the report line the download left behind; without a manifest there is none.
                ShowResult(Level.Err, Text("Str.DownloadFailed"), _report.Count > 0
                    ? string.Format(Text("Str.ResultErrors"), _report.Count)
                    : Text("Str.ManifestFailed"));
                return;
            }

            ShowStep(StepInstall);
            Status(Text("Str.Working"));
            var pins = Pins();
            Report report;
            try { report = await Task.Run(() => Work.Install(TargetFor(card), folder, card.Entry.Preset, pins, _proxy)); }
            catch (Exception ex)
            {
                // The engine turns everything it expects into a report line, and rolls back before
                // it throws. Anything left is unforeseen -- and losing the window mid-install is a
                // worse answer than a line saying so.
                report = Failure(ex);
            }
            Show(report);
            WriteLog(report, $"install {card.Entry.Preset.Label()} -> {card.Path}");
            InstallLog.Write("install", card, TargetFor(card), report, Selected(), pins, folder);
            card.RefreshInstalled();
            // Recorded from the install and not only from the menu, so the version is remembered
            // for somebody who never opened that menu -- which is nearly everybody.
            if (!report.Failed && _version is { } installed)
            {
                card.Entry.AddonVersion = installed.Version.ToString();
                Save();
            }
            ShowStep(report.Failed ? StepInstall : StepDone, report.Failed);
            ShowOutcome(report, "Str.Install", card.Name);
            if (!report.Failed && card.Installed) card.Pulse();
            Status(report.Failed ? Text("Str.LogSaved") : Text("Str.Ready"));
        }
        finally
        {
            Busy(false);
        }
    }

    private async void OnUninstall(object? sender, RoutedEventArgs e)
    {
        if (_selected is not { } card || _busy) return;
        Busy(true, UninstallButton);
        Steps.IsVisible = false;
        ResultBanner.IsVisible = false;
        try
        {
            var report = await RunUninstallAsync(card, force: false);

            // An uninstall can finish cleanly and leave the add-on exactly where it was: a file that
            // no longer hashes to what the install wrote belongs to whoever changed it, so the
            // transaction keeps it, rewrites the manifest around it, and the folder goes on counting
            // as installed. The banner used to say "Removed from X" over the top of a tile still
            // reading Installed, and those two together read as a bug in the tile.
            // Only when forcing would actually get somewhere. A file the running game still has
            // open is retained too, and against that one the answer is "close the game", not a
            // dialog offering to delete it harder.
            var changed = report.Lines.Any(l => l.Text.Contains(Work.ModifiedMarker, StringComparison.Ordinal));
            if (!report.Failed && card.Installed && changed && await AskRemoveAnyway(card))
                report = await RunUninstallAsync(card, force: true);

            if (!report.Failed && card.Installed)
                ShowResult(Level.Warn, string.Format(Text("Str.UninstallLeft"), card.Name), Text("Str.SeeDetails"));
            else
                ShowOutcome(report, "Str.Uninstall", card.Name);
            Status(report.Failed ? Text("Str.LogSaved") : Text("Str.Ready"));
        }
        finally
        {
            Busy(false);
        }
    }

    private async Task<Report> RunUninstallAsync(GameCard card, bool force)
    {
        Report report;
        try { report = await Task.Run(() => Work.Uninstall(TargetFor(card), card.Entry.Preset, force)); }
        catch (Exception ex) { report = Failure(ex); }
        Show(report);
        var what = force ? "uninstall (forced)" : "uninstall";
        WriteLog(report, $"{what} {card.Entry.Preset.Label()} -> {card.Path}");
        InstallLog.Write(what, card, TargetFor(card), report, Selected(), Pins(), null);
        card.RefreshInstalled();
        return report;
    }

    /// <summary>Asks before deleting a file this app did not write. The answer is the whole point:
    /// the pair in a game folder is sometimes replaced by hand -- a locally built add-on, another
    /// tool's copy -- and deleting that without asking is worse than leaving it.</summary>
    private async Task<bool> AskRemoveAnyway(GameCard card) =>
        await EmulatorPicker.ShowAsync(this, Text("Str.UninstallLeftTitle"),
            string.Format(Text("Str.UninstallLeftBody"), card.Name),
            [
                ("force", Text("Str.UninstallForce"), Text("Str.UninstallForceBody")),
                ("keep", Text("Str.UninstallKeep"), Text("Str.UninstallKeepBody")),
            ]) == "force";

    /// <summary>Downloads whatever this route needs, then hands back the folder to install from --
    /// the same shape someone would have unzipped by hand, so the engine cannot tell the
    /// difference.</summary>
    private async Task<string?> EnsurePayloadsAsync(Preset preset)
    {
        var manifest = Selected();
        if (manifest is null)
        {
            Status(Text("Str.ManifestFailed"));
            return null;
        }

        var cache = new PayloadCache(_http);
        var progress = new Progress<DownloadProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress.IsVisible = true;
            Progress.IsIndeterminate = p.Fraction is null;
            if (p.Fraction is { } fraction) Progress.Value = fraction * 100;
            Status($"{Text("Str.Downloading")} {p.File} — {p.Received / 1_048_576} MB");
        }));

        try
        {
            var components = ComponentsFor(preset);
            foreach (var component in components)
                await cache.EnsureAsync(manifest, component, progress);

            Progress.IsVisible = false;
            ShowStep(StepVerify);
            Status(Text("Str.Verifying"));
            // One folder for the install to read, hard-linked out of the cache: the 141 MB of
            // weights are not copied a second time on the way there.
            return cache.Stage(manifest, components);
        }
        catch (Exception e) when (e is InstallException or HttpRequestException or IOException)
        {
            Progress.IsVisible = false;
            _report.Add(new ReportLine(Glyph(Level.Err), e.Message, Brush("Err")));
            Status(Text("Str.DownloadFailed"));
            return null;
        }
    }

    /// <summary>What the engine is pointed at: the executable the detection found, not the library
    /// folder. For an Unreal game that is Binaries\Win64, where ReShade and the add-on have to sit --
    /// the root holds only a launcher stub that loads neither. For a 32-bit game it is also what
    /// settles a folder holding the game beside a benchmark or a launcher.</summary>
    private static string TargetFor(GameCard card) =>
        card.Graphics?.Target is { } target && File.Exists(target) ? target : card.Path;

    /// <summary>What a route needs. Every route needs the add-on and the runtime; the bridge routes
    /// also need their own 32-bit pair and the pinned ReShade beside it.</summary>
    private static string[] ComponentsFor(Preset preset) => preset.Route() == Route.X86
        ?
        [
            PayloadManifest.BridgeComponent, PayloadManifest.X86ExtrasComponent,
            PayloadManifest.RuntimeComponent, PayloadManifest.ShaderComponent,
        ]
        : [
            PayloadManifest.AddonComponent, PayloadManifest.RuntimeComponent,
            PayloadManifest.ReShadeComponent, PayloadManifest.ShaderComponent,
        ];

    /// <summary>The folder a route would install from, when it is already complete in the cache.</summary>
    private string? CachedPayloadFolder(Preset preset)
    {
        var manifest = Selected();
        if (manifest is null) return null;
        try
        {
            var components = ComponentsFor(preset);
            if (!components.All(c => PayloadCache.IsComplete(manifest, c))) return null;
            return new PayloadCache(_http).Stage(manifest, components);
        }
        catch (Exception e) when (e is InstallException or IOException)
        {
            return null;
        }
    }

    /// <summary>The manifest this sheet installs from: the published one, with the chosen
    /// release's add-on swapped in when that is not the version the manifest already pins.</summary>
    private PayloadManifest? Selected() => _manifest is null || _version?.Release is null
        ? _manifest
        : AddonReleases.With(_manifest, _version.Release);

    private PayloadPins Pins()
    {
        try { return Selected()?.Pins() ?? Fallback(); }
        catch (InstallException) { return Fallback(); }

        // Without a manifest the runtime and weights are still the pinned pair the add-on refuses
        // anything but; only the add-on's own size is unknown, and a zero there just means the
        // pre-flight cannot judge it yet.
        static PayloadPins Fallback() => new() { AddonSha = new string('0', 64), AddonSize = 0 };
    }

    // -- Chrome ----------------------------------------------------------------------------------

    private void Show(Report report)
    {
        _report.Clear();
        foreach (var (level, text) in report.Lines)
        {
            _report.Add(new ReportLine(Glyph(level), text, LevelBrush(level)));
        }
        ReportScroll.ScrollToHome();
    }

    /// <summary>An unforeseen exception as a report, so every caller can carry on treating the
    /// result as one. The type name is in it because this is the line that gets pasted into a
    /// support thread.</summary>
    private static Report Failure(Exception e)
    {
        var report = new Report();
        report.Err($"{e.GetType().Name}: {e.Message}");
        return report;
    }

    private static void WriteLog(Report report, string header) =>
        InstallLog.Append(report.ToLog($"{DateTime.Now:s} {header}"));

    private void Busy(bool on, Button? pressed = null)
    {
        _busy = on;
        SetButtons(!on);
        ScanButton.IsEnabled = !on;
        // Changing the route mid-install would change what the running install is told it did.
        PresetBox.IsEnabled = !on;
        if (!on) Progress.IsVisible = false;

        // The pressed button says what it is doing; the other one only waits.
        InstallSpin.IsVisible = on && pressed == InstallButton;
        UninstallSpin.IsVisible = on && pressed == UninstallButton;
        InstallIcon.IsVisible = !InstallSpin.IsVisible;
        UninstallIcon.IsVisible = !UninstallSpin.IsVisible;
        Localize(InstallLabel, InstallSpin.IsVisible ? "Str.Installing" : "Str.Install");
        Localize(UninstallLabel, UninstallSpin.IsVisible ? "Str.Removing" : "Str.Uninstall");

        // While it runs, the verdict slot says so, rather than showing the check that came before.
        if (on && pressed is not null)
        {
            SetVerdict(Level.Info, Text(pressed == InstallButton ? "Str.Installing" : "Str.Removing"), "");
            ResultSpin.IsVisible = true;
        }
    }

    /// <summary>The pre-flight in one sentence. The report itself stays folded under Details.</summary>
    private void ShowVerdict(Report report, GameCard card)
    {
        var warnings = report.Lines.Count(l => l.Level == Level.Warn);
        var errors = report.Lines.Count(l => l.Level == Level.Err);
        if (errors > 0)
            SetVerdict(Level.Err, string.Format(Text("Str.PreflightErr"), errors), Text("Str.SeeDetails"));
        else if (card.Installed)
            SetVerdict(Level.Ok, Text("Str.PreflightInstalled"), Text("Str.PreflightInstalledDetail"));
        else if (warnings > 0)
            SetVerdict(Level.Warn, string.Format(Text("Str.PreflightWarn"), warnings), Text("Str.SeeDetails"));
        else
            SetVerdict(Level.Ok, Text("Str.PreflightOk"), Text("Str.PreflightOkDetail"));
    }

    /// <summary>Lights the chips: everything before <paramref name="current"/> done, the current one
    /// running or failed. Reaching the last step marks it done too.</summary>
    private void ShowStep(int current, bool failed = false)
    {
        Steps.IsVisible = true;
        for (var i = 0; i < Steps.Children.Count; i++)
        {
            var classes = Steps.Children[i].Classes;
            var here = i == current;
            classes.Set("done", i < current || (here && current == StepDone));
            classes.Set("active", here && !failed && current != StepDone);
            classes.Set("failed", here && failed);
        }
    }

    /// <summary>One sentence for what an action did, picked from the report's worst line.
    /// <paramref name="action"/> is the key prefix: Str.Install or Str.Uninstall.</summary>
    private void ShowOutcome(Report report, string action, string game)
    {
        var warnings = report.Lines.Count(l => l.Level == Level.Warn);
        var errors = report.Lines.Count(l => l.Level == Level.Err);
        var level = report.Failed ? Level.Err : warnings > 0 ? Level.Warn : Level.Ok;
        var suffix = level switch { Level.Err => "Fail", Level.Warn => "Warn", _ => "Ok" };
        var title = string.Format(Text(action + suffix), game, warnings);
        var detail = report.Failed
            ? $"{string.Format(Text("Str.ResultErrors"), Math.Max(1, errors))} {Text("Str.LogSaved")}"
            : "";
        ShowResult(level, title, detail);
    }

    private void ShowResult(Level level, string title, string detail)
    {
        SetVerdict(level, title, detail);
        // Anything short of a clean result wants its lines read, so they are opened for it.
        if (level != Level.Ok) DetailsExpander.IsExpanded = true;
        // The panel may have been closed during a long download; then the corner says it instead.
        // With the panel open the banner above already does, and two copies of one sentence is noise.
        if (!Drawer.Classes.Contains("open")) ShowToast(title, level);
    }

    private void SetVerdict(Level level, string title, string detail)
    {
        foreach (var (name, l) in new[] { ("ok", Level.Ok), ("warn", Level.Warn), ("err", Level.Err) })
        {
            ResultBanner.Classes.Set(name, l == level);
            ResultDot.Classes.Set(name, l == level);
        }
        ResultSpin.IsVisible = false;
        ResultGlyph.Data = level == Level.Info ? null : Glyph(level);
        ResultTitle.Text = title;
        ResultDetail.Text = detail;
        ResultDetail.IsVisible = detail.Length > 0;
        // Off and on again, so the entrance plays even when the banner was already up.
        ResultBanner.IsVisible = false;
        ResultBanner.IsVisible = true;
    }

    private void ShowToast(string text, Level level)
    {
        ToastText.Text = text;
        ToastIcon.Data = Glyph(level);
        ToastIcon.Foreground = LevelBrush(level);
        Toast.Classes.Set("show", true);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private static Geometry? Glyph(Level level) => Vector(level switch
    {
        Level.Ok => "IconOk",
        Level.Warn => "IconWarn",
        Level.Err => "IconErr",
        _ => "IconInfo",
    });

    private static Geometry? Vector(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as Geometry : null;

    private IBrush LevelBrush(Level level) => Brush(level switch
    {
        Level.Ok => "Ok",
        Level.Warn => "Warn",
        Level.Err => "Err",
        _ => "Muted",
    });

    /// <summary>Binds a text to a string resource rather than copying it, so a label changed in code
    /// still follows a language switch.</summary>
    private static void Localize(TextBlock block, string key) =>
        block[!TextBlock.TextProperty] = block.GetResourceObservable(key).ToBinding();

    private void SetButtons(bool enabled)
    {
        InstallButton.IsEnabled = enabled && _selected is not null;
        UninstallButton.IsEnabled = enabled && _selected is not null;
    }

    private void Status(string text) => StatusText.Text = text;
    private void Foot(string text) => FootText.Text = text;

    /// <summary>Updates the app in place when the release publishes what that needs, and falls back
    /// to the browser when it does not. The fallback is not a formality: a release without its sums
    /// cannot be verified, and this would rather hand the person a link than run unchecked bytes.
    /// </summary>
    private async void OnOpenReleases(object? sender, RoutedEventArgs e)
    {
        if (_update is null) return;
        if (!_update.CanSelfUpdate)
        {
            AppUpdate.OpenInBrowser(_update.Url);
            return;
        }

        UpdateButton.IsEnabled = false;
        try
        {
            var progress = new Progress<double>(f =>
                UpdateText.Text = $"{Text("Str.UpdateWorking")} {f * 100:0}%");
            var staged = await AppUpdate.FetchAsync(_http, _update, progress);
            AppUpdate.ApplyAndRestart(staged);
            // The replacement is already starting; this one gets out of its way.
            Close();
        }
        catch (Exception ex) when (ex is HttpRequestException or InstallException or IOException
                                      or TaskCanceledException or UnauthorizedAccessException)
        {
            // Nothing was replaced -- Swap puts the old one back if the move fails, and a refused
            // hash never gets that far. The link still works.
            UpdateText.Text = $"{Text("Str.UpdateAvailable")} - v{_update.Version}";
            UpdateButton.IsEnabled = true;
            ShowToast(ex.Message, Level.Warn);
        }
    }

    private void OnDismissUpdate(object? sender, RoutedEventArgs e) => UpdateBanner.IsVisible = false;

    /// <summary>A route's name and its note in the current language. The engine carries both in
    /// English -- it has no resources -- so the translation lives here and falls back to its words
    /// when a key is missing.</summary>
    private string PresetLabel(Preset preset) => Translated($"Str.Preset.{preset}", preset.Label());

    /// <summary>What the route does, and — when it disagrees with the width read off the executable
    /// — that it disagrees. The list no longer hides a mismatched route, so this is what keeps the
    /// choice informed rather than merely possible.</summary>
    private string PresetNote_(Preset preset)
    {
        var note = Translated($"Str.PresetNote.{preset}", preset.Note());
        if (preset.MatchesDetected(_detected)) return note;

        var wants = preset.Route() == Route.X86 ? "32-bit" : "64-bit";
        var found = _detected.Route == Route.X86 ? "32-bit" : "64-bit";
        return string.Format(Text("Str.RouteMismatch"), wants, found) + "\n\n" + note;
    }

    private static string Translated(string key, string fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is string s && s.Length > 0
            ? s
            : fallback;

    private string Text(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is string s ? s : key;

    private IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush b
            ? b
            : Brushes.Gray;
}
