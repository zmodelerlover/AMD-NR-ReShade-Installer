// What the person chose last time, and the JSON the app keeps of its own.

using System.Text.Json;
using System.Text.Json.Serialization;
using AmdNr.Core;

namespace AmdNr.App;

/// <summary>What the person chose last time. Small enough that it is one file and no schema.</summary>
public sealed class Settings
{
    public string? Language { get; set; }

    /// <summary>True once someone reached the end of the first-run wizard. Closing that window with
    /// the X leaves it false, so the wizard asks again rather than silently never running.</summary>
    public bool SetupDone { get; set; }

    /// <summary>True when the wizard's games step was left without a scan being run. The main
    /// window used to scan on its own whenever the list was empty, which is exactly the state
    /// skipping that step leaves behind -- so the one person who said no was the one person it ran
    /// for. Scan games is still there; it just has to be asked for.</summary>
    public bool ScanDeclined { get; set; }

    private static string Path => System.IO.Path.Combine(AppPaths.Root, "settings.json");

    public static Settings Load()
    {
        try
        {
            return File.Exists(Path)
                ? JsonSerializer.Deserialize(File.ReadAllText(Path), AppJson.Default.Settings) ?? new Settings()
                : new Settings();
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return new Settings();
        }
    }

    public void Save()
    {
        try { File.WriteAllText(Path, JsonSerializer.Serialize(this, AppJson.Default.Settings)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A preference that did not persist is not worth interrupting anyone over.
        }
    }
}

/// <summary>The app's own files, described at compile time: a trimmed executable cannot reflect
/// over them, and it is less than half the size of one that could.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNameCaseInsensitive = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppConfig))]
[JsonSerializable(typeof(Settings))]
[JsonSerializable(typeof(List<GameEntry>))]
internal sealed partial class AppJson : JsonSerializerContext;
