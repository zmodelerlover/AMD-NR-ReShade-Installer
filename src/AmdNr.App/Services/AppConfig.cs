// Where the app looks for everything it fetches. The defaults are the real addresses, so config.json
// is an override and not a requirement: a copy of the exe on its own still knows where to look.

using System.Text.Json;
using AmdNr.Core;

namespace AmdNr.App;

// Settable, not init: the compile-time JSON reader builds an init-only object with every property,
// so one the file leaves out arrived as null instead of its default. The shipped config.json names
// only the two URLs under "Payload", and Owner was null behind them.
public sealed class RepoRef
{
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Branch { get; set; } = "main";
    public string File { get; set; } = "payload.json";

    /// <summary>A full address for the payload list, when it is not published on GitHub. Set, it
    /// is tried first; Owner/Repo/Branch/File, when set, are the second place to look.</summary>
    public string? ManifestUrl { get; set; }

    /// <summary>The same, for api-db.json.</summary>
    public string? ApiDbUrl { get; set; }
}

public sealed class AppConfig
{
    public RepoRef App { get; set; } = new() { Owner = "zmodelerlover", Repo = "AMD-NR-ReShade-Installer" };

    /// <summary>The payload list and the API database: published on Hugging Face, where the files
    /// are, and in AMD-NR-Extras on GitHub as the second place to read them from. The defaults are
    /// what the shipped config.json says, so an executable moved away from that file -- to the
    /// desktop, which is where people put it -- reads exactly the same places.</summary>
    public RepoRef Payload { get; set; } = new()
    {
        Owner = "zmodelerlover",
        Repo = "AMD-NR-Extras",
        ManifestUrl = "https://huggingface.co/datasets/zmodelerlover/amd-nr/resolve/main/payload.json",
        ApiDbUrl = "https://huggingface.co/datasets/zmodelerlover/amd-nr/resolve/main/api-db.json",
    };

    /// <summary>The add-on's own repository. Only its releases are read, to know which versions
    /// exist and what each one publishes; the files still come from release asset addresses.
    /// </summary>
    public RepoRef Addon { get; set; } = new() { Owner = "zmodelerlover", Repo = "dlss5-neural-amd" };

    /// <summary>The chat everyone is actually in, and the project the network comes from. Both are
    /// here rather than in the code because an invite can be rotated and a repository can move, and
    /// neither should need a new build of this.</summary>
    public string DiscordUrl { get; set; } = "https://discord.gg/wYhvS3JSHM";

    public string RuntimeUrl { get; set; } = "https://github.com/danielblnc/DLSS-NR-on-AMD";

    /// <summary>Where someone can put money in if they want to. Nothing is behind either of them.
    /// </summary>
    public string KofiUrl { get; set; } = "https://ko-fi.com/proceduralnilo";

    public string VakinhaUrl { get; set; } =
        "https://www.vakinha.com.br/vaquinha/open-source-dlss-amd-nr";

    /// <summary>Every address the payload list can be read from, best first.</summary>
    public IEnumerable<string> ManifestAddresses()
    {
        if (!string.IsNullOrWhiteSpace(Payload.ManifestUrl)) yield return Payload.ManifestUrl;
        if (Payload.Owner.Length > 0 && Payload.Repo.Length > 0)
            yield return $"https://raw.githubusercontent.com/{Payload.Owner}/{Payload.Repo}/{Payload.Branch}/{Payload.File}";
    }

    public static AppConfig Load()
    {
        // The copy next to the exe is the shipped default; one in %AppData% wins, so a mirror can be
        // pointed somewhere else without a new build.
        foreach (var path in new[]
                 {
                     Path.Combine(AppPaths.Root, "config.json"),
                     Path.Combine(AppContext.BaseDirectory, "config.json"),
                 })
        {
            try
            {
                if (File.Exists(path) && JsonSerializer.Deserialize(File.ReadAllText(path), AppJson.Default.AppConfig) is { } c)
                    return c;
            }
            catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
            {
                // A broken config must not stop the app: fall through to the next one, then defaults.
            }
        }
        return new AppConfig();
    }
}
