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

// -- Flows -------------------------------------------------------------------------------------------
// `uishot <out> flows`, against an AMDNR_HOME of its own: the sheet driven the way a person drives it
// -- install, the badge on the tile, trading one route for the other, uninstall, Update once the
// payload moves on -- and the update card in each of its states. The payloads are stand-ins already
// in the cache under a payload list that pins them, and every address points at a port nothing
// listens on, so nothing is downloaded and nothing waits on the network.

void Check(bool ok, string what)
{
    Console.WriteLine($"  {(ok ? "ok  " : "FAIL")} {what}");
    if (!ok) failures++;
}

bool Until(Func<bool> done, int seconds = 30)
{
    var end = DateTime.Now.AddSeconds(seconds);
    while (!done() && DateTime.Now < end) Settle(2);
    return done();
}

void Click(Button button) => button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));

// The smallest file that reads as a 64-bit executable: MZ, the PE signature, AMD64, PE32+.
byte[] Pe64()
{
    var b = new byte[512];
    b[0] = 0x4d; b[1] = 0x5a; b[60] = 128; b[128] = 0x50; b[129] = 0x45;
    b[132] = 0x64; b[133] = 0x86;
    b[152] = 0x0b; b[153] = 0x02;
    return b;
}

// A payload list whose every file is already in the cache. No ReShade component, whose hash is pinned
// in the engine and cannot be stood in for; the add-on route installs without it.
void SeedPayload(string addonVersion)
{
    var components = new System.Text.Json.Nodes.JsonObject();
    void Component(string name, string version, params string[] paths)
    {
        var files = new System.Text.Json.Nodes.JsonArray();
        foreach (var path in paths)
        {
            // The add-on's bytes carry its version, so a second list pins a different build.
            byte[] bytes = path == "amd-nr.addon64"
                ? [.. Pe64(), .. System.Text.Encoding.UTF8.GetBytes(addonVersion)]
                : System.Text.Encoding.UTF8.GetBytes($"stand-in {path}");
            var full = Path.Combine(PayloadCache.FolderFor(name, version), path);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            files.Add(new System.Text.Json.Nodes.JsonObject
            {
                ["name"] = Path.GetFileName(path),
                ["path"] = path,
                ["size"] = bytes.Length,
                ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                ["url"] = "https://127.0.0.1:1/" + Path.GetFileName(path),
            });
        }
        components[name] = new System.Text.Json.Nodes.JsonObject { ["version"] = version, ["files"] = files };
    }
    Component("addon", addonVersion, "amd-nr.addon64");
    Component("runtime", "1.0.0", "dlssnr_amd_pass1.dll", "dlssnr_on_amd_weights.bin");
    Component("optiscaler", "1.0.0", "OptiScaler.dll", "OptiScaler.ini",
        "OptiScaler/amd_fidelityfx_loader_dx12.dll", "OptiScaler/amd_fidelityfx_upscaler_dx12.dll",
        "OptiScaler/amd_fidelityfx_framegeneration_dx12.dll", "OptiScaler/amd_fidelityfx_denoiser_dx12.dll",
        "OptiScaler/amd_fidelityfx_vk.dll", "OptiScaler/libxess.dll", "OptiScaler/libxess_dx11.dll",
        "OptiScaler/libxess_fg.dll", "OptiScaler/libxell.dll", "D3D12_OptiScaler/D3D12Core.dll");
    Component("opti-runtime", "1.0.0", "dlssnr_amd_runtime-0.3.1.dll");
    var manifest = new System.Text.Json.Nodes.JsonObject { ["schema"] = 1, ["components"] = components };
    File.WriteAllText(Path.Combine(AppPaths.Root, "payload.json"), manifest.ToJsonString());
}

