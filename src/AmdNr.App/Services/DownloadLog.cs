// One file per download, in %AppData%\AmdNrInstaller\logs\, in the same shape as an install's; and
// the check of every address the files come from that "This machine" runs on demand.
//
// The error on screen says in a sentence what went wrong at each address. What a support thread needs
// is what that sentence was made from: whether a proxy sits in the way, what the name resolved to,
// what the server answered, how much arrived and how fast, and what is in the cache afterwards. A
// failed download used to leave one line in the rolling log with the same sentence in it.

using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using AmdNr.Core;
using static AmdNr.App.InstallLog;

namespace AmdNr.App;

public static class DownloadLog
{
    /// <summary>When DNS was last cleared from a retry button, and whether Windows did it.</summary>
    private static string _flushed = "not this session";

    /// <summary>Clears Windows' DNS cache and remembers how that went, for the logs that follow.</summary>
    public static bool FlushDns()
    {
        var flushed = PayloadCache.FlushDns();
        _flushed = $"{DateTime.Now:HH:mm:ss}, {(flushed ? "cleared" : "Windows refused")}";
        Append($"{DateTime.Now:s} flush dns: {(flushed ? "cleared" : "refused")}");
        return flushed;
    }

    /// <summary>One component, fetched with a trace running. A log is written when anything had to
    /// be fetched or anything failed: a component already in the cache is nothing to read about, and
    /// every install asks for every component. Throws whatever EnsureAsync throws.</summary>
    public static async Task<string> EnsureAsync(Session session, PayloadManifest manifest, string component,
        IProgress<DownloadProgress>? progress)
    {
        var cache = session.Cache();
        var trace = cache.Trace = new DownloadTrace();
        var clock = Stopwatch.StartNew();
        try
        {
            var dir = await cache.EnsureAsync(manifest, component, progress);
            if (trace.Attempts.Count > 0)
                await Task.Run(() => Write("download", [component], session, manifest, trace, null, clock.Elapsed));
            return dir;
        }
        catch (Exception e)
        {
            await Task.Run(() => Write("download", [component], session, manifest, trace, e, clock.Elapsed));
            throw;
        }
    }

    /// <summary>Writes one log and returns its path, or null if it could not be written. Never
    /// throws: a log that failed must not stand in front of the failure it describes.</summary>
    public static string? Write(string action, IReadOnlyList<string> components, Session session,
        PayloadManifest? manifest, DownloadTrace? trace, Exception? error, TimeSpan took,
        params (string Key, string Value)[] extra)
    {
        try
        {
            var name = components.Count == 1 ? components[0] : "all";
            var path = Path.Combine(AppPaths.Logs, $"{DateTime.Now:yyyyMMdd-HHmmss}-{action}-{name}.log");

            var o = new StringBuilder();
            Header(o, action, error is not null);
            Line(o, "components", string.Join(", ", components));
            Line(o, "took", $"{took.TotalSeconds:0.0} s");
            foreach (var (key, value) in extra) Line(o, key, Indent(value));
            Machine(o);
            Network(o, Urls(manifest, components));
            PayloadList(o, session, manifest, components);

            if (trace is not null)
            {
                Head(o, "Each file");
                foreach (var (file, what) in trace.Files) Line(o, file, what);
                Head(o, "Each address tried");
                if (trace.Attempts.Count == 0) o.AppendLine("  none: nothing had to be fetched");
                for (var i = 0; i < trace.Attempts.Count; i++) Attempt(o, i + 1, trace.Attempts[i]);
            }

            if (error is not null)
            {
                Head(o, "What went wrong");
                // The attempts know what each address did; the exception that ends it only sums them up.
                var kinds = trace?.Attempts.Select(a => a.Kind).OfType<string>().Distinct().ToList() ?? [];
                Line(o, "kind", kinds.Count > 0 ? string.Join(", ", kinds) : PayloadCache.Classify(error));
                Line(o, "network", PayloadCache.IsNetwork(error) ? "yes: the server was never reached" : "no");
                Line(o, "said", Indent(error.Message));
                Line(o, "exception", Indent(error.InnerException is null ? error.GetType().Name : PayloadCache.Chain(error)));
            }

            Head(o, "The cache afterwards");
            CacheState(o, manifest, components);

            File.WriteAllText(path, o.ToString());
            Prune();
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or InstallException)
        {
            return null;
        }
    }

