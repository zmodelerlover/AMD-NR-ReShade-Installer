// What the person makes of a game in this app alone: the name it shows under, and a cover of their own.
// Neither touches the game's folder.

using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmdNr.Core;

namespace AmdNr.App;

public partial class GameSheet
{
    private void OnRename(object? sender, RoutedEventArgs e)
    {
        if (_card is not { } card) return;
        NameBox.Text = card.Name;
        NameView.IsVisible = false;
        NameEdit.IsVisible = true;
        NameBox.Focus();
        NameBox.SelectAll();
    }

    private void OnNameSave(object? sender, RoutedEventArgs e) => EndRename(save: true);

    private void OnNameCancel(object? sender, RoutedEventArgs e) => EndRename(save: false);

    /// <summary>Enter keeps the name, Escape drops it -- and only that: the sheet stays open.</summary>
    private void OnNameKey(object? sender, KeyEventArgs e)
    {
        if (e.Key is not (Key.Enter or Key.Escape)) return;
        e.Handled = true;
        EndRename(save: e.Key == Key.Enter);
    }

    private void EndRename(bool save)
    {
        if (save && _card is { } card) Library.Rename(card, NameBox.Text);
        NameEdit.IsVisible = false;
        NameView.IsVisible = true;
    }

    internal async void OnPickCover(object? sender, RoutedEventArgs e)
    {
        if (_card is { } card && await PickImageAsync("Str.CoverPickTitle") is { } path)
            await SetArtAsync(card, () => Library.SetCoverAsync(card, path));
    }

    internal async void OnResetCover(object? sender, RoutedEventArgs e)
    {
        if (_card is { } card) await SetArtAsync(card, () => Library.SetCoverAsync(card, null));
    }

    private async void OnPickHero(object? sender, RoutedEventArgs e)
    {
        if (_card is { } card && await PickImageAsync("Str.HeroPickTitle") is { } path)
            await SetArtAsync(card, () => Library.SetHeroAsync(card, path));
    }

    private async void OnResetHero(object? sender, RoutedEventArgs e)
    {
        if (_card is { } card) await SetArtAsync(card, () => Library.SetHeroAsync(card, null));
    }

    private async Task<string?> PickImageAsync(string titleKey)
    {
        var picked = await _shell.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Ui.Text(titleKey),
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(Ui.Text("Str.CoverFiles")) { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.webp", "*.bmp"] },
            ],
        });
        return picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
    }

    private async Task SetArtAsync(GameCard card, Func<Task> change)
    {
        try { await change(); }
        catch (Exception)
        {
            // Whatever the decoder makes of a file that is not a picture, and a file that cannot be read:
            // the art stays as it was.
            _shell.Toast(Ui.Text("Str.CoverFailed"), Level.Warn);
        }
        ShowArtMenus(card);
    }

    /// <summary>"Back to the one found online" only where there is a picture of the person's own.</summary>
    private void ShowArtMenus(GameCard card)
    {
        Side.CoverResetItem.IsEnabled = ArtCoverResetItem.IsEnabled = card.Entry.CustomCover is not null;
        HeroResetItem.IsEnabled = card.Entry.CustomHero is not null;
    }

    /// <summary>A newly opened game starts with its name shown, not the previous one's half-typed rename.</summary>
    private void ShowCustom(GameCard card)
    {
        NameEdit.IsVisible = false;
        NameView.IsVisible = true;
        ShowArtMenus(card);
        _settingAutoUpdate = true;
        Side.AutoUpdateSwitch.IsChecked = card.Entry.AutoUpdate;
        _settingAutoUpdate = false;
    }

    private bool _settingAutoUpdate;

    internal void OnAutoUpdateChanged(object? sender, RoutedEventArgs e)
    {
        if (_settingAutoUpdate || _card is not { } card) return;
        card.Entry.AutoUpdate = Side.AutoUpdateSwitch.IsChecked == true;
        Library.Save();
    }

    /// <summary>Update, as the button does it, for a game whose automatic update is on: the sheet opens on
    /// it so the download, the steps and the result are where they always are. Only an update of the route
    /// that is installed -- switching routes asks first, and nobody is there to answer. True when it took.</summary>
    public async Task<bool> UpdateAsync(GameCard card)
    {
        Open(card);
        if (!card.Outdated || RouteUndecided(card) || card.InstalledVia != card.Entry.Preset.Family()) return false;
        await InstallAsync();
        return !card.Outdated;
    }
}
