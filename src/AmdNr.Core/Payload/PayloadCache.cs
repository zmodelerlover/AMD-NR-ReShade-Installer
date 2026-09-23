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

public sealed partial class PayloadCache(HttpClient http)
{
    /// <summary>The client every download goes through. Kept here because a primary constructor's
    /// parameter is only in scope in the declaration that has it, and the downloads live in their own file.</summary>
    private HttpClient Http { get; } = http;

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

        foreach (var entry in c.Extract ?? [])
        {
            var target = Path.Combine(dir, entry.RelativePath);
            if (Engine.SizeOf(target) == entry.Size && Engine.HashFile(target) == entry.Sha256) continue;
            var archive = Path.Combine(dir, c.Files[0].RelativePath);
            await Task.Run(() => ExtractVerified(archive, entry, target), cancel);
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
        var item = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), entry.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        Engine.Require(item is not null, $"{entry.Name} is not inside {Path.GetFileName(archive)}.");

        using var buffer = new MemoryStream();
        using (var source = item!.Open()) source.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var got = Engine.Sha(bytes);
        Engine.Require(got == entry.Sha256,
            $"{entry.Name} inside {Path.GetFileName(archive)} does not match its pinned SHA-256."
            + $"\n      expected {entry.Sha256}\n      got      {got}");

        Engine.MakeParent(target);
        File.WriteAllBytes(target, bytes);
    }

    internal static System.IO.Compression.ZipArchive OpenAppendedZip(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // End of central directory: signature 50 4B 05 06, somewhere in the last 64 KB (the comment
        // field is at most 65535 bytes). It records the directory's size and its offset from the
        // zip's own start, and the directory itself sits right before it -- so the zip starts at
        // (end-of-directory position) - (directory size) - (directory offset).
        for (var at = bytes.Length - 22; at >= Math.Max(0, bytes.Length - 22 - 65535); at--)
        {
            if (bytes[at] != 0x50 || bytes[at + 1] != 0x4b || bytes[at + 2] != 0x05 || bytes[at + 3] != 0x06) continue;
            var size = BitConverter.ToUInt32(bytes, at + 12);
            var offset = BitConverter.ToUInt32(bytes, at + 16);
            var start = (long)at - size - offset;
            Engine.Require(start >= 0, $"{Path.GetFileName(path)} has a damaged archive directory.");
            return new System.IO.Compression.ZipArchive(
                new MemoryStream(bytes, (int)start, bytes.Length - (int)start, writable: false),
                System.IO.Compression.ZipArchiveMode.Read);
        }
        throw new InstallException($"{Path.GetFileName(path)} carries no archive to extract from.");
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
}
