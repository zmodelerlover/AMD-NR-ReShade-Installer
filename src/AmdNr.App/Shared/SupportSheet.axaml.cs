// Support: why it matters and where it goes, opened from the coffee in the rail.

using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace AmdNr.App;

public partial class SupportSheet : UserControl
{
    public SupportSheet()
    {
        InitializeComponent();
        var config = AppConfig.Load();
        KofiLink.Tag = config.KofiUrl;
        VakinhaLink.Tag = config.VakinhaUrl;
    }

    public bool IsOpen => NotesDrawer.Classes.Contains("open");

    public void Show()
    {
        IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            NotesDrawer.Classes.Set("open", true);
            SupportCloseButton.Focus();
        }, DispatcherPriority.Render);
    }

    public async void Close()
    {
        NotesDrawer.Classes.Set("open", false);
        await Task.Delay(220);
        if (!IsOpen) IsVisible = false;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e) => Close();

    private void OnOpen(object? sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string url } && url.StartsWith("https://", StringComparison.Ordinal))
            AppUpdate.OpenInBrowser(url);
    }
}
