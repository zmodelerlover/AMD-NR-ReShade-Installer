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

public sealed record DownloadProgress(string File, long Received, long? Total)
{
    public double? Fraction => Total is > 0 ? (double)Received / Total.Value : null;
}

/// <param name="nearby">Folders to look in for a file before downloading it, and the same component
/// cached under another version beside them: see <see cref="Adopt"/>. Null looks nowhere, which is
/// what a test wants and what the app does not.</param>
public sealed partial class PayloadCache(HttpClient http, IReadOnlyList<string>? nearby = null)
{
    /// <summary>The client every download goes through. Kept here because a primary constructor's
    /// parameter is only in scope in the declaration that has it, and the downloads live in their own file.</summary>
    private HttpClient Http { get; } = http;

    private IReadOnlyList<string>? Nearby { get; } = nearby;

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
    /// about a second, which is worth paying before an install rather than trusting a size -- once:
    /// see <see cref="Verified"/> for why the second time is free.</summary>
    public static bool IsComplete(PayloadManifest manifest, string component)
    {
        var c = manifest.Component(component);
        var dir = FolderFor(component, c.Version);
        return c.Installed.All(f => Verified(Path.Combine(dir, f.RelativePath), f.Size, f.Sha256));
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
    /// A file that is already there and already hashes correctly is not fetched again.
    ///
    /// Every hash runs off the calling thread. The caller is a click handler, and the weights are a
    /// second of hashing: on the thread that draws the window that second was a frozen window, every
    /// time somebody pressed Download all with the cache already full.
    ///
    /// When everything an install reads is already there -- the files taken out of an archive,
    /// imported by hand -- the archive itself is not fetched: nothing reads it.</summary>
    public async Task<string> EnsureAsync(PayloadManifest manifest, string component,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancel = default)
    {
        var c = manifest.Component(component);
        var dir = FolderFor(component, c.Version);
        CreateFolder(dir);
        if (await Task.Run(() => IsComplete(manifest, component), cancel))
        {
            Trace?.Files.Add(("*", "every file already in the cache and verified"));
            return dir;
        }

        foreach (var file in c.Files)
        {
            var path = Path.Combine(dir, file.RelativePath);
            if (await Task.Run(() => Verified(path, file.Size, file.Sha256), cancel))
            {
                Trace?.Files.Add((file.Name, "already in the cache and verified"));
                continue;
            }
            Engine.MakeParent(path);
            if (await Task.Run(() => Adopt(component, file, path), cancel))
            {
                Trace?.Files.Add((file.Name, "taken from a verified copy already on this machine"));
                continue;
            }
            Trace?.Files.Add((file.Name, "not on this machine, so fetched: see the attempts"));
            await FetchAnyAsync(manifest.DownloadUrls(component, file), path, file, progress, cancel);
        }

        // Only what is missing or wrong, and the archive opened once for all of it: the lmxxf
        // weights are some 460 entries, and opening a 228 MB archive per entry is minutes of disk.
        var pending = await Task.Run(() => (c.Extract ?? [])
            .Where(entry => !Verified(Path.Combine(dir, entry.RelativePath), entry.Size, entry.Sha256))
            .ToList(), cancel);
        if (pending.Count > 0)
        {
            var archive = Path.Combine(dir, c.Files[0].RelativePath);
            await Task.Run(() => ExtractVerified(archive, pending, dir), cancel);
            Trace?.Files.Add(($"{pending.Count} entries", $"taken out of {c.Files[0].Name}"));
        }
        return dir;
    }

    /// <summary>The cache folder, or a sentence saying why it cannot be made. A download folder that
    /// cannot be written to is an antivirus or Windows' controlled folder access far more often than
    /// a full disk, and "Access to the path is denied" names neither.</summary>
    private static void CreateFolder(string dir)
    {
        try { Directory.CreateDirectory(dir); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InstallException(
                $"Cannot write to the download folder {dir}: {e.Message} An antivirus or Windows' "
                + "controlled folder access can block this; allowing this app there fixes it.");
        }
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

    /// <summary>One folder holding every file the given components need, which is what an install
    /// reads from. The files are hard-linked out of the cache where the filesystem allows it, so
    /// staging 141 MB of weights costs no disk and no copy; a volume that refuses gets a copy.</summary>
    public string Stage(PayloadManifest manifest, params string[] components)
    {
        // One at a time. The sheet's pre-flight stages in the background and Install stages right
        // after it; two of these on one folder deleted each other's links, and the install read a
        // folder with a file missing from it.
        lock (Staging) return StageLocked(manifest, components);
    }

    private static readonly Lock Staging = new();

    private static string StageLocked(PayloadManifest manifest, string[] components)
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
                // Verified a moment ago and gone now: an antivirus took it. Said as that, rather than
                // as "Could not find file" with a cache path nobody asked about.
                Engine.Require(File.Exists(source),
                    $"{file.Name} was downloaded and verified, then something took it out of {from}. That is "
                    + "almost always an antivirus. Allow that folder in it, or restore the file from its "
                    + "quarantine, and try again.");
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
}
