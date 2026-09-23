// games.json: the folders in the list and what the person chose for each of them.

using System.Text.Json;
using System.Text.Json.Serialization;
using AmdNr.Core;

namespace AmdNr.App;

public sealed class GameEntry
{
    public required string Path { get; set; }
    public string? Name { get; set; }
    public Preset Preset { get; set; } = Preset.Dx11;

    /// <summary>Which library it came from, for the badge on the tile.</summary>
    public GamePlatform Platform { get; set; } = GamePlatform.Manual;

    /// <summary>Steam's app id, when it has one. It is what the cover art is keyed by.</summary>
    public string? AppId { get; set; }

    /// <summary>True once the person picked a route by hand. Until then the route follows what the
    /// detection says, including when a newer API database changes its mind.</summary>
    public bool PresetChosen { get; set; }

    /// <summary>The executable to read the width and the API off, when the person pointed at one.
    /// Detection picks the game's binary out of the folder, and a folder that keeps a launcher in
    /// the root and the game in Bin64 is picked wrong -- which made BeamNG.drive a 32-bit game.
    /// Null means "whatever detection finds", which is the ordinary case and stays the default.</summary>
    public string? Executable { get; set; }

    /// <summary>The add-on version this game was last installed with, or last set to by hand. It
    /// lives per game rather than per app because that is the scope it means anything in: pinning
    /// one game to an older build is a thing people do, and the rest of the library should not
    /// follow it. Unset until an install lands or the menu is touched, and a version that is no
    /// longer published quietly falls back to the newest offered.</summary>
    public string? AddonVersion { get; set; }

    [JsonIgnore]
    public string Display => Name ?? System.IO.Path.GetFileName(Path.TrimEnd('\\', '/')) ?? Path;

    public static GameEntry From(ScannedGame game) => new()
    {
        Path = game.InstallPath,
        Name = game.Name,
        Platform = game.Platform,
        AppId = game.AppId,
        Preset = GameScanner.GuessPreset(game.InstallPath),
    };
}

/// <summary>games.json. Written whole every time -- it is a list of folders, not a database.</summary>
public static class GameStore
{
    public static List<GameEntry> Load()
    {
        try
        {
            return File.Exists(AppPaths.GamesFile)
                ? JsonSerializer.Deserialize(File.ReadAllText(AppPaths.GamesFile), AppJson.Default.ListGameEntry) ?? []
                : [];
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Written to a temporary file and moved over the old one, so a crash or a full disk in
    /// the middle leaves the previous list rather than half of one -- which reads back as no games
    /// at all.</summary>
    public static void Save(IEnumerable<GameEntry> games)
    {
        var temp = AppPaths.GamesFile + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(games.ToList(), AppJson.Default.ListGameEntry));
            File.Move(temp, AppPaths.GamesFile, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Losing the list is an annoyance, not a reason to take the window down mid-install.
        }
    }
}
