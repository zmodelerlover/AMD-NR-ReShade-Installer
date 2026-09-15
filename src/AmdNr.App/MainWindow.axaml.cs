using Avalonia;
using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
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
    private readonly ObservableCollection<GameEntry> _games = [];
    private readonly ObservableCollection<ReportLine> _report = [];
    private readonly List<Preset> _offered = [];

    private PayloadManifest? _manifest;
    private AppRelease? _update;
    private bool _busy;
    private bool _settingPreset;

    public MainWindow()
    {
        InitializeComponent();

        GameList.ItemsSource = _games;
        ReportList.ItemsSource = _report;
        VersionText.Text = $"v{App.Version}";

        LanguageBox.ItemsSource = App.Languages.Select(l => l.Name).ToList();
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(App.Languages, l => l.Code == App.CurrentLanguage));

        foreach (var game in GameStore.Load()) _games.Add(game);
        UpdateEmptyHint();
        ShowSystem();

        if (_games.Count > 0) GameList.SelectedIndex = 0;
        else SetButtons(false);

        _ = StartBackgroundWorkAsync();
    }

    // -- Startup ---------------------------------------------------------------------------------

    /// <summary>The manifest and the update check, neither of which may hold the window up. If
    /// either is unreachable the app still installs from whatever is already in the cache.</summary>
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
            Status(Text("Str.ManifestFailed"));
        }

        _update = await AppUpdate.CheckAsync(_http, _config.App, App.Version);
        if (_update is not null)
        {
            UpdateText.Text = $"{Text("Str.UpdateAvailable")} — v{_update.Version}";
            UpdateBanner.IsVisible = true;
        }

        if (_manifest is not null && GameList.SelectedItem is GameEntry) await RefreshAsync();
    }

    private void ShowSystem()
    {
        var state = GpuService.Read();
        GpuText.Text = state.Gpu;
        DriverText.Text = state.Driver;
        HipText.Text = state.Hip7 ? Text("Str.Ready") : "—";
        HipText.Foreground = state.Hip7 ? Brush("Ok") : Brush("Err");

        if (!state.Hip7) ShowWarning(Text("Str.HipMissing"));
        else if (!state.LooksLikeRadeon) ShowWarning(Text("Str.NotRadeon"));
        else SystemWarning.IsVisible = false;
    }

    private void ShowWarning(string text)
    {
        SystemWarning.Text = text;
        SystemWarning.IsVisible = true;
    }

    // -- The game list ---------------------------------------------------------------------------

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
        if (_games.Any(g => Engine.SamePath(g.Path, path)))
        {
            GameList.SelectedItem = _games.First(g => Engine.SamePath(g.Path, path));
            return;
        }

        var entry = new GameEntry { Path = path, Preset = Guess(path) };
        _games.Add(entry);
        GameStore.Save(_games);
        UpdateEmptyHint();
        GameList.SelectedItem = entry;
    }

    /// <summary>A first guess only, and always overridable: the emulators name themselves, and
    /// anything 32-bit can only be a bridge route.</summary>
    private static Preset Guess(string path)
    {
        if (File.Exists(Path.Combine(path, "pcsx2-qt.exe"))) return Preset.Pcsx2;
        if (File.Exists(Path.Combine(path, "rpcs3.exe"))) return Preset.Rpcs3;
        return Work.Detect(path).Route == Route.X86 ? Preset.X86Dx11 : Preset.Dx11;
    }

    private void OnRemoveGame(object? sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry entry) return;
        _games.Remove(entry);
        GameStore.Save(_games);
        UpdateEmptyHint();
        if (_games.Count == 0) ClearDetails();
    }

    private void UpdateEmptyHint() => EmptyHint.IsVisible = _games.Count == 0;

    private async void OnGameSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry entry)
        {
            ClearDetails();
            return;
        }

        TargetName.Text = entry.Display;
        TargetPath.Text = entry.Path;

        var detected = Work.Detect(entry.Path);
        DetectedLine.Text = detected.Line ?? "";

        // Five of the ten API-by-architecture combinations do not exist; the detected width rules
        // out the rest, so a route that cannot work is never on the list.
        _offered.Clear();
        _offered.AddRange(Presets.Offered(detected));
        _settingPreset = true;
        PresetBox.ItemsSource = _offered.Select(p => p.Label()).ToList();
        var index = _offered.IndexOf(entry.Preset);
        PresetBox.SelectedIndex = index >= 0 ? index : 0;
        _settingPreset = false;
        entry.Preset = _offered[PresetBox.SelectedIndex];
        PresetNote.Text = entry.Preset.Note();

        await RefreshAsync();
    }

    private void ClearDetails()
    {
        TargetName.Text = "—";
        TargetPath.Text = "";
        DetectedLine.Text = "";
        PresetNote.Text = "";
        PresetBox.ItemsSource = null;
        _report.Clear();
        SetButtons(false);
    }

    private async void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingPreset || GameList.SelectedItem is not GameEntry entry) return;
        if (PresetBox.SelectedIndex < 0 || PresetBox.SelectedIndex >= _offered.Count) return;

        entry.Preset = _offered[PresetBox.SelectedIndex];
        PresetNote.Text = entry.Preset.Note();
        GameStore.Save(_games);
        await RefreshAsync();
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

    // -- Pre-flight, install, uninstall -----------------------------------------------------------

    private async Task RefreshAsync()
    {
        if (GameList.SelectedItem is not GameEntry entry || _busy) return;
        var pins = Pins();
        var payloads = CachedPayloadFolder();

        var report = await Task.Run(() => Work.Preflight(entry.Path, payloads ?? "", entry.Preset, pins));
        Show(report);
        SetButtons(true);
        Status(payloads is null ? Text("Str.Verifying") : Text("Str.PayloadsReady"));
    }

    private async void OnRecheck(object? sender, RoutedEventArgs e) => await RefreshAsync();

    private async void OnInstall(object? sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry entry || _busy) return;
        Busy(true);
        try
        {
            var folder = await EnsurePayloadsAsync(entry.Preset);
            if (folder is null) return;

            Status(Text("Str.Working"));
            var pins = Pins();
            var report = await Task.Run(() => Work.Install(entry.Path, folder, entry.Preset, pins));
            Show(report);
            WriteLog(report, $"install {entry.Preset.Label()} -> {entry.Path}");
            Status(report.Failed ? Text("Str.LogSaved") : Text("Str.Ready"));
        }
        finally
        {
            Busy(false);
        }
    }

    private async void OnUninstall(object? sender, RoutedEventArgs e)
    {
        if (GameList.SelectedItem is not GameEntry entry || _busy) return;
        Busy(true);
        try
        {
            var report = await Task.Run(() => Work.Uninstall(entry.Path, entry.Preset));
            Show(report);
            WriteLog(report, $"uninstall {entry.Preset.Label()} -> {entry.Path}");
            Status(report.Failed ? Text("Str.LogSaved") : Text("Str.Ready"));
        }
        finally
        {
            Busy(false);
        }
    }

    /// <summary>Downloads whatever this route needs, then hands back the folder to install from --
    /// which is the same shape of folder someone would have unzipped by hand, so the engine cannot
    /// tell the difference.</summary>
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
            // Every route needs the add-on and the runtime; the bridge routes also need their own
            // pair and the pinned ReShade beside it.
            var components = preset.Route() == Route.X86
                ? new[] { PayloadManifest.BridgeComponent, PayloadManifest.X86ExtrasComponent, PayloadManifest.RuntimeComponent }
                : [PayloadManifest.AddonComponent, PayloadManifest.RuntimeComponent];

            string? first = null;
            foreach (var component in components)
            {
                var folder = await cache.EnsureAsync(_manifest, component, progress);
                first ??= folder;
                // The x64 route reads everything out of one folder, so the add-on and the runtime
                // are merged into the first of them.
                if (preset.Route() == Route.X64 && folder != first) MergeInto(folder, first!);
                if (preset.Route() == Route.X86 && component != PayloadManifest.BridgeComponent) MergeInto(folder, first!);
            }
            Progress.IsVisible = false;
            return first;
        }
        catch (Exception e) when (e is InstallException or HttpRequestException or IOException)
        {
            Progress.IsVisible = false;
            _report.Add(new ReportLine("!", e.Message, Brush("Err")));
            Status(Text("Str.LogSaved"));
            return null;
        }
    }

    /// <summary>Hard-links where the filesystem allows it and copies where it does not, so the
    /// weights are not written twice on the way to one install folder.</summary>
    private static void MergeInto(string from, string to)
    {
        foreach (var source in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(from, source);
            var target = Path.Combine(to, relative);
            if (File.Exists(target) && new FileInfo(target).Length == new FileInfo(source).Length) continue;
            Engine.MakeParent(target);
            File.Copy(source, target, overwrite: true);
        }
    }

    /// <summary>The folder a route would install from, when it is already complete in the cache.</summary>
    private string? CachedPayloadFolder()
    {
        if (_manifest is null) return null;
        try
        {
            if (!PayloadCache.IsComplete(_manifest, PayloadManifest.RuntimeComponent)) return null;
            if (!PayloadCache.IsComplete(_manifest, PayloadManifest.AddonComponent)) return null;
            var runtime = _manifest.Component(PayloadManifest.RuntimeComponent);
            return PayloadCache.FolderFor(PayloadManifest.RuntimeComponent, runtime.Version);
        }
        catch (InstallException)
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
            var (glyph, brush) = level switch
            {
                Level.Ok => ("✓", Brush("Ok")),
                Level.Warn => ("!", Brush("Warn")),
                Level.Err => ("✕", Brush("Err")),
                _ => ("·", Brush("Muted")),
            };
            _report.Add(new ReportLine(glyph, text, brush));
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

    private void Busy(bool on)
    {
        _busy = on;
        SetButtons(!on);
        if (!on) Progress.IsVisible = false;
    }

    private void SetButtons(bool enabled)
    {
        var has = GameList.SelectedItem is GameEntry;
        InstallButton.IsEnabled = enabled && has;
        UninstallButton.IsEnabled = enabled && has;
        RecheckButton.IsEnabled = enabled && has;
    }

    private void Status(string text) => StatusText.Text = text;

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
