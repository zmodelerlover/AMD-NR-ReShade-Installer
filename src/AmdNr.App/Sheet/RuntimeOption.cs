// One line of the danielblnc version box: the version, and beside it on an RX 9000 card a tag that says
// whether that build is the recommended one or an unstable one (Work.BadgeFor).

using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using AmdNr.Core;

namespace AmdNr.App;

public sealed record RuntimeOption(string Version, Work.RuntimeBadge Badge)
{
    public static readonly IDataTemplate Template = new FuncDataTemplate<RuntimeOption>((option, _) =>
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock { Text = option?.Version, VerticalAlignment = VerticalAlignment.Center });
        if (option is { Badge: not Work.RuntimeBadge.None } o)
        {
            var good = o.Badge == Work.RuntimeBadge.Recommended;
            var tag = new Border
            {
                Child = new TextBlock { Text = Ui.Text(good ? "Str.RuntimeRecommended" : "Str.RuntimeUnstable") },
                VerticalAlignment = VerticalAlignment.Center,
            };
            tag.Classes.AddRange(["tag", good ? "good" : "warn"]);
            row.Children.Add(tag);
        }
        return row;
    });
}
