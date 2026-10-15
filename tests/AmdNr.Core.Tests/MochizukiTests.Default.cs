using AmdNr.Core;

namespace AmdNr.Core.Tests;

// Which runtime a fresh OptiScaler.ini runs from OptiScaler 0.5.2 on (MochizukiTests.cs).
public partial class MochizukiTests
{
    /// <summary>From 0.5.2 on mochizuki runs on the finished frame too, and an RX 9000 (where it goes in) gets it as the
    /// runtime with or without an upscaler; another card keeps the package's, danielblnc.</summary>
    [Fact]
    public void From052AFreshIniRunsMochizukiOnAnRx9000AndThePackagesRuntimeElsewhere()
    {
        var (src, staged) = Staged("mz-default");
        var pins = new PayloadPins
        {
            AddonSha = staged.AddonSha, AddonSize = staged.AddonSize, WeightsSha = staged.WeightsSha,
            WeightsSize = staged.WeightsSize, OptiFiles = staged.OptiFiles, OptiRuntimeName = staged.OptiRuntimeName,
            OptiRuntimeSha = staged.OptiRuntimeSha, OptiRuntimeSize = staged.OptiRuntimeSize,
            OptiScalerVersion = "0.5.2-amd-nr", OptiRuntimeVersion = staged.OptiRuntimeVersion,
            MochizukiFiles = staged.MochizukiFiles,
        };

        var rx9000 = Game("mz-default-rx9000");
        var report = Work.Install(rx9000, src, Preset.OptiScaler, pins, mochizuki: true);
        Assert.False(report.Failed, report.ToLog("install"));
        Assert.Equal("mochizuki", Engine.Trim(Engine.GetIni(File.ReadAllText(Path.Combine(rx9000, Work.OptiScalerIni)), "DlssNr", "NrBackend")));
        Assert.True(Fixture.HasAny(report, "runs mochizuki (NrBackend=mochizuki)"), report.ToLog("install"));

        var other = Game("mz-default-other");
        Assert.False(Work.Install(other, src, Preset.OptiScaler, pins).Failed);
        Assert.NotEqual("mochizuki", Engine.Trim(Engine.GetIni(File.ReadAllText(Path.Combine(other, Work.OptiScalerIni)), "DlssNr", "NrBackend")));
    }
}
