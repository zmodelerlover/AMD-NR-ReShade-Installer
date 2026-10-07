// Godot 4: which renderer a game exported from it runs, read from its project settings in its pack.
//
// The executable says nothing useful: it imports opengl32.dll for the Compatibility renderer and loads Vulkan and
// D3D12 at run time. Until Then read as OpenGL from that import, and Vulkan for every Godot 4 game was wrong the
// other way: Compatibility is OpenGL (or D3D11 through ANGLE), and Forward+ and Mobile run the driver the project
// sets in rendering/rendering_device/driver.windows (d3d12 from 4.3 on). The engine's default stays vulkan: 4.6 only
// has the editor write d3d12 into projects it creates (godotengine/godot#113213), and older projects keep vulkan.
//
// The pack is the <exe name>.pck beside the executable or the one embedded at its end. Its header, from Godot's
// core/io/file_access_pack.cpp: "GDPC", the pack format (2 for 4.0 to 4.3, 3 from 4.4), the engine's major, minor
// and patch, flags (bit 0: the directory is encrypted; bit 1: offsets are from the pack's start), the files' base
// (uint64), from format 3 the directory's offset (uint64), 16 reserved uint32, then the directory: a count, and per
// file the path (uint32 length, padded to 4), offset and size (uint64 each), an md5 (16 bytes) and flags (uint32).
// Only the directory and res://project.binary are read, each bounded: this runs for every game in the library.

using System.Text;

namespace AmdNr.Core;

public static partial class GraphicsDetector
{
    /// <param name="Guessed">The API is the engine version's default, not read from the project.</param>
    internal sealed record GodotRenderer(GraphicsApi Api, string Why, bool Guessed);

    private const uint PackEncrypted = 1, PackRelativeBase = 2;
    private const int MaxPackFiles = 200_000, MaxPathBytes = 4096, MaxProjectBytes = 1 << 20;

