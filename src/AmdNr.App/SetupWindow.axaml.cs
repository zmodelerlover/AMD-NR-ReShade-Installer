// The first run. Four steps: which language, whether this machine can run it at all, whether every
// payload is here, and which games are installed.
//
// It is a separate window rather than a page inside the main one on purpose: the main window has a
// busy-state machine of its own around install and uninstall, and folding a wizard into it would
// mean every one of those paths had to know whether setup was still open. Everything this window
// does lands in the same places the main window already reads -- settings.json, games.json and the
// cache -- so it hands nothing back except "the person got to the end".

using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using AmdNr.Core;

namespace AmdNr.App;

/// <summary>One component as the Files step lists it.</summary>
public sealed record SetupPayloadRow(string Name, string Version, string Size, string State, bool Ready)
{
    public bool NotReady => !Ready;
}

public sealed record FoundGameRow(string Name, string Platform);

public partial class SetupWindow : Window
{
    private readonly AppConfig _config = AppConfig.Load();
    private readonly HttpClient _http = PayloadCache.DefaultClient(App.Version);
    private readonly ObservableCollection<SetupPayloadRow> _payloads = [];
    private readonly ObservableCollection<FoundGameRow> _found = [];

    private PayloadManifest? _manifest;
    private int _step;
    private bool _busy;
    private bool _settingLanguage;

    private const int StepLanguage = 0, StepMachine = 1, StepFiles = 2, StepGames = 3, StepCount = 4;

    /// <summary>True once the person reached the end. The caller only opens the main window on that,
    /// so closing the wizard with the X leaves setup un-done and it asks again next time.</summary>
    public bool Completed { get; private set; }

    public SetupWindow()
    {
        InitializeComponent();

        PayloadList.ItemsSource = _payloads;
        FoundList.ItemsSource = _found;

        _settingLanguage = true;
        LanguageBox.ItemsSource = App.Languages.Select(l => l.Name).ToList();
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(App.Languages, l => l.Code == App.CurrentLanguage));
        _settingLanguage = false;

