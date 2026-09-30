// The OptiScaler wiki (github.com/optiscaler/OptiScaler/wiki) says, game by game, which file name
// OptiScaler has to go in as: Forspoken only sees DLSS inputs with it as d3d12.dll, No Man's Sky
// loads it only as dbghelp.dll. tools/ApiDbBuilder reads a clone of the wiki with this and writes the
// answer into api-db.json, so the app never asks the wiki anything at run time.
//
// Two places carry it. A game's own page has a Filename row ("`dxgi.dll`, `winmm.dll`"), best first;
// the compatibility list has a Notes column ("Install OptiScaler as `d3d12.dll` ..."), for games with
// no page. A name tied to a condition this app cannot see -- the Xbox copy, Linux, a mod loader, "if
// not working" -- is not taken: it would move every other copy of the game off a name that works.

using System.Text.RegularExpressions;

namespace AmdNr.Core;

public static partial class OptiScalerWiki
{
    [GeneratedRegex(@"\b(dxgi|winmm|version|d3d12|dbghelp|wininet|winhttp)\.dll\b", RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Name();

    [GeneratedRegex(@"xbox|uwp|\bgp\b|game ?pass|ms store|microsoft store|linux|proton|steam ?deck|wine|mod loader|notes|below|\bif\b|might|needed for|required for|nvidia|reshade|special ?k",
        RegexOptions.IgnoreCase, 2000)]
    private static partial Regex Condition();

    /// <summary>Where one sentence of a note ends: not at the dot in "dxgi.dll".</summary>
    [GeneratedRegex(@"(?<=[.!])\s+", RegexOptions.None, 2000)]
    private static partial Regex Sentence();

    [GeneratedRegex(@"\[(?<title>[^\]]+)\]\((?<page>[^)]+)\)", RegexOptions.None, 2000)]
    private static partial Regex Link();

    /// <summary>Normalised title -> names, best first, from the wiki's game pages (file name -> text, in
    /// AsciiDoc) and its Compatibility-List.md. A page's Filename row wins over the list's notes.</summary>
    public static Dictionary<string, List<string>> Read(IReadOnlyDictionary<string, string> pages, string compatibilityList)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var titleOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var fromNotes = new List<(string Title, List<string> Names)>();

        foreach (var line in compatibilityList.Split('\n'))
        {
            var cells = line.Split('|');
            if (cells.Length < 7 || !line.TrimStart().StartsWith('|')) continue;
            var game = cells[1].Trim();
            var title = game;
            if (Link().Match(game) is { Success: true } link)
            {
                title = link.Groups["title"].Value.Trim();
                titleOf[PageOf(link.Groups["page"].Value)] = title;
            }
            if (Unconditional(Sentence().Split(cells[5])) is { Count: > 0 } names) fromNotes.Add((title, names));
        }

        foreach (var (file, text) in pages)
        {
            var page = Path.GetFileNameWithoutExtension(file);
            if (FilenameRow(text) is not { } row || Unconditional(row.Split(',', ';')) is not { Count: > 0 } names) continue;
            foreach (var title in new[] { titleOf.GetValueOrDefault(page), Uri.UnescapeDataString(page).Replace('-', ' ') })
                if (PcgwParser.NormaliseTitle(title) is { Length: >= 2 } key)
                    result[key] = names;
        }

        foreach (var (title, names) in fromNotes)
            if (PcgwParser.NormaliseTitle(title) is { Length: >= 2 } key)
                result.TryAdd(key, names);
        return result;
    }

    /// <summary>The names in these pieces of text that no condition is attached to, in order.</summary>
    private static List<string> Unconditional(IEnumerable<string> pieces) =>
        pieces.Where(p => !Condition().IsMatch(p))
            .SelectMany(p => Name().Matches(p).Select(m => m.Value.ToLowerInvariant()))
            .Distinct().ToList();

    /// <summary>The text of a page's Filename row: the lines after its header, up to the next header.</summary>
    private static string? FilenameRow(string text)
    {
        var lines = text.Split('\n');
        var start = Array.FindIndex(lines, l => l.Contains("**Filename**", StringComparison.Ordinal));
        if (start < 0) return null;
        var end = Array.FindIndex(lines, start + 1, l => l.TrimStart().StartsWith("|**", StringComparison.Ordinal));
        return string.Join(' ', lines[(start + 1)..(end < 0 ? lines.Length : end)]);
    }

    /// <summary>The page a link points at: "Forspoken", or the last part of a full wiki address.</summary>
    private static string PageOf(string target) => Uri.UnescapeDataString(target.TrimEnd('/').Split('/')[^1]);
}
