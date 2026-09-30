// "What's new", drawn by the app: the notes as headings, bullets and paragraphs, with their bold and
// their code, over the page the way a game's sheet is.

using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using AmdNr.Core;

namespace AmdNr.App;

public partial class NotesSheet : UserControl
{
    private string? _url;
    private int _shown;

    public NotesSheet() => InitializeComponent();

    public bool IsOpen => NotesDrawer.Classes.Contains("open");

    /// <summary>Opens at once, saying the notes are on their way, and fills in when
    /// <paramref name="notes"/> finishes: null from it is notes that could not be read.</summary>
    public async void Show(string title, Task<string?> notes, string? url)
    {
        var shown = ++_shown;
        _url = url;
        NotesTitle.Text = title;
        NotesGitHubButton.IsVisible = url is not null;
        NotesBody.Children.Clear();
        NotesLoading.IsVisible = true;
        IsVisible = true;
        Dispatcher.UIThread.Post(() =>
        {
            NotesDrawer.Classes.Set("open", true);
            NotesCloseButton.Focus();
        }, DispatcherPriority.Render);

        string? body;
        try { body = await notes; }
        catch (Exception) { body = null; }
        if (shown != _shown) return;
        NotesLoading.IsVisible = false;
        if (body is null || body.Trim().Length == 0)
        {
            NotesBody.Children.Add(Paragraph(Ui.Text(body is null ? "Str.WhatsNewFailed" : "Str.WhatsNewNone"), muted: true));
            return;
        }
        foreach (var block in ReleaseNotes.Blocks(body)) NotesBody.Children.Add(Draw(block));
    }

    public async void Close()
    {
        NotesDrawer.Classes.Set("open", false);
        await Task.Delay(220);
        if (!IsOpen) IsVisible = false;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e) => Close();

    private void OnGitHub(object? sender, RoutedEventArgs e)
    {
        if (_url is not null) AppUpdate.OpenInBrowser(_url);
    }

    private static Control Draw(ReleaseNotes.Block block)
    {
        switch (block.Kind)
        {
            case ReleaseNotes.BlockKind.Heading:
                var heading = Paragraph(block.Text, muted: false);
                heading.FontSize = 16;
                heading.FontWeight = FontWeight.SemiBold;
                heading.Margin = new Avalonia.Thickness(0, 6, 0, 0);
                return heading;
            case ReleaseNotes.BlockKind.Bullet:
                var row = new Grid
                {
                    ColumnDefinitions = new ColumnDefinitions("18,*"),
                    Margin = new Avalonia.Thickness(block.Depth * 18, 0, 0, 0),
                };
                row.Children.Add(new Ellipse
                {
                    Width = 5,
                    Height = 5,
                    Fill = Ui.Brush("AccentText"),
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Avalonia.Thickness(3, 8, 0, 0),
                });
                var text = Paragraph(block.Text, muted: false);
                Grid.SetColumn(text, 1);
                row.Children.Add(text);
                return row;
            default:
                return Paragraph(block.Text, muted: false);
        }
    }

    /// <summary>A line of the notes, its **bold** and `code` drawn as such.</summary>
    private static TextBlock Paragraph(string text, bool muted)
    {
        var block = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 21,
            Foreground = Ui.Brush(muted ? "Muted" : "TextSoft"),
            Inlines = [],
        };
        var bold = false;
        foreach (var part in text.Split("**"))
        {
            var code = false;
            foreach (var piece in part.Split('`'))
            {
                if (piece.Length > 0)
                {
                    var run = new Run(piece);
                    if (bold)
                    {
                        run.FontWeight = FontWeight.SemiBold;
                        run.Foreground = Ui.Brush("Text");
                    }
                    if (code)
                    {
                        run.FontFamily = new FontFamily("Cascadia Mono, Consolas, monospace");
                        run.FontSize = 12.5;
                        run.Foreground = Ui.Brush("AccentText");
                    }
                    block.Inlines!.Add(run);
                }
                code = !code;
            }
            bold = !bold;
        }
        return block;
    }
}