    /// <summary>Every address the payload list and the files come from, checked now: what the name
    /// resolves to, the proxy in the way, and what a request for the first byte gets back. Each
    /// has a few seconds, all at once, so a dead host costs seconds rather than minutes.</summary>
    public static async Task<string> DiagnoseAsync(Session session)
    {
        var manifest = session.Manifest;
        var components = manifest?.Everyday.Select(p => p.Key).ToList() ?? [];
        // Every host anything could come from, the on-demand components and every release included:
        // the OptiScaler route downloads from places the first-run files never touch.
        var every = manifest is null
            ? []
            : (manifest.Releases ?? []).SelectMany(r => r.Value).Select(manifest.With).Prepend(manifest)
                .SelectMany(m => Urls(m, m.Components.Keys));
        var urls = session.Config.ManifestAddresses()
            .Select(a => Uri.TryCreate(a, UriKind.Absolute, out var u) ? u : null).OfType<Uri>()
            .Concat(every)
            .DistinctBy(u => u.Authority, StringComparer.OrdinalIgnoreCase).ToList();
        var checks = await Task.WhenAll(urls.Select(u => CheckAsync(session.Http, u)));

        var o = new StringBuilder();
        Header(o, "check addresses", checks.Any(c => c.Failed));
        Line(o, "addresses", $"{checks.Count(c => !c.Failed)} of {checks.Length} answered");
        Machine(o);
        Network(o, urls);
        PayloadList(o, session, manifest, components);
        Head(o, "Each address, now");
        foreach (var (_, text) in checks) o.Append(text);
        Head(o, "The cache now");
        CacheState(o, manifest, components);
        return o.ToString();
    }

