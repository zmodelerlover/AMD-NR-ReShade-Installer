// Files that are already on this machine: the same component cached under another version, a copy
// beside the app, one in the Downloads folder, or a folder somebody picked because the download
// would not work for them. Every one of them is taken only if it hashes to its pin, so a file found
// anywhere is exactly as trustworthy as one that was downloaded -- which is what makes it safe to
// look.

using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace AmdNr.Core;

/// <summary>What an import took and what it still could not find, by file name.</summary>
public sealed record ImportResult(IReadOnlyList<string> Taken, IReadOnlyList<string> Missing);

public sealed partial class PayloadCache
{
    /// <summary>Hashes already worked out, by path, together with the size and the write time they
    /// were worked out for. Opening a game used to hash the 141 MB of weights again every time, to
    /// ask whether the cache was complete; a file that has not changed size or been written since
    /// is the same file. Nothing an install writes relies on this -- the install hashes every byte
    /// it copies, again, as it always has.</summary>
    private static readonly ConcurrentDictionary<string, (ulong Size, DateTime Written, string Sha)> Hashes =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether the file at <paramref name="path"/> is the pinned one. False for a file that
    /// is missing, the wrong size, unreadable, or not those bytes.</summary>
    internal static bool Verified(string path, ulong size, string sha)
    {
        FileInfo info;
        try
        {
            info = new FileInfo(path);
            if (!info.Exists || (ulong)info.Length != size) return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException
                                      or NotSupportedException)
        {
            return false;
        }

        var key = info.FullName;
        var written = info.LastWriteTimeUtc;
        if (Hashes.TryGetValue(key, out var seen) && seen.Size == size && seen.Written == written)
            return seen.Sha == sha;

        string got;
        try { got = Engine.HashFile(path); }
        catch (InstallException) { return false; }
        Hashes[key] = (size, written, got);
        return got == sha;
    }

    /// <summary>Where a file is looked for before it is downloaded, when the app asks for that: the
    /// folder the app runs from and the Downloads folder. Somebody whose download fails is told to
    /// fetch the files with a browser, and a browser puts them in Downloads; somebody else unzips
    /// them beside the app. Either way, pressing the button again finds them.</summary>
    public static IReadOnlyList<string> NearbyFolders()
    {
        var folders = new List<string> { AppContext.BaseDirectory };
        if (DownloadsFolder() is { } downloads) folders.Add(downloads);
        return folders;
    }

