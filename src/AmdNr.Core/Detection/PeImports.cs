// Import tables, read straight off the file.

namespace AmdNr.Core;

/// <summary>The names in a PE image's import and delay-import tables, read by seeking rather than
/// by loading the file: a game executable can be several hundred megabytes and only a few kilobytes
/// of it matter here.</summary>
public static class PeImports
{
    public static IReadOnlyCollection<string> Read(string path)
    {
        try
        {
            using var f = File.OpenRead(path);
            return Read(f);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InstallException
                                      or ArgumentException or EndOfStreamException)
        {
            return [];
        }
    }

    internal static IReadOnlyCollection<string> Read(Stream f)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var peAt = U32(f, 0x3c);
        if (U16(f, 0) != 0x5a4d || U32(f, peAt) != 0x4550) return names;

        var sections = U16(f, peAt + 6);
        var optionalSize = U16(f, peAt + 20);
        var optional = peAt + 24;
        var magic = U16(f, optional);
        if (magic is not (0x10b or 0x20b)) return names;

        var pe32Plus = magic == 0x20b;
        ulong imageBase = pe32Plus ? U64(f, optional + 24) : U32(f, optional + 28);
        var directories = optional + (pe32Plus ? 112u : 96u);
        var directoryCount = U32(f, optional + (pe32Plus ? 108u : 92u));

        // RVA -> file offset, through the section table.
        var table = new List<(uint Va, uint Size, uint Raw, uint RawSize)>();
        var sectionTable = optional + optionalSize;
        for (uint i = 0; i < Math.Min(sections, (ushort)96); i++)
        {
            var s = sectionTable + i * 40;
            table.Add((U32(f, s + 12), U32(f, s + 8), U32(f, s + 20), U32(f, s + 16)));
        }

        long? Offset(ulong rva)
        {
            foreach (var (va, size, raw, rawSize) in table)
            {
                var span = Math.Max(size, rawSize);
                if (rva >= va && rva < (ulong)va + span) return (long)(rva - va + raw);
            }
            return null;
        }

        // Ordinary imports: 20-byte descriptors, name RVA at +12, ended by a zeroed one.
        if (directoryCount > 1 && Offset(U32(f, directories + 8)) is { } imports)
        {
            for (var i = 0; i < 512; i++)
            {
                var d = imports + i * 20;
                var nameRva = U32(f, d + 12);
                if (nameRva == 0 && U32(f, d) == 0 && U32(f, d + 16) == 0) break;
                if (Offset(nameRva) is { } at && Name(f, at) is { } name) names.Add(name);
            }
        }

        // Delay-loaded imports: 32-byte descriptors, name at +4. The oldest linkers wrote VAs here
        // instead of RVAs, which bit 0 of Attributes distinguishes.
        if (directoryCount > 13 && Offset(U32(f, directories + 13 * 8)) is { } delayed)
        {
            for (var i = 0; i < 512; i++)
            {
                var d = delayed + i * 32;
                var attributes = U32(f, d);
                ulong nameRef = U32(f, d + 4);
                if (nameRef == 0) break;
                var rva = (attributes & 1) == 0 && nameRef >= imageBase ? nameRef - imageBase : nameRef;
                if (Offset(rva) is { } at && Name(f, at) is { } name) names.Add(name);
            }
        }

        return names;
    }

    private static string? Name(Stream f, long at)
    {
        if (at < 0 || at >= f.Length) return null;
        f.Position = at;
        Span<byte> buffer = stackalloc byte[128];
        var read = f.Read(buffer);
        var end = buffer[..read].IndexOf((byte)0);
        if (end <= 0) return null;
        foreach (var b in buffer[..end])
            if (b is < 0x20 or > 0x7e) return null;
        return System.Text.Encoding.ASCII.GetString(buffer[..end]);
    }

    private static ushort U16(Stream f, long at)
    {
        Span<byte> b = stackalloc byte[2];
        Fill(f, at, b);
        return (ushort)(b[0] | b[1] << 8);
    }

    private static uint U32(Stream f, long at)
    {
        Span<byte> b = stackalloc byte[4];
        Fill(f, at, b);
        return (uint)(b[0] | b[1] << 8 | b[2] << 16 | b[3] << 24);
    }

    private static ulong U64(Stream f, long at) => U32(f, at) | (ulong)U32(f, at + 4) << 32;

    private static void Fill(Stream f, long at, Span<byte> into)
    {
        if (at < 0 || at + into.Length > f.Length) throw new EndOfStreamException();
        f.Position = at;
        f.ReadExactly(into);
    }
}
