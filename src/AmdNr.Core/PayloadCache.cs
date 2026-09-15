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
    /// <summary>%AppData%\AmdNrInstaller, created on first use -- or wherever AMDNR_HOME says.
    /// The override is what keeps the test suite out of the real cache, and it is also how a
    /// portable install would keep everything beside the executable.</summary>
    public static string Root { get; } = Create(
        Environment.GetEnvironmentVariable("AMDNR_HOME") is { Length: > 0 } home
            ? home
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData,
                    Environment.SpecialFolderOption.Create),
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
        return c.Installed.All(f =>
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

        foreach (var entry in c.Extract ?? [])
        {
            var target = Path.Combine(dir, entry.RelativePath);
            if (Engine.SizeOf(target) == entry.Size && Engine.HashFile(target) == entry.Sha256) continue;
            var archive = Path.Combine(dir, c.Files[0].RelativePath);
            await Task.Run(() => ExtractVerified(archive, entry, target), cancel);
        }
        return dir;
    }

    /// <summary>Takes one entry out of an archive and keeps it only if it hashes to its pin. The
    /// archive may be a zip appended to an executable -- ReShade's installer is exactly that -- whose
    /// offsets count from where the zip starts, not from the start of the file, which is why the
    /// archive is re-based before it is opened.</summary>
    internal static void ExtractVerified(string archive, PayloadFile entry, string target)
    {
        using var zip = OpenAppendedZip(archive);
        var item = zip.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), entry.Name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(e.Name, entry.Name, StringComparison.OrdinalIgnoreCase));
        Engine.Require(item is not null, $"{entry.Name} is not inside {Path.GetFileName(archive)}.");

        using var buffer = new MemoryStream();
        using (var source = item!.Open()) source.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var got = Engine.Sha(bytes);
        Engine.Require(got == entry.Sha256,
            $"{entry.Name} inside {Path.GetFileName(archive)} does not match its pinned SHA-256."
            + $"\n      expected {entry.Sha256}\n      got      {got}");

        Engine.MakeParent(target);
        File.WriteAllBytes(target, bytes);
    }

    internal static System.IO.Compression.ZipArchive OpenAppendedZip(string path)
    {
        var bytes = File.ReadAllBytes(path);
        // End of central directory: signature 50 4B 05 06, somewhere in the last 64 KB (the comment
        // field is at most 65535 bytes). It records the directory's size and its offset from the
        // zip's own start, and the directory itself sits right before it -- so the zip starts at
        // (end-of-directory position) - (directory size) - (directory offset).
        for (var at = bytes.Length - 22; at >= Math.Max(0, bytes.Length - 22 - 65535); at--)
        {
            if (bytes[at] != 0x50 || bytes[at + 1] != 0x4b || bytes[at + 2] != 0x05 || bytes[at + 3] != 0x06) continue;
            var size = BitConverter.ToUInt32(bytes, at + 12);
            var offset = BitConverter.ToUInt32(bytes, at + 16);
            var start = (long)at - size - offset;
            Engine.Require(start >= 0, $"{Path.GetFileName(path)} has a damaged archive directory.");
            return new System.IO.Compression.ZipArchive(
                new MemoryStream(bytes, (int)start, bytes.Length - (int)start, writable: false),
                System.IO.Compression.ZipArchiveMode.Read);
        }
        throw new InstallException($"{Path.GetFileName(path)} carries no archive to extract from.");
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

    /// <summary>One folder holding every file the given components need, which is what an install
    /// reads from. The files are hard-linked out of the cache where the filesystem allows it, so
    /// staging 141 MB of weights costs no disk and no copy; a volume that refuses gets a copy.</summary>
    public string Stage(PayloadManifest manifest, params string[] components)
    {
        var key = string.Join("-", components.Select(c => $"{Sanitise(c)}.{Sanitise(manifest.Component(c).Version)}"));
        var staging = Path.Combine(AppPaths.Cache, "staging", key.Length <= 120 ? key : key[..120]);
        Directory.CreateDirectory(staging);

        foreach (var component in components)
        {
            var c = manifest.Component(component);
            var from = FolderFor(component, c.Version);
            foreach (var file in c.Installed)
            {
                var source = Path.Combine(from, file.RelativePath);
                var target = Path.Combine(staging, file.RelativePath);
                if (Engine.SizeOf(target) == file.Size) continue;
                Engine.MakeParent(target);
                if (File.Exists(target)) File.Delete(target);
                if (!TryHardLink(source, target)) File.Copy(source, target, overwrite: true);
            }
        }
        return staging;
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW",
        CharSet = System.Runtime.InteropServices.CharSet.Unicode, SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(string link, string existing, IntPtr attributes);

    private static bool TryHardLink(string source, string target)
    {
        try { return CreateHardLinkW(target, source, IntPtr.Zero); }
        catch (EntryPointNotFoundException) { return false; }
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

    /// <summary>The copy that ships beside the executable, or one the user dropped in the app's own
    /// folder. This is what makes the app work offline, on a first run behind a captive portal, and
    /// before the content repository exists at all -- the hashes are the same either way, so a local
    /// manifest weakens nothing.</summary>
    public static PayloadManifest? LoadLocalManifest(string fileName = "payload.json")
    {
        foreach (var path in new[]
                 {
                     Path.Combine(AppPaths.Root, fileName),
                     Path.Combine(AppContext.BaseDirectory, fileName),
                 })
        {
            try
            {
                if (File.Exists(path)) return PayloadManifest.Parse(File.ReadAllText(path));
            }
            catch (Exception e) when (e is IOException or InstallException)
            {
                // Try the next one; a broken local copy must not stop the app from starting.
            }
        }
        return null;
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
