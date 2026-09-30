// danielblnc's supporter build, as a supporter meets it: once the payload lists one the add-on runs, a
// titled block under the route offers two cards, the download lit, and says the build is not distributed;
// chosen with no copy on this machine, the supporter card is lit and says to supply one; with his version.dll
// in the game that is the copy, kept, and Install puts it in patched and takes version.dll to the backup; the
// folder is not out of date over it; the download card puts the download back; on the next game with no
// choice made, the kept copy is preselected and the block says so; and the supporter card clicked with a
// copy kept takes it without asking for a file. Run by the flows in Program.cs with the
// ReShade route installed at add-on 1.0.1. The file picker is the one step a headless window cannot take.

using System.Text.Json.Nodes;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AmdNr.App;
using AmdNr.Core;

internal static class RuntimeFlow
{
    public static void Run(MainWindow main, GameSheet sheet, GameCard card, string game,
        Action<bool, string> check, Func<Func<bool>, int, bool> until, Action<Button> click, Action<Window, string> save)
    {
        string S(string key) => main.FindResource(key) as string ?? key;
        T Named<T>(string name) where T : Control => sheet.FindControl<T>(name)!;
        var section = Named<Border>("RuntimeSection");
        var use = Named<Button>("RuntimeFileButton");
        var download = Named<RadioButton>("RuntimeDownloadChoice");
        var supporter = Named<RadioButton>("RuntimeSupporterChoice");
        var files = Named<StackPanel>("RuntimeSupporterPanel");
        var note = Named<TextBlock>("RuntimeNote");
        var details = Named<ItemsControl>("ReportList");
        var install = Named<Button>("InstallButton");
        var runtime = Path.Combine(game, Work.RuntimeName);
        var missing = string.Format(S("Str.SupporterMissing"), "9.9.9");
        bool Says(string text) => details.Items.OfType<ReportLine>().Any(l => l.Text == text);
        void Picture(string name)
        {
            until(() => false, 1);
            if (section.FindAncestorOfType<ScrollViewer>() is { Content: Visual content } scroll
                && section.TranslatePoint(new Point(0, 0), content) is { } at)
                scroll.Offset = new Vector(0, Math.Max(0, at.Y - 120));
            until(() => false, 1);
            save(main, name);
        }

        check(!section.IsVisible, "with no supporter build in the payload list, there is no block");
        var (sha, patched, original) = Seed();
        var reload = main.Session.LoadManifestAsync();
        check(until(() => reload.IsCompleted && section.IsVisible, 10), "a supporter build in the list shows its block");
        check(section.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == S("Str.SupporterTitle"))
              // Said by the (i) on the supporter card, where it is read on the pointer.
              && section.GetVisualDescendants().OfType<Border>().Any(b => b.Classes.Contains("info") && ToolTip.GetTip(b) is StackPanel),
            "titled, and saying it is not distributed");
        check(download.IsChecked == true && supporter.IsChecked != true && !files.IsVisible && card.Entry.UserRuntime is null,
            "two cards, the download lit, and nothing to supply under it");
        Picture("flow-3-supporter-block");

        // Chosen, with no copy on this machine: the block and the check both say to supply one.
        card.Entry.UserRuntime = sha;
        sheet.Show(card);
        check(until(() => note.Text == missing && Says(missing), 10) && supporter.IsChecked == true
              && use.IsEffectivelyVisible && download.IsEffectivelyVisible,
            "chosen with no copy here, the supporter card is lit, says to supply one, and the download is a click away");
        Picture("flow-3-supporter-missing");

        // His setup ran in this game: his version.dll is the copy, and the app keeps it.
        File.WriteAllBytes(Path.Combine(game, "version.dll"), original);
        sheet.Show(card);
        check(until(() => note.Text == string.Format(S("Str.SupporterUsingFile"), "9.9.9") && !Says(missing), 10),
            $"danielblnc's version.dll in the game is found and kept ({note.Text})");
        click(install);
        check(until(() => !main.Session.Busy, 30) && Engine.HashFile(runtime) == patched
              && !File.Exists(Path.Combine(game, "version.dll")),
            "Install puts the build in patched, and version.dll in the backup");
        check(!card.Outdated, "and the folder is not out of date over the runtime it chose");

        download.IsChecked = true;
        check(card.Entry.UserRuntime == "" && until(() => !files.IsVisible && supporter.IsChecked != true, 5),
            "the download card is remembered, and lit");
        click(install);
        check(until(() => !main.Session.Busy, 30) && Engine.HashFile(runtime) != patched, "and Install puts the download back");

        // No choice made, and a copy kept: preselected, and said so.
        card.Entry.UserRuntime = null;
        sheet.Show(card);
        check(until(() => note.Text == string.Format(S("Str.SupporterUsingKept"), "9.9.9"), 10) && supporter.IsChecked == true,
            $"with a kept copy and no choice made, the build is preselected and the block says so ({note.Text})");
        Picture("flow-3-supporter-kept");
        download.IsChecked = true;
        check(card.Entry.UserRuntime == "", "the download chosen");
        supporter.IsChecked = true;
        check(card.Entry.UserRuntime == sha && until(() => files.IsVisible, 5),
            "and the supporter card, clicked with a copy kept, takes it without asking for a file");
        download.IsChecked = true;
        check(card.Entry.UserRuntime == "", "and the download chosen again, for the flows after this one");
    }

    /// <summary>A stand-in build listed under user_runtimes, run by add-ons from 1.0.0 on, with one change.</summary>
    private static (string Sha, string Patched, byte[] Original) Seed()
    {
        var original = new byte[8192];
        new Random(5).NextBytes(original);
        var patched = (byte[])original.Clone();
        new byte[] { 0x90, 0x90, 0x90, 0x90 }.CopyTo(patched, 0x100);
        var path = Path.Combine(AppPaths.Root, "payload.json");
        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["user_runtimes"] = new JsonArray(new JsonObject
        {
            ["runtime"] = "DLSS-NR-on-AMD stand-in",
            ["name"] = "9.9.9",
            ["addon_since"] = "1.0.0",
            ["original_sha256"] = Engine.Sha(original),
            ["original_size"] = original.Length,
            ["patched_sha256"] = Engine.Sha(patched),
            ["changes"] = new JsonArray(new JsonObject
            {
                ["patch"] = "setup-thread",
                ["offset"] = "0x100",
                ["before"] = Convert.ToHexStringLower(original.AsSpan(0x100, 4)),
                ["after"] = "90909090",
            }),
        });
        File.WriteAllText(path, root.ToJsonString());
        return (Engine.Sha(original), Engine.Sha(patched), original);
    }
}
