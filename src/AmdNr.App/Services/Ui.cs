// The few things every view needs from the application's resources: a string in the current
// language, a brush, an icon, and how a report line of each level is drawn. In one place, so every
// page says "Verified" the same way and a key that is missing shows as its own name, not as nothing.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using AmdNr.Core;

namespace AmdNr.App;

internal static class Ui
{
    public static string Text(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is string s ? s : key;

    public static string Format(string key, params object?[] args) => string.Format(Text(key), args);

    /// <summary>A count in a sentence: <paramref name="key"/> says it for one, and key + ".Many" for
    /// any other number. "1 note(s)" reads like a form, not like a sentence.</summary>
    public static string Count(string key, int n) => string.Format(Text(n == 1 ? key : key + ".Many"), n);

    /// <summary>A string from the resources when the language has it, the engine's own English words
    /// when it does not.</summary>
    public static string Translated(string key, string fallback) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is string s && s.Length > 0
            ? s
            : fallback;

    public static IBrush Brush(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true && value is IBrush b ? b : Brushes.Gray;

    public static Geometry? Icon(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as Geometry : null;

    public static Geometry? Glyph(Level level) => Icon(level switch
    {
        Level.Ok => "IconOk",
        Level.Warn => "IconWarn",
        Level.Err => "IconErr",
        _ => "IconInfo",
    });

    public static IBrush LevelBrush(Level level) => Brush(level switch
    {
        Level.Ok => "Ok",
        Level.Warn => "Warn",
        Level.Err => "Err",
        _ => "Muted",
    });

    /// <summary>Binds a text to a string resource rather than copying it, so a label changed in code
    /// still follows a language switch.</summary>
    public static void Localize(TextBlock block, string key) =>
        block[!TextBlock.TextProperty] = block.GetResourceObservable(key).ToBinding();

    /// <summary>A size someone can compare against their connection. Whole megabytes truncate every
    /// component but the weights to "0 MB", which reads as "nothing to download".</summary>
    public static string Megabytes(ulong bytes) => bytes switch
    {
        >= 10 * 1_048_576 => $"{bytes / 1_048_576} MB",
        >= 1_048_576 => $"{bytes / 1_048_576.0:0.0} MB",
        _ => $"{Math.Max(1, bytes / 1024)} KB",
    };

    /// <summary>The level classes a result banner, an icon tile or a status pill can wear; exactly one
    /// of them on at a time.</summary>
    public static void SetLevel(StyledElement element, Level? level)
    {
        element.Classes.Set("ok", level == Level.Ok);
        element.Classes.Set("warn", level == Level.Warn);
        element.Classes.Set("err", level == Level.Err);
    }
}