void Flows()
{
    File.WriteAllText(Path.Combine(AppPaths.Root, "config.json"), """
        { "App": { "Owner": "", "Repo": "" }, "Addon": { "Owner": "", "Repo": "" },
          "Payload": { "Owner": "", "Repo": "", "ManifestUrl": "https://127.0.0.1:1/payload.json",
                       "ApiDbUrl": "https://127.0.0.1:1/api-db.json" } }
        """);
    SeedPayload("1.0.0");
    var game = Path.Combine(AppPaths.Root, "flow-game");
    Directory.CreateDirectory(game);
    File.WriteAllBytes(Path.Combine(game, "Game.exe"), Pe64());
    GameStore.Save([new GameEntry { Path = game, Name = "Flow Game" }]);

    var main = new MainWindow { Width = 1240, Height = 820 };
    main.Show();
    var session = main.Session;
    string S(string key) => main.FindResource(key) as string ?? key;
    Check(Until(() => session.Manifest is not null && main.Library.Cards is [{ Graphics: not null }]),
        "the window starts, on the payload list beside it when the published one cannot be read");
    Check(session.ManifestProblem is not null, "and knows the published one could not be read");
    var card = main.Library.Cards[0];
    var tile = main.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("card"));
    bool TileSays(string text) => tile.GetVisualDescendants().OfType<TextBlock>()
        .Any(t => t.IsEffectivelyVisible && t.Text == text);

    Click(tile);
    var sheet = main.GetVisualDescendants().OfType<GameSheet>().First();
    T Named<T>(string name) where T : Control => sheet.FindControl<T>(name)!;
    var install = Named<Button>("InstallButton");
    var uninstall = Named<Button>("UninstallButton");
    var label = Named<TextBlock>("InstallLabel");
    var verdict = Named<TextBlock>("ResultTitle");
    Check(Until(() => sheet.IsOpen && verdict.Text is { Length: > 0 }), "a tile opens its sheet, with a verdict");
    Check(label.Text == S("Str.Install") && !uninstall.IsVisible, "nothing installed: Install, and no Uninstall");

    Named<RadioButton>("RouteReShade").IsChecked = true;
    Click(install);
    Check(Until(() => !session.Busy), "Install finishes");
    Check(card.InstalledVia == RouteFamily.ReShade && File.Exists(Path.Combine(game, "amd-nr.addon64")),
        $"the ReShade route is in the folder ({verdict.Text})");
    Check(label.Text == S("Str.Reinstall") && uninstall.IsVisible, "the sheet offers Reinstall and Uninstall");
    Check(TileSays("ReShade"), "the tile says ReShade");
    Save(main, "flow-1-reshade");

    Named<RadioButton>("RouteOpti").IsChecked = true;
    Settle(6);
    Check(label.Text == S("Str.SwitchToOpti"), "choosing OptiScaler over it offers the switch");
    Click(install);
    Check(Until(() => main.OwnedWindows.Count > 0, 10), "the switch asks first");
    Settle(6);
    Save(main.OwnedWindows[0], "flow-2-switch-dialog");
    Click(main.OwnedWindows[0].GetVisualDescendants().OfType<Button>().First(b => b.Tag as string == "switch"));
    Check(Until(() => card.InstalledVia == RouteFamily.OptiScaler && !session.Busy),
        $"and trades ReShade for OptiScaler ({verdict.Text})");
    Check(!File.Exists(Path.Combine(game, "amd-nr.addon64")), "leaving no add-on behind");
    Check(label.Text == S("Str.Reinstall") && TileSays("OptiScaler"), "the sheet and the tile say OptiScaler");
    Save(main, "flow-2-optiscaler");

    Click(uninstall);
    Check(Until(() => !session.Busy), "Uninstall finishes");
    Check(card.InstalledVia is null && !uninstall.IsVisible && label.Text == S("Str.InstallOpti"),
        $"the installed route is the one taken out, whatever was selected ({verdict.Text})");

    Named<RadioButton>("RouteReShade").IsChecked = true;
    Settle(6);
    Click(install);
    Check(Until(() => !session.Busy) && card.InstalledVia == RouteFamily.ReShade, $"ReShade again ({verdict.Text})");
    Save(main, "flow-3-reshade-again");
    SeedPayload("1.0.1");
    var reload = session.LoadManifestAsync();
    Check(Until(() => reload.IsCompleted && card.Outdated), "a payload that moves on marks the install out of date");
    Check(Until(() => label.Text == S("Str.Update") && verdict.Text == S("Str.PreflightOutdated"), 10)
          && TileSays(S("Str.UpdateShort")), "the sheet offers Update and says why, and the tile says so");
    Save(main, "flow-3-outdated");
    Click(install);
    Check(Until(() => !session.Busy) && card.InstalledVia == RouteFamily.ReShade && !card.Outdated,
        "Update brings it in line");

    main.ShowPage(MainWindow.Page.Settings);
    var update = typeof(Session).GetProperty(nameof(Session.Update))!;
    Check(Until(() => session.Update.State == UpdateState.Failed, 10), "an update check with nowhere to ask fails");
    foreach (var check in new UpdateCheck[]
             {
                 new(UpdateState.Checking), new(UpdateState.UpToDate, When: DateTimeOffset.Now),
                 new(UpdateState.Available, new AppRelease("9.9.9", "", "https://example.invalid", new Dictionary<string, string>())),
                 new(UpdateState.Failed),
             })
    {
        update.SetValue(session, check);
        main.Relabel();
        Settle(6);
        Save(main, $"flow-4-update-{check.State.ToString().ToLowerInvariant()}");
        Check(main.FindControl<Control>("UpdateDot")!.IsVisible == (check.State == UpdateState.Available),
            $"update {check.State}: the dot on the gear only when there is one");
    }
    Check(!session.Busy && install.IsEnabled, "nothing is left busy");
    main.Close();
}

if (args.Length > 1 && args[1] == "flows")
{
    Flows();
    Console.WriteLine(failures == 0 ? "every flow did what it says" : $"{failures} flow check(s) failed");
    return failures == 0 ? 0 : 1;
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
