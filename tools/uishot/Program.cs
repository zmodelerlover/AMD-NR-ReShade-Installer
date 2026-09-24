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
// in the engine and cannot be stood in for; the add-on route installs without it. An OptiScaler release
// goes under "releases", the way v0.5.0 lists every OptiScaler beyond the one older apps read.
void SeedPayload(string addonVersion, string optiVersion = "1.0.0", string? optiRelease = null)
{
    var components = new System.Text.Json.Nodes.JsonObject();
    string[] optiPaths =
    [
        "OptiScaler.dll", "OptiScaler.ini",
        "OptiScaler/amd_fidelityfx_loader_dx12.dll", "OptiScaler/amd_fidelityfx_upscaler_dx12.dll",
        "OptiScaler/amd_fidelityfx_framegeneration_dx12.dll", "OptiScaler/amd_fidelityfx_denoiser_dx12.dll",
        "OptiScaler/amd_fidelityfx_vk.dll", "OptiScaler/libxess.dll", "OptiScaler/libxess_dx11.dll",
        "OptiScaler/libxess_fg.dll", "OptiScaler/libxell.dll", "D3D12_OptiScaler/D3D12Core.dll",
    ];
    void Component(string name, string version, params string[] paths) =>
        components[name] = Pinned(name, version, paths);
    System.Text.Json.Nodes.JsonObject Pinned(string name, string version, params string[] paths)
    {
        var files = new System.Text.Json.Nodes.JsonArray();
        foreach (var path in paths)
        {
            // The bytes carry the version, so a second list pins a different build.
            byte[] bytes = path == "amd-nr.addon64"
                ? [.. Pe64(), .. System.Text.Encoding.UTF8.GetBytes(addonVersion)]
                : System.Text.Encoding.UTF8.GetBytes($"stand-in {path} {version}");
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
        return new System.Text.Json.Nodes.JsonObject { ["version"] = version, ["files"] = files };
    }
    Component("addon", addonVersion, "amd-nr.addon64");
    Component("runtime", "1.0.0", "dlssnr_amd_pass1.dll", "dlssnr_on_amd_weights.bin");
    Component("optiscaler", optiVersion, optiPaths);
    Component("opti-runtime", "1.0.0", "dlssnr_amd_runtime-0.3.1.dll");
    var manifest = new System.Text.Json.Nodes.JsonObject { ["schema"] = 1, ["components"] = components };
    if (optiRelease is not null)
        manifest["releases"] = new System.Text.Json.Nodes.JsonObject { ["optiscaler"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["version"] = optiRelease, ["components"] =
                new System.Text.Json.Nodes.JsonObject { ["optiscaler"] = Pinned("optiscaler", optiRelease, optiPaths) } }) };
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

    // OptiScaler moves on too, and its badge is the longest one there is -- in Portuguese. It has to
    // fit the narrowest tile the grid makes, 150 wide, inside its 8 of margin either side.
    SeedPayload("1.0.0", "1.0.1");
    var optiReload = session.LoadManifestAsync();
    Check(Until(() => optiReload.IsCompleted && card.Outdated) && Until(() => label.Text == S("Str.Update"), 10),
        "an OptiScaler the payload moved past offers Update");
    App.ChangeLanguage("pt-BR");
    main.Relabel();
    Settle(6);
    var state = tile.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("state"));
    var row = (StackPanel)state.Child!;
    var shown = row.Children.Where(c => c.IsVisible).ToList();
    var need = shown.Sum(c => c.DesiredSize.Width) + row.Spacing * (shown.Count - 1)
               + state.Padding.Left + state.Padding.Right + state.BorderThickness.Left + state.BorderThickness.Right;
    Check(need <= 150 - 16, $"\"{S("Str.UpdateShort")} OptiScaler\" fits the narrowest tile ({need:0} of {150 - 16})");
    Save(main, "flow-2-optiscaler-outdated-pt");
    App.ChangeLanguage("en");
    main.Relabel();
    Click(install);
    Check(Until(() => !session.Busy) && card.InstalledVia == RouteFamily.OptiScaler && !card.Outdated,
        $"Update brings OptiScaler in line ({verdict.Text})");
    Check(card.Entry.OptiScalerVersion == "1.0.1", "and the game remembers the OptiScaler version it has");

    // A newer OptiScaler listed only under "releases": the menu offers both, stays on the one this
    // game has, and picking the newer one installs it.
    SeedPayload("1.0.0", "1.0.1", optiRelease: "1.0.2");
    var releasesReload = session.LoadManifestAsync();
    var optiVersions = Named<ComboBox>("OptiVersionBox");
    Check(Until(() => releasesReload.IsCompleted && card.Outdated)
          && Until(() => optiVersions.IsEffectivelyVisible && optiVersions.ItemCount == 2, 10),
        $"the OptiScaler version menu lists both versions ({optiVersions.ItemCount})");
    Check(optiVersions.SelectedItem is string s1 && s1.StartsWith("v1.0.1"), "and stays on the one this game has");
    Save(main, "flow-2-optiscaler-versions");
    optiVersions.SelectedIndex = 0;
    Settle(6);
    Click(install);
    Check(Until(() => !session.Busy) && card.InstalledVia == RouteFamily.OptiScaler && !card.Outdated
          && card.Entry.OptiScalerVersion == "1.0.2",
        $"picking the newer one installs it ({verdict.Text})");

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

    // A download with nowhere to come from: the sheet and the files panel both say so, stay saying
    // so, and offer the DNS fix, because no answer ever came back.
    Directory.Delete(PayloadCache.FolderFor("addon", "1.0.1"), recursive: true);
    Click(install);
    Check(Until(() => !session.Busy), "an install whose download fails finishes");
    Check(verdict.Text == S("Str.DownloadFailed") && Named<Button>("DnsRetryButton").IsVisible,
        "and says so, offering to clear DNS");
    Save(main, "flow-5-sheet-download-failed");
    main.ShowPage(MainWindow.Page.Machine);
    Settle(10);
    var files = main.GetVisualDescendants().OfType<PayloadPanel>().First(p => p.IsEffectivelyVisible);
    Click(files.FindControl<Button>("DownloadButton")!);
    Check(Until(() => !session.Busy && files.FindControl<Border>("ErrorBox")!.IsVisible),
        "Download all now that reaches nothing says so, and it stays said");
    Check(files.FindControl<Button>("DnsButton")!.IsVisible, "and offers to clear DNS");
    Save(main, "flow-5-files-download-failed");

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
    // Busy went on and off while the sheet was closed; a sheet opened afterwards has to work.
    main.ShowPage(MainWindow.Page.Games);
    Settle(6);
    // Found again rather than trusted: the grid is free to replace a tile.
    Click(main.GetVisualDescendants().OfType<Button>().First(b => b.Classes.Contains("card")));
    Check(Until(() => sheet.IsOpen, 10) && !session.Busy && install.IsEnabled,
        "nothing is left busy: a sheet opened afterwards can install");

    // Closed while a game folder is being written: refused, and said. Any other time: closed.
    session.Writing = true;
    main.Close();
    Settle(2);
    Check(main.IsVisible, "the window stays open while a game folder is being written");
    session.Writing = false;
    main.Close();
    Settle(2);
    Check(!main.IsVisible, "and closes once it is not");

    // A list written by a version with a route this one does not know: kept aside, not saved over by
    // the scan an empty list starts on its own.
    File.WriteAllText(AppPaths.GamesFile, """[{ "Path": "C:\\Games\\X", "Preset": "NotARouteYet" }]""");
    var fresh = new MainWindow { Width = 1240, Height = 820 };
    fresh.Show();
    Check(Until(() => Directory.GetFiles(AppPaths.Root, "games.json.unreadable-*").Length == 1, 10)
          && File.ReadAllText(Directory.GetFiles(AppPaths.Root, "games.json.unreadable-*")[0]).Contains("NotARouteYet"),
        "an unreadable games.json is kept aside, word for word");
    Until(() => !fresh.Session.Busy);
    fresh.Close();
}

