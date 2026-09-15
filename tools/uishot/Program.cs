// Renders the windows without opening one, to a PNG, plus a dump of the visual tree with every
// control's position and size.
//
// It is here because the tree is what found the layout bug a screenshot only hinted at: the credits
// panel measured 819 wide inside a page 764 wide, which is what Padding on a ScrollViewer does to
// content that wraps. Run it, look at the PNG, and grep the .tree.txt whenever a width or a height
// is not what it should be.
//
//   dotnet run --project tools\uishot -- <output folder>
//
// The project has to be self-contained like the app it renders, which is why the csproj pins win-x64.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AmdNr.App;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);

AppBuilder.Configure<App>()
    .UseSkia()
    .WithInterFont()
    .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
    .SetupWithoutStarting();

void Dump(Visual v, int depth, TextWriter w)
{
    if (depth > 24) return;
    var name = (v as Control)?.Name;
    var classes = v is StyledElement s && s.Classes.Count > 0 ? "." + string.Join(".", s.Classes) : "";
    if (v.Bounds.Width > 0)
        w.WriteLine($"{new string(' ', depth * 2)}{v.GetType().Name}{(name is null ? "" : "#" + name)}{classes} " +
                    $"x={v.Bounds.X:0} w={v.Bounds.Width:0} h={v.Bounds.Height:0}");
    foreach (var child in v.GetVisualChildren()) Dump(child, depth + 1, w);
}

void Shot(Window window, string name, int width, int height, bool dump = false)
{
    window.Width = width;
    window.Height = height;
    window.Show();
    for (var i = 0; i < 12; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(60); }
    Dispatcher.UIThread.RunJobs();
    using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, name + ".png"));
    if (dump)
    {
        using var w = new StreamWriter(Path.Combine(outDir, name + ".tree.txt"));
        Dump(window, 0, w);
    }
    Console.WriteLine($"{name}: {window.Width}x{window.Height}");
    window.Close();
}

Shot(new SetupWindow(), "setup", 820, 900, dump: true);
Shot(new SetupWindow(), "setup-small", 700, 640);

var main = new MainWindow();
main.Width = 1240;
main.Height = 820;
main.Show();
for (var i = 0; i < 15; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(80); }
foreach (var (name, visible) in new[] { ("GamesPage", false), ("SystemPage", false), ("SettingsPage", true) })
    if (main.FindControl<Control>(name) is { } page) page.IsVisible = visible;
for (var i = 0; i < 10; i++) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(80); }
using (var f = main.CaptureRenderedFrame()) f?.Save(Path.Combine(outDir, "settings.png"));
Console.WriteLine("settings: ok");
main.Close();
