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
public sealed record ReportLine(string Glyph, string Text, IBrush Brush);

public partial class MainWindow : Window
{
    private readonly AppConfig _config = AppConfig.Load();
    private readonly HttpClient _http = PayloadCache.DefaultClient(App.Version);
    private readonly List<GameCard> _all = [];
    private readonly ObservableCollection<GameCard> _shown = [];
    private readonly ObservableCollection<ReportLine> _report = [];
    private readonly List<Preset> _offered = [];

    private PayloadManifest? _manifest;
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
        _report.CollectionChanged += (_, _) => ReportEmpty.IsVisible = _report.Count == 0;
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            Toast.Classes.Set("show", false);
        };
        VersionText.Text = $"v{App.Version}";
        AboutVersion.Text = $"v{App.Version} · {AppPaths.Root}";
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

        _ = StartBackgroundWorkAsync();
    }

    // -- Startup ---------------------------------------------------------------------------------

    private async Task StartBackgroundWorkAsync()
    {
        var cache = new PayloadCache(_http);
        try
        {
            _manifest = await cache.FetchManifestAsync(
                _config.Payload.Owner, _config.Payload.Repo, _config.Payload.Branch, _config.Payload.File);
        }
        catch (Exception e) when (e is HttpRequestException or InstallException or TaskCanceledException)
        {
            // Offline, or the content repository is not up yet: the copy beside the executable pins
            // the same hashes, so everything still works against whatever is already cached.
            _manifest = PayloadCache.LoadLocalManifest();
        }

        ShowPayloadState();

        // Which API each game supports: the database the content repository publishes, then the
        // last copy fetched, then the one shipped beside the exe. Detection runs either way; the
        // database only corrects what a game's own files get wrong.
        _apiDb = await ApiDatabase.LoadAsync(_http, _config.Payload.Owner, _config.Payload.Repo, _config.Payload.Branch);
        await DetectAllAsync();
        await LoadCoversAsync();

        _update = await AppUpdate.CheckAsync(_http, _config.App, App.Version);
        if (_update is not null)
        {
            UpdateText.Text = $"{Text("Str.UpdateAvailable")} — v{_update.Version}";
            UpdateBanner.IsVisible = true;
        }

        // A first run has nothing in the list, and a scan is what it wants. Reading the launchers
        // is read-only -- nothing is installed or changed by looking -- so it just happens.
        if (_all.Count == 0) OnScan(null, new RoutedEventArgs());
    }

    private void ShowSystem()
    {
        var state = GpuService.Read();
        GpuText.Text = state.Gpu;
        DriverText.Text = state.Driver;
        HipText.Text = state.Hip7 ? Text("Str.Ready") : "—";
        HipText.Foreground = state.Hip7 ? Brush("Ok") : Brush("Err");

        GpuPillText.Text = state.Gpu.Length > 34 ? state.Gpu[..34] + "…" : state.Gpu;
        GpuDot.Fill = state.Ready ? Brush("Ok") : Brush("Err");

        if (!state.Hip7) ShowWarning(Text("Str.HipMissing"));
        else if (!state.LooksLikeRadeon) ShowWarning(Text("Str.NotRadeon"));
        else SystemWarning.IsVisible = false;
    }

    private void ShowWarning(string text)
    {
        SystemWarning.Text = text;
        SystemWarning.IsVisible = true;
    }

    private void ShowPayloadState()
    {
        if (_manifest is null)
        {
            PayloadState.Text = Text("Str.ManifestFailed");
            return;
        }

        var lines = new List<string>();
        foreach (var (name, component) in _manifest.Components)
        {
            var complete = false;
            try { complete = PayloadCache.IsComplete(_manifest, name); }
            catch (InstallException)
            {
                // A component this build does not understand is not a reason to show nothing.
            }
            lines.Add($"{name} {component.Version} — {(complete ? Text("Str.PayloadsReady") : Text("Str.NotDownloaded"))}");
        }
        PayloadState.Text = string.Join("\n", lines);
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
        if (noMatch) EmptyBody.Text = string.Format(Text("Str.NoMatch"), needle);
        else Localize(EmptyBody, "Str.NoGames");
        Foot(_all.Count == 0 ? "" : $"{_all.Count} {Text("Str.Games").ToLowerInvariant()}");
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
            var found = await Task.Run(GameScanner.ScanAll);
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
                GraphicsDetector.Detect(card.Path, card.Entry.Name).With(db?.Lookup(card.Entry.AppId, card.Entry.Name)));
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

    private async void OnAddGame(object? sender, RoutedEventArgs e)
    {
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

        var graphics = card.Graphics ?? GraphicsDetector.Detect(card.Path, card.Entry.Name);
        ApiTag.Text = graphics.Tag;
        DetectedLine.Text = graphics.Why;
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
        ApiHintBox.IsVisible = ApiHint.Text.Length > 0;

        // The width comes from the executable the detection picked, so a folder holding a 32-bit
        // launcher beside a 64-bit game is decided by the game and not by the launcher.
        var detected = graphics.Width is { } width
            ? Detected.On(width, Path.GetFileName(graphics.Executable ?? card.Path))
            : Work.Detect(card.Path);

        // Five of the ten API-by-architecture combinations do not exist; the detected width rules
        // out the rest, so a route that cannot work is never on the list.
        _offered.Clear();
        _offered.AddRange(Presets.Offered(detected));
        _settingPreset = true;
        PresetBox.ItemsSource = _offered.Select(p => p.Label()).ToList();
        var index = _offered.IndexOf(card.Entry.Preset);
        PresetBox.SelectedIndex = index >= 0 ? index : 0;
        _settingPreset = false;

        card.Entry.Preset = _offered[PresetBox.SelectedIndex];
        card.RefreshRoute();
        PresetNote.Text = card.Entry.Preset.Note();

        await RefreshAsync();
    }

    private void OnCloseDrawer(object? sender, RoutedEventArgs e) => CloseDrawer();

    private void OpenDrawer()
    {
        if (Drawer.Classes.Contains("open")) return;
        Drawer.IsVisible = true;
        // One layout pass while visible before the class lands, or the transition has no starting
        // frame and the drawer just appears.
        Dispatcher.UIThread.Post(() => Drawer.Classes.Set("open", true), DispatcherPriority.Render);
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

    /// <summary>Tiles are a fixed size; the slots they sit in share the row evenly, so the leftover
    /// width is spread between them instead of piling up at the right edge.</summary>
    private void OnGridResized(object? sender, SizeChangedEventArgs e)
    {
        if (CardList.ItemsPanelRoot is not WrapPanel panel) return;
        const double slot = 192; // 164 tile + 12 button padding + 16 gap
        var width = e.NewSize.Width - GridScroll.Padding.Left - GridScroll.Padding.Right;
        var columns = Math.Max(1, Math.Floor(width / slot));
        panel.ItemWidth = Math.Floor(width / columns);
    }

    private async void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingPreset || _selected is null) return;
        if (PresetBox.SelectedIndex < 0 || PresetBox.SelectedIndex >= _offered.Count) return;

        _selected.Entry.Preset = _offered[PresetBox.SelectedIndex];
        _selected.Entry.PresetChosen = true;
        _selected.RefreshRoute();
        PresetNote.Text = _selected.Entry.Preset.Note();
        Save();
        await RefreshAsync();
    }

    private void OnTabChanged(object? sender, RoutedEventArgs e)
    {
        if (GamesPage is null) return; // Fires once while the window is still being built.
        GamesPage.IsVisible = TabGames.IsChecked == true;
        SystemPage.IsVisible = TabSystem.IsChecked == true;
        SettingsPage.IsVisible = TabSettings.IsChecked == true;
        if (TabSystem.IsChecked == true) ShowPayloadState();
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
            return (Work.Preflight(TargetFor(card), staged ?? "", card.Entry.Preset, pins), staged);
        });

        Show(report);
        card.RefreshInstalled();
        SetButtons(true);
        Status(payloads is null ? Text("Str.WillDownload") : Text("Str.PayloadsReady"));
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
            var report = await Task.Run(() => Work.Install(TargetFor(card), folder, card.Entry.Preset, pins));
            Show(report);
            WriteLog(report, $"install {card.Entry.Preset.Label()} -> {card.Path}");
            card.RefreshInstalled();
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
            var report = await Task.Run(() => Work.Uninstall(TargetFor(card), card.Entry.Preset));
            Show(report);
            WriteLog(report, $"uninstall {card.Entry.Preset.Label()} -> {card.Path}");
            card.RefreshInstalled();
            ShowOutcome(report, "Str.Uninstall", card.Name);
            Status(report.Failed ? Text("Str.LogSaved") : Text("Str.Ready"));
        }
        finally
        {
            Busy(false);
        }
    }

    /// <summary>Downloads whatever this route needs, then hands back the folder to install from --
    /// the same shape someone would have unzipped by hand, so the engine cannot tell the
    /// difference.</summary>
    private async Task<string?> EnsurePayloadsAsync(Preset preset)
    {
        if (_manifest is null)
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
                await cache.EnsureAsync(_manifest, component, progress);

            Progress.IsVisible = false;
            ShowStep(StepVerify);
            Status(Text("Str.Verifying"));
            // One folder for the install to read, hard-linked out of the cache: the 141 MB of
            // weights are not copied a second time on the way there.
            return cache.Stage(_manifest, components);
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
        card.Graphics?.Executable is { } exe && File.Exists(exe) ? exe : card.Path;

    /// <summary>What a route needs. Every route needs the add-on and the runtime; the bridge routes
    /// also need their own 32-bit pair and the pinned ReShade beside it.</summary>
    private static string[] ComponentsFor(Preset preset) => preset.Route() == Route.X86
        ?
        [
            PayloadManifest.BridgeComponent, PayloadManifest.X86ExtrasComponent,
            PayloadManifest.RuntimeComponent,
        ]
        : [PayloadManifest.AddonComponent, PayloadManifest.RuntimeComponent, PayloadManifest.ReShadeComponent];

    /// <summary>The folder a route would install from, when it is already complete in the cache.</summary>
    private string? CachedPayloadFolder(Preset preset)
    {
        if (_manifest is null) return null;
        try
        {
            var components = ComponentsFor(preset);
            if (!components.All(c => PayloadCache.IsComplete(_manifest, c))) return null;
            return new PayloadCache(_http).Stage(_manifest, components);
        }
        catch (Exception e) when (e is InstallException or IOException)
        {
            return null;
        }
    }

    private PayloadPins Pins()
    {
        try { return _manifest?.Pins() ?? Fallback(); }
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

    private void WriteLog(Report report, string header)
    {
        try
        {
            var path = Path.Combine(AppPaths.Logs, "amd-nr-installer.log");
            File.AppendAllText(path, report.ToLog($"{DateTime.Now:s} {header}") + Environment.NewLine);
        }
        catch (IOException)
        {
            // The report is on screen; a log that could not be written is not worth a second error.
        }
    }

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
        Localize(InstallLabel, InstallSpin.IsVisible ? "Str.Installing" : "Str.Install");
        Localize(UninstallLabel, UninstallSpin.IsVisible ? "Str.Removing" : "Str.Uninstall");
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
        foreach (var (name, l) in new[] { ("ok", Level.Ok), ("warn", Level.Warn), ("err", Level.Err) })
            ResultBanner.Classes.Set(name, l == level);
        ResultGlyph.Text = Glyph(level);
        ResultDot.Background = LevelBrush(level);
        ResultTitle.Text = title;
        ResultDetail.Text = detail;
        ResultDetail.IsVisible = detail.Length > 0;
        // Off and on again, so the entrance plays even when the banner was already up.
        ResultBanner.IsVisible = false;
        ResultBanner.IsVisible = true;
        // The drawer may have been closed during a long download; the corner still says it.
        ShowToast(title, level);
    }

    private void ShowToast(string text, Level level)
    {
        ToastText.Text = text;
        ToastDot.Fill = LevelBrush(level);
        Toast.Classes.Set("show", true);
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private static string Glyph(Level level) => level switch
    {
        Level.Ok => "✓",
        Level.Warn => "!",
        Level.Err => "✕",
        _ => "·",
    };

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

    private void OnOpenReleases(object? sender, RoutedEventArgs e)
    {
        if (_update is not null) AppUpdate.OpenInBrowser(_update.Url);
    }

    private void OnDismissUpdate(object? sender, RoutedEventArgs e) => UpdateBanner.IsVisible = false;

    private string Text(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is string s ? s : key;

    private IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush b
            ? b
            : Brushes.Gray;
}
