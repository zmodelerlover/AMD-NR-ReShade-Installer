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
public sealed class InstallException(string message) : Exception(message)
{
    /// <summary>What failed was reaching the server at all -- a name that did not resolve, a
    /// connection never made or gone quiet -- rather than what came back. Those are the failures a
    /// stale DNS cache causes, and the ones clearing it can fix.</summary>
    public bool Network { get; init; }
}

public static partial class Engine
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
    /// <summary>Where installs before v0.6.5 put their backups. Still read, never written:
    /// the files are on disk under this name in every folder installed back then, and a
    /// manifest carried over to the new name still points at them. Refusing the prefix made
    /// the whole manifest undecodable, which failed the install with "Unsafe backup entry"
    /// and rolled it back -- reported from GTA IV, where it meant the add-on could not be
    /// installed at all.</summary>
    public const string LegacyBackupDir = ".dlss5-x86bridge-backups";

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
        // The companion effect. The only entry here with a directory in it: it is a ReShade
        // effect, so it goes where ReShade's default EffectSearchPaths looks rather than in
        // the game's root. Path.Combine takes the forward slashes, and SafePath still walks
        // every prefix refusing links, so the write cannot leave the folder the user chose.
        "reshade-shaders/Shaders/AMD_Neural_Feed.fx",
        // The names add-on v0.6.0 and earlier installed. They stay in this set because a
        // manifest written by an older install names them, and a manifest naming anything
        // outside this set is refused -- which would take that folder's state and its
        // uninstall with it. They are also what Legacy below has to be allowed to delete.
        "dlss5-neural.ini",
        "dlss5-neural.addon32",
        "dlss5-neural.addon64",
        "dlss5-neural-host64.exe",
        // The OptiScaler route. OptiScaler itself goes in under a proxy name: dxgi.dll above, or
        // winmm.dll. It drives the runtime through one copy per pass, which the ReShade route
        // retired, and keeps its upscaler libraries in an OptiScaler folder beside the game.
        "winmm.dll",
        "OptiScaler.ini",
        "dlssnr_amd_pass2.dll",
        "dlssnr_amd_pass3.dll",
        "OptiScaler/amd_fidelityfx_loader_dx12.dll",
        "OptiScaler/amd_fidelityfx_upscaler_dx12.dll",
        "OptiScaler/amd_fidelityfx_framegeneration_dx12.dll",
        "OptiScaler/amd_fidelityfx_denoiser_dx12.dll",
        "OptiScaler/amd_fidelityfx_vk.dll",
        "OptiScaler/libxess.dll",
        "OptiScaler/libxess_dx11.dll",
        "OptiScaler/libxess_fg.dll",
        "OptiScaler/libxell.dll",
        "OptiScaler/D3D12_OptiScaler/D3D12Core.dll",
        "experimental_lighting/GatherCS.cso",
        "experimental_lighting/ResolveCS.cso",
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
        name is "ReShade.ini" or "dgVoodoo.conf" or "amd-nr.ini" or "OptiScaler.ini";
}
