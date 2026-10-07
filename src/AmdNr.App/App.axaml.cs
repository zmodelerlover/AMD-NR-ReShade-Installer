using System.Globalization;
using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
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
        ("es", "Español", () => new Languages.Spanish()),
        ("fr", "Français", () => new Languages.French()),
        ("de", "Deutsch", () => new Languages.German()),
        ("it", "Italiano", () => new Languages.Italian()),
        ("pl", "Polski", () => new Languages.Polish()),
        ("ro", "Română", () => new Languages.Romanian()),
        ("hu", "Magyar", () => new Languages.Hungarian()),
        ("hr", "Hrvatski", () => new Languages.Croatian()),
        ("lt", "Lietuvių", () => new Languages.Lithuanian()),
        ("ru", "Русский", () => new Languages.Russian()),
        ("uk", "Українська", () => new Languages.Ukrainian()),
        ("tr", "Türkçe", () => new Languages.Turkish()),
        ("hi", "हिन्दी", () => new Languages.Hindi()),
        ("zh-CN", "简体中文", () => new Languages.ChineseSimplified()),
        ("ja", "日本語", () => new Languages.Japanese()),
        ("ko", "한국어", () => new Languages.Korean()),
        ("th", "ไทย", () => new Languages.Thai()),
        ("ar", "العربية", () => new Languages.Arabic()),
        ("x-pirate", "Pirate English", () => new Languages.Pirate()),
    ];

    public static string CurrentLanguage { get; private set; } = "en";

    /// <summary>With the fourth number when this build is a hotfix (see <see cref="AppVersion"/>): cut to
    /// three, a hotfix would call itself the release it fixes and never be offered.</summary>
    public static string Version { get; } =
        AmdNr.Core.AppVersion.Of(Assembly.GetExecutingAssembly().GetName().Version ?? new System.Version(0, 0, 0));

    /// <summary>"v0.7.5", or "v0.7.5 hotfix 1".</summary>
    public static string Label(string version) =>
        AmdNr.Core.AppVersion.Hotfix(version) is > 0 and var n
            ? Ui.Format("Str.HotfixLabel", AmdNr.Core.AppVersion.Release(version), n)
            : $"v{version}";

    /// <summary>What the update banner and the settings page say about a release that is out.</summary>
    public static string UpdateOut(string latest) =>
        AmdNr.Core.AppVersion.IsHotfixOf(latest, Version)
            ? Ui.Format("Str.HotfixOut", AmdNr.Core.AppVersion.Release(latest), AmdNr.Core.AppVersion.Hotfix(latest))
            : Ui.Format("Str.UpdateOut", latest, Version);

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);

        // Windows opened after a language change take its direction too.
        Window.WindowOpenedEvent.AddClassHandler<Window>((window, _) => window.FlowDirection = Flow);

        // Every way an exception can end the app is written down, each one added to the crash log rather than
        // over the last, and named in the rolling log too: a window that closed right after an install had left
        // nothing anywhere to say why (Digimon World: Next Order, 2026-10-07).
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Crashed("unhandled", args.ExceptionObject);
        // One escaping a UI handler is written down and the window kept: what it was doing is lost, the app is not.
        Avalonia.Threading.Dispatcher.UIThread.UnhandledException += (_, args) =>
        {
            Crashed("ui thread", args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Crashed("unobserved task", args.Exception);
            args.SetObserved();
        };
    }

    private static void Crashed(string where, object exception)
    {
        try
        {
            File.AppendAllText(AppPaths.CrashLog, $"{DateTime.Now:s} v{Version} {where}{Environment.NewLine}{exception}"
                                                  + Environment.NewLine + Environment.NewLine);
        }
        catch
        {
            // Last resort: there is nothing left to try if writing the crash log fails.
        }
        InstallLog.Append($"{DateTime.Now:s} crash ({where}): {(exception as Exception)?.GetType().Name}: "
                          + $"{(exception as Exception)?.Message ?? exception.ToString()} -- the whole of it is in crash.log");
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

        // Chinese, Japanese and Korean share characters that each draws its own way, so the font
        // falling back behind Inter has to be the one made for the language on screen; Thai and Hindi are in neither.
        Current!.Resources["UiFont"] = new FontFamily(code switch
        {
            "zh-CN" => "Inter, Microsoft YaHei UI, Segoe UI, sans-serif",
            "ja" => "Inter, Yu Gothic UI, Meiryo UI, Segoe UI, sans-serif",
            "ko" => "Inter, Malgun Gothic, Segoe UI, sans-serif",
            "th" => "Inter, Leelawadee UI, Segoe UI, sans-serif",
            "hi" => "Inter, Nirmala UI, Segoe UI, sans-serif",
            _ => "Inter, Segoe UI, sans-serif",
        });

        if (Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (var window in desktop.Windows) window.FlowDirection = Flow;
    }

    /// <summary>Arabic reads right to left; every other language here left to right.</summary>
    public static FlowDirection Flow => CurrentLanguage == "ar" ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
}
