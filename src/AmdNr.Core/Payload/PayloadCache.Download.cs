// One file off the network: every address in turn, resumed where a part is already on disk, and
// kept only once it hashes to its pin.

using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;

namespace AmdNr.Core;

public sealed partial class PayloadCache
{
    /// <summary>How many times one address is picked up again after it cut a download off part
    /// way. Only then: a connection that dropped after 80 MB is worth resuming, and one that never
    /// got going will not get going on a second try either -- the next address might.</summary>
    private const int Resumes = 3;

    /// <summary>The same file from whichever address answers. Every one of them is checked against
    /// the same SHA-256, so falling through to a mirror weakens nothing -- a mirror that serves the
    /// wrong bytes fails exactly as the first address would have.
    ///
    /// When none of them works, every address is named with what went wrong there, in words. "The
    /// download failed" with nothing after it is what sent people to copy files into the cache by
    /// hand and guess at which folder they went in.</summary>
    private async Task FetchAnyAsync(IReadOnlyList<Uri> urls, string path, PayloadFile file,
        IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        var failures = new List<string>();
        var network = false;
        for (var i = 0; i < urls.Count; i++)
        {
            for (var attempt = 1; ; attempt++)
            {
                var before = Engine.SizeOf(path + ".part") ?? 0;
                var record = new DownloadAttempt
                {
                    File = file.Name, Url = urls[i], Expected = file.Size,
                    Proxy = Trace is null ? "-" : ProxyFor(urls[i]),
                };
                Trace?.Attempts.Add(record);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    await FetchAsync(urls[i], path, file, progress, record, cancel);
                    return;
                }
                catch (Exception e) when (e is HttpRequestException or InstallException or IOException
                                              or UnauthorizedAccessException && !cancel.IsCancellationRequested)
                {
                    record.Error ??= e;
                    record.Kind ??= Classify(e);
                    // Not this address's fault, and not one the next can fix: it would be thrown away
                    // and fetched again into the same full disk. What arrived stays for the retry.
                    if (IsDiskFull(e))
                        throw new InstallException(
                            $"The drive holding {AppPaths.Cache} is full: {file.Name} needs "
                            + $"{file.Size / 1_048_576 + 1} MB. Free some space there and try again; what "
                            + "already arrived is kept.");
                    if ((Engine.SizeOf(path + ".part") ?? 0) > before && attempt <= Resumes)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(attempt), cancel);
                        continue;
                    }
                    failures.Add($"{urls[i].Host}: {Describe(e)}");
                    network |= IsNetwork(e);
                    record.FellThrough = i + 1 < urls.Count;
                    break;
                }
                finally
                {
                    record.Took = clock.Elapsed;
                    record.Lookup = await LookUpOnceAsync(urls[i].Host);
                }
            }

            if (i + 1 >= urls.Count) break;
            // Another address has the same bytes, but whatever this one left behind is not
            // resumable against it: a half-written .part plus a Range request to a different
            // server splices two answers together. The hash would catch that, having spent the
            // whole download to do it, so the partial goes instead.
            try { File.Delete(path + ".part"); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Held open somehow: the size and hash checks still refuse to install it.
            }
        }

        throw new InstallException($"Could not download {file.Name}.\n      " + string.Join("\n      ", failures))
        {
            Network = network,
        };
    }

    /// <summary>No answer at all, as opposed to a wrong one: an HTTP failure with no status code is
    /// a name, a connection or a timeout. Any address failing that way is enough, because the first
    /// one is where nearly everything comes from.</summary>
    public static bool IsNetwork(Exception e) =>
        e is InstallException { Network: true } or HttpRequestException { StatusCode: null } or TaskCanceledException
            or HttpIOException or IOException { InnerException: SocketException };

    private const string Cut =
        "the connection was cut. An antivirus or a firewall that inspects downloads can do this; "
        + "trying again picks up where it stopped.";

    /// <summary>ERROR_DISK_FULL and ERROR_HANDLE_DISK_FULL, as the HResult an IOException carries.</summary>
    private static bool IsDiskFull(Exception e) =>
        e is IOException { HResult: unchecked((int)0x80070070) or unchecked((int)0x80070027) };

    /// <summary>Why one address did not work, as somebody who has to do something about it would
    /// put it. The cases are the ones that actually happen: no network, a name that cannot be
    /// looked up because DNS is filtered, an address that never answers, and a security suite that
    /// breaks HTTPS for every program that is not a browser.</summary>
    internal static string Describe(Exception e) => e switch
    {
        InstallException => e.Message,
        HttpRequestException { StatusCode: { } code } => $"the server answered {(int)code} {code}.",
        HttpRequestException { InnerException: SocketException s } => s.SocketErrorCode switch
        {
            SocketError.HostNotFound or SocketError.NoData or SocketError.TryAgain =>
                "the address could not be looked up. There is no internet connection, or something on "
                + "this network is blocking the name.",
            SocketError.TimedOut => "it never answered. A firewall may be dropping the connection.",
            SocketError.ConnectionRefused => "it refused the connection.",
            SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown =>
                "the network could not reach it.",
            SocketError.ConnectionReset or SocketError.ConnectionAborted => Cut,
            _ => s.Message,
        },
        HttpRequestException { HttpRequestError: HttpRequestError.SecureConnectionError }
            or HttpRequestException { InnerException: AuthenticationException } =>
            "the secure connection was refused. An antivirus or proxy that inspects HTTPS, or a wrong "
            + "date and time on this PC, causes this.",
        // Cut before or during the body. The body's own is an IOException, the type a write that
        // failed is too, and it read as "could not be written -- an antivirus": the wrong fix.
        HttpRequestException { InnerException: IOException } or HttpIOException
            or IOException { InnerException: SocketException } => Cut,
        HttpRequestException h => h.InnerException?.Message ?? h.Message,
        UnauthorizedAccessException or IOException =>
            $"the file could not be written: {e.Message} An antivirus, or Windows' controlled folder "
            + "access, can block this.",
        _ => e.Message,
    };

    /// <summary>How long a download may go without one byte arriving before the address is given
    /// up on. A server that refuses or drops the connection says so; one that accepts and then
    /// stops sending says nothing at all, and the client's own 30-minute timeout is then the only
    /// thing that ever ends it. A minute of silence on a file that was arriving is already
    /// dead.</summary>
    private static readonly TimeSpan Stall = TimeSpan.FromMinutes(1);

    /// <summary>The same call as <see cref="FetchOneAsync"/>, with every timeout turned into the
    /// failure it actually is.
    ///
    /// A timeout -- the stall timer's or the client's -- arrives as a cancellation that nobody
    /// asked for, which is a TaskCanceledException. That type is in none of the filters this
    /// travels through: not the mirror fall-through above, and not the three EnsureAsync call
    /// sites, which all list HttpRequestException, InstallException and IOException. So a primary
    /// that hung rather than refused never tried the mirror, and the exception went on to escape an
    /// async void handler. It is a download that failed, so it leaves here saying so.</summary>
    private async Task FetchAsync(Uri url, string path, PayloadFile file,
        IProgress<DownloadProgress>? progress, DownloadAttempt record, CancellationToken cancel)
    {
        try
        {
            await FetchOneAsync(url, path, file, progress, record, cancel);
        }
        catch (OperationCanceledException e) when (!cancel.IsCancellationRequested)
        {
            record.Error = e;
            record.Kind = Classify(e);
            // The client's own timeouts -- the connect one, the one up to the headers -- carry a
            // TimeoutException: nothing ever came back. The stall timer's does not: it went quiet.
            throw new InstallException(e.InnerException is TimeoutException
                ? "it never answered. A firewall, a VPN or a proxy may be dropping the connection."
                : "it stopped answering. Whatever arrived is kept, so trying again picks up where this left off.")
            {
                Network = true,
            };
        }
    }

    /// <summary>One file, resumed if a part of it is already on disk, verified before it is allowed
    /// to take the final name. A partial download can never be mistaken for a complete one, because
    /// the name only changes after the hash matches.</summary>
    private async Task FetchOneAsync(Uri url, string path, PayloadFile file,
        IProgress<DownloadProgress>? progress, DownloadAttempt record, CancellationToken cancel)
    {
        // Re-armed by every byte that lands, so a slow connection has all the time it needs and a
        // silent one has a minute. The hash below is deliberately not under it: that is a second of
        // disk and CPU with nothing arriving, which is exactly what this timer is looking for.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(Stall);
        var live = stall.Token;

        var part = path + ".part";
        var have = Engine.SizeOf(part) ?? 0;
        if (have > file.Size) // A stale part from a different build: start over rather than splice.
        {
            File.Delete(part);
            have = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue((long)have, null);

        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, live);
        record.Status = (int)response.StatusCode;
        if (have > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            // The server ignored the range and is sending the whole thing: take it from the top.
            have = 0;
            File.Delete(part);
        }
        else if (have > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Already have every byte; fall through to the hash check below.
            have = (ulong)new FileInfo(part).Length;
        }
        else
        {
            if (!response.IsSuccessStatusCode) record.Kind = $"http {(int)response.StatusCode}";
            Engine.Require(response.IsSuccessStatusCode,
                $"Could not download {file.Name}: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        record.ResumedFrom = have;
        record.Received = (long)have;
        if (response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var total = (response.Content.Headers.ContentLength ?? 0) + (long)have;
            await using var source = await response.Content.ReadAsStreamAsync(live);
            await using var target = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None);

            var buffer = new byte[128 * 1024];
            var received = (long)have;
            int read;
            while ((read = await source.ReadAsync(buffer, live)) > 0)
            {
                stall.CancelAfter(Stall);
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                received += read;
                record.Received = received;
                progress?.Report(new DownloadProgress(file.Name, received, total > 0 ? total : null));
            }
        }

        var got = await Task.Run(() => Engine.HashFile(part), cancel);
        if (got != file.Sha256)
        {
            record.Kind = "sha mismatch";
            File.Delete(part);
            throw new InstallException(
                $"{file.Name} downloaded, but it does not match the SHA-256 the manifest gives."
                + $"\n      expected {file.Sha256}\n      got      {got}"
                + "\n      Nothing was installed. Try again; if it keeps happening the published file changed.");
        }

        try { File.Move(part, path, overwrite: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Verified a moment ago and gone or locked now: that is an antivirus taking the file,
            // which is the one explanation worth giving -- the add-on and ReShade are both DLLs that
            // hook into other programs, which is exactly what heuristics flag.
            record.Kind = "antivirus (taken after it verified)";
            record.Error = e;
            throw new InstallException(
                $"{file.Name} downloaded and matched its hash, then something took it away ({e.Message}). "
                + $"That is almost always an antivirus. Allow {AppPaths.Cache} in it, or restore the file "
                + "from its quarantine, and try again.");
        }
    }
}
