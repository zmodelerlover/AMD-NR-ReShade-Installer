using System.Runtime.InteropServices;
using System.Text;

namespace AmdNr.Core.Tests;

/// <summary>A PE that says what it is in its version resource, the way ReShade and Ultimate ASI Loader do: Windows'
/// own ping.exe, copied, with its RT_VERSION replaced through UpdateResource. What a file is, is read from that
/// resource (Work.Identify), so a stand-in has to carry one.</summary>
internal static class Versioned
{
    public static void Write(string path, string product, string version)
    {
        File.Copy(Path.Combine(Environment.SystemDirectory, "ping.exe"), path, overwrite: true);
        var info = VersionInfo(product, version);
        var update = BeginUpdateResource(path, false);
        if (update == IntPtr.Zero) throw new IOException($"BeginUpdateResource failed: {Marshal.GetLastWin32Error()}");
        if (!UpdateResource(update, (IntPtr)16, (IntPtr)1, 0x409, info, (uint)info.Length))
            throw new IOException($"UpdateResource failed: {Marshal.GetLastWin32Error()}");
        if (!EndUpdateResource(update, false)) throw new IOException($"EndUpdateResource failed: {Marshal.GetLastWin32Error()}");
    }

    /// <summary>VS_VERSIONINFO with a fixed part, one string table (040904b0) and its translation.</summary>
    private static byte[] VersionInfo(string product, string version)
    {
        var parts = version.Split('.').Select(p => uint.TryParse(p, out var n) ? n : 0).Concat([0u, 0u, 0u, 0u]).Take(4).ToArray();
        var ms = (parts[0] << 16) | parts[1];
        var ls = (parts[2] << 16) | parts[3];
        var fixedInfo = new uint[] { 0xFEEF04BD, 0x00010000, ms, ls, ms, ls, 0x3F, 0, 0x40004, 1, 0, 0, 0 }
            .SelectMany(BitConverter.GetBytes).ToArray();
        byte[] Text(string s) => Encoding.Unicode.GetBytes(s + "\0");
        byte[] String(string key, string value) => Block(key, Text(value), 1, (ushort)(value.Length + 1), []);
        var table = Block("040904b0", [], 1, 0,
            [String("ProductName", product), String("FileDescription", product), String("ProductVersion", version),
             String("FileVersion", version)]);
        var strings = Block("StringFileInfo", [], 1, 0, [table]);
        var translation = Block("VarFileInfo", [], 1, 0, [Block("Translation", BitConverter.GetBytes(0x04b00409), 0, 4, [])]);
        return Block("VS_VERSION_INFO", fixedInfo, 0, (ushort)fixedInfo.Length, [strings, translation]);
    }

    private static byte[] Block(string key, byte[] value, ushort type, ushort valueLength, byte[][] children)
    {
        var o = new List<byte>(new byte[6]);
        o.AddRange(Encoding.Unicode.GetBytes(key + "\0"));
        Pad(o);
        o.AddRange(value);
        foreach (var child in children)
        {
            Pad(o);
            o.AddRange(child);
        }
        var bytes = o.ToArray();
        BitConverter.GetBytes((ushort)bytes.Length).CopyTo(bytes, 0);
        BitConverter.GetBytes(valueLength).CopyTo(bytes, 2);
        BitConverter.GetBytes(type).CopyTo(bytes, 4);
        return bytes;

        static void Pad(List<byte> b)
        {
            while (b.Count % 4 != 0) b.Add(0);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern IntPtr BeginUpdateResource(string file, bool deleteExisting);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateResource(IntPtr update, IntPtr type, IntPtr name, ushort language, byte[] data, uint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool EndUpdateResource(IntPtr update, bool discard);
}
