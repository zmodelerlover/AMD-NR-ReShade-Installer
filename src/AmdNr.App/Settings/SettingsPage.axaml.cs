// Settings: the language, whether this is the newest release, and who made what.

using Avalonia.Controls;
using Avalonia.Interactivity;
using AmdNr.Core;

namespace AmdNr.App;

public partial class SettingsPage : UserControl
{
    private MainWindow _shell = null!;
    private bool _settingLanguage;

    public SettingsPage()
    {
        InitializeComponent();
        AboutVersion.Text = App.Label(App.Version);
        DataFolderText.Text = AppPaths.Root;
        ToolTip.SetTip(DataFolderText, AppPaths.Root);

        _settingLanguage = true;
        LanguageBox.ItemsSource = App.Languages.Select(l => l.Name).ToList();
        LanguageBox.SelectedIndex = Math.Max(0, Array.FindIndex(App.Languages, l => l.Code == App.CurrentLanguage));
        _settingLanguage = false;
    }

    public void Attach(MainWindow shell)
    {
        _shell = shell;
        shell.Session.UpdateChanged += ShowUpdate;
        shell.Library.IgnoredChanged += ShowIgnored;
        ShowUpdate();
        ShowIgnored();
    }

    private void ShowIgnored()
    {
        var ignored = _shell.Library.Ignored.OrderBy(i => i.Name ?? i.Path, StringComparer.CurrentCultureIgnoreCase).ToList();
        IgnoredList.ItemsSource = ignored;
        IgnoredEmpty.IsVisible = ignored.Count == 0;
    }

    private void OnRestoreIgnored(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: IgnoredGame game }) return;
        var name = game.Name ?? Path.GetFileName(game.Path.TrimEnd('\\', '/'));
        _shell.Toast(Ui.Format(_shell.Library.Restore(game) ? "Str.IgnoredRestored" : "Str.IgnoredGone", name),
            Level.Ok);
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        var index = LanguageBox.SelectedIndex;
        if (_settingLanguage || index < 0 || index >= App.Languages.Length) return;
        var code = App.Languages[index].Code;
        if (code == App.CurrentLanguage) return;

        App.ChangeLanguage(code);
        var settings = Settings.Load();
        settings.Language = code;
        settings.Save();
        // Labels bound with DynamicResource follow the swap on their own; the ones written in code
        // were written in the old language, so everything that wrote one writes it again.
        _shell.Relabel();
    }

    /// <summary>The update card in the state the last check left it: checking, the newest already,
    /// a newer one out, or no answer. "Up to date" is said out loud, because a button that does
    /// nothing visible when there is nothing to do reads as a button that is broken.</summary>
    public void ShowUpdate()
    {
        var update = _shell?.Session.Update ?? new UpdateCheck(UpdateState.Checking);
        UpdateSpin.IsVisible = update.State == UpdateState.Checking;
        UpdateIcon.IsVisible = !UpdateSpin.IsVisible;
        UpdatePill.IsVisible = update.State is UpdateState.UpToDate or UpdateState.Available;
        Ui.SetLevel(UpdatePill, update.State == UpdateState.UpToDate ? Level.Ok : update.State == UpdateState.Available ? Level.Warn : null);
        Ui.SetLevel(UpdateTile, update.State == UpdateState.Available ? Level.Warn : null);
        UpdatePillIcon.Data = Ui.Icon(update.State == UpdateState.UpToDate ? "IconCheck" : "IconDownload");
        UpdatePillText.Text = Ui.Text(update.State == UpdateState.UpToDate ? "Str.UpToDate" : "Str.UpdateShort");

        UpdateText.Text = update.State switch
        {
            UpdateState.Checking => Ui.Text("Str.UpdateChecking"),
            UpdateState.UpToDate => Ui.Format("Str.UpdateLatest", App.Version, update.When?.LocalDateTime.ToString("t") ?? ""),
            UpdateState.Available => App.UpdateOut(update.Release!.Version),
            _ => Ui.Text("Str.UpdateFailed"),
        };
        UpdateButton.IsEnabled = update.State != UpdateState.Checking;
        UpdateNotesButton.IsVisible = update.State != UpdateState.Checking;
        UpdateButton.Content = Ui.Text(update is { State: UpdateState.Available, Release: { } release }
            ? release.ActionKey
            : "Str.UpdateCheck");
        UpdateButton.Classes.Set("primary", update.State == UpdateState.Available);
        UpdateButton.Classes.Set("ghost", update.State != UpdateState.Available);
    }

    private void OnUpdate(object? sender, RoutedEventArgs e) => _shell.Run("update", async () =>
    {
        if (_shell.Session.Update.State == UpdateState.Available)
        {
            UpdateButton.IsEnabled = false;
            await _shell.ApplyUpdateAsync(p => UpdateText.Text = $"{Ui.Text("Str.UpdateWorking")} {p * 100:0}%");
            ShowUpdate();
            return;
        }
        await _shell.Session.CheckForUpdateAsync();
    });

    /// <summary>The notes of the release on offer, or of this one when there is nothing newer.</summary>
    private void OnUpdateNotes(object? sender, RoutedEventArgs e) =>
        _shell.ShowWhatsNew(_shell.Session.Update.Release is { } release ? release.Version : App.Version,
            _shell.Session.Update.Release?.Notes);

    private void OnOpenData(object? sender, RoutedEventArgs e) => AppUpdate.OpenFolder(AppPaths.Root);
}
