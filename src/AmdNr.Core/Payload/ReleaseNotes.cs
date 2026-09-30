// What a release says about itself: the notes published with a GitHub release, for "What's new".
//
// One API call per release, the first time it is asked for, and then the copy on disk: a release's
// notes are written once, and the anonymous 60 calls an hour are better spent on the version lists.

using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AmdNr.Core;

public static partial class ReleaseNotes
{
    private static string Folder => Path.Combine(AppPaths.Cache, "notes");

    /// <summary>The notes of one tagged release as published, markdown and all; null when GitHub did not
    /// answer and nothing was kept from before. An empty string is a release published without notes.</summary>
    public static async Task<string?> GetAsync(HttpClient http, string owner, string repo, string tag,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(owner) || string.IsNullOrWhiteSpace(repo) || string.IsNullOrWhiteSpace(tag))
            return null;
        var cached = Path.Combine(Folder, Safe($"{owner}-{repo}-{tag}") + ".md");
        try
        {
            if (File.Exists(cached)) return await File.ReadAllTextAsync(cached, cancel);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Asked again below.
        }

        try
        {
            var url = $"https://api.github.com/repos/{owner}/{repo}/releases/tags/{Uri.EscapeDataString(tag)}";
            using var response = await http.GetAsync(url, cancel);
            if (!response.IsSuccessStatusCode) return null;
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            var body = doc.RootElement.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String
                ? b.GetString() ?? ""
                : "";
            try
            {
                Directory.CreateDirectory(Folder);
                await File.WriteAllTextAsync(cached, body, cancel);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Shown this time; asked for again next time.
            }
            return body;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            return null;
        }
    }

    /// <summary>The repository and tag of a GitHub release asset address, or null for any other address.</summary>
    public static (string Owner, string Repo, string Tag)? FromAssetUrl(string? url)
    {
        if (url is null || AssetUrl().Match(url) is not { Success: true } m) return null;
        return (m.Groups[1].Value, m.Groups[2].Value, Uri.UnescapeDataString(m.Groups[3].Value));
    }

    public enum BlockKind { Heading, Bullet, Text }

    /// <param name="Depth">How far a bullet is nested, from 0.</param>
    /// <param name="Text">The line with its heading or bullet mark taken off and links reduced to their
    /// words. **Bold** and `code` marks stay, for whoever draws it.</param>
    public sealed record Block(BlockKind Kind, int Depth, string Text);

    /// <summary>The notes as the few shapes they are written in: headings, bullets and paragraphs. A blank
    /// line ends a paragraph; a line that follows another joins it, as markdown reads it.</summary>
    public static IReadOnlyList<Block> Blocks(string markdown)
    {
        var blocks = new List<Block>();
        var joining = false;
        foreach (var raw in markdown.Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.TrimEnd();
            var t = line.TrimStart();
            if (t.Length == 0 || t is "---" or "***")
            {
                joining = false;
                continue;
            }
            t = Link().Replace(t, "$1");
            var indent = line.Length - line.TrimStart().Length;
            if (t.StartsWith('#'))
            {
                blocks.Add(new Block(BlockKind.Heading, 0, t.TrimStart('#').Trim()));
                joining = false;
            }
            else if (t.StartsWith("- ") || t.StartsWith("* "))
            {
                blocks.Add(new Block(BlockKind.Bullet, indent / 2, t[2..].Trim()));
                joining = true;
            }
            else if (joining && blocks.Count > 0)
            {
                blocks[^1] = blocks[^1] with { Text = blocks[^1].Text + " " + t };
            }
            else
            {
                blocks.Add(new Block(BlockKind.Text, 0, t));
                joining = true;
            }
        }
        return blocks;
    }

    private static string Safe(string name) =>
        string.Concat(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    [GeneratedRegex(@"^https://github\.com/([^/]+)/([^/]+)/releases/download/([^/]+)/")]
    private static partial Regex AssetUrl();

    [GeneratedRegex(@"\[([^\]]+)\]\([^)]+\)")]
    private static partial Regex Link();
}
