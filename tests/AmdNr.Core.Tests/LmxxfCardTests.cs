using System.Text;
using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>An RX 7900 XT got NrBackend=lmxxf from OptiScaler 0.4.9's own ini and 590 MB of lmxxf it cannot run. Off an
/// RX 9000 card a package ini naming lmxxf runs danielblnc, and lmxxf's weights are neither pinned nor installed.</summary>
public class LmxxfCardTests
{
    [Fact]
    public void OffAnRx9000CardTheIniRunsDanielblncAndTheWeightsStayOut()
    {
        var game = Fixture.Temp("lmxxf-card");
        File.WriteAllBytes(Path.Combine(game, "Game.exe"), Fixture.PeWithImports(true, ["d3d12.dll"]));
        File.WriteAllText(Path.Combine(game, "nvngx_dlss.dll"), "dlss");
        var (src, pins) = OptiScalerRouteTests.Payloads("lmxxf-card");
        var ini = Encoding.UTF8.GetBytes("[DlssNr]\r\nNrBackend = lmxxf\r\n");
        File.WriteAllBytes(Path.Combine(src, Work.OptiScalerIni), ini);
        ((Dictionary<string, string>)pins.OptiFiles)[Work.OptiScalerIni] = Engine.Sha(ini);

        var report = Work.Install(game, src, Preset.OptiScaler, pins, mochizuki: false);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.Equal("daniel", Engine.Trim(Engine.GetIni(File.ReadAllText(Path.Combine(game, Work.OptiScalerIni)), "DlssNr", "NrBackend")));
        Assert.DoesNotContain(report.Lines, l => l.Text.Contains("went in too, with its weights"));

        // With lmxxf's weights in the install -- an RX 9000 card, or one nobody could read -- the package's choice stands.
        var rx9000 = Fixture.Temp("lmxxf-card-9000");
        File.WriteAllBytes(Path.Combine(rx9000, "Game.exe"), Fixture.PeWithImports(true, ["d3d12.dll"]));
        var weight = Encoding.UTF8.GetBytes("stand-in lmxxf weights");
        Directory.CreateDirectory(Path.Combine(src, "native-game-tiled-assets"));
        File.WriteAllBytes(Path.Combine(src, "native-game-tiled-assets", "noise.f32"), weight);
        ((Dictionary<string, string>)pins.OptiFiles)["native-game-tiled-assets/noise.f32"] = Engine.Sha(weight);
        Assert.False(Work.Install(rx9000, src, Preset.OptiScaler, pins, mochizuki: false).Failed);
        Assert.Equal("lmxxf", Engine.Trim(Engine.GetIni(File.ReadAllText(Path.Combine(rx9000, Work.OptiScalerIni)), "DlssNr", "NrBackend")));

        var manifest = new PayloadManifest
        {
            Components = new()
            {
                [PayloadManifest.OptiScalerComponent] = new() { Version = "1", Files = [] },
                [PayloadManifest.LmxxfWeightsComponent] = new() { Version = "1", Files = [] },
                [PayloadManifest.LmxxfGfx1200Component] = new() { Version = "1", Files = [] },
            },
        };
        var without = manifest.WithoutLmxxf();
        Assert.True(without.Has(PayloadManifest.OptiScalerComponent));
        Assert.False(without.Has(PayloadManifest.LmxxfWeightsComponent) || without.Has(PayloadManifest.LmxxfGfx1200Component));
    }
}
