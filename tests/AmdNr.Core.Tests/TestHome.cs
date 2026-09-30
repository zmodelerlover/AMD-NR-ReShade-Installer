using System.Runtime.CompilerServices;

namespace AmdNr.Core.Tests;

internal static class TestHome
{
    /// <summary>Runs before any test touches AppPaths, which reads AMDNR_HOME once and caches it.
    /// Without this the download tests write into the real %AppData% cache -- which they did, once,
    /// and left five folders behind.</summary>
    [ModuleInitializer]
    internal static void Redirect()
    {
        var run = Guid.NewGuid().ToString("N");
        Environment.SetEnvironmentVariable("AMDNR_HOME", Path.Combine(Path.GetTempPath(), "amdnr-tests", run));
        // The same for the Vulkan layers: a key of this run's, never the loader's own.
        Environment.SetEnvironmentVariable("AMDNR_LAYER_KEY", $@"SOFTWARE\AmdNrTests\{run}\ImplicitLayers");
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            using var tests = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\AmdNrTests", writable: true);
            tests?.DeleteSubKeyTree(run, throwOnMissingSubKey: false);
            if (tests?.SubKeyCount == 0) Microsoft.Win32.Registry.CurrentUser.DeleteSubKey(@"SOFTWARE\AmdNrTests", false);
        };
    }
}
