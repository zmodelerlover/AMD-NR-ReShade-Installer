// Download all, one row at a time: each component's row says where that component is -- waiting,
// downloading, verified, failed -- as it happens, not all together once the set is done. Run by the
// flows in Program.cs on This machine, once the failed download there has been looked at.
//
// Three components: one already in the cache, one taken from another version already on this
// machine (so it finishes without the network), and one whose address answers the connection and
// then never says anything, which holds it in "downloading" until the flow lets it fail.

using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.VisualTree;
using AmdNr.App;
using AmdNr.Core;

internal static class FilesFlow
{
    public static void Run(MainWindow main, byte[] pe64, Action<bool, string> check, Func<Func<bool>, int, bool> until,
        Action<Button> click, Action<Window, string> save)
    {
        var session = main.Session;
        var manifestPath = Path.Combine(AppPaths.Root, "payload.json");
        var original = File.ReadAllText(manifestPath);
        var runtime = PayloadCache.FolderFor("runtime", "1.0.0");
        var aside = Path.Combine(AppPaths.Root, "runtime-aside");
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        TcpClient? held = null;
        _ = Task.Run(async () => { try { held = await listener.AcceptTcpClientAsync(); } catch (SocketException) { } catch (ObjectDisposedException) { } });

        // The add-on the earlier step deleted, under a version beside it: found there, not fetched.
        var addon = Path.Combine(PayloadCache.FolderFor("addon", "0.0.9"), "amd-nr.addon64");
        Directory.CreateDirectory(Path.GetDirectoryName(addon)!);
        File.WriteAllBytes(addon, [.. pe64, .. "1.0.1"u8]);
        // The runtime out of the cache, and its address one that never answers.
        Directory.Move(runtime, aside);
        var json = JsonNode.Parse(original)!;
        foreach (var file in json["components"]!["runtime"]!["files"]!.AsArray())
            file!["url"] = $"https://127.0.0.1:{port}/{file["name"]!.GetValue<string>()}";
        // A third row that is already there.
        var shader = "stand-in companion effect"u8.ToArray();
        Directory.CreateDirectory(PayloadCache.FolderFor("shader", "1.0.0"));
        File.WriteAllBytes(Path.Combine(PayloadCache.FolderFor("shader", "1.0.0"), Work.ShaderName), shader);
        json["components"]!["shader"] = new JsonObject
        {
            ["version"] = "1.0.0",
            ["files"] = new JsonArray(new JsonObject
            {
                ["name"] = Work.ShaderName, ["size"] = shader.Length,
                ["sha256"] = Engine.Sha(shader), ["url"] = "https://127.0.0.1:1/" + Work.ShaderName,
            }),
        };
        File.WriteAllText(manifestPath, json.ToJsonString());

        var files = main.GetVisualDescendants().OfType<PayloadPanel>().First(p => p.IsEffectivelyVisible);
        IReadOnlyList<PayloadRow> Rows() => (files.FindControl<ItemsControl>("Rows")!.ItemsSource as IEnumerable<PayloadRow>)?.ToList() ?? [];
        PayloadRow Row(string name) => Rows().FirstOrDefault(r => r.Name == name) ?? new PayloadRow(name, "", "");
        var reload = session.LoadManifestAsync();
        // The add-on's row still says Failed, from the download the step before let fail.
        check(until(() => reload.IsCompleted && Rows().Count == 3 && Row("shader").Ready && !Row("addon").Ready && Row("runtime").Waiting, 20),
            "the files panel lists one component ready and two not yet");

        click(files.FindControl<Button>("DownloadButton")!);
        check(until(() => Row("runtime").Working, 20) && session.Busy,
            $"Download all marks the row it is downloading, in the row ({Row("runtime").State})");
        check(Row("addon").Ready && Row("addon").State == (main.FindResource("Str.PayloadReadyShort") as string)
              && Row("shader").Ready, "and the one already done is green while the next is still coming");
        save(main, "flow-7-files-downloading");

        held?.Dispose();
        listener.Stop();
        check(until(() => !session.Busy, 30), "the download that is let go ends");
        check(Row("shader").Ready && Row("addon").Ready && Row("runtime").Failed && !Row("runtime").Ready,
            "afterwards the rows that came are green, and the one that failed says so");
        check(files.FindControl<Border>("ErrorBox")!.IsVisible, "with the failure spelled out above them");
        save(main, "flow-7-files-after");

        File.WriteAllText(manifestPath, original);
        if (Directory.Exists(runtime)) Directory.Delete(runtime, recursive: true);
        Directory.Move(aside, runtime);
        var back = session.LoadManifestAsync();
        until(() => back.IsCompleted && !session.Busy, 20);
    }
}
