// What the OptiScaler route writes into a fresh OptiScaler.ini, and which lmxxf files it carries: lmxxf as the NR
// runtime on an RX 9000 card with an upscaler and danielblnc anywhere else, NR without upscaling from 0.5.0 on, and
// the game as the only process to hook where a launcher beside it loads OptiScaler first.

namespace AmdNr.Core;

public static partial class Work
{
    /// <summary>Whether this OptiScaler carries lmxxf with the kernels of both RX 9000 series (9070 and 9060).</summary>
    private static bool LmxxfEverywhere(PayloadPins pins) =>
        pins.OptiFiles.ContainsKey(LmxxfRuntimeName)
        && pins.OptiFiles.Keys.Any(k => k.StartsWith("lmxxf-modules-gfx1200/", StringComparison.Ordinal));

    /// <summary>On an RX 9000 card, the one mochizuki goes in on, a fresh OptiScaler.ini runs lmxxf; anywhere else a
    /// package's ini that names lmxxf runs danielblnc instead: 0.4.9's did, and an RX 7900 XT got a runtime it cannot run.</summary>
    private static byte[] RunsLmxxf(byte[] ini, bool wanted)
    {
        var text = System.Text.Encoding.UTF8.GetString(ini);
        if (wanted) return System.Text.Encoding.UTF8.GetBytes(Engine.SetIni(text, "DlssNr", "NrBackend", "lmxxf"));
        return Engine.Trim(Engine.GetIni(text, "DlssNr", "NrBackend")) == "lmxxf"
            ? System.Text.Encoding.UTF8.GetBytes(Engine.SetIni(text, "DlssNr", "NrBackend", "daniel"))
            : ini;
    }

    /// <summary>Whether lmxxf's weights are in this install: they are left out on a card lmxxf does not run on
    /// (<see cref="PayloadManifest.WithoutLmxxf"/>).</summary>
    private static bool LmxxfWeighted(PayloadPins pins) =>
        pins.OptiFiles.Keys.Any(k => k.StartsWith(LmxxfWeightsFolder, StringComparison.Ordinal));

    private const string LmxxfWeightsFolder = "native-game-tiled-assets/";

    /// <summary>From OptiScaler 0.5.0 on, a fresh OptiScaler.ini runs the network on the finished frame of a
    /// game with no upscaler running. It stands aside by itself while the game's upscaler runs.</summary>
    private static byte[] WithoutUpscaler(byte[] ini, bool everywhere) =>
        everywhere
            ? System.Text.Encoding.UTF8.GetBytes(
                Engine.SetIni(System.Text.Encoding.UTF8.GetString(ini), "DlssNr", "PresentWithoutUpscaler", "true"))
            : ini;

    private static byte[] OnlyInTheGame(string dir, byte[] ini, Report report)
    {
        if (LauncherBeside(dir) is not { } l) return ini;
        report.Info($"{OptiScalerIni} names {l.Game} as the only process to hook: {l.Launcher} loads OptiScaler first.");
        var text = System.Text.Encoding.UTF8.GetString(ini);
        return System.Text.Encoding.UTF8.GetBytes(Engine.SetIni(text, "ProcessFilter", "TargetProcessName", l.Game));
    }
}
