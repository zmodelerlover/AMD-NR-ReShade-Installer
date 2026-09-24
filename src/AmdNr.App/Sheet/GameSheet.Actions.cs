// What the sheet does to a game: the pre-flight, the install, the uninstall, and the download they
// need. The smaller things around them -- play, the folder, the executable, the report -- are in
// GameSheet.Tools.cs.

using Avalonia.Interactivity;
using Avalonia.Threading;
using AmdNr.Core;

namespace AmdNr.App;

public partial class GameSheet
{
    // The install steps, in the order the chips sit in the sheet.
    private const int StepDownload = 0, StepVerify = 1, StepInstall = 2, StepDone = 3;

    /// <summary>How many pre-flights have started, so only the latest one is shown.</summary>
    private int _checks;

    /// <summary>Whether the busy flag is this sheet's own install or uninstall, and whether a check
    /// was put off because it was somebody else's.</summary>
    private bool _ownBusy, _checkWaiting;

    /// <summary>The pre-flight for the open game, off the UI thread: deciding whether the cache is
    /// complete can mean hashing 141 MB, which is a second of frozen window the first time.</summary>
    private async Task RefreshAsync()
    {
        if (_card is not { } card) return;
        if (Session.Busy)
        {
            // Someone else's work -- a scan, Download all -- is running: what is on screen may be
            // another game's verdict, so it goes, and the check runs once that work is done.
            if (!_ownBusy)
            {
                _checkWaiting = true;
                Steps.IsVisible = false;
                ResultBanner.IsVisible = false;
                _report.Clear();
            }
            return;
        }
        _checkWaiting = false;
        var check = ++_checks;
        var pins = Pins();
        var preset = card.Entry.Preset;
        var proxy = _proxy;

        // A result belongs to the action that produced it; a new check replaces it.
        Steps.IsVisible = false;
        ResultBanner.IsVisible = false;
        _report.Clear();

        Report report;
        string? staged;
        try
        {
            (report, staged) = await Task.Run(() =>
            {
                var folder = CachedPayloadFolder(preset);
                return (Work.Preflight(TargetFor(card), folder ?? "", preset, pins, proxy), folder);
            });
        }
        catch (Exception e)
        {
            report = Failure(e);
            staged = null;
        }
        // The sheet moved on while this ran -- another game, a route, a version, a name -- and the
        // check that started after this one is the one to show, even if it finished first.
        if (check != _checks || _card != card || Session.Busy) return;

        Show(report);
        card.RefreshInstalled();
        ShowVerdict(report, card);
        ShowInstallLabel();
        Status(staged is null ? Ui.Text("Str.WillDownload") : Ui.Text("Str.PayloadsReady"));
    }

    private void OnInstall(object? sender, RoutedEventArgs e) => _shell.Run("install", InstallAsync);

    private async Task InstallAsync()
    {
        if (_card is not { } card || Session.Busy) return;

        // The other route is in the folder, and the engine will not put a second one over it. Say
        // what is about to happen, and do it in the order it has to happen in.
        var switching = card.InstalledVia is { } via && via != card.Entry.Preset.Family();
        if (switching && !await AskSwitchAsync(card)) return;

        Busy(true, InstallButton);
        // What this install is, fixed now: the route, the hashes of the chosen version and the name
        // ReShade goes in as, as the sheet showed them when Install was pressed. Read again after a
        // long download they could be something else by then.
        var preset = card.Entry.Preset;
        var pins = Pins();
        var proxy = _proxy;
        try
        {
            if (switching)
            {
                var removed = await RunUninstallAsync(card, InstalledPreset(card), removeConfig: false);
                if (removed.Failed || card.Installed)
                {
                    ShowOutcome(removed, "Str.Uninstall", card.Name);
                    return;
                }
            }

            ResultBanner.IsVisible = false;
            ShowStep(StepDownload);
            var folder = await EnsurePayloadsAsync(preset);
            if (folder is null)
            {
                ShowStep(StepDownload, failed: true);
                ShowResult(Level.Err, Ui.Text("Str.DownloadFailed"), Ui.Text("Str.DownloadFailedDetail"));
                DnsRetryButton.IsVisible = _unreachable;
                return;
            }

            ShowStep(StepInstall);
            Status(Ui.Text("Str.Working"));
            var target = TargetFor(card);
            Report report;
            try { report = await WritingAsync(() => Work.Install(target, folder, preset, pins, proxy)); }
            catch (Exception ex)
            {
                // The engine turns everything it expects into a report line, and rolls back before
                // it throws. Anything left is unforeseen -- and losing the window mid-install is a
                // worse answer than a line saying so.
                report = Failure(ex);
            }
            Show(report);
            WriteLog(report, $"install {preset.Label()} -> {card.Path}");
            InstallLog.Write("install", card, target, report, Selected(), pins, folder);
            card.RefreshInstalled();
            // Recorded from the install and not only from the menu, so the version is remembered
            // for somebody who never opened that menu -- which is nearly everybody.
            if (!report.Failed && _version is { } installed)
            {
                Remember(card, installed);
                Library.Save();
            }
            ShowStep(report.Failed ? StepInstall : StepDone, report.Failed);
            ShowOutcome(report, "Str.Install", card.Name);
            if (!report.Failed && card.Installed) card.Pulse();
            Status(report.Failed ? Ui.Text("Str.LogSaved") : Ui.Text("Str.Ready"));
        }
        finally
        {
            Busy(false);
            ShowRoutes(card, card.Graphics ?? GraphicsDetector.Detect(card.Path));
        }
    }

