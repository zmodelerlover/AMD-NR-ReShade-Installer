// The files every install is made of, as one panel: what is in the cache, downloading the rest, and
// the two ways past a download that does not work here -- fetching each file with a browser, and
// importing a folder of files fetched however somebody could. Shared by the first-run wizard and the
// "This machine" page, which used to carry two copies of the same list with two different bugs.

using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using AmdNr.Core;

namespace AmdNr.App;

/// <summary>One downloadable component as the panel lists it. Its state changes in place while
/// Download all runs, so each row says where it is -- waiting, downloading, verified, failed -- the
/// moment that is true, rather than every row changing together when the whole set is done.</summary>
public sealed class PayloadRow(string name, string title, string caption) : INotifyPropertyChanged
{
    public string Name { get; } = name;
    public string Title { get; } = title;
    public string Caption { get; } = caption;
    public string State { get; private set; } = "";
    public bool Ready { get; private set; }
    public bool Failed { get; private set; }
    public bool Working { get; private set; }
    public bool Waiting => !Ready && !Failed && !Working;

    public event PropertyChangedEventHandler? PropertyChanged;

    public PayloadRow Set(string state, bool ready = false, bool failed = false, bool working = false)
    {
        (State, Ready, Failed, Working) = (state, ready, failed, working);
        foreach (var property in new[] { nameof(State), nameof(Ready), nameof(Failed), nameof(Working), nameof(Waiting) })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        return this;
    }
}

/// <summary>One file somebody can fetch by hand, and every address it is published at.</summary>
public sealed record ManualFile(string Name, string Caption, IReadOnlyList<ManualLink> Links);

public sealed record ManualLink(string Host, string Url);

public partial class PayloadPanel : UserControl
{
    private Session _session = null!;
    private Window? _owner;
    private string? _failed;

    public PayloadPanel()
    {
        InitializeComponent();
        CacheFolder.Text = AppPaths.Cache;
        ToolTip.SetTip(CacheFolder, AppPaths.Cache);
    }

    /// <summary>Raised when what is in the cache may have changed: a download, an import, a clear.</summary>
    public event Action? Changed;

    /// <summary>How many components are still missing, as of the last check; -1 before one.</summary>
    public int Missing { get; private set; } = -1;

    public void Attach(Session session, Window owner)
    {
        _session = session;
        _owner = owner;
        session.ManifestChanged += () => _ = RefreshAsync();
        session.BusyChanged += () => Lock(session.Busy);
    }

    private void Lock(bool busy)
    {
        DownloadButton.IsEnabled = !busy && _session.Manifest is not null;
        RetryButton.IsEnabled = !busy;
        DnsButton.IsEnabled = !busy;
        ImportButton.IsEnabled = !busy && _session.Manifest is not null;
        ImportFooterButton.IsEnabled = ImportButton.IsEnabled;
        ClearButton.IsEnabled = !busy;
    }

