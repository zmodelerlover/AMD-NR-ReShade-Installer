// What the sheet says about the game and about what was just done to it: one sentence in the banner,
// the steps an install goes through, and every line of the report folded under Details.

using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using AmdNr.Core;

namespace AmdNr.App;

/// <summary>One line of a report, with the colour it reads in. The engine already decided the
/// level; this only chooses how it looks.</summary>
public sealed record ReportLine(Geometry? Glyph, string Text, IBrush Brush);

public partial class GameSheet
{
    private void Show(Report report)
    {
        _report.Clear();
        foreach (var (level, text) in report.Lines)
            _report.Add(new ReportLine(Ui.Glyph(level), text, Ui.LevelBrush(level)));
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

    private void Status(string text) => StatusText.Text = text;

    /// <summary>The pressed button says what it is doing; the other one only waits, and everything
    /// else is locked through <see cref="Session.Busy"/>.</summary>
    private void Busy(bool on, Button? pressed = null)
    {
        _ownBusy = on;
        Session.Busy = on;
        InstallSpin.IsVisible = on && pressed == InstallButton;
        UninstallSpin.IsVisible = on && pressed == UninstallButton;
        InstallIcon.IsVisible = !InstallSpin.IsVisible;
        UninstallIcon.IsVisible = !UninstallSpin.IsVisible;
        if (InstallSpin.IsVisible) Ui.Localize(InstallLabel, "Str.Installing");
        Ui.Localize(UninstallLabel, UninstallSpin.IsVisible ? "Str.Removing" : "Str.Uninstall");
        if (!on) ShowInstallLabel();

        // While it runs, the verdict slot says so, rather than showing the check that came before.
        if (on && pressed is not null)
        {
            SetVerdict(Level.Info, Ui.Text(pressed == InstallButton ? "Str.Installing" : "Str.Removing"), "");
            ResultSpin.IsVisible = true;
        }
    }

    /// <summary>The pre-flight in the header's badge. The report itself stays folded under Details.</summary>
    private void ShowVerdict(Report report, GameCard card)
    {
        var warnings = report.Lines.Count(l => l.Level == Level.Warn);
        var errors = report.Lines.Count(l => l.Level == Level.Err);
        if (errors > 0)
            ShowPill(Level.Err, Ui.Count("Str.PreflightErr", errors), Ui.Text("Str.SeeDetails"));
        // The same amber the tile and the button already say it in; a green "installed" under an
        // Update button read as two answers to one question.
        else if (card.Outdated)
            ShowPill(Level.Warn, Ui.Text("Str.PreflightOutdated"), Ui.Text("Str.PreflightOutdatedDetail"));
        else if (card.Installed)
            ShowPill(Level.Ok, Ui.Text("Str.PreflightInstalled"), Ui.Text("Str.PreflightInstalledDetail"));
        else if (warnings > 0)
            ShowPill(Level.Warn, Ui.Count("Str.PreflightWarn", warnings), Ui.Text("Str.SeeDetails"));
        else
            ShowPill(Level.Ok, Ui.Text("Str.PreflightOk"), Ui.Text("Str.PreflightOkDetail"));
    }

    /// <summary>The check's answer beside the platform and the API, where the big banner used to stand
    /// before anything was done. Null is a check still running. What an action does still takes the banner.</summary>
    private void ShowPill(Level? level, string title, string detail = "")
    {
        ResultBanner.IsVisible = false;
        Ui.SetLevel(VerdictPill, level);
        VerdictSpin.IsVisible = level is null;
        VerdictGlyph.IsVisible = level is not null;
        VerdictGlyph.Data = level is { } l ? Ui.Glyph(l) : null;
        VerdictTitle.Text = title;
        ToolTip.SetTip(VerdictPill, detail.Length > 0 ? detail : null);
        VerdictPill.IsVisible = true;
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
        var title = level switch
        {
            Level.Err => Ui.Format(action + "Fail", game),
            Level.Warn => string.Format(Ui.Text(warnings == 1 ? action + "Warn" : action + "Warn.Many"), game, warnings),
            _ => Ui.Format(action + "Ok", game),
        };
        var detail = report.Failed
            ? $"{Ui.Count("Str.ResultErrors", Math.Max(1, errors))} {Ui.Text("Str.LogSaved")}"
            : "";
        ShowResult(level, title, detail);
    }

    private void ShowResult(Level level, string title, string detail)
    {
        SetVerdict(level, title, detail, details: level != Level.Ok);
        // Anything short of a clean result wants its lines read, so they are opened for it.
        if (level != Level.Ok) DetailsExpander.IsExpanded = true;
        // The sheet may have been closed during a long download; then the corner says it instead.
        if (!IsOpen) _shell.Toast(title, level);
    }

    private void SetVerdict(Level level, string title, string detail, bool details = false)
    {
        VerdictPill.IsVisible = false;
        Ui.SetLevel(ResultBanner, level == Level.Info ? null : level);
        Ui.SetLevel(ResultDot, level == Level.Info ? null : level);
        ResultSpin.IsVisible = false;
        ResultGlyph.Data = level == Level.Info ? null : Ui.Glyph(level);
        ResultTitle.Text = title;
        ResultDetail.Text = detail;
        ResultDetail.IsVisible = detail.Length > 0 && !details;
        // "See the details below" pointed five hundred pixels away; this goes there.
        ShowDetailsButton.IsVisible = details;
        DnsRetryButton.IsVisible = false;
        CloseGameButton.IsVisible = false;
        CleanRetryButton.IsVisible = false;
        // Off and on again, so the entrance plays even when the banner was already up.
        ResultBanner.IsVisible = false;
        ResultBanner.IsVisible = true;
    }

    /// <summary>The engine's refusals that mean an earlier install's record is in the way: another API's install
    /// it did not clear, or a record whose settings entries an older build wrote differently.</summary>
    private static readonly string[] RecordInTheWay = ["Uninstall previous preset before changing API", "Manifest config mismatch"];

    /// <summary>After a failed install: when an earlier install's record was what refused it, says so in plain words
    /// and offers to take that install out, keeping the settings, and install again.</summary>
    private void OfferCleanUp(Report report)
    {
        if (!report.Lines.Any(l => l.Level == Level.Err && RecordInTheWay.Any(r => l.Text.Contains(r, StringComparison.Ordinal))))
            return;
        SetVerdict(Level.Err, Ui.Text("Str.CleanRetryTitle"), Ui.Text("Str.CleanRetryDetail"), details: true);
        CleanRetryButton.IsVisible = true;
    }

    private void OnCleanRetry(object? sender, RoutedEventArgs e) => _shell.Run("clean up", async () =>
    {
        if (_card is not { } card || Session.Busy) return;
        Busy(true, UninstallButton);
        Report removed;
        try { removed = await RunUninstallAsync(card, InstalledPreset(card), removeConfig: false); }
        finally { Busy(false); }
        if (removed.Failed) ShowOutcome(removed, "Str.Uninstall", card.Name);
        else await InstallAsync();
    });

    private void OnShowDetails(object? sender, RoutedEventArgs e)
    {
        DetailsExpander.IsExpanded = true;
        DetailsExpander.BringIntoView();
    }
}
