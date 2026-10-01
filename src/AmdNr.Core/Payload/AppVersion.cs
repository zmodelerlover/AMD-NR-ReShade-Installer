// A hotfix is a fourth number on the app's version: 0.7.5.1 is v0.7.5's first hotfix. The updater
// compares all four, so a hotfix is offered like any release and nobody has to name a new version for
// a fix; only the words change -- "hotfix 1 for v0.7.5" instead of "version 0.7.5.1".

namespace AmdNr.Core;

public static class AppVersion
{
    /// <summary>The version as the app states it: three numbers, and the fourth only when it is a hotfix.</summary>
    public static string Of(Version v) => v.Revision > 0 ? v.ToString(4) : v.ToString(3);

    /// <summary>The hotfix number, or 0 for a release.</summary>
    public static int Hotfix(string version) =>
        Version.TryParse(version, out var v) && v.Revision > 0 ? v.Revision : 0;

    /// <summary>The release a hotfix belongs to: 0.7.5.1 is 0.7.5.</summary>
    public static string Release(string version) =>
        Version.TryParse(version, out var v) ? v.ToString(3) : version;

    /// <summary>Whether <paramref name="latest"/> is a hotfix of the release <paramref name="current"/> is on,
    /// rather than a release of its own.</summary>
    public static bool IsHotfixOf(string latest, string current) =>
        Hotfix(latest) > 0 && Release(latest) == Release(current);
}
