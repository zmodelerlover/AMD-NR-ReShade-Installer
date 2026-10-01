using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class AppVersionTests
{
    /// <summary>A hotfix is a fourth number the updater already compares: 0.7.5.1 is offered over 0.7.5,
    /// and is said as v0.7.5's hotfix rather than as a version of its own.</summary>
    [Fact]
    public void AHotfixIsTheFourthNumberAndIsOfferedOverItsRelease()
    {
        Assert.Equal("0.7.5", AppVersion.Of(new Version(0, 7, 5)));
        Assert.Equal("0.7.5", AppVersion.Of(new Version(0, 7, 5, 0)));
        Assert.Equal("0.7.5.1", AppVersion.Of(new Version(0, 7, 5, 1)));

        Assert.Equal(0, AppVersion.Hotfix("0.7.5"));
        Assert.Equal(2, AppVersion.Hotfix("0.7.5.2"));
        Assert.Equal("0.7.5", AppVersion.Release("0.7.5.2"));

        Assert.True(AppVersion.IsHotfixOf("0.7.5.1", "0.7.5"));
        Assert.True(AppVersion.IsHotfixOf("0.7.5.2", "0.7.5.1"));
        Assert.False(AppVersion.IsHotfixOf("0.7.6", "0.7.5.1"));
        Assert.False(AppVersion.IsHotfixOf("0.7.6.1", "0.7.5"));

        // What AppUpdate.CheckAsync compares: a hotfix is newer than its release and older than the next one.
        Assert.True(Version.Parse("0.7.5.1") > Version.Parse("0.7.5"));
        Assert.True(Version.Parse("0.7.6") > Version.Parse("0.7.5.1"));
    }
}