    /// <summary>What is in the cache, and what it would still cost to fill it. Hashing the weights is
    /// about a second the first time, so it runs off the UI thread.</summary>
    public async Task RefreshAsync()
    {
        var manifest = _session.Manifest;
        ListNote.IsVisible = _session.ManifestProblem is not null && manifest is not null;
        ListNote.Text = Ui.Text("Str.ManifestLocal");
        if (manifest is null)
        {
            Rows.ItemsSource = null;
            Missing = -1;
            ShowError(Ui.Text("Str.ManifestFailed"), _session.ManifestProblem ?? "");
            SetSummary(null, Ui.Text("Str.ManifestFailedShort"));
            Lock(_session.Busy);
            return;
        }

        // The OptiScaler route's components come down when that route is installed, not before, so
        // they are not counted as missing here.
        var rows = await Task.Run(() => manifest.Everyday.Select(pair =>
        {
            var ready = false;
            try { ready = PayloadCache.IsComplete(manifest, pair.Key); }
            catch (Exception e) when (e is InstallException or IOException or UnauthorizedAccessException)
            {
                // A component this build does not understand is not a reason to show nothing.
            }
            return (Name: pair.Key, pair.Value, Ready: ready);
        }).ToList());

        Rows.ItemsSource = rows.Select(r => new PayloadRow(r.Name,
            Ui.Translated($"Str.Component.{r.Name}", r.Name),
            $"{r.Name} · {r.Value.Version} · {Ui.Megabytes(Bytes(r.Value))}").Set(
            Ui.Text(r.Ready ? "Str.PayloadReadyShort" : r.Name == _failed ? "Str.FailedShort" : "Str.NotDownloadedShort"),
            ready: r.Ready,
            failed: !r.Ready && r.Name == _failed)).ToList();

        Missing = rows.Count(r => !r.Ready);
        var missingBytes = rows.Where(r => !r.Ready).Aggregate(0UL, (sum, r) => sum + Bytes(r.Value));
        if (Missing == 0)
        {
            SetSummary(Level.Ok, Ui.Format("Str.PayloadsAllReadyShort", rows.Count));
            ErrorBox.IsVisible = false;
            ManualBox.IsVisible = false;
            _failed = null;
        }
        else
        {
            SetSummary(null, Ui.Format("Str.PayloadsMissingShort", Missing, Ui.Megabytes(missingBytes)));
        }
        // The main action while something is missing, a quiet one once there is nothing to fetch.
        DownloadButton.Classes.Set("primary", Missing > 0);
        DownloadButton.Classes.Set("ghost", Missing == 0);
        ManualList.ItemsSource = ManualFiles(manifest, rows.Where(r => !r.Ready).Select(r => r.Name));
        Lock(_session.Busy);
    }

    private void SetSummary(Level? level, string text)
    {
        Ui.SetLevel(SummaryPill, level);
        SummaryIcon.Data = Ui.Icon(level == Level.Ok ? "IconCheck" : "IconDot");
        SummaryText.Text = text;
    }

    private void ShowError(string title, string text)
    {
        ErrorTitle.Text = title;
        ErrorText.Text = text;
        ErrorBox.IsVisible = true;
    }

    private static ulong Bytes(PayloadComponent component) =>
        component.Files.Aggregate(0UL, (sum, file) => sum + file.Size);

    /// <summary>Every file the missing components are downloaded as, with every address it is
    /// published at. For a component taken out of an archive that is the archive, because that is
    /// what a browser gets.</summary>
    private static List<ManualFile> ManualFiles(PayloadManifest manifest, IEnumerable<string> components) =>
        components.SelectMany(component => manifest.Component(component).Files.Select(file =>
            new ManualFile(file.AssetName, $"{Ui.Megabytes(file.Size)} · SHA-256 {file.Sha256[..16]}…",
                manifest.DownloadUrls(component, file).Select(u => new ManualLink(u.Host, u.AbsoluteUri)).ToList())))
            .ToList();

    /// <summary>Fetches every component now rather than at the first install that needs one. The
    /// cache is shared by every game, so this is paid once; anything already there and hashing
    /// correctly is not fetched again, so this is also how a half-finished download is finished.</summary>
    private void OnDownload(object? sender, RoutedEventArgs e) => Run(DownloadAsync);

