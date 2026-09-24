// Downloading the payloads, and keeping them.
//
// A component lands in %AppData%\AmdNrInstaller\cache\<component>\<version>\, verified, and the
// install reads from there -- which is the same shape of folder the engine has always accepted, so
// nothing downstream knows or cares whether the files came from here or from a folder someone
// unzipped by hand.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AmdNr.Core;

public static class AppPaths
{
    /// <summary>%AppData%\AmdNrInstaller, created on first use -- or wherever AMDNR_HOME says.
    /// The override is what keeps the test suite out of the real cache, and it is also how a
    /// portable install would keep everything beside the executable.</summary>
    public static string Root { get; } = Create(
        Environment.GetEnvironmentVariable("AMDNR_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                    Environment.SpecialFolderOption.Create),
                "AmdNrInstaller"));

    public static string Cache => Create(Path.Combine(Root, "cache"));
    public static string Logs => Create(Path.Combine(Root, "logs"));
    public static string GamesFile => Path.Combine(Root, "games.json");
    public static string CrashLog => Path.Combine(Logs, "crash.log");

    private static string Create(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed record DownloadProgress(string File, long Received, long? Total)
{
    public double? Fraction => Total is > 0 ? (double)Received / Total.Value : null;
}

public sealed class PayloadCache(HttpClient http)
{
    /// <summary>Where a component's files sit once they are verified.</summary>
    public static string FolderFor(string component, string version) =>
        Path.Combine(AppPaths.Cache, Sanitise(component), Sanitise(version));

    /// <summary>A manifest is remote data: it names the folder this writes into, so anything that
    /// is not a plain name is refused rather than escaped through.</summary>
    private static string Sanitise(string s)
    {
        Engine.Require(s.Length is > 0 and <= 64, $"Unusable component or version name: '{s}'");
        foreach (var c in s)
            Engine.Require(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_',
                $"Unusable component or version name: '{s}'");
        Engine.Require(s is not ("." or ".."), $"Unusable component or version name: '{s}'");
        return s;
    }

    /// <summary>Every file present and hashing to what the manifest says. Hashing 141 MB takes
    /// about a second, which is worth paying before an install rather than trusting a size.</summary>
    public static bool IsComplete(PayloadManifest manifest, string component)
    {
        var c = manifest.Component(component);
        var dir = FolderFor(component, c.Version);
        return c.Installed.All(f =>
        {
            var path = Path.Combine(dir, f.RelativePath);
            return Engine.SizeOf(path) == f.Size && Engine.HashFile(path) == f.Sha256;
        });
    }

    /// <summary>Empties the cache and says how many bytes went with it. Everything in here comes
    /// back: the payloads download again, the covers download again, and the release list is one
    /// request. Nothing installed in a game is touched -- this is the download folder, not the
    /// install.
    ///
    /// The staging folders are deleted but not counted: their files are hard links into the
    /// component folders beside them, so counting both would report twice the disk that is
    /// actually coming back.</summary>
    public static ulong Clear()
    {
        var freed = 0UL;
        // The whole listing first: deleting out of a directory that is still being walked is not
        // something Windows promises anything about. The listing itself is inside the try because
        // it can fail on its own -- an ACL changed after startup, antivirus holding the cache root,
        // a cache on a drive that went away -- and this is called through an async void handler, so
        // an exception escaping here is the window closing rather than a toast.
        string[] entries;
        try { entries = Directory.GetFileSystemEntries(AppPaths.Cache); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var entry in entries)
        {
            try
            {
                if (!Directory.Exists(entry))
                {
                    // Measured before, added after: a file something else has open throws out of
                    // Delete, and counting it first reported disk space that never came back.
                    var size = Engine.SizeOf(entry) ?? 0;
                    File.Delete(entry);
                    freed += size;
                    continue;
                }
                var inside = Path.GetFileName(entry) == "staging"
                    ? 0UL
                    : Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories)
                        .Aggregate(0UL, (sum, f) => sum + (Engine.SizeOf(f) ?? 0));
                Directory.Delete(entry, recursive: true);
                freed += inside;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Something has a file open. The rest still goes, and the caller re-reads what is
                // left rather than believing this.
            }
        }
        return freed;
    }

    /// <summary>Downloads whatever is missing or wrong and returns the folder to install from.
    /// A file that is already there and already hashes correctly is not fetched again.</summary>
    public async Task<string> EnsureAsync(PayloadManifest manifest, string component,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancel = default)
    {
        var c = manifest.Component(component);
        var dir = FolderFor(component, c.Version);
        Directory.CreateDirectory(dir);

        foreach (var file in c.Files)
        {
            var path = Path.Combine(dir, file.RelativePath);
            if (Engine.SizeOf(path) == file.Size && Engine.HashFile(path) == file.Sha256) continue;
            Engine.MakeParent(path);
            await FetchAnyAsync(manifest.DownloadUrls(component, file), path, file, progress, cancel);
        }

        // Only what is missing or wrong, and the archive opened once for all of it: the lmxxf
        // weights are some 460 entries, and opening a 228 MB archive per entry is minutes of disk.
        var pending = (c.Extract ?? []).Where(entry =>
        {
            var target = Path.Combine(dir, entry.RelativePath);
            return !(Engine.SizeOf(target) == entry.Size && Engine.HashFile(target) == entry.Sha256);
        }).ToList();
        if (pending.Count > 0)
        {
            var archive = Path.Combine(dir, c.Files[0].RelativePath);
            await Task.Run(() => ExtractVerified(archive, pending, dir), cancel);
        }
        return dir;
    }

    /// <summary>Takes one entry out of an archive and keeps it only if it hashes to its pin. The
    /// archive may be a zip appended to an executable -- ReShade's installer is exactly that -- whose
    /// offsets count from where the zip starts, not from the start of the file, which is why the
    /// archive is re-based before it is opened.</summary>
    internal static void ExtractVerified(string archive, PayloadFile entry, string target)
    {
        using var zip = OpenAppendedZip(archive);
        ExtractOne(zip, archive, entry, target);
    }

    /// <summary>Several entries out of one archive, each to its RelativePath under
    /// <paramref name="dir"/>, each kept only if it hashes to its pin.</summary>
    internal static void ExtractVerified(string archive, IEnumerable<PayloadFile> entries, string dir)
    {
        using var zip = OpenAppendedZip(archive);
        foreach (var entry in entries) ExtractOne(zip, archive, entry, Path.Combine(dir, entry.RelativePath));
    }

    /// <summary>The entry is found by the path it is kept under first, then by its name as a full
    /// path, and only then by its bare name in any folder. The OptiScaler package holds README.md
    /// both at its root and in lmxxf-modules, and a bare-name match took whichever came first.
    ///
    /// It streams to a .part file and is hashed on the way, rather than held in memory: one lmxxf
    /// weight is 201 MB.</summary>
    private static void ExtractOne(System.IO.Compression.ZipArchive zip, string archive, PayloadFile entry, string target)
    {
        static string Slashed(string s) => s.Replace('\\', '/');
        var item = zip.Entries.FirstOrDefault(e => string.Equals(Slashed(e.FullName), entry.RelativePath, StringComparison.OrdinalIgnoreCase))
                   ?? zip.Entries.FirstOrDefault(e => string.Equals(Slashed(e.FullName), entry.Name, StringComparison.OrdinalIgnoreCase))
                   ?? zip.Entries.FirstOrDefault(e => string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        Engine.Require(item is not null, $"{entry.Name} is not inside {Path.GetFileName(archive)}.");

        Engine.MakeParent(target);
        var part = target + ".part";
        string got;
        using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            using (var source = item!.Open())
            using (var sink = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    sha.AppendData(buffer, 0, read);
                    sink.Write(buffer, 0, read);
                }
            }
            got = Convert.ToHexStringLower(sha.GetHashAndReset());
        }

        if (got != entry.Sha256)
        {
            File.Delete(part);
            throw new InstallException(
                $"{entry.Name} inside {Path.GetFileName(archive)} does not match its pinned SHA-256."
                + $"\n      expected {entry.Sha256}\n      got      {got}");
        }
        File.Move(part, target, overwrite: true);
    }

