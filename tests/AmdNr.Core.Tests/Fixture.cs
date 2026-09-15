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
