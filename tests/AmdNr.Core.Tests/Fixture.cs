using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>Scratch folders and stand-in payloads.
///
/// The real weights are 141 MB, so almost every test here pins stand-ins instead: the hashes are
/// data now (PayloadPins) rather than constants compiled into the installer, which is exactly what
/// makes that possible. The tests that need the genuine bytes read AMDNR_TEST_PAYLOAD_DIR and skip
/// when it is not set.</summary>
internal static class Fixture
{
    public static string Temp(string tag)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"amdnr-{tag}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>A minimal well-formed PE image, the same shape the Rust and C++ fixtures build.</summary>
    public static byte[] Pe(bool x64)
    {
        var b = new byte[512];
        b[0] = 0x4d;
        b[1] = 0x5a;
        b[60] = 128;
        b[128] = 0x50;
        b[129] = 0x45;
        var machine = x64 ? Engine.MachineX64 : Engine.MachineX86;
        b[132] = (byte)(machine & 0xff);
        b[133] = (byte)(machine >> 8);
        var magic = x64 ? 0x20b : 0x10b;
        b[152] = (byte)(magic & 0xff);
        b[153] = (byte)(magic >> 8);
        return b;
    }

    /// <summary>A PE image with a real import table and delay-import table in one section, laid out
    /// the way a linker does: descriptors, their terminators, then the name strings.</summary>
    public static byte[] PeWithImports(bool x64, string[] imports, string[]? delayed = null)
    {
        delayed ??= [];
        const int peAt = 0x80;
        const int sectionRva = 0x1000;
        const int sectionRaw = 0x200;
        var optionalSize = x64 ? 240 : 224;
        var optional = peAt + 24;

        var importLength = (imports.Length + 1) * 20;
        var delayLength = (delayed.Length + 1) * 32;
        var section = new List<byte>(new byte[importLength + delayLength]);
        var nameRvas = new List<int>();
        foreach (var name in imports.Concat(delayed))
        {
            nameRvas.Add(sectionRva + section.Count);
            section.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
            section.Add(0);
        }
        var body = section.ToArray();

        for (var i = 0; i < imports.Length; i++)
        {
            var d = i * 20;
            BitConverter.GetBytes(1).CopyTo(body, d);              // OriginalFirstThunk
            BitConverter.GetBytes(nameRvas[i]).CopyTo(body, d + 12);
            BitConverter.GetBytes(1).CopyTo(body, d + 16);         // FirstThunk
        }
        for (var i = 0; i < delayed.Length; i++)
        {
            var d = importLength + i * 32;
            BitConverter.GetBytes(1).CopyTo(body, d);              // Attributes: RVA-based
            BitConverter.GetBytes(nameRvas[imports.Length + i]).CopyTo(body, d + 4);
        }

        var b = new byte[sectionRaw + body.Length];
        b[0] = 0x4d;
        b[1] = 0x5a;
        BitConverter.GetBytes(peAt).CopyTo(b, 0x3c);
        b[peAt] = 0x50;
        b[peAt + 1] = 0x45;
        BitConverter.GetBytes(x64 ? Engine.MachineX64 : Engine.MachineX86).CopyTo(b, peAt + 4);
        BitConverter.GetBytes((ushort)1).CopyTo(b, peAt + 6);
        BitConverter.GetBytes((ushort)optionalSize).CopyTo(b, peAt + 20);
        BitConverter.GetBytes((ushort)(x64 ? 0x20b : 0x10b)).CopyTo(b, optional);

        var dirs = optional + (x64 ? 112 : 96);
        BitConverter.GetBytes(16).CopyTo(b, optional + (x64 ? 108 : 92));
        if (imports.Length > 0)
        {
            BitConverter.GetBytes(sectionRva).CopyTo(b, dirs + 8);
            BitConverter.GetBytes(importLength).CopyTo(b, dirs + 12);
        }
        if (delayed.Length > 0)
        {
            BitConverter.GetBytes(sectionRva + importLength).CopyTo(b, dirs + 13 * 8);
            BitConverter.GetBytes(delayLength).CopyTo(b, dirs + 13 * 8 + 4);
        }

        var table = optional + optionalSize;
        BitConverter.GetBytes(body.Length).CopyTo(b, table + 8);   // VirtualSize
        BitConverter.GetBytes(sectionRva).CopyTo(b, table + 12);   // VirtualAddress
        BitConverter.GetBytes(body.Length).CopyTo(b, table + 16);  // SizeOfRawData
        BitConverter.GetBytes(sectionRaw).CopyTo(b, table + 20);   // PointerToRawData

        body.CopyTo(b, sectionRaw);
        return b;
    }

    /// <summary>A payload folder holding stand-ins, and the pins that match them.</summary>
    public static (string Dir, PayloadPins Pins) Payloads(string tag)
    {
        var dir = Temp($"payload-{tag}");
        var addon = Pe(x64: true);
        var runtime = Encoding.UTF8.GetBytes("stand-in runtime");
        var weights = Encoding.UTF8.GetBytes("stand-in weights");

        File.WriteAllBytes(Path.Combine(dir, Work.AddonName), addon);
        File.WriteAllBytes(Path.Combine(dir, Work.RuntimeName), runtime);
        File.WriteAllBytes(Path.Combine(dir, Work.WeightsName), weights);

        return (dir, new PayloadPins
        {
            AddonSha = Engine.Sha(addon),
            AddonSize = (ulong)addon.Length,
            RuntimeSha = Engine.Sha(runtime),
            RuntimeSize = (ulong)runtime.Length,
            WeightsSha = Engine.Sha(weights),
            WeightsSize = (ulong)weights.Length,
        });
    }

    public static bool HasErr(Report r, string needle) =>
        r.Lines.Any(l => l.Level == Level.Err && l.Text.Contains(needle, StringComparison.Ordinal));

    public static bool HasAny(Report r, string needle) =>
        r.Lines.Any(l => l.Text.Contains(needle, StringComparison.Ordinal));

    public static IEnumerable<string> Walk(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) : [];
}
