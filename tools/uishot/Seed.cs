// What the flows stand in for: a 64-bit executable header, and a payload list whose every file is
// already in the cache.

using AmdNr.Core;

internal static class Seed
{
    // The smallest file that reads as a 64-bit executable: MZ, the PE signature, AMD64, PE32+.
    public static byte[] Pe64()
    {
        var b = new byte[512];
        b[0] = 0x4d; b[1] = 0x5a; b[60] = 128; b[128] = 0x50; b[129] = 0x45;
        b[132] = 0x64; b[133] = 0x86;
        b[152] = 0x0b; b[153] = 0x02;
        return b;
    }

    // An executable that imports one DLL, which is what detection reads the API from: the import table in
    // one section, as tests\AmdNr.Core.Tests\Fixture.PeWithImports lays it out.
    public static byte[] PeImporting(bool x64, string import)
    {
        const int peAt = 0x80, sectionRva = 0x1000, sectionRaw = 0x200;
        var optional = peAt + 24;
        var body = new byte[40 + import.Length + 1];
        BitConverter.GetBytes(1).CopyTo(body, 0);
        BitConverter.GetBytes(sectionRva + 40).CopyTo(body, 12);
        BitConverter.GetBytes(1).CopyTo(body, 16);
        System.Text.Encoding.ASCII.GetBytes(import).CopyTo(body, 40);
        var b = new byte[sectionRaw + body.Length];
        b[0] = 0x4d; b[1] = 0x5a;
        BitConverter.GetBytes(peAt).CopyTo(b, 0x3c);
        b[peAt] = 0x50; b[peAt + 1] = 0x45;
        BitConverter.GetBytes(x64 ? Engine.MachineX64 : Engine.MachineX86).CopyTo(b, peAt + 4);
        BitConverter.GetBytes((ushort)1).CopyTo(b, peAt + 6);
        BitConverter.GetBytes((ushort)(x64 ? 240 : 224)).CopyTo(b, peAt + 20);
        BitConverter.GetBytes((ushort)(x64 ? 0x20b : 0x10b)).CopyTo(b, optional);
        var dirs = optional + (x64 ? 112 : 96);
        BitConverter.GetBytes(16).CopyTo(b, optional + (x64 ? 108 : 92));
        BitConverter.GetBytes(sectionRva).CopyTo(b, dirs + 8);
        BitConverter.GetBytes(40).CopyTo(b, dirs + 12);
        var section = optional + (x64 ? 240 : 224);
        BitConverter.GetBytes(body.Length).CopyTo(b, section + 8);
        BitConverter.GetBytes(sectionRva).CopyTo(b, section + 12);
        BitConverter.GetBytes(body.Length).CopyTo(b, section + 16);
        BitConverter.GetBytes(sectionRaw).CopyTo(b, section + 20);
        body.CopyTo(b, sectionRaw);
        return b;
    }

    // A payload list whose every file is already in the cache. No ReShade component, whose hash is pinned
    // in the engine and cannot be stood in for; the add-on route installs without it. An OptiScaler release
    // goes under "releases", the way v0.5.0 lists every OptiScaler beyond the one older apps read.
    public static void SeedPayload(string addonVersion, string optiVersion = "1.0.0", string? optiRelease = null)
    {
        var components = new System.Text.Json.Nodes.JsonObject();
        string[] optiPaths =
        [
            "OptiScaler.dll", "OptiScaler.ini",
            "OptiScaler/amd_fidelityfx_loader_dx12.dll", "OptiScaler/amd_fidelityfx_upscaler_dx12.dll",
            "OptiScaler/amd_fidelityfx_framegeneration_dx12.dll", "OptiScaler/amd_fidelityfx_denoiser_dx12.dll",
            "OptiScaler/amd_fidelityfx_vk.dll", "OptiScaler/libxess.dll", "OptiScaler/libxess_dx11.dll",
            "OptiScaler/libxess_fg.dll", "OptiScaler/libxell.dll", "D3D12_OptiScaler/D3D12Core.dll",
        ];
        void Component(string name, string version, params string[] paths) =>
            components[name] = Pinned(name, version, paths);
        System.Text.Json.Nodes.JsonObject Pinned(string name, string version, params string[] paths)
        {
            var files = new System.Text.Json.Nodes.JsonArray();
            foreach (var path in paths)
            {
                // The bytes carry the version, so a second list pins a different build.
                byte[] bytes = path == "amd-nr.addon64"
                    ? [.. Pe64(), .. System.Text.Encoding.UTF8.GetBytes(addonVersion)]
                    : System.Text.Encoding.UTF8.GetBytes($"stand-in {path} {version}");
                var full = Path.Combine(PayloadCache.FolderFor(name, version), path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllBytes(full, bytes);
                files.Add(new System.Text.Json.Nodes.JsonObject
                {
                    ["name"] = Path.GetFileName(path),
                    ["path"] = path,
                    ["size"] = bytes.Length,
                    ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                    ["url"] = "https://127.0.0.1:1/" + Path.GetFileName(path),
                });
            }
            return new System.Text.Json.Nodes.JsonObject { ["version"] = version, ["files"] = files };
        }
        Component("addon", addonVersion, "amd-nr.addon64");
        Component("runtime", "1.0.0", "dlssnr_amd_pass1.dll", "dlssnr_on_amd_weights.bin");
        Component("optiscaler", optiVersion, optiPaths);
        Component("opti-runtime", "1.0.0", "dlssnr_amd_runtime-0.3.1.dll");
        var manifest = new System.Text.Json.Nodes.JsonObject { ["schema"] = 1, ["components"] = components };
        if (optiRelease is not null)
            manifest["releases"] = new System.Text.Json.Nodes.JsonObject { ["optiscaler"] = new System.Text.Json.Nodes.JsonArray(
                new System.Text.Json.Nodes.JsonObject { ["version"] = optiRelease, ["components"] =
                    new System.Text.Json.Nodes.JsonObject { ["optiscaler"] = Pinned("optiscaler", optiRelease, optiPaths) } }) };
        File.WriteAllText(Path.Combine(AppPaths.Root, "payload.json"), manifest.ToJsonString());
    }
}
