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
