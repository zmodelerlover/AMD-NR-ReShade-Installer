using AmdNr.Core;

namespace AmdNr.Core.Tests;

public class OptiScalerWikiTests
{
    private static string Page(string filename) =>
        $"[cols=\"1,1\"]\n|===\n|**Last Tested Version**\n|v0.9\n\n|**Filename**\n|{filename}\n\n|**OS**\n|Windows 11\n|===\n";

    private const string List =
        "| Game | Status | Upscalers | FG | Notes | Screenshots |\n"
        + "|---|---|---|---|---|---|\n"
        + "| [Forspoken](Forspoken) | ✅ | DLSS |  | `OptiScaler.dll`  must be renamed to `d3d12.dll`  to detect DLSS input | [1](x) |\n"
        + "| A Quiet Place: The Road Ahead | ✅ | DLSS |  | Use OptiScaler as `winmm.dll`. | [1](x) |\n"
        + "| Aphelion | ✅ | DLSS |  | Install as `winmm.dll` for Xbox version. | [1](x) |\n"
        + "| Redfall | ✅ | DLSS |  | Might require using OptiScaler as `winmm.dll` | |\n"
        + "| [Infinity Nikki](https://github.com/optiscaler/OptiScaler/wiki/Infinity-Nikki) | ✅ | DLSS |  | Install as `version.dll`. Make sure to use signed build. | |\n";

    [Fact]
    public void APagesFilenameRowWinsAndConditionalNamesAreLeftOut()
    {
        var pages = new Dictionary<string, string>
        {
            ["Forspoken.asciidoc"] = Page("`d3d12.dll`"),
            ["Avowed.asciidoc"] = Page("`dxgi.dll` (Steam/Battle.net), `version.dll`/`winmm.dll` (Xbox)"),
            ["NINJA-GAIDEN-2-Black.asciidoc"] = Page("`winmm.dll` (probably best for UWP/GP), `dxgi.dll` should also work"),
            ["Marvels-Midnight-Suns.asciidoc"] = Page(" `d3d12.dll`, `dbghelp.dll` _(check Notes)_"),
            ["Dying-Light-2.asciidoc"] = Page("`dxgi.dll`, try `version.dll` if not working"),
            ["Clair-Obscur-Expedition-33.asciidoc"] = Page("`dxgi.dll`, `OptiScaler.asi`"),
            ["Monster-Hunter-Wilds.asciidoc"] = Page("_Check below_"),
        };
        var names = OptiScalerWiki.Read(pages, List);

        Assert.Equal(["d3d12.dll"], names[PcgwParser.NormaliseTitle("Forspoken")]);
        Assert.Equal(["dxgi.dll"], names[PcgwParser.NormaliseTitle("Avowed")]);
        Assert.Equal(["dxgi.dll"], names[PcgwParser.NormaliseTitle("NINJA GAIDEN 2 Black")]);
        Assert.Equal(["d3d12.dll"], names[PcgwParser.NormaliseTitle("Marvel's Midnight Suns")]);
        Assert.Equal(["dxgi.dll"], names[PcgwParser.NormaliseTitle("Dying Light 2")]);
        Assert.Equal(["dxgi.dll"], names[PcgwParser.NormaliseTitle("Clair Obscur: Expedition 33")]);
        Assert.False(names.ContainsKey(PcgwParser.NormaliseTitle("Monster Hunter Wilds")));

        // The list's notes, for games with no page, when nothing ties the name to a condition.
        Assert.Equal(["winmm.dll"], names[PcgwParser.NormaliseTitle("A Quiet Place: The Road Ahead")]);
        Assert.Equal(["version.dll"], names[PcgwParser.NormaliseTitle("Infinity Nikki")]);
        Assert.False(names.ContainsKey(PcgwParser.NormaliseTitle("Aphelion")));
        Assert.False(names.ContainsKey(PcgwParser.NormaliseTitle("Redfall")));
    }

    [Fact]
    public void TheDatabaseFindsTheNamesByTheGamesTitleOrItsSteamRecord()
    {
        var db = ApiDatabase.Parse(
            "{\"schema\":1,\"games\":{\"steam:1680880\":{\"t\":\"Forspoken\",\"api\":[\"D3D12\"]}},"
            + "\"optiscaler\":{\"forspoken\":[\"d3d12.dll\"]}}");
        Assert.Equal(["d3d12.dll"], db.OptiScalerNames("1680880", "FORSPOKEN Digital Deluxe"));
        Assert.Equal(["d3d12.dll"], db.OptiScalerNames(null, "Forspoken"));
        Assert.Empty(db.OptiScalerNames(null, "Cyberpunk 2077"));
        // A database written before the key existed still reads.
        Assert.Empty(ApiDatabase.Parse("{\"schema\":1,\"games\":{}}").OptiScalerNames(null, "Forspoken"));
    }
}
