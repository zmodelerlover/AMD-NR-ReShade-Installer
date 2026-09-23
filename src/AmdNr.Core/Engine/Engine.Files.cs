// Where a write may go and whether it can: reparse points, the BasePath redirect, a folder that is
// read-only or held open, and the one rename that has to reach the disk before anything else.

using System.Runtime.InteropServices;
using System.Text;

namespace AmdNr.Core;

public static partial class Engine
{
    // -- Paths ---------------------------------------------------------------------------------

    /// <summary>Walk every prefix of the path and refuse links. A reparse point anywhere in the
    /// chain could put a write outside the directory the user chose, so this is checked before
    /// each write, not once.</summary>
    public static void SafePath(string p)
    {
        var absolute = Absolute(p);
        var walk = string.Empty;
        foreach (var part in Components(absolute))
        {
            walk = walk.Length == 0 ? part : Path.Combine(walk, part);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(walk); }
            catch { continue; } // Does not exist yet: nothing to impersonate.
            Require((attributes & FileAttributes.ReparsePoint) == 0, $"Reparse path refused: {walk}");
        }
    }

    /// <summary>Root (C:\, \\server\share) first, then one entry per level.</summary>
    private static IEnumerable<string> Components(string absolute)
    {
        var root = Path.GetPathRoot(absolute) ?? string.Empty;
        if (root.Length > 0) yield return root;
        var rest = absolute[root.Length..].Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        foreach (var part in rest)
            if (part.Length > 0) yield return part;
    }

    internal static string Absolute(string p) =>
        Path.IsPathFullyQualified(p) ? p : Path.GetFullPath(p);

    /// <summary>The std::filesystem::weakly_canonical of the original: absolute, with '.' and '..'
    /// resolved textually and no trailing separator, whether or not the path exists.
    ///
    /// Deviation from the Rust, deliberately: that one called canonicalize(), which also folds the
    /// on-disk casing. Here comparisons use OrdinalIgnoreCase instead (see <see cref="IsInside"/>),
    /// which is what the filesystem itself does on Windows, so the fold buys nothing.</summary>
    public static string WeaklyCanonical(string p)
    {
        var full = Path.GetFullPath(Absolute(p));
        return full.Length > 3 ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>Component-aware containment: C:\game must not contain C:\gameX.</summary>
    public static bool IsInside(string root, string candidate)
    {
        var r = root.TrimEnd(Path.DirectorySeparatorChar);
        return candidate.Length > r.Length
               && candidate.StartsWith(r, StringComparison.OrdinalIgnoreCase)
               && (candidate[r.Length] == Path.DirectorySeparatorChar
                   || candidate[r.Length] == Path.AltDirectorySeparatorChar);
    }

    public static bool SamePath(string a, string b) =>
        string.Equals(a.TrimEnd(Path.DirectorySeparatorChar), b.TrimEnd(Path.DirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>Where the add-on actually goes. ReShade's [INSTALL] BasePath is honoured, but only
    /// when it resolves inside the selected game directory -- Half-Life 2 loads its proxy from bin,
    /// and a BasePath pointing anywhere else is an escape, not a layout.</summary>
    public static string InstallDirectory(string target)
    {
        // Either end of the pointer works: an executable, whose folder is the root, or the folder
        // itself. The x86 side has always named an executable because it needs the PE header; the
        // x64 side has always named a folder. Accepting both is what lets one screen serve both.
        var absoluteTarget = Absolute(target);
        var root = Directory.Exists(absoluteTarget)
            ? WeaklyCanonical(absoluteTarget)
            : WeaklyCanonical(Path.GetDirectoryName(absoluteTarget) is { Length: > 0 } parent ? parent : ".");

        SafePath(root);
        var redirect = Path.Combine(root, "ReShade.ini");
        SafePath(redirect);
        if (!File.Exists(redirect)) return root;

        var text = Encoding.UTF8.GetString(Read(redirect));
        var configured = Trim(GetIni(text, "INSTALL", "BasePath"));
        if (configured.Length == 0) return root;

        var candidate = WeaklyCanonical(Path.IsPathFullyQualified(configured)
            ? configured
            : Path.Combine(root, configured));
        SafePath(candidate);

        Require(IsInside(root, candidate) && !SamePath(root, candidate),
            "ReShade BasePath must stay inside the selected game directory");
        Require(Directory.Exists(candidate), "ReShade BasePath is not an existing directory");
        return candidate;
    }

    // -- Environment ---------------------------------------------------------------------------

    /// <summary>Can this folder be written to at all? Program Files without elevation is the usual
    /// answer.</summary>
    public static bool FolderIsWritable(string dir)
    {
        var probe = Path.Combine(dir, ".amd-nr-installer-write-probe");
        try
        {
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>A file that exists but cannot be opened for writing is held by something -- on
    /// Windows that is nearly always the game still running, which is the single most common way
    /// an install fails.</summary>
    public static bool IsLocked(string path)
    {
        if (!File.Exists(path)) return false;
        try
        {
            using var _ = File.Open(path, FileMode.Open, FileAccess.Write, FileShare.None);
            return false;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Free bytes available to this user, quota included.</summary>
    public static ulong? FreeBytes(string dir)
    {
        try
        {
            // ponytail: DriveInfo covers local volumes, which is every case a game folder has had
            // so far. It throws on a UNC path; swap in GetDiskFreeSpaceExW if that ever shows up.
            var root = Path.GetPathRoot(Absolute(dir));
            if (string.IsNullOrEmpty(root)) return null;
            var available = new DriveInfo(root).AvailableFreeSpace;
            return available >= 0 ? (ulong)available : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Same file, same bytes? Only the length is compared, which is what keeps the guard
    /// cheap.</summary>
    public static ulong? SizeOf(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? (ulong)info.Length : null;
        }
        catch
        {
            return null;
        }
    }

    // -- Commit --------------------------------------------------------------------------------

    // DllImport rather than LibraryImport: the generated marshalling needs AllowUnsafeBlocks on the
    // whole assembly, and this is the only P/Invoke in it.
    [DllImport("kernel32.dll", EntryPoint = "MoveFileExW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool MoveFileExW(string from, string to, uint flags);

    private const uint MoveFileReplaceExisting = 0x1;
    private const uint MoveFileWriteThrough = 0x8;

    /// <summary>File.Move(overwrite: true) would do the replace, but not the write-through: the
    /// journal only means anything if it is on the disk before the writes it describes.</summary>
    internal static void CommitRename(string from, string to)
    {
        var ok = MoveFileExW(from, to, MoveFileReplaceExisting | MoveFileWriteThrough);
        Require(ok, "Manifest commit failed");
    }

    public static void MakeParent(string p)
    {
        var parent = Path.GetDirectoryName(p);
        if (string.IsNullOrEmpty(parent)) return;
        try { Directory.CreateDirectory(parent); }
        catch (Exception e) { throw new InstallException($"Cannot create {parent}: {e.Message}"); }
    }
}