    private static async Task<(bool Failed, string Text)> CheckAsync(HttpClient http, Uri url)
    {
        var o = new StringBuilder();
        Line(o, url.Authority, url.AbsoluteUri);
        Line(o, "  proxy", PayloadCache.ProxyFor(url));
        Line(o, "  name lookup", await PayloadCache.LookUpAsync(url.Host, TimeSpan.FromSeconds(5)));
        var clock = Stopwatch.StartNew();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            // The first byte only: enough to prove the file is there, and nobody's bandwidth.
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, 0);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            var landed = response.RequestMessage?.RequestUri?.Host;
            Line(o, "  answered", $"{(int)response.StatusCode} {response.ReasonPhrase} in {clock.ElapsedMilliseconds} ms"
                                  + (landed is not null && landed != url.Host ? $", redirected to {landed}" : ""));
            return (!response.IsSuccessStatusCode, o.ToString());
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            var kind = timeout.IsCancellationRequested ? "no answer in 10 s" : PayloadCache.Classify(e);
            Line(o, "  answered", $"FAILED after {clock.ElapsedMilliseconds} ms: {kind}");
            Line(o, "  exception", Indent(PayloadCache.Chain(e)));
            return (true, o.ToString());
        }
    }

    // -- Sections ----------------------------------------------------------------------------------

    private static void Network(StringBuilder o, IEnumerable<Uri> urls)
    {
        Head(o, "Network and disk");
        Line(o, "dns cleared", _flushed);
        Line(o, "data folder", AppPaths.Root
                               + (Environment.GetEnvironmentVariable("AMDNR_HOME") is { Length: > 0 } ? "  (AMDNR_HOME)" : ""));
        Line(o, "cache", AppPaths.Cache);
        Line(o, "free there", Engine.FreeBytes(AppPaths.Cache) is { } free ? $"{free:N0} bytes" : "unknown");
        foreach (var url in urls.DistinctBy(u => u.Host, StringComparer.OrdinalIgnoreCase))
            Line(o, $"proxy {url.Host}", PayloadCache.ProxyFor(url));
    }

    private static void PayloadList(StringBuilder o, Session session, PayloadManifest? manifest,
        IReadOnlyList<string> components)
    {
        Head(o, "Payload list");
        Line(o, "read from", session.ManifestSource);
        if (session.ManifestProblem is { } problem) Line(o, "published one", Indent(problem));
        if (manifest is null)
        {
            Line(o, "manifest", "not read");
            return;
        }
        foreach (var (name, c) in manifest.Components) Line(o, name, c.Version);
        foreach (var (name, releases) in manifest.Releases ?? [])
            Line(o, $"releases of {name}", string.Join(", ",
                releases.Select(r => $"{r.Version} ({string.Join(", ", r.Components.Keys)})")));
        foreach (var component in components.Where(manifest.Has))
        {
            o.AppendLine();
            foreach (var file in manifest.Component(component).Files)
            {
                Line(o, file.RelativePath, $"{file.Size:N0} bytes  sha256 {file.Sha256}");
                foreach (var url in manifest.DownloadUrls(component, file)) Line(o, "", url.AbsoluteUri);
            }
        }
    }

    private static void Attempt(StringBuilder o, int number, DownloadAttempt a)
    {
        var fresh = a.Received - (long)a.ResumedFrom;
        var speed = a.Took.TotalSeconds > 0 && fresh > 0 ? $", {fresh / 1024.0 / a.Took.TotalSeconds:0} KB/s" : "";
        o.AppendLine();
        Line(o, $"#{number} {a.File}", a.Url.AbsoluteUri);
        Line(o, "  proxy", a.Proxy);
        Line(o, "  name lookup", a.Lookup);
        Line(o, "  status", a.Status?.ToString() ?? "no answer");
        Line(o, "  bytes", $"{a.Received:N0} of {a.Expected:N0}"
                           + (a.ResumedFrom > 0 ? $", resumed from {a.ResumedFrom:N0} in .part" : ""));
        Line(o, "  took", $"{a.Took.TotalSeconds:0.00} s{speed}");
        Line(o, "  result", a.Kind is null
            ? "ok"
            : $"FAILED: {a.Kind}" + (a.FellThrough ? ", so the next address was tried" : ""));
        if (a.Error is not null) Line(o, "  exception", Indent(PayloadCache.Chain(a.Error)));
    }

    private static void CacheState(StringBuilder o, PayloadManifest? manifest, IReadOnlyList<string> components)
    {
        if (manifest is null) return;
        foreach (var component in components.Where(manifest.Has))
        {
            Line(o, component, PayloadCache.FolderFor(component, manifest.Component(component).Version));
            // The lmxxf weights are some 460 files out of one archive: past a screenful, only the
            // ones that are not right are worth a line each.
            var states = PayloadCache.CacheState(manifest, component);
            var verified = states.Count(s => s.State.EndsWith(", verified", StringComparison.Ordinal));
            var shown = states.Count <= 16 ? states : states.Where(s => !s.State.EndsWith(", verified", StringComparison.Ordinal)).Take(16);
            foreach (var (file, state) in shown) Line(o, "  " + file, state);
            if (states.Count > 16) Line(o, "  ...", $"{verified} of {states.Count} verified; only the others are listed");
        }
    }

    private static IEnumerable<Uri> Urls(PayloadManifest? manifest, IEnumerable<string> components) =>
        manifest is null
            ? []
            : components.Where(manifest.Has)
                .SelectMany(c => manifest.Component(c).Files.SelectMany(f => manifest.DownloadUrls(c, f)));

    /// <summary>A value that runs over lines, lined up under the column it started in.</summary>
    private static string Indent(string text) => text.ReplaceLineEndings(Environment.NewLine + new string(' ', 27));
}