        ShowStep(StepLanguage);
    }

    // -- Steps -------------------------------------------------------------------------------------

    private void ShowStep(int step)
    {
        _step = step;
        PageLanguage.IsVisible = step == StepLanguage;
        PageMachine.IsVisible = step == StepMachine;
        PageFiles.IsVisible = step == StepFiles;
        PageGames.IsVisible = step == StepGames;

        StepOf.Text = string.Format(Text("Str.SetupStepOf"), step + 1, StepCount);
        BackButton.IsVisible = step > StepLanguage;
        // Only the two steps that do work are skippable; there is nothing to skip past on the others.
        SkipButton.IsVisible = step is StepFiles or StepGames;
        Localize(NextLabel, step == StepGames ? "Str.SetupFinish" : "Str.SetupNext");

        for (var i = 0; i < Pips.Children.Count; i++)
        {
            Pips.Children[i].Classes.Set("done", i < step);
            Pips.Children[i].Classes.Set("here", i == step);
        }

        // Each step reads its own state when it is reached, not on construction: the machine can be
        // fixed and the cache filled while the wizard is open.
        if (step == StepMachine) ShowMachine();
        if (step == StepFiles) _ = CheckFilesAsync();
    }

    private void OnBack(object? sender, RoutedEventArgs e)
    {
        if (_busy || _step == StepLanguage) return;
        ShowStep(_step - 1);
    }

    private void OnNext(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_step < StepGames)
        {
            ShowStep(_step + 1);
            return;
        }

        var settings = Settings.Load();
        settings.SetupDone = true;
        settings.Language = App.CurrentLanguage;
        settings.Save();
        Completed = true;
        Close();
    }

    // -- 1. Language -------------------------------------------------------------------------------

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_settingLanguage) return;
        var index = LanguageBox.SelectedIndex;
        if (index < 0 || index >= App.Languages.Length) return;
        var code = App.Languages[index].Code;
        if (code == App.CurrentLanguage) return;

        App.ChangeLanguage(code);
        var settings = Settings.Load();
        settings.Language = code;
        settings.Save();

        // The header counter and anything else written in code was written in the old language.
        ShowStep(_step);
    }

    // -- 2. This machine ---------------------------------------------------------------------------

    private void ShowMachine()
    {
        var state = GpuService.Read();
        GpuText.Text = state.Gpu;
        DriverText.Text = state.Hip7 ? state.Driver : Text("Str.Missing");

        SetStatus(GpuStatus, GpuStatusIcon, state.LooksLikeRadeon);
        Localize(GpuStatusText, state.LooksLikeRadeon ? "Str.GpuOk" : "Str.GpuBad");
        SetStatus(HipStatus, HipStatusIcon, state.Hip7);
        Localize(HipText, state.Hip7 ? "Str.Ready" : "Str.Missing");

        MachineVerdictTile.Classes.Set("ok", state.Ready);
        MachineVerdictTile.Classes.Set("err", !state.Ready);
        MachineVerdictIcon.Data = Vector(state.Ready ? "IconOk" : "IconWarn");
        Localize(MachineVerdictText, state.Ready ? "Str.SetupMachineOk" : "Str.SetupMachineBad");

        // The specific reason, which is what someone actually needs to go and fix.
        if (!state.Hip7) ShowMachineWarning(Text("Str.HipMissing"));
        else if (!state.LooksLikeRadeon) ShowMachineWarning(Text("Str.NotRadeon"));
        else MachineWarningBox.IsVisible = false;
    }

    private void ShowMachineWarning(string text)
    {
        MachineWarning.Text = text;
        MachineWarningBox.IsVisible = true;
    }

    private static void SetStatus(Border pill, PathIcon icon, bool ok)
    {
        pill.Classes.Set("ok", ok);
        pill.Classes.Set("err", !ok);
        icon.Data = Vector(ok ? "IconCheck" : "IconErr");
    }

    // -- 3. Files ----------------------------------------------------------------------------------

    /// <summary>What is in the cache already. Hashing every payload is about a second for the 141 MB
    /// of weights, so it runs off the UI thread and the verdict says it is working meanwhile.</summary>
    private async Task CheckFilesAsync()
    {
        Busy(true);
        FilesSpin.IsVisible = true;
        FilesVerdictIcon.Data = null;
        FilesVerdictTile.Classes.Set("ok", false);
        FilesVerdictTile.Classes.Set("err", false);
        Localize(FilesVerdictText, "Str.SetupFilesCheck");
        FilesDetail.Text = "";
        DownloadButton.IsVisible = false;

        _manifest ??= await LoadManifestAsync();
        if (_manifest is null)
        {
            FilesSpin.IsVisible = false;
            FilesVerdictIcon.Data = Vector("IconWarn");
            FilesVerdictTile.Classes.Set("err", true);
            Localize(FilesVerdictText, "Str.ManifestFailed");
            FilesDetail.Text = Text("Str.SetupFilesOffline");
            _payloads.Clear();
            Busy(false);
            return;
        }

        var manifest = _manifest;
        var rows = await Task.Run(() => manifest.Components
            .Select(pair =>
            {
                var complete = false;
                try { complete = PayloadCache.IsComplete(manifest, pair.Key); }
                catch (Exception e) when (e is InstallException or IOException)
                {
                    // A component this build does not understand is not a reason to show nothing.
                }
                var bytes = pair.Value.Files.Aggregate(0UL, (sum, f) => sum + f.Size);
                return new SetupPayloadRow(
                    pair.Key,
                    pair.Value.Version,
                    Size(bytes),
                    Text(complete ? "Str.PayloadReadyShort" : "Str.NotDownloadedShort"),
                    complete);
            })
            .ToList());

        _payloads.Clear();
        foreach (var row in rows) _payloads.Add(row);

        FilesSpin.IsVisible = false;
        var missing = rows.Count(r => !r.Ready);
        if (missing == 0)
        {
            FilesVerdictIcon.Data = Vector("IconOk");
            FilesVerdictTile.Classes.Set("ok", true);
            Localize(FilesVerdictText, "Str.SetupFilesAllReady");
            FilesDetail.Text = "";
            DownloadButton.IsVisible = false;
        }
        else
        {
            FilesVerdictIcon.Data = Vector("IconInfo");
            FilesVerdictText.Text = string.Format(Text("Str.SetupFilesMissing"), missing, rows.Count);
            FilesDetail.Text = Text("Str.SetupFilesLater");
            DownloadButton.IsVisible = true;
        }
        Busy(false);
    }

    private async Task<PayloadManifest?> LoadManifestAsync()
    {
        var cache = new PayloadCache(_http);
        try
        {
            return await cache.FetchManifestAsync(
                _config.Payload.Owner, _config.Payload.Repo, _config.Payload.Branch, _config.Payload.File,
                _config.Payload.ManifestUrl);
        }
        catch (Exception e) when (e is HttpRequestException or InstallException or TaskCanceledException)
        {
            // Offline, or the content repository is not up: the copy beside the executable pins the
            // same hashes, so whatever is already cached still installs.
            return PayloadCache.LoadLocalManifest();
        }
    }

    private async void OnDownload(object? sender, RoutedEventArgs e)
    {
        if (_busy || _manifest is null) return;
        Busy(true);
        DownloadSpin.IsVisible = true;
        DownloadIcon.IsVisible = false;

        var progress = new Progress<DownloadProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress.IsVisible = true;
            Progress.IsIndeterminate = p.Fraction is null;
            if (p.Fraction is { } fraction) Progress.Value = fraction * 100;
            FilesStatus.Text = $"{Text("Str.Downloading")} {p.File} — {p.Received / 1_048_576} MB";
        }));

        var cache = new PayloadCache(_http);
        try
        {
            foreach (var component in _manifest.Components.Keys.ToList())
                await cache.EnsureAsync(_manifest, component, progress);
            FilesStatus.Text = "";
        }
        catch (Exception ex) when (ex is InstallException or HttpRequestException or IOException)
        {
            FilesStatus.Text = ex.Message;
            Localize(DownloadLabel, "Str.SetupFilesRetry");
        }
        finally
        {
            Progress.IsVisible = false;
            DownloadSpin.IsVisible = false;
            DownloadIcon.IsVisible = true;
            Busy(false);
        }

        // Re-read rather than assume: a component that failed has to keep saying so.
        await CheckFilesAsync();
    }

    // -- 4. Games ----------------------------------------------------------------------------------

    private async void OnScan(object? sender, RoutedEventArgs e)
    {
        if (_busy) return;
        Busy(true);
        ScanSpin.IsVisible = true;
        ScanIcon.IsVisible = false;
        Localize(ScanLabel, "Str.ScanningShort");
        ScanStatus.Text = Text("Str.Scanning");
        try
        {
            var found = await Task.Run(GameScanner.ScanAll);

            // Merged with whatever is already listed rather than replacing it: the wizard can be run
            // again from a machine that already has games added by hand.
            var games = GameStore.Load();
            foreach (var game in found)
            {
                if (games.Any(g => Engine.SamePath(g.Path, game.InstallPath))) continue;
                games.Add(GameEntry.From(game));
            }
            games.Sort((a, b) => string.Compare(a.Display, b.Display, StringComparison.CurrentCultureIgnoreCase));
            GameStore.Save(games);

            _found.Clear();
            foreach (var game in games) _found.Add(new FoundGameRow(game.Display, game.Platform.ToString()));

            ScanStatus.Text = games.Count == 0
                ? Text("Str.SetupGamesNone")
                : string.Format(Text("Str.SetupGamesFound"), games.Count);
        }
        finally
        {
            ScanSpin.IsVisible = false;
            ScanIcon.IsVisible = true;
            Localize(ScanLabel, "Str.Scan");
            Busy(false);
        }
    }

    // -- Chrome ------------------------------------------------------------------------------------

    private void Busy(bool on)
    {
        _busy = on;
        NextButton.IsEnabled = !on;
        BackButton.IsEnabled = !on;
        SkipButton.IsEnabled = !on;
        DownloadButton.IsEnabled = !on;
        ScanButton.IsEnabled = !on;
        LanguageBox.IsEnabled = !on;
    }

    /// <summary>A download size someone can compare against their connection. Whole megabytes
    /// truncate every component but the weights to "0 MB", which reads as "nothing to download".</summary>
    private static string Size(ulong bytes) => bytes switch
    {
        >= 10 * 1_048_576 => $"{bytes / 1_048_576} MB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    private static Geometry? Vector(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as Geometry : null;

    private static void Localize(TextBlock block, string key) =>
        block[!TextBlock.TextProperty] = block.GetResourceObservable(key).ToBinding();

    private static string Text(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is string s ? s : key;
}
