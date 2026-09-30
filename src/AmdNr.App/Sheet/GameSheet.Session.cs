// How the last session went. The install cannot say whether the network then ran, and "is it working?"
// is the question Discord gets most; the logs the add-on and the runtime write beside the game answer
// it, so the sheet reads them whenever it shows the game and whenever the window comes back to the front.

using System.Globalization;
using AmdNr.Core;

namespace AmdNr.App;

public partial class GameSheet
{
    private int _sessionReads;

    /// <summary>Reads the last session off the game's folders and shows it. Only for a game with the mod
    /// in it: without that there is nothing that could have run.</summary>
    public async void ShowSession(GameCard card)
    {
        if (!card.Installed)
        {
            LastSession.IsVisible = false;
            return;
        }
        var read = ++_sessionReads;
        var folders = card.Folders;
        var result = await Task.Run(() => folders.Select(SessionLog.Read).OfType<SessionResult>()
            .OrderByDescending(r => r.When).FirstOrDefault());
        if (read != _sessionReads || _card != card) return;

        LastSession.IsVisible = true;
        LastSession.SessionLine.Text = result?.Line ?? "";
        LastSession.SessionLine.IsVisible = LastSession.SessionLine.Text.Length > 0;
        if (result is null)
        {
            Paint(Level.Info, "Str.SessionNever", "", null);
            return;
        }

        var (level, title) = result.Outcome switch
        {
            SessionOutcome.Ran => (Level.Ok, "Str.SessionRan"),
            SessionOutcome.Crashed => (Level.Err, "Str.SessionCrashed"),
            SessionOutcome.Failed => (Level.Err, "Str.SessionFailed"),
            SessionOutcome.NotLoaded => (Level.Err, "Str.SessionNotLoaded"),
            _ => (Level.Warn, "Str.SessionNoFrames"),
        };
        var hint = result.Outcome switch
        {
            SessionOutcome.Ran => null,
            SessionOutcome.NoFrames => card.InstalledVia == RouteFamily.OptiScaler ? "Str.SessionNoFramesOpti" : "Str.SessionNoFramesReShade",
            SessionOutcome.NotLoaded => "Str.SessionNotLoadedHint",
            _ => "Str.SessionReportHint",
        };

        var culture = Culture();
        // The date the way the logs write theirs, the same in every language: a localised one mixed its
        // digits into the version beside it once the sentence read right to left.
        var parts = new List<string> { result.When.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) };
        if (result.Frames is { } frames && frames > 0) parts.Add(Ui.Format("Str.SessionFrames", frames.ToString("N0", culture)));
        if (result.NetworkMs is { } ms) parts.Add(Ui.Format("Str.SessionGpu", ms.ToString("0.0", culture)));
        if (result.Runtime is { } runtime) parts.Add(Ui.Format("Str.SessionRuntime", runtime));
        Paint(level, title, string.Join(" · ", parts), hint);

        void Paint(Level shown, string key, string detail, string? hintKey)
        {
            LastSession.SessionGlyph.Data = Ui.Glyph(shown);
            LastSession.SessionGlyph.Foreground = Ui.LevelBrush(shown);
            Ui.Localize(LastSession.SessionTitle, key);
            LastSession.SessionDetail.Text = detail;
            LastSession.SessionDetail.IsVisible = detail.Length > 0;
            LastSession.SessionHint.IsVisible = hintKey is not null;
            if (hintKey is not null) Ui.Localize(LastSession.SessionHint, hintKey);
        }
    }

    /// <summary>Dates and numbers the way the language on screen writes them. A language .NET has no
    /// culture for writes them as English does.</summary>
    internal static CultureInfo Culture()
    {
        try { return CultureInfo.GetCultureInfo(App.CurrentLanguage, predefinedOnly: true); }
        catch (CultureNotFoundException) { return CultureInfo.GetCultureInfo("en-US"); }
    }
}
