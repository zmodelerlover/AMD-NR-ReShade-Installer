using AmdNr.Core;
using Xunit;

namespace AmdNr.Core.Tests;

/// <summary>GameQuirks: the games whose files do not say what they need.</summary>
public class QuirksTests
{
    private static string Folder(string tag, params (string Name, byte[] Bytes)[] files)
    {
        var root = Fixture.Temp(tag);
        foreach (var (name, bytes) in files) File.WriteAllBytes(Path.Combine(root, name), bytes);
        return root;
    }

    /// <summary>Just Cause 2 imports only d3d9.dll and renders D3D10: the 32-bit D3D10/D3D11 route,
    /// and no database record replaces that.</summary>
    [Fact]
    public void JustCause2IsAD3D10Game()
    {
        var root = Folder("quirk-jc2", ("JustCause2.exe", Fixture.PeWithImports(false, ["d3d9.dll"])));
        var d = GraphicsDetector.Detect(root);
        Assert.Equal(GraphicsApi.D3D10, d.Api);
        Assert.Equal(Preset.X86Dx11, d.Preset);
        Assert.NotNull(d.Quirk);
        var wiki = d.With(new PcgwApi("Just Cause 2", [GraphicsApi.D3D9], true, false));
        Assert.Equal(Preset.X86Dx11, wiki.Preset);
    }

    /// <summary>GoldSrc: hl.exe with hw.dll is OpenGL, the servers beside it are not the game, and
    /// opengl32.dll goes in read-only, which reinstall and uninstall both get past.</summary>
    [Fact]
    public void HalfLifeKeepsItsOpengl32()
    {
        var root = Folder("quirk-hl",
            ("hl.exe", Fixture.PeWithImports(false, ["KERNEL32.dll"])),
            ("hw.dll", Fixture.PeWithImports(false, ["OPENGL32.dll"])),
            ("hlds.exe", Fixture.PeWithImports(false, ["KERNEL32.dll"])),
            ("hltv.exe", Fixture.PeWithImports(false, ["KERNEL32.dll"])));
        var d = GraphicsDetector.Detect(root);
        Assert.Equal("hl.exe", Path.GetFileName(d.Executable));
        Assert.Equal(GraphicsApi.OpenGL, d.Api);
        Assert.Equal(Preset.X86OpenGL, d.Preset);

        // A folder, not the executable: the servers do not make it ambiguous. With no release here,
        // the next thing missing is the release.
        var (src, pins) = Fixture.Payloads("quirk-hl-folder");
        var report = Work.Install(root, src, Preset.X86OpenGL, pins);
        Assert.False(Fixture.HasErr(report, "More than one 32-bit executable"), report.ToLog("servers"));

        var exe = Path.Combine(root, "hl.exe");
        var (installer, _) = UninstallInvariantTests.X86Release("quirk-hl");
        installer.Install(exe, "OpenGL");
        var proxy = Path.Combine(root, "opengl32.dll");
        Assert.True(File.GetAttributes(proxy).HasFlag(FileAttributes.ReadOnly));
        Assert.Contains(installer.Log, l => l.Contains("read-only", StringComparison.Ordinal));

        installer.Install(exe, "OpenGL");
        Assert.True(File.GetAttributes(proxy).HasFlag(FileAttributes.ReadOnly));

        installer.Uninstall(root, removeConfigs: true);
        Assert.False(File.Exists(proxy));
    }

    /// <summary>Without hw.dll, hl.exe is not GoldSrc's OpenGL build and nothing is assumed.</summary>
    [Fact]
    public void ANameAloneIsNotAQuirk() =>
        Assert.Null(GameQuirks.For(Path.Combine(
            Folder("quirk-hl-sw", ("hl.exe", Fixture.PeWithImports(false, ["KERNEL32.dll"]))), "hl.exe")));

    /// <summary>A D3D9 import beside a D3D10 name in the executable is said, not acted on.</summary>
    [Fact]
    public void AD3D10NameBesideAD3D9ImportIsSaid()
    {
        byte[] exe = [.. Fixture.PeWithImports(false, ["d3d9.dll"]), .. "\0D3D10_1.DLL\0"u8.ToArray()];
        var d = GraphicsDetector.Detect(Folder("quirk-hint", ("Game.exe", exe)));
        Assert.Equal(GraphicsApi.D3D9, d.Api);
        Assert.Contains("names d3d10.dll", d.Why, StringComparison.Ordinal);
    }
}