    internal static System.IO.Compression.ZipArchive OpenAppendedZip(string path)
    {
        // End of central directory: signature 50 4B 05 06, somewhere in the last 64 KB (the comment
        // field is at most 65535 bytes). It records the directory's size and its offset from the
        // zip's own start, and the directory itself sits right before it -- so the zip starts at
        // (end-of-directory position) - (directory size) - (directory offset).
        var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            var tailLength = (int)Math.Min(file.Length, 22 + 65535);
            var tail = new byte[tailLength];
            file.Seek(-tailLength, SeekOrigin.End);
            file.ReadExactly(tail);
            for (var at = tail.Length - 22; at >= 0; at--)
            {
                if (tail[at] != 0x50 || tail[at + 1] != 0x4b || tail[at + 2] != 0x05 || tail[at + 3] != 0x06) continue;
                var size = BitConverter.ToUInt32(tail, at + 12);
                var offset = BitConverter.ToUInt32(tail, at + 16);
                var start = file.Length - tailLength + at - size - offset;
                Engine.Require(start >= 0, $"{Path.GetFileName(path)} has a damaged archive directory.");
                // A plain zip is read from disk as it is. Only a zip behind something else, like
                // the few MB of ReShade's setup, is re-based in memory.
                if (start == 0)
                {
                    file.Seek(0, SeekOrigin.Begin);
                    return new System.IO.Compression.ZipArchive(file, System.IO.Compression.ZipArchiveMode.Read);
                }
                var bytes = new byte[file.Length - start];
                file.Seek(start, SeekOrigin.Begin);
                file.ReadExactly(bytes);
                file.Dispose();
                return new System.IO.Compression.ZipArchive(new MemoryStream(bytes, writable: false),
                    System.IO.Compression.ZipArchiveMode.Read);
            }
        }
        catch
        {
            file.Dispose();
            throw;
        }
        file.Dispose();
        throw new InstallException($"{Path.GetFileName(path)} carries no archive to extract from.");
    }

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

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, live);
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

    /// <summary>One folder holding every file the given components need, which is what an install
    /// reads from. The files are hard-linked out of the cache where the filesystem allows it, so
    /// staging 141 MB of weights costs no disk and no copy; a volume that refuses gets a copy.</summary>
    public string Stage(PayloadManifest manifest, params string[] components)
    {
        var key = string.Join("-", components.Select(c => $"{Sanitise(c)}.{Sanitise(manifest.Component(c).Version)}"));
        var staging = Path.Combine(AppPaths.Cache, "staging", key.Length <= 120 ? key : key[..120]);
        Directory.CreateDirectory(staging);

        foreach (var component in components)
        {
            var c = manifest.Component(component);
            var from = FolderFor(component, c.Version);
            foreach (var file in c.Installed)
            {
                var source = Path.Combine(from, file.RelativePath);
                var target = Path.Combine(staging, file.RelativePath);
                // Remade every time, rather than skipped when the size matches. Size is not
                // identity: a build re-cut and published under an unchanged version is the same
                // number of bytes with different content, and the stale link then survived here
                // while the component cache beside it had already corrected itself -- the install
                // failed at verification with "does not match the expected SHA-256" against a file
                // the app had just downloaded correctly. The staging key is versions only, so this
                // is the layer that has to notice.
                //
                // A hard link costs nothing to remake, so there is nothing to save by being clever.
                // On a volume that refuses links this re-copies instead, weights included; that is
                // the price of the folder being right, and it is only paid where linking fails.
                Engine.MakeParent(target);
                if (File.Exists(target)) File.Delete(target);
                if (!TryHardLink(source, target)) File.Copy(source, target, overwrite: true);
            }
        }
        return staging;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr attributes);

    private static bool TryHardLink(string source, string target)
    {
        try { return CreateHardLinkW(target, source, IntPtr.Zero); }
        catch (EntryPointNotFoundException) { return false; }
    }

    /// <summary>The manifest itself. From <paramref name="url"/> when config.json names one, so the
    /// list can live anywhere; otherwise from raw.githubusercontent.com, which unlike the REST API
    /// has no rate limit to share with everyone else on the same address.</summary>
    public async Task<PayloadManifest> FetchManifestAsync(string owner, string repo, string branch = "main",
        string file = "payload.json", string? url = null, CancellationToken cancel = default)
    {
        var address = string.IsNullOrWhiteSpace(url)
            ? $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{file}"
            : url;
        using var response = await http.GetAsync(address, cancel);
        Engine.Require(response.IsSuccessStatusCode,
            $"Could not read the payload list: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        return PayloadManifest.Parse(await response.Content.ReadAsStringAsync(cancel));
    }

    /// <summary>The copy that ships beside the executable, or one the user dropped in the app's own
    /// folder. This is what makes the app work offline, on a first run behind a captive portal, and
    /// before the content repository exists at all -- the hashes are the same either way, so a local
    /// manifest weakens nothing.</summary>
    public static PayloadManifest? LoadLocalManifest(string fileName = "payload.json")
    {
        foreach (var path in new[]
                 {
                     Path.Combine(AppPaths.Root, fileName),
                     Path.Combine(AppContext.BaseDirectory, fileName),
                 })
        {
            try
            {
                if (File.Exists(path)) return PayloadManifest.Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or InstallException)
            {
                // Try the next one; a broken local copy must not stop the app from starting.
            }
        }
        return null;
    }

    /// <summary>A default client with a User-Agent, because GitHub refuses requests without one.</summary>
    public static HttpClient DefaultClient(string version)
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            // An address that is down has a mirror behind it; without this the connect attempt sits
            // there until the timeout below, which is not a fall-through, it is a hang.
            ConnectTimeout = TimeSpan.FromSeconds(20),
        })
        {
            // The whole of a 141 MB download, on a slow line. What ends a dead one is the stall
            // timer, not this.
            Timeout = TimeSpan.FromMinutes(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AMD-NR-ReShade-Installer/{version}");
        return client;
    }
}
