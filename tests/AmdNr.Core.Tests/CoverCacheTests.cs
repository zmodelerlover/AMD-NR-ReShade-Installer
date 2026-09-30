using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class CoverCacheTests
{
    [Fact]
    public void AGameIsLookedUpByItsNameTheNameBeforeADashAndAFolderThatIsNotPlumbing()
    {
        Assert.Equal(["Cyberpunk 2077 - REDmod", "Cyberpunk 2077", "Satisfactory"],
            CoverCache.Titles("Cyberpunk 2077 - REDmod", @"E:\Games\Satisfactory"));
        Assert.Equal(["Crimson Desert"], CoverCache.Titles("bin64", @"E:\Games\Crimson Desert\bin64")[1..]);
        // The same title twice is looked up once.
        Assert.Equal(["Red Dead Redemption"], CoverCache.Titles("Red Dead Redemption", @"E:\Games\Red Dead Redemption"));
    }

    [Fact]
    public void AStorePageRenamedForAReReleaseStillMatchesButNothingLooser()
    {
        Assert.Equal(CoverCache.Bare("Crimson Desert"), CoverCache.Bare("Crimson Desert Enhanced"));
        Assert.NotEqual(CoverCache.Bare("Crimson Desert"), CoverCache.Bare("Crimson Desert Enhanced: Charting the Unknown"));
        Assert.NotEqual(CoverCache.Bare("Red Dead Redemption"), CoverCache.Bare("Red Dead Redemption 2"));
    }

    [Fact]
    public void TheApiDatabaseGivesTheSteamAppIdOfAnExactTitle()
    {
        var db = ApiDatabase.Parse("{\"schema\":1,\"games\":{\"steam:1091500\":{\"t\":\"Cyberpunk 2077\",\"api\":[\"D3D12\"]}}}");
        Assert.Equal("1091500", db.SteamAppIdFor("CYBERPUNK 2077"));
        Assert.Null(db.SteamAppIdFor("Cyberpunk"));
    }
}
