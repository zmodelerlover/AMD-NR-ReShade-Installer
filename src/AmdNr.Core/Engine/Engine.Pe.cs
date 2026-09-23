// Reading a PE header: the machine type that decides the route, and the sidecar name a D3D8
// wrapper can carry.

namespace AmdNr.Core;

public static partial class Engine
{
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
}
