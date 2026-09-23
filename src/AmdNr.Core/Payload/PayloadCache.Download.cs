// One file off the network: every address in turn, resumed where a part is already on disk, and
// kept only once it hashes to its pin.

using System.Net;
using System.Net.Http.Headers;

namespace AmdNr.Core;

public sealed partial class PayloadCache
{
    /// <summary>The same file from whichever address answers. Every one of them is checked against
    /// the same SHA-256, so falling through to a mirror weakens nothing -- a mirror that serves the
    /// wrong bytes fails exactly as the first address would have.
    ///
    /// The last failure is the one reported: by then every address has been tried, and the first
    /// one's message is no more useful than the last one's.</summary>
    private async Task FetchAnyAsync(IReadOnlyList<Uri> urls, string path, PayloadFile file,
        IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        for (var i = 0; i < urls.Count; i++)
        {
            try
            {
                await FetchAsync(urls[i], path, file, progress, cancel);
                return;
            }
            catch (Exception e) when (e is HttpRequestException or InstallException or IOException
                                          && i + 1 < urls.Count)
            {
                // Another address has the same bytes, but whatever this one left behind is not
                // resumable against it: a half-written .part plus a Range request to a different
                // server splices two answers together. The hash would catch that, having spent the
                // whole download to do it, so the partial goes instead.
                try { File.Delete(path + ".part"); }
                catch (IOException)
                {
                    // Held open somehow: the size and hash checks still refuse to install it.
                }
            }
        }
    }

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
        IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        try
        {
            await FetchOneAsync(url, path, file, progress, cancel);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new InstallException(
                $"Could not download {file.Name}: {url.Host} accepted the connection and then stopped "
                + "answering. Whatever arrived is kept, so trying again picks up where this left off.");
        }
    }

    /// <summary>One file, resumed if a part of it is already on disk, verified before it is allowed
    /// to take the final name. A partial download can never be mistaken for a complete one, because
    /// the name only changes after the hash matches.</summary>
    private async Task FetchOneAsync(Uri url, string path, PayloadFile file,
        IProgress<DownloadProgress>? progress, CancellationToken cancel)
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
            Engine.Require(response.IsSuccessStatusCode,
                $"Could not download {file.Name}: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

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
                progress?.Report(new DownloadProgress(file.Name, received, total > 0 ? total : null));
            }
        }

        var got = await Task.Run(() => Engine.HashFile(part), cancel);
        if (got != file.Sha256)
        {
            File.Delete(part);
            throw new InstallException(
                $"{file.Name} downloaded, but it does not match the SHA-256 the manifest gives."
                + $"\n      expected {file.Sha256}\n      got      {got}"
                + "\n      Nothing was installed. Try again; if it keeps happening the published file changed.");
        }

        File.Move(part, path, overwrite: true);
    }

}