    private async Task<bool> AskSwitchAsync(GameCard card)
    {
        var to = card.Entry.Preset.Family();
        var from = to == RouteFamily.OptiScaler ? RouteFamily.ReShade : RouteFamily.OptiScaler;
        return await ChoiceDialog.ShowAsync(_shell, Ui.Format("Str.SwitchTitle", to),
            Ui.Format("Str.SwitchBody", card.Name, from, to),
            [
                ("switch", Ui.Format("Str.SwitchYes", from, to), Ui.Text("Str.SwitchYesBody")),
                ("keep", Ui.Text("Str.Dismiss"), Ui.Format("Str.SwitchNoBody", from)),
            ]) == "switch";
    }

    private void OnUninstall(object? sender, RoutedEventArgs e) => _shell.Run("uninstall", async () =>
    {
        if (_card is not { } card || Session.Busy) return;
        Busy(true, UninstallButton);
        Steps.IsVisible = false;
        ResultBanner.IsVisible = false;
        try
        {
            var preset = InstalledPreset(card);
            var report = await RunUninstallAsync(card, preset, removeConfig: false);

            // Everything of this app's goes by name or by pinned hash, so the one thing that leaves
            // a folder installed now is a file the running game still has open -- and against that
            // the answer is "close the game", which the line in Details says.
            if (!report.Failed && card.Installed)
                ShowResult(Level.Warn, Ui.Format("Str.UninstallLeft", card.Name), Ui.Text("Str.SeeDetails"));
            else
                ShowOutcome(report, "Str.Uninstall", card.Name);
            Status(report.Failed ? Ui.Text("Str.LogSaved") : Ui.Text("Str.Ready"));
        }
        finally
        {
            Busy(false);
            ShowRoutes(card, card.Graphics ?? GraphicsDetector.Detect(card.Path));
        }
    });

    /// <summary>The preset to take an install back with. What is in the folder decides, not what the
    /// sheet is set to: somebody who has picked OptiScaler on a game that has the ReShade route is
    /// uninstalling the ReShade route. Only the route and whether it is OptiScaler matter to the
    /// engine here, and the 32-bit side is known by its own manifest.</summary>
    private static Preset InstalledPreset(GameCard card)
    {
        if (card.InstalledVia == RouteFamily.OptiScaler) return Preset.OptiScaler;
        if (!card.Entry.Preset.IsOptiScaler()) return card.Entry.Preset;
        var folder = Directory.Exists(TargetFor(card)) ? TargetFor(card) : Path.GetDirectoryName(TargetFor(card)) ?? card.Path;
        return File.Exists(Path.Combine(folder, Engine.ManifestName)) || File.Exists(Path.Combine(folder, Work.Addon32Name))
            ? Preset.X86Dx11
            : Preset.Dx11;
    }

    private async Task<Report> RunUninstallAsync(GameCard card, Preset preset, bool removeConfig)
    {
        var target = TargetFor(card);
        // The ReShade this payload pins, besides the builds the engine knows: a ReShade copied in by
        // hand is taken out only when it is one of those.
        var pinned = new[] { Pins().ReShade64Sha };
        Report report;
        try { report = await WritingAsync(() => Work.Uninstall(target, preset, removeConfig, pinned)); }
        catch (Exception ex) { report = Failure(ex); }
        Show(report);
        var what = removeConfig ? "uninstall (settings too)" : "uninstall";
        WriteLog(report, $"{what} {preset.Label()} -> {card.Path}");
        InstallLog.Write(what, card, target, report, Selected(), Pins(), null);
        card.RefreshInstalled();
        return report;
    }

    /// <summary>Work inside the game folder, off the UI thread and marked for as long as it runs, so
    /// the window is not closed on it: see <see cref="Session.Writing"/>.</summary>
    private async Task<Report> WritingAsync(Func<Report> work)
    {
        Session.Writing = true;
        try { return await Task.Run(work); }
        finally { Session.Writing = false; }
    }

    /// <summary>Clears Windows' DNS cache and runs the install again. Offered only when the download
    /// never reached the server, which is the failure a stale cache causes.</summary>
    private void OnFlushDns(object? sender, RoutedEventArgs e) => _shell.Run("flush dns", async () =>
    {
        if (Session.Busy) return;
        var flushed = DownloadLog.FlushDns();
        if (!flushed)
        {
            _shell.Toast(Ui.Text("Str.DnsFlushFailed"), Level.Warn);
            return;
        }
        await InstallAsync();
    });

    /// <summary>The last download never reached the server: see <see cref="PayloadCache.IsNetwork"/>.</summary>
    private bool _unreachable;