    /// <summary>Takes a file this component needs from somewhere else on the machine: the same
    /// component under any other version in the cache, then <see cref="Nearby"/>. A version bump that
    /// changed nothing in one file is how the weights would otherwise be downloaded again for nothing,
    /// and a copy somebody fetched by hand is how a machine that cannot reach the host installs at
    /// all. Nothing is taken that does not hash to the pin.</summary>
    private bool Adopt(string component, PayloadFile file, string target)
    {
        if (Nearby is null) return false;

        var places = new List<string>();
        try
        {
            var versions = Path.Combine(AppPaths.Cache, Sanitise(component));
            if (Directory.Exists(versions))
                foreach (var version in Directory.GetDirectories(versions))
                    places.Add(Path.Combine(version, file.RelativePath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A cache that cannot be listed has nothing to offer; the nearby folders still might.
        }
        foreach (var folder in Nearby)
        {
            places.Add(Path.Combine(folder, file.Name));
            if (file.AssetName != file.Name) places.Add(Path.Combine(folder, file.AssetName));
        }

        return places.Any(place => !Engine.SamePath(Path.GetFullPath(place), Path.GetFullPath(target))
                                   && TakeVerified(place, file, target));
    }

    /// <summary>Copies <paramref name="source"/> to <paramref name="target"/> when it is the pinned
    /// file. The copy lands under a temporary name and takes the real one only once it is complete,
    /// so a copy cut short can never pass for a finished file.</summary>
    private static bool TakeVerified(string source, PayloadFile file, string target)
    {
        if (!Verified(source, file.Size, file.Sha256)) return false;
        var part = target + ".part";
        try
        {
            Engine.MakeParent(target);
            File.Copy(source, part, overwrite: true);
            File.Move(part, target, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstallException)
        {
            try { File.Delete(part); }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException)
            {
                // Left for the next attempt, which starts it over.
            }
            return false;
        }
    }

    /// <summary>Every file the given components need, taken out of a folder somebody downloaded
    /// them into by hand. It is searched a few levels deep, because an unzipped release has
    /// subfolders and nobody should have to know which one holds what; each file is matched by its
    /// name and size and taken only if it hashes to its pin. A file taken out of an archive is
    /// accepted on its own as well -- ReShade64.dll without the setup it came in -- and the archive,
    /// when that is what was found, is unpacked the way a download would have been.</summary>
    public static ImportResult Import(PayloadManifest manifest, IEnumerable<string> components, string folder)
    {
        var found = Index(folder);
        var taken = new List<string>();
        var missing = new List<string>();

        foreach (var component in components)
        {
            var c = manifest.Component(component);
            var dir = FolderFor(component, c.Version);
            CreateFolder(dir);

            foreach (var file in c.Installed.Concat(c.Extract is { Count: > 0 } ? c.Files : []))
            {
                var target = Path.Combine(dir, file.RelativePath);
                if (Verified(target, file.Size, file.Sha256)) continue;
                var candidates = found.GetValueOrDefault(file.Name, []).Concat(found.GetValueOrDefault(file.AssetName, []));
                if (candidates.Distinct(StringComparer.OrdinalIgnoreCase).Any(path => TakeVerified(path, file, target)))
                    taken.Add(file.Name);
            }

            foreach (var entry in c.Extract ?? [])
            {
                var target = Path.Combine(dir, entry.RelativePath);
                var archive = Path.Combine(dir, c.Files[0].RelativePath);
                if (Verified(target, entry.Size, entry.Sha256) || !Verified(archive, c.Files[0].Size, c.Files[0].Sha256))
                    continue;
                ExtractVerified(archive, entry, target);
            }

            missing.AddRange(c.Installed
                .Where(f => !Verified(Path.Combine(dir, f.RelativePath), f.Size, f.Sha256))
                .Select(f => f.Name));
        }
        return new ImportResult(taken.Distinct().ToList(), missing.Distinct().ToList());
    }

    /// <summary>The files under a folder, by name, a few levels down and no further: somebody may
    /// pick their whole Downloads folder, and that should take seconds, not the rest of the day.
    /// Links are not followed, so a folder cannot lead the search out of itself.</summary>
    private static Dictionary<string, List<string>> Index(string folder, int depth = 4, int limit = 20000)
    {
        var index = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var seen = 0;
        Walk(folder, 0);
        return index;

        void Walk(string dir, int level)
        {
            string[] files, dirs;
            try
            {
                files = Directory.GetFiles(dir);
                dirs = level < depth ? Directory.GetDirectories(dir) : [];
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }
            foreach (var file in files)
            {
                if (++seen > limit) return;
                var name = Path.GetFileName(file);
                if (!index.TryGetValue(name, out var list)) index[name] = list = [];
                list.Add(file);
            }
            foreach (var sub in dirs)
            {
                if (new DirectoryInfo(sub).Attributes.HasFlag(FileAttributes.ReparsePoint)) continue;
                Walk(sub, level + 1);
            }
        }
    }

    /// <summary>The Downloads folder where Windows really keeps it, which is not always under the
    /// profile: it can be moved to another drive from its own properties.</summary>
    private static string? DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var path) == 0
                && Directory.Exists(path))
                return path;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException or COMException)
        {
            // Fall back to the usual place below.
        }
        var usual = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
        return Directory.Exists(usual) ? usual : null;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags,
        IntPtr token, [MarshalAs(UnmanagedType.LPWStr)] out string path);
}
