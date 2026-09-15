// Credits and where everything comes from, on the first screen and in Settings both.
//
// The addresses are read from config.json rather than written into the markup: an invite can be
// rotated and a fork repoints its own repositories, and neither should need a new build.

using Avalonia.Controls;
using Avalonia.Interactivity;

namespace AmdNr.App;

public partial class CreditsPanel : UserControl
{
    public CreditsPanel()
    {
        InitializeComponent();

        var config = AppConfig.Load();
        RuntimeLink.Tag = config.RuntimeUrl;
        AddonLink.Tag = $"https://github.com/{config.Addon.Owner}/{config.Addon.Repo}";
        InstallerLink.Tag = $"https://github.com/{config.App.Owner}/{config.App.Repo}";
        DiscordLink.Tag = config.DiscordUrl;
    }

    /// <summary>The whole row is the link, so the logo is what gets clicked.</summary>
    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        // An address out of a file opens the browser, so it is checked rather than trusted: https
        // only, which leaves out file:// and the shell handlers behind every other scheme.
        if (sender is Button { Tag: string url } && url.StartsWith("https://", StringComparison.Ordinal))
            AppUpdate.OpenInBrowser(url);
    }
}