    /// <summary>Downloads whatever this route needs, then hands back the folder to install from --
    /// the same shape someone would have unzipped by hand, so the engine cannot tell the difference.
    /// A failure says why at every address it tried, in the Details, and opens them.</summary>
    private async Task<string?> EnsurePayloadsAsync(Preset preset)
    {
        _unreachable = false;
        var manifest = Selected();
        if (manifest is null)
        {
            _report.Add(new ReportLine(Ui.Glyph(Level.Err), Session.ManifestProblem ?? Ui.Text("Str.ManifestFailed"), Ui.Brush("Err")));
            return null;
        }

        var cache = Session.Cache();
        var progress = new Progress<DownloadProgress>(p => Dispatcher.UIThread.Post(() =>
        {
            Progress.IsVisible = true;
            Progress.IsIndeterminate = p.Fraction is null;
            if (p.Fraction is { } fraction) Progress.Value = fraction * 100;
            Status($"{Ui.Text("Str.Downloading")} {p.File} — {Ui.Megabytes((ulong)Math.Max(0, p.Received))}");
        }));

        try
        {
            // Filtered against the manifest: a manifest that predates a component skips it.
            var components = ComponentsFor(preset).Where(manifest.Has).ToArray();
            foreach (var component in components)
                await DownloadLog.EnsureAsync(Session, manifest, component, progress);

            Progress.IsVisible = false;
            ShowStep(StepVerify);
            Status(Ui.Text("Str.Verifying"));
            // One folder for the install to read, hard-linked out of the cache: the 141 MB of
            // weights are not copied a second time on the way there.
            return await Task.Run(() => cache.Stage(manifest, components));
        }
        catch (Exception e) when (e is InstallException or HttpRequestException or IOException
                                      or UnauthorizedAccessException)
        {
            Progress.IsVisible = false;
            _unreachable = PayloadCache.IsNetwork(e);
            _report.Add(new ReportLine(Ui.Glyph(Level.Err), e.Message, Ui.Brush("Err")));
            _report.Add(new ReportLine(Ui.Glyph(Level.Info), Ui.Text("Str.DownloadFailedHelp"), Ui.Brush("Muted")));
            Status(Ui.Text("Str.DownloadFailed"));
            return null;
        }
    }

    /// <summary>What the engine is pointed at: the executable the detection found, not the library
    /// folder. For an Unreal game that is Binaries\Win64, where ReShade and the add-on have to sit --
    /// the root holds only a launcher stub that loads neither.</summary>
    private static string TargetFor(GameCard card) =>
        card.Graphics?.Target is { } target && File.Exists(target) ? target : card.Path;

    /// <summary>What a route needs. Every ReShade route needs the add-on and the runtime; the bridge
    /// routes also need their own 32-bit pair and the pinned ReShade beside it. The OptiScaler route
    /// needs OptiScaler, the runtime build it drives, and the weights out of the runtime component.</summary>
    private static string[] ComponentsFor(Preset preset) =>
        preset.IsOptiScaler()
            ? [PayloadManifest.OptiScalerComponent, PayloadManifest.OptiRuntimeComponent, PayloadManifest.RuntimeComponent,
               PayloadManifest.LmxxfWeightsComponent]
            : preset.Route() == Route.X86
                ? [PayloadManifest.BridgeComponent, PayloadManifest.X86ExtrasComponent,
                   PayloadManifest.RuntimeComponent, PayloadManifest.ShaderComponent]
                : [PayloadManifest.AddonComponent, PayloadManifest.RuntimeComponent,
                   PayloadManifest.ReShadeComponent, PayloadManifest.ShaderComponent];

    /// <summary>The folder a route would install from, when it is already complete in the cache.</summary>
    private string? CachedPayloadFolder(Preset preset)
    {
        var manifest = Selected();
        if (manifest is null) return null;
        try
        {
            var components = ComponentsFor(preset).Where(manifest.Has).ToArray();
            if (!components.All(c => PayloadCache.IsComplete(manifest, c))) return null;
            return Session.Cache().Stage(manifest, components);
        }
        catch (Exception e) when (e is InstallException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The manifest this sheet installs from: the published one, with the chosen release's
    /// add-on swapped in when that is not the version the manifest already pins, or the chosen
    /// OptiScaler version's components in place of the ones it pins.</summary>
    private PayloadManifest? Selected() =>
        Session.Manifest is not { } manifest ? null
        : _version?.Opti is { } opti ? manifest.With(opti)
        : _version?.Release is { } release ? AddonReleases.With(manifest, release)
        : manifest;

    private PayloadPins Pins()
    {
        try { return Selected()?.Pins() ?? Fallback(); }
        catch (InstallException) { return Fallback(); }

        // Without a manifest the runtime and weights are still the pinned pair the add-on refuses
        // anything but; only the add-on's own size is unknown, and a zero there just means the
        // pre-flight cannot judge it yet.
        static PayloadPins Fallback() => new() { AddonSha = new string('0', 64), AddonSize = 0 };
    }
}
