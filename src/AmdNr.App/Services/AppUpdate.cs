// Whether a newer release of this app is out, and replacing this executable with it.

using System.Diagnostics;
using System.Text.Json;
using AmdNr.Core;

namespace AmdNr.App;

public sealed record AppRelease(string Version, string Notes, string Url,
    IReadOnlyDictionary<string, string> Assets)
{
    /// <summary>Whether this release publishes both the executable and the sums that pin it.
    /// Without the pin there is nothing to check the download against, and an update that runs
    /// unverified bytes is worse than one the person fetches by hand.</summary>
    public bool CanSelfUpdate =>
        Assets.ContainsKey(AppUpdate.ExeAsset) && Assets.ContainsKey(AppUpdate.SumsAsset);

    /// <summary>The button says what pressing it does: download and install here, or send the
    /// person to GitHub for a release this cannot verify. v0.5.0 said "Open the releases page" on
    /// a button that actually downloaded and restarted.</summary>
    public string ActionKey => CanSelfUpdate ? "Str.UpdateNow" : "Str.UpdateOnGitHub";
}

public enum UpdateState
{
    Checking,
    UpToDate,
    Available,
    Failed,
}

/// <summary>What one check found. "No newer release" and "could not ask" used to be the same null,
/// which is why the app could never say "you are on the latest": it did not know.</summary>
public sealed record UpdateCheck(UpdateState State, AppRelease? Release = null, DateTimeOffset? When = null);

public static class AppUpdate
{
    public static async Task<UpdateCheck> CheckAsync(HttpClient http, RepoRef repo, string currentVersion,
        CancellationToken cancel = default)
    {
        if (string.IsNullOrWhiteSpace(repo.Owner) || string.IsNullOrWhiteSpace(repo.Repo))
            return new UpdateCheck(UpdateState.Failed);

        try
        {
            var url = $"https://api.github.com/repos/{repo.Owner}/{repo.Repo}/releases/latest";
            using var response = await http.GetAsync(url, cancel);
            if (!response.IsSuccessStatusCode) return new UpdateCheck(UpdateState.Failed);

            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancel));
            if (!doc.RootElement.TryGetProperty("tag_name", out var tag)) return new UpdateCheck(UpdateState.Failed);

            var latest = Normalise(tag.GetString() ?? "");
            if (latest.Length == 0) return new UpdateCheck(UpdateState.Failed);
            var newer = Version.TryParse(latest, out var theirs) && Version.TryParse(Normalise(currentVersion), out var mine)
                ? theirs > mine
                : latest != Normalise(currentVersion);
            return newer
                ? new UpdateCheck(UpdateState.Available, Build(doc, latest), DateTimeOffset.Now)
                : new UpdateCheck(UpdateState.UpToDate, When: DateTimeOffset.Now);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException
                                      or InvalidOperationException)
        {
            return new UpdateCheck(UpdateState.Failed); // Offline is not an error worth a dialog.
        }

        static string Normalise(string tag)
        {
            var t = tag.Trim();
            if (t.StartsWith('v') || t.StartsWith('V')) t = t[1..];
            var plus = t.IndexOf('+');
            return plus >= 0 ? t[..plus] : t;
        }

        AppRelease Build(JsonDocument doc, string version)
        {
            var assets = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("assets", out var list) &&
                list.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in list.EnumerateArray())
                    if (item.TryGetProperty("name", out var n) &&
                        item.TryGetProperty("browser_download_url", out var u) &&
                        n.GetString() is { Length: > 0 } name && u.GetString() is { Length: > 0 } address)
                        assets[name] = address;
            }
            return new AppRelease(
                version,
                doc.RootElement.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                doc.RootElement.TryGetProperty("html_url", out var link)
                    ? link.GetString() ?? $"https://github.com/{repo.Owner}/{repo.Repo}/releases"
                    : $"https://github.com/{repo.Owner}/{repo.Repo}/releases",
                assets);
        }
    }

    public const string ExeAsset = "AMD-NR-ReShade-Installer.exe";
    public const string SumsAsset = "SHA256SUMS.txt";

    /// <summary>Fetches the new executable and checks it against the sums the same release
    /// publishes, then puts it beside the running one. Returns where it landed.
    ///
    /// The verification is not optional: this writes a file the app is about to run as itself, so
    /// an unverified update is a remote code execution with extra steps. A release that does not
    /// publish its sums is refused rather than trusted -- that is what CanSelfUpdate is for.</summary>
    public static async Task<string> FetchAsync(HttpClient http, AppRelease release,
        IProgress<double>? progress = null, CancellationToken cancel = default)
    {
        Engine.Require(release.CanSelfUpdate,
            "That release does not publish the file and the hashes this would need. Use the link instead.");

        var sums = await http.GetStringAsync(release.Assets[SumsAsset], cancel);

        // The client's timeout ends at the headers, so a body that goes quiet is ended by this, re-armed
        // by every read -- the same minute the payload downloads get. Without it the button sat at
        // "Updating… 40%" for good.
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        stall.CancelAfter(TimeSpan.FromMinutes(1));
        byte[] bytes;
        try
        {
            using var response = await http.GetAsync(release.Assets[ExeAsset],
                HttpCompletionOption.ResponseHeadersRead, stall.Token);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength;
            await using var body = await response.Content.ReadAsStreamAsync(stall.Token);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await body.ReadAsync(chunk, stall.Token)) > 0)
            {
                stall.CancelAfter(TimeSpan.FromMinutes(1));
                buffer.Write(chunk, 0, read);
                if (total is > 0) progress?.Report((double)buffer.Length / total.Value);
            }
            bytes = buffer.ToArray();
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            throw new InstallException("The update download stopped answering. Nothing was replaced; try again.");
        }

        Engine.Require(AppUpdater.Verify(bytes, sums, ExeAsset),
            "The update downloaded, but it does not match the SHA-256 the release publishes. "
            + "Nothing was replaced.");

        var staged = Path.Combine(AppPaths.Root, $"update-{release.Version}.exe");
        await File.WriteAllBytesAsync(staged, bytes, cancel);
        return staged;
    }

    /// <summary>Swaps the staged executable in and starts it. The caller closes the window straight
    /// after: two copies of this app in one folder is the state the swap exists to pass through
    /// quickly, and the outgoing one is swept on the next start.</summary>
    public static void ApplyAndRestart(string staged)
    {
        var current = Environment.ProcessPath
                      ?? throw new InstallException("Cannot tell which file this app is running from.");
        var parked = AppUpdater.Swap(current, staged);
        try { Process.Start(new ProcessStartInfo(current, Program.AfterUpdate) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException
                                      or InvalidOperationException)
        {
            // In place and would not start: an antivirus that took the new, unsigned file, or blocked
            // it. The one that runs goes back, or closing this window would leave no app at all --
            // only the parked copy, which the next start would have swept.
            try { File.Move(parked, current, overwrite: true); }
            catch (Exception e2) when (e2 is IOException or UnauthorizedAccessException) { }
            throw new InstallException(
                $"The update was put in place but would not start ({e.Message}). That is almost always an "
                + "antivirus; the version you had is back where it was.");
        }
    }

    public static void OpenInBrowser(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or FileNotFoundException
                                      or InvalidOperationException)
        {
            // No browser association: the link is on screen either way.
        }
    }

    /// <summary>A folder in Explorer. Nobody is interrupted when there is no shell to do it.</summary>
    public static void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            // No shell association for a folder is not something to interrupt anyone over.
        }
    }
}
