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

public sealed record FoundGameRow(string Name, string Platform);

public partial class SetupWindow : Window
{
    private readonly Session _session = new();
    private readonly ObservableCollection<FoundGameRow> _found = [];

    private int _step;
    private bool _busy;
    private bool _settingLanguage;
    private bool _scanned;

    private const int StepLanguage = 0, StepMachine = 1, StepFiles = 2, StepGames = 3, StepCount = 4;

    /// <summary>True once the person reached the end. The caller only opens the main window on that,
    /// so closing the wizard with the X leaves setup un-done and it asks again next time.</summary>
    public bool Completed { get; private set; }

    public SetupWindow()
    {
        InitializeComponent();

        Payloads.Attach(_session, this);
        _session.BusyChanged += () => Busy(_session.Busy);
        FoundList.ItemsSource = _found;

        _settingLanguage = true;
        // It used to be 720x620 and fixed: a page either fitted that or was scrolled to blind, and
        // the first page did not fit. It resizes now, and WindowFit keeps it inside the screen.
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
        // Leaving this step without having scanned -- by Skip, or by Continue on an untouched page
        // -- is an answer, and the main window used to ignore it: it scanned anything with an empty
        // list, which read as the wizard having done it anyway after being told not to.
        settings.ScanDeclined = !_scanned;
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

    private async void ShowMachine()
    {
        var state = await _session.ReadMachineAsync();
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

    /// <summary>What is in the cache already, read when the step is reached: the list is fetched
    /// the first time, and the panel does the rest -- downloading, retrying, taking files by hand.</summary>
    private async Task CheckFilesAsync()
    {
        if (_session.Manifest is null) await _session.LoadManifestAsync();
        else await Payloads.RefreshAsync();
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
            IReadOnlyList<ScannedGame> found;
            // Reading someone else's launcher data is the least predictable thing this app does, and
            // this is an async void handler: an escape here takes the wizard down, and SetupDone is
            // not saved yet -- so the next launch opens the same wizard and it falls over again.
            try { found = await Task.Run(GameScanner.ScanAll); }
            catch (Exception ex)
            {
                InstallLog.Append($"{DateTime.Now:s} setup scan  {ex.GetType().Name}: {ex.Message}");
                ScanStatus.Text = Text("Str.Unexpected");
                return;
            }
            _scanned = true;

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
        ScanButton.IsEnabled = !on;
        LanguageBox.IsEnabled = !on;
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        WindowFit.ToScreen(this);
    }

    private static Geometry? Vector(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as Geometry : null;

    private static void Localize(TextBlock block, string key) =>
        block[!TextBlock.TextProperty] = block.GetResourceObservable(key).ToBinding();

    private static string Text(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is string s ? s : key;
}
