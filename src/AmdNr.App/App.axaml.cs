using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using AmdNr.Core;

namespace AmdNr.App;

public partial class App : Application
{
    /// <summary>Every language file that ships. The first is the fallback, and a key missing from
    /// one of the others falls through to it.</summary>
    public static readonly (string Code, string Name, Func<ResourceDictionary> Strings)[] Languages =
    [
        ("en", "English", () => new Languages.English()),
        ("pt-BR", "Português (Brasil)", () => new Languages.Portuguese()),
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

            // A first run configures itself before the library is any use: which language, whether
            // this machine can run the add-on, whether every payload is here, and which games are
            // installed. The wizard writes what it finds to the same files the main window reads, so
            // it hands nothing over -- the main window just starts with them already filled in.
            if (Settings.Load().SetupDone)
            {
                desktop.MainWindow = new MainWindow();
            }
            else
            {
                var setup = new SetupWindow();
                // Shutdown follows the main window, and there is none yet: without this the process
                // would exit the moment the wizard closes.
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                setup.Closed += (_, _) =>
                {
                    desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
                    // Closed with the X rather than finished: nothing was saved as done, so there is
                    // no half-configured state to open into.
                    if (!setup.Completed)
                    {
                        desktop.Shutdown();
                        return;
                    }
                    var main = new MainWindow();
                    desktop.MainWindow = main;
                    main.Show();
                };
                desktop.MainWindow = setup;
            }
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
        var language = Languages.FirstOrDefault(l => l.Code == code);
        var dictionaries = Current?.Resources.MergedDictionaries;
        if (language.Strings is null || dictionaries is null) return;
        if (dictionaries.Count > 0) dictionaries.RemoveAt(0);
        dictionaries.Insert(0, language.Strings());
        CurrentLanguage = code;
    }
}
