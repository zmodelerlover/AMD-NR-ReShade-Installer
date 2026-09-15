using System.Runtime.CompilerServices;

namespace AmdNr.Core.Tests;

internal static class TestHome
{
    /// <summary>Runs before any test touches AppPaths, which reads AMDNR_HOME once and caches it.
    /// Without this the download tests write into the real %AppData% cache -- which they did, once,
    /// and left five folders behind.</summary>
    [ModuleInitializer]
    internal static void Redirect() =>
        Environment.SetEnvironmentVariable("AMDNR_HOME",
            Path.Combine(Path.GetTempPath(), "amdnr-tests", Guid.NewGuid().ToString("N")));
}
