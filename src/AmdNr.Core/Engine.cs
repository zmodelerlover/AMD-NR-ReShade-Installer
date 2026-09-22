// Ported from installer/src/engine.rs of dlss5-neural-amd (MIT), which was itself ported from
// installer-x86/core.h. Behaviour is the contract here, not style: the message text of a refusal
// is asserted by tests, and the manifest bytes are a compatibility surface with installs that
// already exist on disk.

using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace AmdNr.Core;

/// <summary>Which side of the bridge an install belongs to. The preset name alone is ambiguous:
/// "D3D11" is valid on both routes, and without this a 64-bit install and a 32-bit one would look
/// interchangeable to <see cref="Transaction.Apply"/>, which refuses only a *different* preset.</summary>
public enum Route
{
    X86,
    X64,
}

/// <summary>Every refusal carries the sentence the user sees.</summary>
public sealed class InstallException(string message) : Exception(message);

public static class Engine
{
    public const string Notice =
        "Install official ReShade Full Add-on Support for the translated API. D3D8 uses the pinned d3d8to9 compatibility layer and the native D3D9 frontend.";

    public const string RuntimeSha = "70af3fb757f83f71ec947ce461970fdecc9636864bc01d952abffb36ae310be6";
    public const string WeightsSha = "6bf8dc931ef3ccffe18c82de26ab374156e7f19539ffcf8eabaa25dca5cf15ab";
    public const string ReShadeSha = "da430e0a9c6eecefa0d1b27d05e16c426fb5d04e808b194d914eaac4b31bc0f8";

    /// <summary>ReShade64.dll from the same official ReShade_Setup_6.8.0_Addon.exe whose ReShade32.dll
    /// is <see cref="ReShadeSha"/> -- verified by extracting both from that one installer.</summary>
    public const string ReShade64Sha = "0cee63f9c9f13f3ac909c5b4903f4dbb4b719a7ab3b4f13b0deaf83c814b94f7";
    public const string D3d8To9Version = "v1.15.1";
    public const string D3d8To9Commit = "65870f2302e9c496cd6d873d6095961d5c777668";
    public const string D3d8To9Sha = "ab6bf7a9a9f4b3e66a75ca038d8d10289c88acbfe8d52c3b5a8a9a259cb26cd5";

    public const string ManifestName = "amd-nr-x86bridge.install.json";
    public const string ManifestNameX64 = "amd-nr.install.json";
    public const string BackupDir = ".amd-nr-x86bridge-backups";

    /// <summary>x86 keeps the name installer-x86 already wrote, so existing installs stay readable.</summary>
    public static string ManifestFileName(this Route route) =>
        route == Route.X86 ? ManifestName : ManifestNameX64;

    public static void Require(bool condition, string why)
    {
        if (!condition) throw new InstallException(why);
    }

    internal static string Lower(string s)
    {
        // ASCII-only, like the Rust `to_ascii_lowercase`. A culture-aware lower would fold the
        // Turkish dotless i and change which section header matches on a Turkish system.
        Span<char> buf = s.Length <= 256 ? stackalloc char[s.Length] : new char[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            buf[i] = c is >= 'A' and <= 'Z' ? (char)(c + 32) : c;
        }
        return new string(buf);
    }

    /// <summary>Trims whitespace and a UTF-8 byte order mark. ReShade writes its INI with one,
    /// which puts an invisible character in front of the first [SECTION] header -- and a header
    /// that does not start with '[' is not a header, so every key in the first section becomes
    /// unreachable. That is how Half-Life 2's [INSTALL] BasePath read as absent and sent its
    /// install to the game root instead of bin.</summary>
    internal static string Trim(string s) => s.Trim(' ', '\t', '\r', '\n', '\uFEFF');

    // -- Hashing -------------------------------------------------------------------------------

