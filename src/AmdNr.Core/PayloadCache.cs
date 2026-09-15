// Downloading the payloads, and keeping them.
//
// A component lands in %AppData%\AmdNrInstaller\cache\<component>\<version>\, verified, and the
// install reads from there -- which is the same shape of folder the engine has always accepted, so
// nothing downstream knows or cares whether the files came from here or from a folder someone
// unzipped by hand.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace AmdNr.Core;

public static class AppPaths
{
    /// <summary>%AppData%\AmdNrInstaller. Created on first use.</summary>
    public static string Root { get; } = Create(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.Create),
        "AmdNrInstaller"));

    public static string Cache => Create(Path.Combine(Root, "cache"));
    public static string Logs => Create(Path.Combine(Root, "logs"));
    public static string GamesFile => Path.Combine(Root, "games.json");
    public static string CrashLog => Path.Combine(Logs, "crash.log");

    private static string Create(string dir)
    {
        Directory.CreateDirectory(dir);
        return dir;
    }
}

public sealed record DownloadProgress(string File, long Received, long? Total)
{
    public double? Fraction => Total is > 0 ? (double)Received / Total.Value : null;
}

public sealed class PayloadCache(HttpClient http)
{
    /// <summary>Where a component's files sit once they are verified.</summary>
    public static string FolderFor(string component, string version) =>
        Path.Combine(AppPaths.Cache, Sanitise(component), Sanitise(version));

    /// <summary>A manifest is remote data: it names the folder this writes into, so anything that
    /// is not a plain name is refused rather than escaped through.</summary>
    private static string Sanitise(string s)
    {
        Engine.Require(s.Length is > 0 and <= 64, $"Unusable component or version name: '{s}'");
        foreach (var c in s)
            Engine.Require(char.IsAsciiLetterOrDigit(c) || c is '.' or '-' or '_',
                $"Unusable component or version name: '{s}'");
        Engine.Require(s is not ("." or ".."), $"Unusable component or version name: '{s}'");
        return s;
    }

    /// <summary>Every file present and hashing to what the manifest says. Hashing 141 MB takes
    /// about a second, which is worth paying before an install rather than trusting a size.</summary>
    public static bool IsComplete(PayloadManifest manifest, string component)
    {
        var c = manifest.Component(component);
        var dir = FolderFor(component, c.Version);
        return c.Files.All(f =>
        {
            var path = Path.Combine(dir, f.RelativePath);
            return Engine.SizeOf(path) == f.Size && Engine.HashFile(path) == f.Sha256;
        });
    }

    /// <summary>Downloads whatever is missing or wrong and returns the folder to install from.
    /// A file that is already there and already hashes correctly is not fetched again.</summary>
    public async Task<string> EnsureAsync(PayloadManifest manifest, string component,
        IProgress<DownloadProgress>? progress = null, CancellationToken cancel = default)
    {
        var c = manifest.Component(component);
        var dir = FolderFor(component, c.Version);
        Directory.CreateDirectory(dir);

        foreach (var file in c.Files)
        {
            var path = Path.Combine(dir, file.RelativePath);
            if (Engine.SizeOf(path) == file.Size && Engine.HashFile(path) == file.Sha256) continue;
            Engine.MakeParent(path);
            await FetchAsync(manifest.DownloadUrl(component, file), path, file, progress, cancel);
        }
        return dir;
    }

    /// <summary>One file, resumed if a part of it is already on disk, verified before it is allowed
    /// to take the final name. A partial download can never be mistaken for a complete one, because
    /// the name only changes after the hash matches.</summary>
    private async Task FetchAsync(Uri url, string path, PayloadFile file,
        IProgress<DownloadProgress>? progress, CancellationToken cancel)
    {
        var part = path + ".part";
        var have = Engine.SizeOf(part) ?? 0;
        if (have > file.Size) // A stale part from a different build: start over rather than splice.
        {
            File.Delete(part);
            have = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue((long)have, null);

        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        if (have > 0 && response.StatusCode == HttpStatusCode.OK)
        {
            // The server ignored the range and is sending the whole thing: take it from the top.
            have = 0;
            File.Delete(part);
        }
        else if (have > 0 && response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            // Already have every byte; fall through to the hash check below.
            have = (ulong)new FileInfo(part).Length;
        }
        else
        {
            Engine.Require(response.IsSuccessStatusCode,
                $"Could not download {file.Name}: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        }

        if (response.StatusCode != HttpStatusCode.RequestedRangeNotSatisfiable)
        {
            var total = (response.Content.Headers.ContentLength ?? 0) + (long)have;
            await using var source = await response.Content.ReadAsStreamAsync(cancel);
            await using var target = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None);

            var buffer = new byte[128 * 1024];
            var received = (long)have;
            int read;
            while ((read = await source.ReadAsync(buffer, cancel)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), cancel);
                received += read;
                progress?.Report(new DownloadProgress(file.Name, received, total > 0 ? total : null));
            }
        }

        var got = await Task.Run(() => Engine.HashFile(part), cancel);
        if (got != file.Sha256)
        {
            File.Delete(part);
            throw new InstallException(
                $"{file.Name} downloaded, but it does not match the SHA-256 the manifest gives."
                + $"\n      expected {file.Sha256}\n      got      {got}"
                + "\n      Nothing was installed. Try again; if it keeps happening the published file changed.");
        }

        File.Move(part, path, overwrite: true);
    }

    /// <summary>The manifest itself, from raw.githubusercontent.com -- no REST API, so no rate
    /// limit to share with everyone else on the same address.</summary>
    public async Task<PayloadManifest> FetchManifestAsync(string owner, string repo, string branch = "main",
        string file = "payload.json", CancellationToken cancel = default)
    {
        var url = $"https://raw.githubusercontent.com/{owner}/{repo}/{branch}/{file}";
        using var response = await http.GetAsync(url, cancel);
        Engine.Require(response.IsSuccessStatusCode,
            $"Could not read the payload list: the server answered {(int)response.StatusCode} {response.ReasonPhrase}.");
        return PayloadManifest.Parse(await response.Content.ReadAsStringAsync(cancel));
    }

    /// <summary>A default client with a User-Agent, because GitHub refuses requests without one.</summary>
    public static HttpClient DefaultClient(string version)
    {
        var client = new HttpClient(new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All })
        {
            Timeout = TimeSpan.FromMinutes(30),
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd($"AMD-NR-ReShade-Installer/{version}");
        return client;
    }
}
