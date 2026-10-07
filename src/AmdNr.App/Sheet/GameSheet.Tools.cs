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
    internal void OnPlay(object? sender, RoutedEventArgs e)
    {
        if (!Session.Busy && _card is { } card) Play(card, _shell);
    }

    /// <summary>Starts a game, from the sheet or from its tile; says so in the corner when it cannot.</summary>
    internal static void Play(GameCard card, MainWindow shell)
    {
        var what = card.Entry.Platform == GamePlatform.Steam && card.Entry.AppId is { Length: > 0 } id
            ? $"steam://rungameid/{id}"
            : card.Graphics?.Executable;
        if (what is null || (!what.StartsWith("steam://", StringComparison.Ordinal) && !File.Exists(what)))
        {
            shell.Toast(Ui.Text("Str.PlayFailed"), Level.Warn);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(what) { UseShellExecute = true, WorkingDirectory = card.Path });
            card.Entry.LastPlayed = DateTime.Now;
            shell.Library.Save();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException
                                      or InvalidOperationException)
        {
            shell.Toast(Ui.Text("Str.PlayFailed"), Level.Warn);
        }
    }

    internal void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        if (_card is { } card) AppUpdate.OpenFolder(card.Path, create: false);
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
    internal void OnRemoveGame(object? sender, RoutedEventArgs e)
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
                SuggestedStartLocation = await _shell.StorageProvider.TryGetFolderFromPathAsync(
                    Path.GetDirectoryName(card.Graphics?.Executable) ?? card.Path),
            });
            var path = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
            if (string.IsNullOrWhiteSpace(path)) return;

            // Inside this game's folder, and a PE this can read. Both are refusals with a sentence
            // rather than a silent fallback to detection, which would look like the pick was taken.
            // An emulator's own executable may be anywhere: a launcher keeps its builds in folders of
            // their own, shadPS4's under %APPDATA%, and the install goes beside the one picked.
            var sameEmulator = card.Graphics?.Emulator is { } emulator
                               && emulator.Executables.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase);
            if (!sameEmulator && !Engine.IsInside(Engine.WeaklyCanonical(card.Path), Engine.WeaklyCanonical(path)))
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
            var path = await Task.Run(() => SupportReport.Save(card, target, Session.Manifest));
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

    /// <summary>The game running from the folder an install writes to, as the last check found it.</summary>
    private IReadOnlyList<(int Pid, DateTime Started, string Name)> _running = [];

    /// <summary>Where and for which route <see cref="_running"/> was read, to read it again before ending anything.</summary>
    private (string Target, Preset Preset) _runningFrom;

    /// <summary>When the game is running, the banner says so and offers to close it: an install or an uninstall
    /// cannot replace what it has loaded (Cyberpunk 2077, left running, failed eleven times in a row).</summary>
    private async Task OfferCloseAsync(GameCard card, Preset preset)
    {
        var target = TargetFor(card);
        _runningFrom = (target, preset);
        _running = await Task.Run(() => Work.RunningFrom(target, preset));
        if (_running.Count == 0 || _card != card) return;
        var names = string.Join(", ", _running.Select(p => p.Name));
        SetVerdict(Level.Err, Ui.Format("Str.GameRunning", names), "", details: true);
        CloseGameButton.Content = Ui.Format("Str.CloseGame", names);
        CloseGameButton.IsVisible = true;
    }

    /// <summary>Ends the game, only after asking: whatever it has not saved is lost.</summary>
    private void OnCloseGame(object? sender, RoutedEventArgs e) => _shell.Run("close game", async () =>
    {
        if (_running.Count == 0 || Session.Busy) return;
        var names = string.Join(", ", _running.Select(p => p.Name));
        if (await ChoiceDialog.ShowAsync(_shell, Ui.Format("Str.CloseGame", names), Ui.Format("Str.CloseGameAsk", names),
                [("close", Ui.Format("Str.CloseGame", names), ""), ("keep", Ui.Text("Str.Dismiss"), "")]) != "close")
            return;
        // Read again after the question, and only what still holds the folder and is still the process that was
        // named: an id Windows has handed to another program since is left alone.
        var confirmed = _running;
        var (target, preset) = _runningFrom;
        var left = await Task.Run(() =>
        {
            foreach (var (pid, started, _) in Work.RunningFrom(target, preset)
                         .Where(p => confirmed.Any(c => c.Pid == p.Pid && c.Started == p.Started)))
            {
                try
                {
                    using var process = Process.GetProcessById(pid);
                    if (!Engine.SameProcess(process, started)) continue;
                    process.Kill();
                    process.WaitForExit(10_000);
                }
                catch (Exception x) when (x is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // Gone already, or not ours to end: the check that follows says which.
                }
            }
            return Work.RunningFrom(target, preset).Where(p => confirmed.Any(c => c.Pid == p.Pid)).ToList();
        });
        // One that could not be verified or ended is still there: said, rather than left to look like nothing happened.
        if (left.Count > 0)
            _shell.Toast(Ui.Format("Str.CloseGameFailed", string.Join(", ", left.Select(p => p.Name))), Level.Warn);
        _running = [];
        await RefreshAsync();
    });
}