    public static string Sha(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public static byte[] Read(string path)
    {
        try { return File.ReadAllBytes(path); }
        catch (Exception e) { throw new InstallException($"Cannot read {path}: {e.Message}"); }
    }

    public static void Write(string path, byte[] bytes)
    {
        try { File.WriteAllBytes(path, bytes); }
        catch (Exception e) { throw new InstallException($"Cannot write {path}: {e.Message}"); }
    }

    /// <summary>Streams rather than reading whole: the weights are 141 MB and this runs per file
    /// on every install and uninstall.</summary>
    public static string HashFile(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        }
        catch (Exception e) when (e is not InstallException)
        {
            throw new InstallException($"Cannot read {path}: {e.Message}");
        }
    }

    internal static void HashIs(ReadOnlySpan<byte> bytes, string expected, string what) =>
        Require(Sha(bytes) == expected, $"{what} SHA256 mismatch");

    internal static bool IsHex(string s, int len)
    {
        if (s.Length != len) return false;
        foreach (var c in s)
            if (!(c is >= '0' and <= '9' or >= 'a' and <= 'f')) return false;
        return true;
    }

    // -- PE ------------------------------------------------------------------------------------

    public const ushort MachineX86 = 0x14c;
    public const ushort MachineX64 = 0x8664;

    private static ushort U16At(ReadOnlySpan<byte> b, int p)
    {
        Require(p >= 0 && p + 2 <= b.Length, "Truncated PE");
        return (ushort)(b[p] | (b[p + 1] << 8));
    }

    private static uint U32At(ReadOnlySpan<byte> b, int p) =>
        U16At(b, p) | ((uint)U16At(b, p + 2) << 16);

    /// <summary>Reads the PE machine type, rejecting anything that is not a well-formed PE32 or
    /// PE32+ image. This is how bitness is decided -- a detection, never a question.</summary>
    public static ushort Machine(ReadOnlySpan<byte> b)
    {
        Require(b.Length >= 64 && U16At(b, 0) == 0x5a4d, "Not a PE executable");
        var p = (long)U32At(b, 60);
        Require(p >= 0 && p <= b.Length && b.Length - p >= 26 && U32At(b, (int)p) == 0x4550,
            "Invalid PE header");
        var m = U16At(b, (int)p + 4);
        var magic = U16At(b, (int)p + 24);
        Require((m == MachineX86 && magic == 0x10b) || (m == MachineX64 && magic == 0x20b),
            "Unsupported PE format");
        return m;
    }

