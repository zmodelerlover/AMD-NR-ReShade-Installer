// A game's NR settings out to one file and back in: to keep a tuning before trying another, or to take
// somebody's from Discord. What travels, and how it goes back, is SettingsTransfer's.

using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmdNr.Core;

namespace AmdNr.App;

public partial class GameSheet
{
    private static readonly FilePickerFileType TransferFiles = new("AMD-NR") { Patterns = ["*" + SettingsTransfer.Extension, "*.zip"] };

    /// <summary>Where the settings are: beside the executable the install went next to.</summary>
    private static string SettingsFolder(GameCard card)
    {
        var target = TargetFor(card);
        return File.Exists(target) ? Path.GetDirectoryName(target)! : target;
    }

    internal void OnExportSettings(object? sender, RoutedEventArgs e) => _shell.Run("export settings", async () =>
    {
        if (_card is not { InstalledVia: { } route } card || Session.Busy) return;
        var picked = await _shell.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Ui.Text("Str.TransferExport"),
            SuggestedFileName = Safe($"{card.Name} - {route}") + SettingsTransfer.Extension,
            FileTypeChoices = [TransferFiles],
        });
        if (picked?.TryGetLocalPath() is not { Length: > 0 } path) return;

        var folder = SettingsFolder(card);
        var written = await Task.Run(() =>
        {
            try { return SettingsTransfer.Export(folder, route, card.Name, App.Version, path) ? "" : null; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ex.Message; }
        });
        switch (written)
        {
            case null:
                _shell.Toast(Ui.Text("Str.TransferNothing"), Level.Warn);
                break;
            case "":
                _shell.Toast(Ui.Format("Str.TransferExported", Path.GetFileName(path)), Level.Ok);
                break;
            default:
                _shell.Toast(written, Level.Err);
                break;
        }
    });

    internal void OnImportSettings(object? sender, RoutedEventArgs e) => _shell.Run("import settings", async () =>
    {
        if (_card is not { InstalledVia: { } route } card || Session.Busy) return;
        var picked = await _shell.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Ui.Text("Str.TransferImport"),
            AllowMultiple = false,
            FileTypeFilter = [TransferFiles],
        });
        if (picked.Count == 0 || picked[0].TryGetLocalPath() is not { Length: > 0 } path) return;

        var folder = SettingsFolder(card);
        var backup = Path.Combine(AppPaths.Root, "backups", "settings", Safe(card.Name) + $"-{DateTime.Now:yyyyMMdd-HHmmss}");
        var (level, message) = await Task.Run<(Level, string)>(() =>
        {
            try
            {
                SettingsTransfer.Import(path, folder, route, backup);
                return (Level.Ok, Ui.Format("Str.TransferImported", Path.GetFileName(path), backup));
            }
            catch (SettingsTransfer.WrongRouteException wrong)
            {
                return (Level.Err, Ui.Format("Str.TransferWrongRoute", wrong.Route, route));
            }
            catch (Exception ex) when (ex is SettingsTransferException or InvalidDataException)
            {
                return (Level.Err, Ui.Text("Str.TransferInvalid"));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return (Level.Err, ex.Message);
            }
        });
        _shell.Toast(message, level);
    });

    private static string Safe(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)).Trim();
}