// `uishot <out> perf`: how long a big library takes to open, from the window being built to every
// tile laid out and the first frame drawn. Headless draws in software, so the numbers are for
// comparing one build against another, not a promise about anybody's machine.
if (args.Length > 1 && args[1] == "perf")
{
    SeedLibrary(300);
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var main = new MainWindow { Width = 1920, Height = 1040 };
    var built = clock.ElapsedMilliseconds;
    long listed = 0;
    main.Library.Changed += () => listed = listed == 0 ? clock.ElapsedMilliseconds : listed;
    main.Show();
    while (main.GetVisualDescendants().OfType<Button>().Count(b => b.Classes.Contains("card")) < 300
           && clock.Elapsed < TimeSpan.FromSeconds(60))
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick(1);
    }
    var tiles = clock.ElapsedMilliseconds;
    using (main.CaptureRenderedFrame()) { }
    Console.WriteLine($"300 games: window built {built} ms, list read {listed} ms, every tile laid out {tiles} ms, "
                      + $"first frame {clock.ElapsedMilliseconds} ms");

    // Typing in the search, a key at a time, then clearing it: what each key costs.
    var search = main.GetVisualDescendants().OfType<TextBox>().First(t => t.Name == "SearchBox");
    foreach (var text in new[] { "S", "So", "Som", "" })
    {
        clock.Restart();
        search.Text = text;
        Dispatcher.UIThread.RunJobs();
        using (main.CaptureRenderedFrame()) { }
        Console.WriteLine($"  search \"{text}\": {clock.ElapsedMilliseconds} ms");
    }
    main.Close();
    return 0;
}

