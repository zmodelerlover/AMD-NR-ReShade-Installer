// A small "which one?" dialog, built in code rather than markup: it is a title, a sentence and a
// list of buttons, and a second .axaml file for that costs more to read than it saves.

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace AmdNr.App;

public static class ChoiceDialog
{
    /// <summary>The window and a way to read what was picked. Separate from <see cref="ShowAsync"/>
    /// so the headless render harness can build and capture it without a modal loop -- a dialog
    /// nobody can look at is how the centring in here was wrong in the first place.</summary>
    /// <param name="items">Names to show between the sentence and the choices -- the files a question
    /// is about, so nobody has to open Details to know what they are deciding.</param>
    /// <param name="primary">The choice the dialog recommends, drawn in the accent colour.</param>
    public static (Window Window, Func<string?> Chosen) Build(
        Window owner, string title, string body,
        IReadOnlyList<(string Id, string Title, string Detail)> options,
        IReadOnlyList<string>? items = null, string? primary = null)
    {
        string? chosen = null;

        var list = new StackPanel { Spacing = 8 };
        foreach (var (id, name, detail) in options)
        {
            var main = id == primary;
            // Centred, both halves: the options read as a row of choices, and a left-aligned title
            // over a long wrapped sentence made each card look ragged.
            var text = new StackPanel { Spacing = 4, HorizontalAlignment = HorizontalAlignment.Stretch };
            text.Children.Add(new TextBlock
            {
                Text = name,
                FontWeight = FontWeight.SemiBold,
                FontSize = 14,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
            });
            text.Children.Add(new TextBlock
            {
                Text = detail,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontSize = 12,
                LineHeight = 17,
                Foreground = main ? new SolidColorBrush(Color.FromArgb(0xD9, 0xFF, 0xFF, 0xFF)) : Brush(owner, "Muted"),
            });

            var button = new Button
            {
                Content = text,
                Tag = id,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Padding = new Thickness(16, 14),
            };
            button.Classes.Add("pick");
            button.Classes.Set("main", main);
            list.Children.Add(button);
        }

        var panel = new StackPanel { Spacing = 16, Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 20,
            FontWeight = FontWeight.SemiBold,
            TextAlignment = TextAlignment.Center,
        });
        panel.Children.Add(new TextBlock
        {
            Text = body,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Foreground = Brush(owner, "Muted"),
            LineHeight = 19,
        });
        if (items is { Count: > 0 })
        {
            var names = new StackPanel { Spacing = 4 };
            foreach (var item in items)
                names.Children.Add(new TextBlock { Text = item, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new Border
            {
                Child = names,
                Background = Brush(owner, "Surface"),
                BorderBrush = Brush(owner, "Border"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(16, 10),
                HorizontalAlignment = HorizontalAlignment.Center,
                MinWidth = 200,
            });
        }
        panel.Children.Add(list);

        var window = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brush(owner, "Bg"),
            ShowInTaskbar = false,
            Content = new ScrollViewer { Content = panel },
        };

        foreach (var child in list.Children)
        {
            if (child is not Button button) continue;
            button.Click += (_, _) =>
            {
                chosen = button.Tag as string;
                window.Close();
            };
        }

        return (window, () => chosen);
    }

    /// <summary>Shows the choices and returns the id of the one picked, or null if the window was
    /// dismissed.</summary>
    public static async Task<string?> ShowAsync(
        Window owner, string title, string body,
        IReadOnlyList<(string Id, string Title, string Detail)> options,
        IReadOnlyList<string>? items = null, string? primary = null)
    {
        var (window, chosen) = Build(owner, title, body, options, items, primary);
        await window.ShowDialog(owner);
        return chosen();
    }

    private static IBrush Brush(Window owner, string key) =>
        owner.TryFindResource(key, out var value) && value is IBrush brush ? brush : Brushes.Gray;
}
