using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Markup.Xaml.Styling;
using AmdNr.Core;

namespace AmdNr.App;

public partial class App : Application
{
    /// <summary>Every language file that ships. The first is the fallback, and a key missing from
    /// one of the others falls through to it.</summary>
    public static readonly (string Code, string Name)[] Languages =
    [
        ("en", "English"),
        ("pt-BR", "Português (Brasil)"),
    ];

    public static string CurrentLanguage { get; private set; } = "en";

    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            try { File.WriteAllText(AppPaths.CrashLog, args.ExceptionObject.ToString()); }
            catch
            {
                // Last resort: there is nothing left to try if writing the crash log fails.
            }
        };
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            ChangeLanguage(PreferredLanguage());
            desktop.MainWindow = new MainWindow();
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>The system language when it is one we have, English otherwise. A saved choice wins
    /// over both.</summary>
    private static string PreferredLanguage()
    {
        var saved = Settings.Load().Language;
        if (!string.IsNullOrWhiteSpace(saved) && Languages.Any(l => l.Code == saved)) return saved!;

        var culture = CultureInfo.CurrentUICulture;
        var exact = Languages.FirstOrDefault(l => l.Code.Equals(culture.Name, StringComparison.OrdinalIgnoreCase));
        if (exact.Code is not null) return exact.Code;

        var language = culture.TwoLetterISOLanguageName;
        var loose = Languages.FirstOrDefault(l => l.Code.StartsWith(language, StringComparison.OrdinalIgnoreCase));
        return loose.Code ?? "en";
    }

    /// <summary>Swaps the string dictionary in place. Index 0 of the merged dictionaries is the
    /// language and nothing else ever goes in front of it; every label binds with DynamicResource,
    /// so the window relabels itself without being rebuilt.</summary>
    public static void ChangeLanguage(string code)
    {
        try
        {
            var include = new ResourceInclude(new Uri("avares://AMD-NR-ReShade-Installer/App.axaml"))
            {
                Source = new Uri($"avares://AMD-NR-ReShade-Installer/Languages/Strings.{code}.axaml"),
            };

            var dictionaries = Current?.Resources.MergedDictionaries;
            if (dictionaries is null) return;
            if (dictionaries.Count > 0) dictionaries.RemoveAt(0);
            dictionaries.Insert(0, include);
            CurrentLanguage = code;
        }
        catch (Exception e) when (e is UriFormatException or FileNotFoundException or KeyNotFoundException)
        {
            // A missing language file leaves the previous one in place rather than an empty window.
        }
    }
}