if (args.Length > 1 && args[1] == "flows")
{
    Flows();
    Console.WriteLine(failures == 0 ? "every flow did what it says" : $"{failures} flow check(s) failed");
    return failures == 0 ? 0 : 1;
}

// Every step of the first run, not only the first: the files step is the payload panel the main
// window shares, and it is the step people get stuck on.
void Wizard(string suffix, int width, int height)
{
    var setup = new SetupWindow { Width = width, Height = height };
    setup.Show();
    Settle();
    var next = setup.FindControl<Button>("NextButton")!;
    var files = setup.FindControl<PayloadPanel>("Payloads")!;
    for (var step = 1; step <= 4; step++)
    {
        // The files step reads the payload list off the network; offline it says so, which is a
        // page worth seeing too, so the wait is bounded and not a check.
        if (step == 3) Until(() => files.Missing >= 0, 20);
        var name = $"setup{suffix}-{step}-{width}x{height}";
        Save(setup, name, dump: step == 1 && suffix.Length == 0 && width == 820);
        CheckScrollReach(setup, name);
        if (step < 4)
        {
            Click(next);
            Settle(8);
        }
    }
    Console.WriteLine($"setup{suffix} {width}x{height}: ok");
    setup.Close();
}

// The main window at one size: every page, each scrolled to its end, and the sheet a tile opens.
void Main(int w, int h, string suffix = "")
{
    var main = new MainWindow { Width = w, Height = h };
    main.Show();
    Settle(15);
    // The update check is real, and a newer release puts a banner over the grid: waited for, or it
    // lands between scrolling to the end and measuring, and the last row reads as cut off.
    Until(() => main.Session.Update.State != UpdateState.Checking, 20);
    Settle(4);
    foreach (var page in new[] { "GamesPage", "SystemPage", "SettingsPage" })
    {
        foreach (var other in new[] { "GamesPage", "SystemPage", "SettingsPage" })
            if (main.FindControl<Control>(other) is { } c) c.IsVisible = other == page;
        Settle(8);
        var name = $"{page.Replace("Page", "").ToLowerInvariant()}{suffix}-{w}x{h}";
        Save(main, name, dump: w == 1920 && page == "GamesPage" && suffix.Length == 0);
        CheckScrollReach(main, name);
        Save(main, name + "-end");
    }

    // The sheet a tile opens, over the library, with its own scroller.
    foreach (var other in new[] { "GamesPage", "SystemPage", "SettingsPage" })
        if (main.FindControl<Control>(other) is { } c) c.IsVisible = other == "GamesPage";
    if (main.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Classes.Contains("card")) is { } card)
    {
        Click(card);
        Settle(20);
        Save(main, $"sheet{suffix}-{w}x{h}", dump: w == 1240 && suffix.Length == 0);
        CheckScrollReach(main, $"sheet{suffix}-{w}x{h}");
        Save(main, $"sheet{suffix}-{w}x{h}-end");
    }
    Console.WriteLine($"main{suffix} {w}x{h}: ok");
    main.Close();
}

Wizard("", 820, 900);
Wizard("", 700, 640);
SeedLibrary(61);
foreach (var (w, h) in new[] { (1240, 820), (1920, 1040), (2560, 1400), (980, 620) }) Main(w, h);

// Portuguese runs longer than English almost everywhere, so it is where a label wraps or a row
// overflows first: the smallest sizes, where that shows.
App.ChangeLanguage("pt-BR");
Wizard("-pt", 700, 640);
Main(1240, 820, "-pt");
Main(980, 620, "-pt");

Console.WriteLine(failures == 0 ? "every scroll reaches its end" : $"{failures} scroll(s) cut off");
return failures == 0 ? 0 : 1;
