namespace AmdNr.Core.Tests;

/// <summary>A file the game still holds names the process holding it, and a d3d11.dll another mod left in the
/// folder is warned about (Batman: Arkham Knight, 2026-10-03: frozen game with no window, dxgi.dll held).</summary>
public class HeldFilesTests
{
    [Fact]
    public void AHeldFileNamesTheProcessHoldingIt()
    {
        var dir = Fixture.Temp("held");
        var path = Path.Combine(dir, "dxgi.dll");
        File.WriteAllBytes(path, [1, 2, 3]);
        using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var me = $"{System.Diagnostics.Process.GetCurrentProcess().ProcessName}.exe (PID {Environment.ProcessId})";
            Assert.Contains(me, Engine.HoldersOf([path]));
            var said = Engine.OpenElsewhere(dir, ["dxgi.dll"]);
            Assert.Contains($"dxgi.dll is open in {me}", said);
            Assert.Contains("Task Manager", said);
            var thrown = Assert.Throws<InstallException>(() => Engine.Write(path, [4]));
            Assert.Contains(me, thrown.Message);
        }
        Assert.Empty(Engine.HoldersOf([path]));
        Assert.Contains("open by another program", Engine.OpenElsewhere(dir, ["dxgi.dll"]));
    }

    [Fact]
    public void AnotherModsD3d11IsWarnedAboutOnTheD3d11RouteOnly()
    {
        var dir = Fixture.Temp("d3d11-wrapper");
        File.WriteAllBytes(Path.Combine(dir, "d3d11.dll"), new byte[132_608]);
        File.WriteAllBytes(Path.Combine(dir, "ori_d3d11.dll"), new byte[16]);

        var report = new Report();
        Work.CheckForeignD3d11(dir, Preset.Dx11, report, "dxgi.dll");
        var warning = Assert.Single(report.Lines, l => l.Item1 == Level.Warn).Item2;
        Assert.Contains("d3d11.dll in this folder is not Windows' own", warning);
        Assert.Contains("ori_d3d11.dll", warning);

        foreach (var (preset, keep) in new[] { (Preset.Dx12, "dxgi.dll"), (Preset.Dx11, "d3d11.dll") })
        {
            var quiet = new Report();
            Work.CheckForeignD3d11(dir, preset, quiet, keep);
            Assert.Empty(quiet.Lines);
        }
    }
}
