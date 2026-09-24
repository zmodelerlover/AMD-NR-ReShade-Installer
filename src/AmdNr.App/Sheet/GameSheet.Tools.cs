// The smaller things the sheet does around an install: start the game, open its folder or its
// PCGamingWiki page, take the row out of the list, point the detection at another executable, and
// gather everything a support thread would ask for into one zip.

using System.Diagnostics;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using AmdNr.Core;

namespace AmdNr.App;

public partial class GameSheet
{
    /// <summary>Starts the game. Through Steam when it has an app id, because that is the path the
    /// game expects -- its own launcher, its DRM, its overlay -- and running the executable straight
    /// is what breaks that. Not while an install is writing the very DLLs the game would open.</summary>
    private void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (Session.Busy || _card is not { } card) return;
        var what = card.Entry.Platform == GamePlatform.Steam && card.Entry.AppId is { Length: > 0 } id
            ? $"steam://rungameid/{id}"
            : card.Graphics?.Executable;
        if (what is null || (!what.StartsWith("steam://", StringComparison.Ordinal) && !File.Exists(what)))
        {
            _shell.Toast(Ui.Text("Str.PlayFailed"), Level.Warn);
            return;
        }
        try { Process.Start(new ProcessStartInfo(what) { UseShellExecute = true, WorkingDirectory = card.Path }); }
        catch (Exception e2) when (e2 is System.ComponentModel.Win32Exception or FileNotFoundException
                                       or InvalidOperationException)
        {
            _shell.Toast(Ui.Text("Str.PlayFailed"), Level.Warn);
        }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_card is { } card) AppUpdate.OpenFolder(card.Path);
    }

    /// <summary>The game's page on PCGamingWiki, which is where the API question is actually
    /// settled: files are honest about what they link and silent about what the game offers.</summary>
    private void OnOpenWiki(object? sender, RoutedEventArgs e)
    {
        if (_card is { } card)
            AppUpdate.OpenInBrowser("https://www.pcgamingwiki.com/w/index.php?search=" + Uri.EscapeDataString(card.Name));
    }

    /// <summary>Takes a game out of the list. It removes a row and nothing else: whatever is
    /// installed in that folder stays installed, which is why the message says so.</summary>
    private void OnRemoveGame(object? sender, RoutedEventArgs e)
    {
        if (_card is not { } card || Session.Busy) return;
        var installed = card.Installed;
        Close();
        Library.Remove(card);
        _shell.Toast(Ui.Format(installed ? "Str.RemovedGameInstalled" : "Str.RemovedGame", card.Name),
            installed ? Level.Warn : Level.Ok);
    }

    /// <summary>Point the detection at a file, or put it back on its own answer. The route list is
    /// rebuilt from it, because the width it reads is what orders that list.</summary>
    private void OnChooseExecutable(object? sender, RoutedEventArgs e) => _shell.Run("choose executable", async () =>
    {
        if (_card is not { } card || Session.Busy) return;
        if (card.Entry.Executable is { Length: > 0 })
        {
            card.Entry.Executable = null;
        }
        else
        {
            var picked = await _shell.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Ui.Text("Str.ExeSection"),
                AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("*.exe") { Patterns = ["*.exe"] }],
                SuggestedStartLocation = await _shell.StorageProvider.TryGetFolderFromPathAsync(card.Path),
            });
            var path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (string.IsNullOrWhiteSpace(path)) return;

            // Inside this game's folder, and a PE this can read. Both are refusals with a sentence
            // rather than a silent fallback to detection, which would look like the pick was taken.
            if (!Engine.IsInside(Engine.WeaklyCanonical(card.Path), Engine.WeaklyCanonical(path)))
            {
                _shell.Toast(Ui.Text("Str.ExeNotHere"), Level.Err);
                return;
            }
            if (Engine.MachineOfFile(path) is null)
            {
                _shell.Toast(Ui.Text("Str.ExeUnreadable"), Level.Err);
                return;
            }
            card.Entry.Executable = path;
        }
        Library.Redetect(card);
        Show(card);
    });

    /// <summary>Everything someone would otherwise be asked for, three messages at a time, in one
    /// zip. Nothing is sent: the file is saved and Explorer opens with it selected.</summary>
    private void OnReport(object? sender, RoutedEventArgs e) => _shell.Run("report", async () =>
    {
        if (Session.Busy) return;
        ReportButton.IsEnabled = false;
        Status(Ui.Text("Str.ReportWorking"));
        try
        {
            var card = _card;
            var target = card is null ? null : TargetFor(card);
            // Reading a whole game folder and a ReShade log is disk work, not UI work.
            var path = await Task.Run(() => SupportReport.Save(card, target));
            if (path is null)
            {
                Status(Ui.Format("Str.ReportFailed", AppPaths.Logs));
                _shell.Toast(Ui.Format("Str.ReportFailed", AppPaths.Logs), Level.Err);
                return;
            }
            Status(Ui.Format("Str.ReportSaved", path));
            _shell.Toast(Ui.Format("Str.ReportSaved", Path.GetFileName(path)), Level.Ok);
        }
        finally
        {
            ReportButton.IsEnabled = true;
        }
    });
}