    /// <summary>What a Godot 4 game renders with, or null when the executable is not one.</summary>
    internal static GodotRenderer? Godot4(string exe)
    {
        try
        {
            using var pack = OpenPack(exe, out var start);
            if (pack is null) return null;
            var head = new BinaryReader(pack, Encoding.UTF8, leaveOpen: true);
            pack.Seek(start, SeekOrigin.Begin);
            if (head.ReadUInt32() != 0x43504447) return null; // "GDPC"
            var format = head.ReadUInt32();
            var (major, minor) = (head.ReadInt32(), head.ReadInt32());
            if (major != 4) return null;
            head.ReadInt32(); // patch
            // A header that says Godot 4 is enough to know the engine: a directory or project.binary that cannot be
            // parsed after it is settings not read, a guess, never "not Godot" and the OpenGL its import says.
            Dictionary<string, string>? settings;
            try { settings = format is 2 or 3 ? ProjectSettings(pack, head, start, format) : null; }
            catch (Exception e) when (e is IOException or EndOfStreamException or ArgumentException or OverflowException)
            {
                settings = null;
            }
            return Renderer(Path.GetFileName(exe), minor, settings);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or EndOfStreamException
                                      or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>The pack beside the executable, or the executable itself when one is embedded at its end, with
    /// where the pack starts in it. Null when there is no Godot pack.</summary>
    private static FileStream? OpenPack(string exe, out long start)
    {
        start = 0;
        var beside = Path.ChangeExtension(exe, ".pck");
        if (File.Exists(beside)) return File.OpenRead(beside);
        // Closed on every way out but the one that hands it over: a handle left open on the game's executable would
        // read as the game running (Work.RunningFrom).
        var f = File.OpenRead(exe);
        try
        {
            if (f.Length >= 12)
            {
                f.Seek(-12, SeekOrigin.End);
                var tail = new byte[12];
                f.ReadExactly(tail);
                start = f.Length - 12 - BitConverter.ToInt64(tail, 0);
                if (tail.AsSpan(8).SequenceEqual("GDPC"u8) && start >= 0 && start < f.Length) return f;
            }
        }
        catch
        {
            f.Dispose();
            throw;
        }
        f.Dispose();
        return null;
    }

    /// <summary>The project's settings that name a renderer, as key and string value, or null when the directory is
    /// encrypted or project.binary is not in it.</summary>
    private static Dictionary<string, string>? ProjectSettings(Stream pack, BinaryReader r, long start, uint format)
    {
        var flags = r.ReadUInt32();
        if ((flags & PackEncrypted) != 0) return null;
        var fileBase = r.ReadInt64();
        if (format == 3 || (flags & PackRelativeBase) != 0) fileBase += start;
        long? directory = format == 3 ? r.ReadInt64() + start : null;
        if (directory is { } at) pack.Seek(at, SeekOrigin.Begin);
        else pack.Seek(16 * 4, SeekOrigin.Current);

        var count = r.ReadUInt32();
        if (count > MaxPackFiles) return null;
        for (var i = 0; i < count; i++)
        {
            var length = r.ReadInt32();
            if (length is < 0 or > MaxPathBytes) return null;
            var path = Encoding.UTF8.GetString(r.ReadBytes(length)).TrimEnd('\0');
            var offset = r.ReadInt64();
            var size = r.ReadInt64();
            r.ReadBytes(16); // md5
            var fileFlags = r.ReadUInt32();
            if (path.Replace("res://", "") != "project.binary") continue;
            if ((fileFlags & 1) != 0 || size is <= 0 or > MaxProjectBytes) return null; // encrypted, or not a project
            pack.Seek(fileBase + offset, SeekOrigin.Begin);
            return RendererSettings(r.ReadBytes((int)size));
        }
        return null;
    }

    private static readonly string[] RendererKeys =
    [
        "rendering/renderer/rendering_method", "rendering/rendering_device/driver.windows",
        "rendering/rendering_device/driver", "rendering/gl_compatibility/driver.windows",
    ];

    /// <summary>The renderer keys of a binary project.binary ("ECFG", a count, then per setting its key as a length
    /// and UTF-8 and its value as a length and an encoded Variant), with their values when they are strings.</summary>
    internal static Dictionary<string, string>? RendererSettings(byte[] data)
    {
        if (data.Length < 8 || !data.AsSpan(0, 4).SequenceEqual("ECFG"u8)) return null;
        var found = new Dictionary<string, string>(StringComparer.Ordinal);
        var at = 8;
        var count = BitConverter.ToUInt32(data, 4);
        for (var i = 0; i < count && at + 4 <= data.Length; i++)
        {
            var keyLength = BitConverter.ToInt32(data, at);
            if (keyLength < 0 || at + 4 + keyLength + 4 > data.Length) break;
            var key = Encoding.UTF8.GetString(data, at + 4, keyLength).TrimEnd('\0');
            at += 4 + keyLength;
            var valueLength = BitConverter.ToInt32(data, at);
            if (valueLength < 0 || at + 4 + valueLength > data.Length) break;
            // A String Variant: type 4 in the low byte of its header, then its length and bytes.
            if (RendererKeys.Contains(key) && valueLength >= 8 && (BitConverter.ToUInt32(data, at + 4) & 0xff) == 4
                && BitConverter.ToInt32(data, at + 8) is var textLength && textLength >= 0 && textLength <= valueLength - 8)
                found[key] = Encoding.UTF8.GetString(data, at + 12, textLength);
            at += 4 + valueLength;
        }
        return found;
    }

    /// <summary>The renderer the settings name, or, with no driver set, the engine's default, Vulkan, in every 4.x.
    /// Settings that cannot be read are a guess: Vulkan up to 4.5, and D3D12 from 4.6, whose editor writes d3d12 into
    /// the projects it creates (godotengine/godot#113213).</summary>
    private static GodotRenderer Renderer(string exeName, int minor, Dictionary<string, string>? settings)
    {
        var version = $"Godot 4.{minor}";
        var fallback = minor >= 6 ? GraphicsApi.D3D12 : GraphicsApi.Vulkan;
        if (settings is null)
            return new GodotRenderer(fallback, $"{exeName} is a {version} game whose project settings could not be read, so "
                + $"this is its default, {GraphicsDetection.Short(fallback)}. The project can also have picked "
                + $"{(fallback == GraphicsApi.D3D12 ? "Vulkan" : "DX12")}, or the Compatibility renderer, which is OpenGL.", true);
        var method = settings.GetValueOrDefault("rendering/renderer/rendering_method", "forward_plus");
        if (method == "gl_compatibility")
            return settings.GetValueOrDefault("rendering/gl_compatibility/driver.windows") == "opengl3_angle"
                ? new GodotRenderer(GraphicsApi.D3D11, $"{exeName} is a {version} game on the Compatibility renderer through ANGLE, which is D3D11.", false)
                : new GodotRenderer(GraphicsApi.OpenGL, $"{exeName} is a {version} game on the Compatibility renderer, which is OpenGL.", false);
        var driver = settings.GetValueOrDefault("rendering/rendering_device/driver.windows")
                     ?? settings.GetValueOrDefault("rendering/rendering_device/driver");
        var api = driver == "d3d12" ? GraphicsApi.D3D12 : GraphicsApi.Vulkan;
        return new GodotRenderer(api, $"{exeName} is a {version} game on the {(method == "mobile" ? "Mobile" : "Forward+")} renderer, "
            + (driver is "d3d12" or "vulkan" ? $"which its project sets to {GraphicsDetection.Short(api)}."
                : "on Vulkan, the engine's default."), false);
    }
}
