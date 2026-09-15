// Reading what PCGamingWiki says a game supports.
//
// A game's import table says what its executable links against, which is not always what it
// renders with: Frostbite imports d3d12.dll in games that only ever run D3D11, and Unity loads D3D
// dynamically. PCGamingWiki records what each game actually offers.
//
// The app never asks the wiki itself. Asked live from every user's machine, the wiki's Cloudflare
// front starts refusing after a library's worth of lookups -- measured: a scan of sixteen games
// was enough to turn every later request from .NET into a 403 while curl from the same address
// still passed. So the wiki is read once, slowly, by tools/ApiDbBuilder, into api-db.json, and the
// app reads that file from the content repository. This file is the part both sides share: how a
// page's {{API}} template is read, and how a store title is matched to a wiki title.

using System.Text.RegularExpressions;

namespace AmdNr.Core;

public sealed record PcgwApi(string Page, IReadOnlyList<GraphicsApi> Supported, bool? Has32Bit, bool? Has64Bit);

public static partial class PcgwParser
{
    [GeneratedRegex(@"\{\{API[ \t]*\r?\n(?<body>.*?)\r?\n\}\}", RegexOptions.Singleline, 2000)]
    private static partial Regex ApiTemplate();

    // [ \t]* and never \s*: \s matches the newline, so an empty field would swallow the next line as
    // its value -- which is how Metro 2033 once read as supporting Vulkan and OpenGL.
    [GeneratedRegex(@"^[ \t]*\|[ \t]*(?<key>[^=\r\n]+?)[ \t]*=[ \t]*(?<value>[^\r\n]*?)[ \t]*\r?$", RegexOptions.Multiline, 2000)]
    private static partial Regex TemplateField();

    [GeneratedRegex(@"<ref[^>]*/>|<ref[^>]*>.*?</ref>|\{\{[^{}]*\}\}", RegexOptions.Singleline, 2000)]
    private static partial Regex References();

    /// <summary>Reads the {{API}} template. Versions are free text -- "9.0c, 11", "10, 11", "true",
    /// "1.3" -- so this reads the major numbers it recognises and ignores the rest.</summary>
    public static PcgwApi? ParseApiTemplate(string page, string wikitext)
    {
        var match = ApiTemplate().Match(wikitext);
        if (!match.Success) return null;

        var fields = TemplateField().Matches(match.Groups["body"].Value)
            .GroupBy(m => m.Groups["key"].Value.Trim().ToLowerInvariant())
            .ToDictionary(g => g.Key, g => References().Replace(g.First().Groups["value"].Value, "").Trim());

        string Field(string key) => fields.TryGetValue(key, out var v) ? v : "";

        var supported = new List<GraphicsApi>();
        foreach (var version in Field("direct3d versions").Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var api = new string(version.TakeWhile(char.IsDigit).ToArray()) switch
            {
                "8" => GraphicsApi.D3D8,
                "9" => GraphicsApi.D3D9,
                "11" => GraphicsApi.D3D11,
                "12" => GraphicsApi.D3D12,
                _ => GraphicsApi.Unknown, // 7 and 10 have no route here
            };
            if (api != GraphicsApi.Unknown && !supported.Contains(api)) supported.Add(api);
        }
        if (Offered(Field("vulkan versions"))) supported.Add(GraphicsApi.Vulkan);
        if (Offered(Field("opengl versions"))) supported.Add(GraphicsApi.OpenGL);

        return supported.Count == 0
            ? null
            : new PcgwApi(page.Replace('_', ' '), supported, Flag(Field("windows 32-bit exe")), Flag(Field("windows 64-bit exe")));

        static bool Offered(string value) =>
            value.Length > 0 && value.ToLowerInvariant() is not ("false" or "unknown" or "n/a" or "hackable");

        static bool? Flag(string value) => value.ToLowerInvariant() switch
        {
            "true" => true,
            "false" => false,
            _ => null,
        };
    }

    // A closed list of qualifiers, not "any few words before Edition": that version stripped the
    // "Skyrim" out of "Skyrim Special Edition".
    [GeneratedRegex(@"\s*(?:[:\-–]\s*)?(?:the\s+)?(?:(?:special|complete|definitive|enhanced|anniversary|goty|game\s+of\s+the\s+year|deluxe|digital\s+deluxe|ultimate|gold|premium|legendary|royal|director'?s\s+cut)\s+)?(?:edition|remastered|remaster)\s*$",
        RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Edition();

    /// <summary>Lowercase letters and digits only, with a trailing ", The" moved back to the front the
    /// way the wiki files it, a leading "The" dropped, and edition suffixes dropped -- the store sells
    /// "Grand Theft Auto IV: The Complete Edition", the wiki files "Grand Theft Auto IV".</summary>
    public static string NormaliseTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var t = title.Trim();
        if (t.EndsWith(", The", StringComparison.OrdinalIgnoreCase)) t = "The " + t[..^5];
        t = Edition().Replace(t, "");
        if (t.StartsWith("The ", StringComparison.OrdinalIgnoreCase)) t = t[4..];
        return new string(t.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
    }

    /// <summary>Only an exact match after normalising, never "the closest": a search for a game the
    /// wiki lacks returns some other game, and reporting that game's APIs would be worse than
    /// reporting nothing.</summary>
    public static string? BestTitle(string wanted, IEnumerable<string> titles) =>
        titles.FirstOrDefault(t => NormaliseTitle(t) == NormaliseTitle(wanted));
}
