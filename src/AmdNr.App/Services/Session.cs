// What every page of the app shares: the configuration, the one HTTP client, the payload list and
// whether it came off the network, the add-on releases, the API database, the update check, and
// whether something is working right now. Loaded in the background after the window is up, and
// announced as each part lands, so a page redraws what depends on it instead of waiting for all of it.

using AmdNr.Core;

namespace AmdNr.App;

public sealed class Session
{
    public AppConfig Config { get; } = AppConfig.Load();
    public HttpClient Http { get; } = PayloadCache.DefaultClient(App.Version);

    public PayloadManifest? Manifest { get; private set; }

    /// <summary>Why the published list could not be read, with every address that was tried; null
    /// when it was read. With it set, <see cref="Manifest"/> is the copy beside or inside the app.</summary>
    public string? ManifestProblem { get; private set; }

    public IReadOnlyList<AddonRelease> Releases { get; private set; } = [];
    public ApiDatabase? ApiDb { get; private set; }
    public UpdateCheck Update { get; private set; } = new(UpdateState.Checking);
    public SystemState? Machine { get; private set; }

    public event Action? ManifestChanged;
    public event Action? UpdateChanged;
    public event Action? BusyChanged;

    private bool _busy;

    /// <summary>True while an install, an uninstall, a download, an import or a scan is running.
    /// Everything that writes checks it first: two of those at once are two writers on one cache or
    /// one game folder, and the second one's result would land on the first one's screen.</summary>
    public bool Busy
    {
        get => _busy;
        set
        {
            if (_busy == value) return;
            _busy = value;
            BusyChanged?.Invoke();
        }
    }

    /// <summary>A cache that looks beside the app and in Downloads before it downloads anything.</summary>
    public PayloadCache Cache() => new(Http, PayloadCache.NearbyFolders());

    public async Task LoadManifestAsync()
    {
        try
        {
            Manifest = await Cache().FetchManifestAsync(Config.ManifestAddresses());
            ManifestProblem = null;
        }
        catch (Exception e)
        {
            // Offline, or every address filtered: the copy beside the executable, or the one inside
            // it, pins the same hashes, so everything already cached still installs.
            ManifestProblem = e.Message;
            Manifest = PayloadCache.LoadLocalManifest();
        }
        ManifestChanged?.Invoke();
    }

    public async Task LoadReleasesAsync()
    {
        try { Releases = await AddonReleases.ListAsync(Http, Config.Addon.Owner, Config.Addon.Repo); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            Releases = [];
        }
    }

    public async Task LoadApiDbAsync()
    {
        try
        {
            ApiDb = await ApiDatabase.LoadAsync(Http, Config.Payload.Owner, Config.Payload.Repo,
                Config.Payload.Branch, Config.Payload.ApiDbUrl);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException
                                      or InstallException)
        {
            ApiDb = null;
        }
    }

    public async Task<SystemState> ReadMachineAsync() => Machine ??= await Task.Run(GpuService.Read);

    public async Task CheckForUpdateAsync()
    {
        Update = new UpdateCheck(UpdateState.Checking);
        UpdateChanged?.Invoke();
        Update = await AppUpdate.CheckAsync(Http, Config.App, App.Version);
        UpdateChanged?.Invoke();
    }

    /// <summary>Installs the release the last check found, in place when it publishes what that
    /// needs and through the browser when it does not. A release without its sums cannot be
    /// verified, and this would rather hand the person a link than run unchecked bytes. Returns true
    /// when the new executable is starting and this one should close.</summary>
    public async Task<bool> ApplyUpdateAsync(IProgress<double> progress)
    {
        if (Update.Release is not { } release || Busy) return false;
        if (!release.CanSelfUpdate)
        {
            AppUpdate.OpenInBrowser(release.Url);
            return false;
        }
        // Busy for all of it: the window closes the moment the new executable starts, and an install
        // begun during the download would be cut off inside its transaction.
        Busy = true;
        try
        {
            var staged = await AppUpdate.FetchAsync(Http, release, progress);
            AppUpdate.ApplyAndRestart(staged);
            return true;
        }
        finally
        {
            Busy = false;
        }
    }
}
