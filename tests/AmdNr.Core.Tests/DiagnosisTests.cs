using AmdNr.Core;

namespace AmdNr.Core.Tests;

/// <summary>The top of report.txt: what the logs beside the game already name as the cause, with the line as evidence.</summary>
public class DiagnosisTests
{
    [Fact]
    public void EachRuleNamesItsCauseWithTheLineItCameFrom()
    {
        var dir = Fixture.Temp("diagnosis");
        Assert.Empty(Diagnosis.Of(dir, null, true));

        File.WriteAllText(Path.Combine(dir, SessionLog.ReShadeLog),
            "INFO | Initializing crosire's ReShade version '6.1.1.1878' (64-bit)\n"
            + "WARN | Skipped loading add-on \"amd-nr.addon64\" because ReShade was built with limited add-on functionality.\n");
        File.WriteAllText(Path.Combine(dir, SessionLog.MochizukiLog), "[mochizuki] insufficient VRAM for 2 passes\n");
        File.WriteAllText(Path.Combine(dir, SessionLog.RuntimeLog),
            "dlssnr_amd v0.6.0 loaded\nCRASH: one\nhipStreamSynchronize failed: error 719\nCRASH: two\nCRASH: three\n");

        var found = Diagnosis.Of(dir, null, true);
        Assert.Contains(found, f => f.Cause.Contains("limited add-on functionality") && f.Evidence.Contains("Skipped loading add-on"));
        Assert.Contains(found, f => f.Cause.Contains("not enough VRAM") && f.Evidence.StartsWith(SessionLog.MochizukiLog));
        Assert.Contains(found, f => f.Cause.Contains("HIP error 719") && f.Evidence.Contains("error 719"));
        Assert.Contains(found, f => f.Cause.Contains("several sessions") && f.Evidence.Contains("(3 lines)"));
        Assert.DoesNotContain(found, f => f.Cause.Contains("Two ReShades"));

        // Shadow of the Tomb Raider: the HIP SDK's amdhip64_7.dll in the game folder found no GPU.
        var hip = Fixture.Temp("diagnosis-hip");
        File.WriteAllText(Path.Combine(hip, "amd_presr.log"),
            "1506500 HIP runtime: amdhip64_7.dll source=default\n1514187 HIP device enumeration failed: code=100 devices=0\n");
        Assert.Contains(Diagnosis.Of(hip, null, true), f => f.Cause.Contains("HIP found no GPU") && f.Evidence.StartsWith("amd_presr.log"));

        // Whole words only: "chip" and "relationship" beside a 719 are not a GPU reset.
        var quiet = Fixture.Temp("diagnosis-quiet");
        File.WriteAllText(Path.Combine(quiet, SessionLog.RuntimeLog), "chip temperature error 719\nrelationship error 719 frames\n");
        Assert.DoesNotContain(Diagnosis.Of(quiet, null, true), f => f.Cause.Contains("HIP error 719"));
        File.WriteAllText(Path.Combine(quiet, SessionLog.RuntimeLog), "HIP: hipErrorLaunchFailure on pass 2\n");
        Assert.Contains(Diagnosis.Of(quiet, null, true), f => f.Cause.Contains("HIP error 719"));
        foreach (var line in new[] { "hip: hipModuleLaunchKernel returned 719", "HIP launch failed, code=719", "device_hung after 3 frames" })
        {
            File.WriteAllText(Path.Combine(quiet, SessionLog.RuntimeLog), line + "\n");
            Assert.Contains(Diagnosis.Of(quiet, null, true), f => f.Cause.Contains("HIP error 719"));
        }
        File.WriteAllText(Path.Combine(quiet, SessionLog.RuntimeLog), "CHIP code 719\n");
        Assert.DoesNotContain(Diagnosis.Of(quiet, null, true), f => f.Cause.Contains("HIP error 719"));
    }
}