    /// <summary>Read only enough of a file to answer the machine question. Scanning a game folder
    /// means opening every executable in it, and the header sits in the first few hundred bytes.</summary>
    public static ushort? MachineOfFile(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            var head = new byte[64 * 1024];
            var read = f.ReadAtLeast(head, head.Length, throwOnEndOfStream: false);
            return Machine(head.AsSpan(0, read));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Some maintained game wrappers deliberately forward Direct3D 8 to d3d8R.dll. Detect
    /// only an explicit embedded sidecar name; otherwise fail closed rather than replacing an
    /// unknown wrapper.</summary>
    public static bool AdvertisesD3d8Sidecar(ReadOnlySpan<byte> b)
    {
        ReadOnlySpan<byte> marker = "d3d8r.dll"u8;
        var n = marker.Length;
        for (var i = 0; i < b.Length; i++)
        {
            var rest = b.Length - i;
            if (rest >= n)
            {
                var ascii = true;
                for (var j = 0; j < n && ascii; j++) ascii = ToLowerByte(b[i + j]) == marker[j];
                if (ascii) return true;
            }
            if (rest >= n * 2)
            {
                var utf16 = true;
                for (var j = 0; j < n && utf16; j++)
                    utf16 = ToLowerByte(b[i + j * 2]) == marker[j] && b[i + j * 2 + 1] == 0;
                if (utf16) return true;
            }
        }
        return false;

        static byte ToLowerByte(byte c) => c is >= (byte)'A' and <= (byte)'Z' ? (byte)(c + 32) : c;
    }

    // -- INI -----------------------------------------------------------------------------------
    // Preserve all unedited INI bytes, including comments and unrelated preferences.

    public static string GetIni(string s, string section, string key)
    {
        var sec = string.Empty;
        foreach (var line in s.Split('\n'))
        {
            var t = Trim(line);
            if (t.Length > 1 && t[0] == '[' && t[^1] == ']')
            {
                sec = Lower(t[1..^1]);
            }
            else if (sec == Lower(section))
            {
                var eq = t.IndexOf('=');
                if (eq >= 0 && Lower(Trim(t[..eq])) == Lower(key)) return t[(eq + 1)..];
            }
        }
        return string.Empty;
    }

    public static string SetIni(string s, string section, string key, string value)
    {
        var nl = s.Contains("\r\n") ? "\r\n" : "\n";
        var outText = s;
        var sec = string.Empty;
        var start = 0;
        int? insert = null;
        var found = false;

        while (start < outText.Length)
        {
            var newline = outText.IndexOf('\n', start);
            var end = newline >= 0 ? newline + 1 : outText.Length;
            var t = Trim(outText[start..end]);

            if (t.Length > 1 && t[0] == '[' && t[^1] == ']')
            {
                if (found && sec == Lower(section))
                {
                    insert = start;
                    break;
                }
                sec = Lower(t[1..^1]);
                if (sec == Lower(section))
                {
                    found = true;
                    insert = end;
                }
            }
            else if (sec == Lower(section))
            {
                var eq = t.IndexOf('=');
                if (eq >= 0 && Lower(Trim(t[..eq])) == Lower(key))
                    return string.Concat(outText.AsSpan(0, start), $"{key}={value}{nl}", outText.AsSpan(end));
                insert = end;
            }
            start = end;
        }

        if (found)
        {
            var at = insert ?? outText.Length;
            var lead = at > 0 && outText[at - 1] != '\n' ? nl : string.Empty;
            return outText.Insert(at, $"{lead}{key}={value}{nl}");
        }

        if (outText.Length > 0 && !outText.EndsWith('\n')) outText += nl;
        return $"{outText}{nl}[{section}]{nl}{key}={value}{nl}";
    }

    public static string FreshIni() =>
        "[amd-nr]\r\n; x86 fresh-install overrides. All other values follow upstream defaults.\r\nScale=1.0\r\nColourStrength=0.25\r\nStructure=1\r\nSkin=1\r\nPasses=1\r\n";

    private static string? DockIdIn(string chunk)
    {
        var at = chunk.IndexOf("DockId=0x", StringComparison.Ordinal);
        if (at < 0) return null;
        var rest = chunk[(at + "DockId=".Length)..];
        var end = rest.Length;
        for (var i = 2; i < rest.Length; i++)
        {
            if (!Uri.IsHexDigit(rest[i]))
            {
                end = i;
                break;
            }
        }
        return rest[..end];
    }

    /// <summary>Dock the panel once, on a fresh layout only. A saved layout is authoritative even
    /// when the user undocked the panel, so this never rebuilds or guesses a target.</summary>
    public static string FirstDock(string ini, uint width, uint height)
    {
        var windows = GetIni(ini, "OVERLAY", "Window");
        const string panel = "[Window][AMD Neural Rendering]";
        if (windows.Contains(panel, StringComparison.Ordinal)) return ini;

        var dock = string.Empty;
        var home = windows.IndexOf("[Window][###home]", StringComparison.Ordinal);
        if (home >= 0)
        {
            var next = windows.IndexOf("[Window]", home + 1, StringComparison.Ordinal);
            var chunk = next >= 0 ? windows[home..next] : windows[home..];
            dock = DockIdIn(chunk) ?? string.Empty;
        }

        if (dock.Length == 0)
        {
            // Respect an existing layout without a docked Home. No destructive rebuild, no guessed target.
            if (windows.Length > 0 || GetIni(ini, "OVERLAY", "Docking").Length > 0) return ini;
            Require(width >= 320 && height >= 240, "Viewport size unavailable for fresh docking");

            var left = width * 35 / 100;
            var layout =
                $"[Docking][Data],DockSpace   ID=0xB0DF600F Pos=0,,0 Size={width},,{height} Split=X,  DockNode  ID=0x00000001 Parent=0xB0DF600F SizeRef={left},,{height},  DockNode  ID=0x00000002 Parent=0xB0DF600F SizeRef={width - left},,{height} CentralNode=1";
            ini = SetIni(ini, "OVERLAY", "Docking", layout);
            dock = "0x00000001";

            var tabs = new[] { "###home", "###addons", "###settings", "###statistics", "###log", "###about" };
            var builder = new StringBuilder(windows);
            for (var tab = 0; tab < tabs.Length; tab++)
            {
                if (builder.Length > 0) builder.Append(',');
                builder.Append($"[Window][{tabs[tab]}],Collapsed=0,DockId={dock},,{tab}");
            }
            windows = builder.ToString();
        }

        if (windows.Length > 0) windows += ",";
        windows += $"{panel},Collapsed=0,DockId={dock}";
        return SetIni(ini, "OVERLAY", "Window", windows);
    }

    // -- Ownership -----------------------------------------------------------------------------

    /// <summary>The only filenames this installer will ever create, back up or remove. A manifest
    /// naming anything else is rejected, which is what stops a tampered manifest from deleting
    /// arbitrary files. Case-sensitive, like the Rust set: d3d8R.dll is spelled that way on disk.</summary>
    public static readonly IReadOnlySet<string> Allowed = new HashSet<string>(StringComparer.Ordinal)
    {
        "dxgi.dll",
        // The 64-bit routes install ReShade too, under d3d11.dll or d3d12.dll when dxgi.dll already
        // belongs to something else (OptiScaler, DXVK). Adding names here changes no existing
        // manifest: decoding only refuses names that are *not* in this set.
        "d3d11.dll",
        "d3d12.dll",
        "d3d8.dll",
        "d3d8R.dll",
        "d3d9.dll",
        // The OpenGL route's proxy. ReShade under this name hooks the system opengl32 and the
        // add-on loads inside it exactly as it does under dxgi.dll.
        "opengl32.dll",
        // The way into a game that reaches its graphics API through something none of the names
        // above can displace. The pinned ReShade exports DirectInput8Create, so it really loads
        // under this one; without it here the proxy menu offered a name Transaction then refused.
        "dinput8.dll",
        "dgVoodoo.conf",
        "ReShade.ini",
        "amd-nr.ini",
        "amd-nr.addon32",
        "amd-nr.addon64",
        "amd-nr-host64.exe",
        "dlssnr_amd_pass1.dll",
        "dlssnr_on_amd_weights.bin",
        // The names add-on v0.6.0 and earlier installed. They stay in this set because a
        // manifest written by an older install names them, and a manifest naming anything
        // outside this set is refused -- which would take that folder's state and its
        // uninstall with it. They are also what Legacy below has to be allowed to delete.
        "dlss5-neural.ini",
        "dlss5-neural.addon32",
        "dlss5-neural.addon64",
        "dlss5-neural-host64.exe",
    };

    /// <summary>What add-on v0.6.0 and earlier left in a game folder, under the name it used
    /// then. ReShade loads every .addon64 in the folder, so an upgrade that writes
    /// amd-nr.addon64 beside an existing dlss5-neural.addon64 gets two add-ons, two overlays
    /// and two engines competing for the same present. The ini is NOT in here: the add-on
    /// copies it to amd-nr.ini on first run and leaves the original where it is.</summary>
    public static readonly IReadOnlyList<string> Legacy = new[]
    {
        "dlss5-neural.addon64",
        "dlss5-neural.addon32",
        "dlss5-neural-host64.exe",
    };

    /// <summary>The name the 32-bit route loads ReShade under when nobody has chosen one. D3D8 is
    /// translated to D3D9 before anything else, so it is the D3D9 name there too.</summary>
    public static string X86ProxyName(string preset) => preset == "D3D11" ? "dxgi.dll" : "d3d9.dll";

    public static bool IsConfig(string name) =>
        name is "ReShade.ini" or "dgVoodoo.conf" or "amd-nr.ini";

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
