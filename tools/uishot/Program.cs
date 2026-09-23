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
// Point AMDNR_HOME at an empty folder first: the library page is filled with a made-up list of
// games written there, so it never reads -- or writes -- the real one.
//
// The project has to be self-contained like the app it renders, which is why the csproj pins win-x64.
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using AmdNr.App;
using AmdNr.Core;

var outDir = args.Length > 0 ? args[0] : ".";
Directory.CreateDirectory(outDir);
var failures = 0;

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

void Settle(int rounds = 12)
{
    // Transitions run on the render clock, which a headless window only advances when told to:
    // without the ticks a sheet is captured halfway through fading in.
    for (var i = 0; i < rounds; i++)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(4);
        Thread.Sleep(60);
    }
    Dispatcher.UIThread.RunJobs();
}

void Save(Window window, string name, bool dump = false)
{
    using (var frame = window.CaptureRenderedFrame()) frame?.Save(Path.Combine(outDir, name + ".png"));
    if (!dump) return;
    using var w = new StreamWriter(Path.Combine(outDir, name + ".tree.txt"));
    Dump(window, 0, w);
}

void Shot(Window window, string name, int width, int height, bool dump = false)
{
    window.Width = width;
    window.Height = height;
    window.Show();
    Settle();
    Save(window, name, dump);
    Console.WriteLine($"{name}: {window.Width}x{window.Height}");
    window.Close();
}

/// <summary>Scrolls every visible ScrollViewer to its end and checks that the last thing inside it
/// can actually be reached: its bottom edge has to land inside the viewport. This is the check the
/// "scrolled to the bottom and the last row is still cut off" bug fails, whatever caused it.</summary>
void CheckScrollReach(Window window, string name)
{
    foreach (var scroll in window.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible))
    {
        if (scroll.Extent.Height <= scroll.Viewport.Height + 1) continue;
        scroll.ScrollToEnd();
        Settle(6);
        if (scroll.Content is not Visual content) continue;
        var last = content.GetVisualDescendants().OfType<Control>()
            .Where(c => c.IsEffectivelyVisible && c.Bounds.Height > 0 && c.GetVisualChildren().All(k => k is not Control))
            .Select(c => c.TranslatePoint(new Point(0, c.Bounds.Height), scroll))
            .Where(p => p is not null)
            .Select(p => p!.Value.Y)
            .DefaultIfEmpty(0)
            .Max();
        var ok = last <= scroll.Viewport.Height + 1;
        if (!ok) failures++;
        Console.WriteLine($"  {name} scroll {scroll.Name ?? "(unnamed)"}: extent {scroll.Extent.Height:0}, " +
                          $"viewport {scroll.Viewport.Height:0}, last content edge at {last:0} " +
                          (ok ? "ok" : "CUT OFF"));
    }
}

/// <summary>A made-up library in AMDNR_HOME, one folder per game, so the grid has something to lay
/// out at every size. Nothing in those folders is a real game, which is fine: the tiles only need a
/// name and a path.</summary>
void SeedLibrary(int count)
{
    var root = Path.Combine(AppPaths.Root, "fake-games");
    var games = new List<GameEntry>();
    for (var i = 0; i < count; i++)
    {
        var dir = Path.Combine(root, $"Game {i + 1:00}");
        Directory.CreateDirectory(dir);
        // Something that is not ours, or the library takes the folder for a game that was uninstalled.
        File.WriteAllText(Path.Combine(dir, "data.pak"), "game data");
        games.Add(new GameEntry { Path = dir, Name = $"Some Game With A Long Title {i + 1:00}" });
    }
    GameStore.Save(games);
}

Shot(new SetupWindow(), "setup", 820, 900, dump: true);
Shot(new SetupWindow(), "setup-small", 700, 640);

SeedLibrary(61);
foreach (var (w, h) in new[] { (1240, 820), (1920, 1040), (2560, 1400), (980, 620) })
{
    var main = new MainWindow { Width = w, Height = h };
    main.Show();
    Settle(15);
    foreach (var page in new[] { "GamesPage", "SystemPage", "SettingsPage" })
    {
        foreach (var other in new[] { "GamesPage", "SystemPage", "SettingsPage" })
            if (main.FindControl<Control>(other) is { } c) c.IsVisible = other == page;
        Settle(8);
        var name = $"{page.Replace("Page", "").ToLowerInvariant()}-{w}x{h}";
        Save(main, name, dump: w == 1920 && page == "GamesPage");
        CheckScrollReach(main, name);
        Save(main, name + "-end");
    }

    // The sheet a tile opens, over the library, with its own scroller.
    foreach (var other in new[] { "GamesPage", "SystemPage", "SettingsPage" })
        if (main.FindControl<Control>(other) is { } c) c.IsVisible = other == "GamesPage";
    if (main.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("card")) is { } card)
    {
        card.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Settle(20);
        Save(main, $"sheet-{w}x{h}", dump: w == 1240);
        CheckScrollReach(main, $"sheet-{w}x{h}");
        Save(main, $"sheet-{w}x{h}-end");
    }
    Console.WriteLine($"main {w}x{h}: ok");
    main.Close();
}

Console.WriteLine(failures == 0 ? "every scroll reaches its end" : $"{failures} scroll(s) cut off");
return failures == 0 ? 0 : 1;