    private async Task DownloadAsync()
    {
        if (_session.Busy) return;
        if (_session.Manifest is null)
        {
            // Nothing to download from yet: reading the list again is the retry.
            await _session.LoadManifestAsync();
            return;
        }

        var manifest = _session.Manifest;
        _session.Busy = true;
        ErrorBox.IsVisible = false;
        ProgressBox.IsVisible = true;
        Progress.IsIndeterminate = true;
        ProgressText.Text = Ui.Text("Str.Verifying");
        DownloadIcon.IsVisible = false;
        DownloadSpin.IsVisible = true;
        var current = "";
        try
        {
            // One component whose folder cannot be read is skipped, as it always was, rather than
            // taking every other download down with it.
            var pending = await Task.Run(() => manifest.Everyday.Select(p => p.Key)
                .Where(name =>
                {
                    try { return !PayloadCache.IsComplete(manifest, name); }
                    catch (Exception e) when (e is InstallException or IOException or UnauthorizedAccessException) { return false; }
                }).ToList());
            if (pending.Count == 0)
            {
                Toast(Ui.Text("Str.PayloadsAlreadyThere"), Level.Ok);
                return;
            }

            // One bar for the whole set, not one per file: six files of wildly different sizes each
            // running 0 to 100 tells nobody how much of the download is left.
            var sizes = pending.SelectMany(name => manifest.Component(name).Files)
                .GroupBy(file => file.Name).ToDictionary(g => g.Key, g => g.First().Size);
            var total = sizes.Values.Aggregate(0UL, (sum, size) => sum + size);
            var finished = 0UL;
            var seen = "";
            Progress.IsIndeterminate = false;

            // And each row its own state, as it happens: the one downloading says how far it is,
            // and one that is done turns green then, not when the last of the set is.
            var rows = (Rows.ItemsSource as IEnumerable<PayloadRow>)?.ToDictionary(r => r.Name) ?? [];
            PayloadRow? row = null;
            var rowFiles = new HashSet<string>();
            var rowSeen = "";
            var rowFinished = 0UL;
            var rowTotal = 0UL;
            var progress = new Progress<DownloadProgress>(p => Dispatcher.UIThread.Post(() =>
            {
                if (p.File != seen)
                {
                    if (seen.Length > 0 && sizes.TryGetValue(seen, out var done)) finished += done;
                    seen = p.File;
                }
                var received = finished + (ulong)Math.Max(0, p.Received);
                Progress.Value = total == 0 ? 0 : Math.Min(100, 100.0 * received / total);
                ProgressText.Text = $"{p.File} — {Ui.Megabytes(received)} / {Ui.Megabytes(total)}";

                // Posted, so one can land after its component is done: only the row's own files count.
                if (row is null || rowTotal == 0 || !rowFiles.Contains(p.File)) return;
                if (p.File != rowSeen)
                {
                    if (rowSeen.Length > 0 && sizes.TryGetValue(rowSeen, out var rowDone)) rowFinished += rowDone;
                    rowSeen = p.File;
                }
                var percent = (int)Math.Min(99, 100.0 * (rowFinished + (ulong)Math.Max(0, p.Received)) / rowTotal);
                row.Set(Ui.Format("Str.DownloadingPercent", percent), working: true);
            }));

            foreach (var component in pending)
            {
                current = component;
                row = rows.GetValueOrDefault(component);
                rowFiles = manifest.Component(component).Files.Select(f => f.Name).ToHashSet();
                (rowSeen, rowFinished, rowTotal) = ("", 0UL, Bytes(manifest.Component(component)));
                row?.Set(Ui.Text("Str.DownloadingShort"), working: true);
                await DownloadLog.EnsureAsync(_session, manifest, component, progress);
                row?.Set(Ui.Text("Str.PayloadReadyShort"), ready: true);
                row = null;
            }
            _failed = null;
            Toast(Ui.Text("Str.PayloadsReady"), Level.Ok);
        }
        catch (Exception ex) when (ex is InstallException or HttpRequestException or IOException
                                       or UnauthorizedAccessException)
        {
            _failed = current;
            if ((Rows.ItemsSource as IEnumerable<PayloadRow>)?.FirstOrDefault(r => r.Name == current) is { } failed)
                failed.Set(Ui.Text("Str.FailedShort"), failed: true);
            ShowError(Ui.Format("Str.DownloadFailedOne", Ui.Translated($"Str.Component.{current}", current)), ex.Message);
            DnsButton.IsVisible = PayloadCache.IsNetwork(ex);
            InstallLog.Append($"{DateTime.Now:s} download {current}\n{ex.Message}");
        }
        finally
        {
            ProgressBox.IsVisible = false;
            DownloadIcon.IsVisible = true;
            DownloadSpin.IsVisible = false;
            _session.Busy = false;
            // Re-read rather than assume: a component that failed has to keep saying so.
            await RefreshAsync();
            Changed?.Invoke();
        }
    }

    /// <summary>Clears Windows' DNS cache and downloads again. Offered only when the download never
    /// reached the server, which is the failure a stale cache causes.</summary>
    private void OnFlushDns(object? sender, RoutedEventArgs e) => Run(async () =>
    {
        if (_session.Busy) return;
        var flushed = DownloadLog.FlushDns();
        if (!flushed)
        {
            Toast(Ui.Text("Str.DnsFlushFailed"), Level.Warn);
            return;
        }
        await DownloadAsync();
    });

