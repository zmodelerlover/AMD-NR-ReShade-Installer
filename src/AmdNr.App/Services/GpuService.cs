// What the machine says about itself. This exists because "nothing happens in game" is nearly always
// one of two things -- not a Radeon, or no HIP 7 -- and both are answerable here instead of in a
// support thread.
//
// The adapters come from DXGI, the same list a game's renderer picks from. WMI was used before: it
// is a second or more on a cold start, it ran on the thread that draws the window, and the library
// that reaches it cannot be trimmed out of a single-file executable safely. DXGI answers in
// milliseconds, lists only adapters that are actually there -- not ones left behind in the registry
// by a card that was swapped out -- and reports the driver's own version.

using System.Runtime.InteropServices;

namespace AmdNr.App;

/// <param name="Rdna4">Whether the card is RDNA4 (RX 9000 series), which the mochizuki runtime needs:
/// null when no adapter could be read, so nobody is told their card cannot do what it can.</param>
public sealed record SystemState(string Gpu, string Driver, bool Hip7, bool LooksLikeRadeon, bool? Rdna4 = null)
{
    public bool Ready => Hip7 && LooksLikeRadeon;
}

public static unsafe class GpuService
{
    private const uint AmdVendor = 0x1002;
    private const uint SoftwareAdapter = 2; // DXGI_ADAPTER_FLAG_SOFTWARE

    public static SystemState Read()
    {
        var name = "unknown";
        var driver = "unknown";
        var radeon = false;
        bool? rdna4 = null;
        try
        {
            // A laptop reports the integrated adapter too; the Radeon is the one that matters, and
            // of two Radeons the one with its own memory.
            var adapters = Adapters();
            var best = adapters.Where(a => a.Vendor == AmdVendor).OrderByDescending(a => a.Memory).FirstOrDefault()
                       ?? adapters.FirstOrDefault();
            if (best is not null)
            {
                name = best.Name;
                driver = best.Driver ?? driver;
                radeon = best.Vendor == AmdVendor || best.Name.Contains("Radeon", StringComparison.OrdinalIgnoreCase);
                rdna4 = IsRdna4(best.Vendor, best.Device, best.Name);
            }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException
                                      or SEHException)
        {
            // No DXGI is a machine this cannot run on anyway; say "unknown" rather than fall over.
        }
        return new SystemState(name, driver, FindHip7() is not null, radeon, rdna4);
    }

    /// <summary>The Navi 48 and Navi 44 device ids: RX 9070 XT, 9070 and 9070 GRE, Radeon AI PRO R9700,
    /// RX 9060 XT and 9060.</summary>
    private static readonly uint[] Rdna4Devices = [0x7550, 0x7551, 0x7590, 0x7591];

    /// <summary>RDNA4, by device id, or by the name every RDNA4 card has carried so far: RX 9000-something,
    /// or AI PRO R9000-something. A card named after neither is taken for what it says it is.</summary>
    public static bool IsRdna4(uint vendor, uint device, string name) =>
        vendor == AmdVendor
        && (Rdna4Devices.Contains(device)
            || System.Text.RegularExpressions.Regex.IsMatch(name, @"\bRX\s*9\d{3}|\bAI\s*PRO\s*R9\d{3}",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase));

    /// <summary>amdhip64_7.dll on the search path. HIP 6 does not count, which is why the name is
    /// checked rather than "some HIP".</summary>
    public static string? FindHip7()
    {
        var places = new List<string> { Environment.SystemDirectory };
        places.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(';',
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

        foreach (var dir in places)
        {
            try
            {
                var candidate = Path.Combine(dir, "amdhip64_7.dll");
                if (File.Exists(candidate)) return candidate;
            }
            catch (ArgumentException)
            {
                // A malformed PATH entry is not worth a crash.
            }
        }
        return null;
    }

    private sealed record Adapter(string Name, uint Vendor, uint Device, ulong Memory, string? Driver);

    [StructLayout(LayoutKind.Sequential)]
    private struct AdapterDesc1
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    private static readonly Guid IdxgiFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private static readonly Guid IdxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    /// <summary>A COM method, by its slot in the object's vtable.</summary>
    private static IntPtr Slot(IntPtr com, int slot) => (*(IntPtr**)com)[slot];

    private static void Release(IntPtr com) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Slot(com, 2))(com);

    /// <summary>Every hardware adapter DXGI lists, with its driver version. The slots are IDXGIFactory1
    /// EnumAdapters1 (12), IDXGIAdapter CheckInterfaceSupport (9) and IDXGIAdapter1 GetDesc1 (10).</summary>
    private static List<Adapter> Adapters()
    {
        var found = new List<Adapter>();
        if (CreateDXGIFactory1(IdxgiFactory1, out var factory) < 0 || factory == IntPtr.Zero) return found;
        try
        {
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(factory, 12);
            for (uint i = 0; i < 16; i++)
            {
                IntPtr adapter;
                if (enumAdapters(factory, i, &adapter) < 0) break; // DXGI_ERROR_NOT_FOUND ends the list
                try
                {
                    AdapterDesc1 desc;
                    var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, AdapterDesc1*, int>)Slot(adapter, 10);
                    if (getDesc(adapter, &desc) < 0 || (desc.Flags & SoftwareAdapter) != 0) continue;

                    // The user-mode driver's version, as the driver itself reports it: the number
                    // Adrenalin shows as the driver version.
                    long umd;
                    var id = IdxgiDevice;
                    var check = (delegate* unmanaged[Stdcall]<IntPtr, Guid*, long*, int>)Slot(adapter, 9);
                    string? driver = check(adapter, &id, &umd) >= 0
                        ? $"{(umd >> 48) & 0xffff}.{(umd >> 32) & 0xffff}.{(umd >> 16) & 0xffff}.{umd & 0xffff}"
                        : null;

                    found.Add(new Adapter(new string(desc.Description).TrimEnd('\0', ' '), desc.VendorId,
                        desc.DeviceId, desc.DedicatedVideoMemory, driver));
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        finally
        {
            Release(factory);
        }
        return found;
    }
}
