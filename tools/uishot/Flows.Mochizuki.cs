// mochizuki as a person meets it: a version of OptiScaler that carries it shows the choice, off until
// ticked; ticked, Install puts the runtime and its dlssnr-amd folder beside OptiScaler and names it the
// NR runtime in OptiScaler.ini, and the game remembers the choice; with no choice on record the box
// follows the folder, and unticked, Install takes it out again. Run by the flows in Program.cs with
// OptiScaler 1.0.2 installed from "releases"; it leaves 1.0.3 installed with mochizuki, for the
// uninstall flow to take back.

using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AmdNr.App;
using AmdNr.Core;

internal static class MochizukiFlow
{
    public static void Run(MainWindow main, GameSheet sheet, GameCard card, string game,
        Action<bool, string> check, Func<Func<bool>, int, bool> until, Action<Button> click, Action<Window, string> save)
    {
        T Named<T>(string name) where T : Control => sheet.FindControl<T>(name)!;
        var section = Named<StackPanel>("MochizukiSection");
        var box = Named<CheckBox>("MochizukiBox");
        var note = Named<TextBlock>("MochizukiNote");
        var versions = Named<ComboBox>("OptiVersionBox");
        var install = Named<Button>("InstallButton");
        var ini = Path.Combine(game, Work.OptiScalerIni);

        check(!section.IsVisible, "a version of OptiScaler without mochizuki does not offer it");
        Seed("1.0.2", "1.0.3");
        var reload = main.Session.LoadManifestAsync();
        check(until(() => reload.IsCompleted && versions.ItemCount == 3, 10), $"the menu lists the version that carries it ({versions.ItemCount})");
        versions.SelectedIndex = 0;
        check(until(() => section.IsVisible, 5), "and picking that version offers mochizuki");
        check(box.IsChecked != true && card.Entry.Mochizuki is null, "off until ticked");

        var machine = main.Session.Machine;
        check(box.IsEnabled == (machine?.Rdna4 != false) && note.Text is { Length: > 0 },
            $"tickable unless the card is known not to be RDNA4 ({machine?.Gpu}, rdna4 {machine?.Rdna4?.ToString() ?? "unknown"}): {note.Text}");
        if (machine?.Rdna4 == false) return; // the rest needs a card it is offered on
        box.IsChecked = true;
        check(card.Entry.Mochizuki == true, "ticking it is remembered for the game");
        until(() => false, 1); // a second of layout and render ticks before the picture
        if (section.FindAncestorOfType<ScrollViewer>() is { Content: Visual content } scroll
            && section.TranslatePoint(new Point(0, 0), content) is { } at)
            scroll.Offset = new Vector(0, Math.Max(0, at.Y - 160));
        until(() => false, 1);
        save(main, "flow-2-optiscaler-mochizuki");

        click(install);
        check(until(() => !main.Session.Busy, 30) && card.InstalledVia == RouteFamily.OptiScaler
              && card.Entry.OptiScalerVersion == "1.0.3", "Install brings OptiScaler 1.0.3");
        foreach (var file in new[] { "MochizukiNrRuntime.dll", "dlssnr-amd/dlssnr.bin", "dlssnr-amd/prewarm/manifest.txt",
                     "dlssnr-amd/shaders/g_attn.spv", "dlssnr-amd/shaders/runtime/cascade_blur.spv" })
            check(File.Exists(Path.Combine(game, file)), $"with {file}");
        check(Engine.GetIni(File.ReadAllText(ini), "DlssNr", "NrBackend").Trim() == "mochizuki",
            "and OptiScaler.ini names mochizuki the NR runtime");

        // The choice forgotten -- the game added again, the app on another PC: the box follows the folder.
        card.Entry.Mochizuki = null;
        versions.SelectedIndex = 1;
        versions.SelectedIndex = 0;
        check(until(() => section.IsVisible && box.IsChecked == true, 5), "with no choice on record, the box follows the folder: on");

        // Unticked, the next install takes it out, and the package's ini no longer names it.
        box.IsChecked = false;
        check(card.Entry.Mochizuki == false, "unticking it is remembered too");
        until(() => false, 1);
        click(install);
        check(until(() => !main.Session.Busy, 30) && card.InstalledVia == RouteFamily.OptiScaler
              && !File.Exists(Path.Combine(game, "MochizukiNrRuntime.dll")) && !Directory.Exists(Path.Combine(game, "dlssnr-amd"))
              && Engine.GetIni(File.ReadAllText(ini), "DlssNr", "NrBackend").Trim() != "mochizuki",
            "unticked, Install takes mochizuki out and OptiScaler.ini is the package's again");
        check(!card.Outdated, "and the folder is not out of date over files it no longer has");

        // Ticked again, for the uninstall flow to take back.
        box.IsChecked = true;
        until(() => false, 1);
        click(install);
        check(until(() => !main.Session.Busy, 30) && File.Exists(Path.Combine(game, "MochizukiNrRuntime.dll")),
            "ticked again, Install brings it back");
    }

    /// <summary>A newer OptiScaler under "releases" with the same OptiScaler files as <paramref name="from"/>
    /// and the mochizuki runtime and model, every file already in the cache like the rest of the flows'.</summary>
    private static void Seed(string from, string to)
    {
        var path = Path.Combine(AppPaths.Root, "payload.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var releases = root["releases"]!["optiscaler"]!.AsArray();
        var previous = releases.First(r => (string?)r!["version"] == from)!;
        var opti = previous["components"]!["optiscaler"]!.DeepClone().AsObject();
        opti["version"] = to;
        var source = PayloadCache.FolderFor(PayloadManifest.OptiScalerComponent, from);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(PayloadCache.FolderFor(PayloadManifest.OptiScalerComponent, to), Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        releases.Insert(0, new JsonObject
        {
            ["version"] = to,
            ["components"] = new JsonObject
            {
                [PayloadManifest.OptiScalerComponent] = opti,
                [PayloadManifest.MochizukiComponent] = Pinned(PayloadManifest.MochizukiComponent, to,
                    "MochizukiNrRuntime.dll", "dlssnr-amd.prewarm/manifest.txt", "dlssnr-amd.shaders/g_attn.spv",
                    "dlssnr-amd.shaders.runtime/cascade_blur.spv"),
                [PayloadManifest.MochizukiModelComponent] = Pinned(PayloadManifest.MochizukiModelComponent, "1", "dlssnr-amd/dlssnr.bin"),
            },
        });
        File.WriteAllText(path, root.ToJsonString());
    }

    private static JsonObject Pinned(string component, string version, params string[] paths)
    {
        var files = new JsonArray();
        foreach (var p in paths)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes($"stand-in {p} {version}");
            var full = Path.Combine(PayloadCache.FolderFor(component, version), p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, bytes);
            files.Add(new JsonObject
            {
                ["name"] = Path.GetFileName(p),
                ["path"] = p,
                ["size"] = bytes.Length,
                ["sha256"] = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)),
                ["url"] = "https://127.0.0.1:1/" + Path.GetFileName(p),
            });
        }
        return new JsonObject { ["version"] = version, ["files"] = files };
    }
}