    private void OnManual(object? sender, RoutedEventArgs e) => ManualBox.IsVisible = !ManualBox.IsVisible;

    private void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && url.StartsWith("https://", StringComparison.Ordinal))
            AppUpdate.OpenInBrowser(url);
    }

    /// <summary>Files fetched by hand, from whatever folder they ended up in. Nothing has to be put
    /// anywhere in particular or renamed: each one is found by its name and size, and taken only if
    /// it hashes to its pin.</summary>
    private void OnImport(object? sender, RoutedEventArgs e) => Run(async () =>
    {
        if (_session.Busy || _session.Manifest is not { } manifest || _owner is null) return;
        var picked = await _owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Ui.Text("Str.ImportPick"),
            AllowMultiple = false,
        });
        var folder = picked.Count > 0 ? picked[0].TryGetLocalPath() : null;
        if (string.IsNullOrWhiteSpace(folder)) return;

        _session.Busy = true;
        try
        {
            var components = manifest.Everyday.Select(p => p.Key).ToList();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = await Task.Run(() => PayloadCache.Import(manifest, components, folder));
            await Task.Run(() => DownloadLog.Write("import", components, _session, manifest, null, null, clock.Elapsed,
                ("folder", folder), ("taken", string.Join(", ", result.Taken)),
                ("still missing", string.Join(", ", result.Missing))));
            Toast(result.Missing.Count == 0
                    ? Ui.Text("Str.ImportAll")
                    : result.Taken.Count == 0
                        ? Ui.Text("Str.ImportNothing")
                        : Ui.Format("Str.ImportSome", result.Taken.Count, result.Missing.Count),
                result.Missing.Count == 0 ? Level.Ok : result.Taken.Count == 0 ? Level.Warn : Level.Info);
        }
        finally
        {
            _session.Busy = false;
            await RefreshAsync();
            Changed?.Invoke();
        }
    });

    /// <summary>Empties the download cache. Nothing in it is unrecoverable, but it is about 165 MB of
    /// coming back, so it asks first. What is installed in a game is not in here and is not touched.</summary>
    private void OnClearCache(object? sender, RoutedEventArgs e) => Run(async () =>
    {
        if (_session.Busy || _owner is null) return;
        var answer = await ChoiceDialog.ShowAsync(_owner, Ui.Text("Str.ClearCache"), Ui.Text("Str.ClearCacheBody"),
        [
            ("clear", Ui.Text("Str.ClearCacheYes"), Ui.Text("Str.ClearCacheYesBody")),
            ("keep", Ui.Text("Str.Dismiss"), Ui.Text("Str.ClearCacheNoBody")),
        ]);
        if (answer != "clear") return;

        _session.Busy = true;
        try
        {
            var freed = await Task.Run(PayloadCache.Clear);
            Toast(Ui.Format("Str.CacheCleared", Ui.Megabytes(freed)), Level.Ok);
        }
        finally
        {
            _session.Busy = false;
            // Re-read rather than assume: a file something else had open is still there.
            await RefreshAsync();
            Changed?.Invoke();
        }
    });

    private void OnOpenCache(object? sender, RoutedEventArgs e) => AppUpdate.OpenFolder(AppPaths.Cache);

    private void Toast(string text, Level level)
    {
        if (_owner is MainWindow main)
        {
            main.Toast(text, level);
            return;
        }
        Outcome.Text = text;
        Outcome.Foreground = Ui.LevelBrush(level);
        Outcome.IsVisible = true;
    }

    /// <summary>Every handler here is async void, so nothing may escape one: it would close the
    /// window. What does escape is logged and said.</summary>
    private async void Run(Func<Task> work)
    {
        try { await work(); }
        catch (Exception e)
        {
            InstallLog.Append($"{DateTime.Now:s} files  {e.GetType().Name}: {e.Message}");
            ShowError(Ui.Text("Str.Unexpected"), $"{e.GetType().Name}: {e.Message}");
        }
    }
}
