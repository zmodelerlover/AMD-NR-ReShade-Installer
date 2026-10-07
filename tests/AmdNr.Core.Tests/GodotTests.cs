using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>Godot 4 imports opengl32.dll for its Compatibility renderer and loads Vulkan or D3D12 at run time, so the
/// executable says nothing (Until Then read as OpenGL). Its project settings, in its pack, say which: Compatibility is
/// OpenGL (D3D11 through ANGLE), Forward+ and Mobile run the driver set, or the version's default, Vulkan up to 4.5 and
/// D3D12 from 4.6. A pack that cannot be read is that default, said as a guess.</summary>
public class GodotTests
{
    private static byte[] Project(params (string Key, string Value)[] settings)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("ECFG"u8);
        w.Write((uint)settings.Length);
        foreach (var (key, value) in settings)
        {
            var k = Encoding.UTF8.GetBytes(key);
            w.Write(k.Length);
            w.Write(k);
            var v = Encoding.UTF8.GetBytes(value);
            var padded = (v.Length + 3) / 4 * 4;
            w.Write(8 + padded);
            w.Write(4u); // Variant::STRING
            w.Write(v.Length);
            w.Write(v);
            w.Write(new byte[padded - v.Length]);
        }
        return ms.ToArray();
    }

    /// <summary>A pack as Godot writes it: format 2 with the directory after the header, format 3 with the directory
    /// after the files; offsets from the pack's start either way.</summary>
    private static byte[] Pack(int minor, byte[] project, int format = 2, bool encrypted = false)
    {
        var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        w.Write("GDPC"u8);
        w.Write((uint)format);
        w.Write(4);
        w.Write(minor);
        w.Write(0);
        w.Write((encrypted ? 1u : 0u) | 2u);
        var baseAt = ms.Position;
        w.Write(0L);
        var dirAt = ms.Position;
        if (format == 3) w.Write(0L);
        w.Write(new byte[16 * 4]);
        var path = Encoding.UTF8.GetBytes(format == 3 ? "project.binary" : "res://project.binary");
        void Directory()
        {
            w.Write(2u);
            foreach (var (name, size) in new[] { (Encoding.UTF8.GetBytes("res://icon.svg"), 0L), (path, (long)project.Length) })
            {
                var padded = (name.Length + 3) / 4 * 4;
                w.Write(padded);
                w.Write(name);
                w.Write(new byte[padded - name.Length]);
                w.Write(0L);
                w.Write(size);
                w.Write(new byte[16]);
                w.Write(0u);
            }
        }
        if (format == 2) Directory();
        var files = ms.Position;
        w.Write(project);
        var directory = ms.Position;
        if (format == 3) Directory();
        ms.Position = baseAt;
        w.Write(files);
        if (format == 3) w.Write(directory);
        return ms.ToArray();
    }

    private static readonly byte[] Exe = Fixture.PeWithImports(true, ["opengl32.dll", "KERNEL32.dll"]);

    private static GraphicsDetection Beside(string tag, byte[] pack)
    {
        var root = Fixture.Temp(tag);
        File.WriteAllBytes(Path.Combine(root, "Game.exe"), Exe);
        File.WriteAllBytes(Path.Combine(root, "Game.pck"), pack);
        return GraphicsDetector.Detect(root);
    }

    private static GraphicsDetection Embedded(string tag, byte[] pack)
    {
        var root = Fixture.Temp(tag);
        File.WriteAllBytes(Path.Combine(root, "Game.exe"), [.. Exe, .. pack, .. BitConverter.GetBytes((long)pack.Length), .. "GDPC"u8]);
        return GraphicsDetector.Detect(root);
    }

    [Fact]
    public void TheProjectSettingsNameTheRenderer()
    {
        const string method = "rendering/renderer/rendering_method", driver = "rendering/rendering_device/driver.windows";
        var compatibility = Beside("godot-compat", Pack(3, Project((method, "gl_compatibility"))));
        Assert.Equal(GraphicsApi.OpenGL, compatibility.Api);
        Assert.Contains("Compatibility", compatibility.Why);
        Assert.Equal(GraphicsApi.D3D11, Beside("godot-angle", Pack(3, Project((method, "gl_compatibility"),
            ("rendering/gl_compatibility/driver.windows", "opengl3_angle")))).Api);
        Assert.Equal(GraphicsApi.D3D12, Embedded("godot-d3d12", Pack(4, Project((driver, "d3d12")), format: 3)).Api);
        Assert.Equal(GraphicsApi.Vulkan, Beside("godot-vulkan", Pack(6, Project((method, "mobile"), (driver, "vulkan")), format: 3)).Api);

        var defaults46 = Beside("godot-46", Pack(6, Project(("application/config/name", "Until Then")), format: 3));
        Assert.Equal(GraphicsApi.D3D12, defaults46.Api);
        Assert.False(defaults46.Guessed);
        Assert.Equal(GraphicsApi.Vulkan, Embedded("godot-42", Pack(2, Project())).Api);
    }

    [Fact]
    public void AnEncryptedPackIsTheVersionsDefaultSaidAsAGuessAndNoRouteIsToldItDoesNotFit()
    {
        var guessed = Beside("godot-encrypted", Pack(6, Project(("rendering/renderer/rendering_method", "gl_compatibility")), encrypted: true));
        Assert.Equal(GraphicsApi.D3D12, guessed.Api);
        Assert.True(guessed.Guessed);
        Assert.Contains("Compatibility", guessed.Why);

        var game = Fixture.Temp("godot-encrypted-route");
        File.WriteAllBytes(Path.Combine(game, "Game.exe"), Exe);
        File.WriteAllBytes(Path.Combine(game, "Game.pck"), Pack(4, Project(), format: 3, encrypted: true));
        var (src, pins) = Fixture.Payloads("godot-encrypted-route");
        Assert.False(Fixture.HasAny(Work.Preflight(game, src, Preset.Dx12, pins), "This route needs"));
    }

    [Fact]
    public void Godot3StaysOpenGLAndAPackItCannotReadIsNoGodot()
    {
        var godot3 = Fixture.Temp("godot3");
        File.WriteAllBytes(Path.Combine(godot3, "Old.exe"), Exe);
        var pack3 = Pack(5, Project());
        BitConverter.GetBytes(3).CopyTo(pack3, 8);
        File.WriteAllBytes(Path.Combine(godot3, "Old.pck"), pack3);
        Assert.Equal(GraphicsApi.OpenGL, GraphicsDetector.Detect(godot3).Api);

        Assert.Equal(GraphicsApi.OpenGL, Beside("godot-junk", "GDPC-but-nothing-after"u8.ToArray()).Api);
        var huge = Pack(4, Project());
        BitConverter.GetBytes(int.MaxValue).CopyTo(huge, 5 * 4 + 4 + 8 + 64 + 4); // the first path's length
        Assert.Equal(GraphicsApi.Vulkan, Beside("godot-bounded", huge).Api);

        // A directory cut short after a good header is still Godot 4, as a guess: never the OpenGL of its import.
        var cut = Beside("godot-cut", Pack(4, Project())[..110]);
        Assert.Equal(GraphicsApi.Vulkan, cut.Api);
        Assert.True(cut.Guessed);
    }
}
