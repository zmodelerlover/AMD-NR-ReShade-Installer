// What to download, from where, and what it has to hash to.
//
// This is the file that replaces the constants the Rust installer compiled in. It lives in the
// content repository rather than in the app, so a new runtime build is a release there and not a
// release here -- but nothing is ever written to a game folder without matching one of these
// hashes, so moving it out of the binary costs no guarantee.
//
// It is fetched from raw.githubusercontent.com and the files come from release asset URLs, neither
// of which touches the GitHub REST API. That is deliberate: the API is rate-limited per IP and
// unauthenticated users share it, so the download path stays clear of it entirely and only the
// update check spends a call.

using System.Text.Json;
using System.Text.Json.Serialization;

namespace AmdNr.Core;

public sealed class PayloadFile
{
    /// <summary>The file's name, which is also the name it takes in the game folder when it is one
    /// of the files that gets installed.</summary>
    public required string Name { get; init; }

    /// <summary>The release asset it is published as, when that differs from Name. GitHub flattens
    /// asset names, so the bridge pair is published loose and put back under files\ by Path.</summary>
    public string? Asset { get; init; }

    /// <summary>Where it goes inside the component folder, relative, with forward slashes. Defaults
    /// to Name. The x86 bridge needs its pair under files\ next to payload.sha256, because that is
    /// the shape its installer reads.</summary>
    public string? Path { get; init; }

    public required ulong Size { get; init; }
    public required string Sha256 { get; init; }

    /// <summary>A full https address, for a file that is not published on GitHub at all -- ReShade's
    /// own installer, fetched from reshade.me exactly as a person would download it.</summary>
    public string? Url { get; init; }

    /// <summary>Further addresses for the same bytes, tried in order when the first one cannot be
    /// reached. Every one of them is checked against the same SHA-256, so a mirror can be anywhere
    /// and is never trusted further than the hash -- which is what makes it safe to host these on a
    /// free service and add a second one later without shipping a new build.</summary>
    public List<string>? Mirrors { get; init; }

    [JsonIgnore]
    public string AssetName => Asset ?? Name;

    [JsonIgnore]
    public string RelativePath => Path ?? Name;
}

public sealed class PayloadComponent
{
    public required string Version { get; init; }
    public required List<PayloadFile> Files { get; init; }

    /// <summary>Owner, repository and tag the assets are published under. Absent means "the same
    /// release this manifest came from".</summary>
    public string? Owner { get; init; }
    public string? Repo { get; init; }
    public string? Tag { get; init; }

    /// <summary>Files taken out of the downloaded archive, each pinned by its own hash. The archive
    /// is only the envelope: what gets installed is what is listed here.</summary>
    public List<PayloadFile>? Extract { get; init; }

    /// <summary>When this version was published, as yyyy-MM-dd, for the version menu. Optional.</summary>
    public string? Published { get; init; }

    /// <summary>What an install reads from this component: the extracted files when there are any,
    /// otherwise the downloaded ones.</summary>
    [JsonIgnore]
    public IReadOnlyList<PayloadFile> Installed => Extract is { Count: > 0 } ? Extract : Files;
}

/// <summary>Another version of a component, with whatever else that version needs to install. The
/// components listed here take the place of the manifest's own for as long as this version is the
/// one chosen.</summary>
public sealed class ComponentRelease
{
    public required string Version { get; init; }
    public required Dictionary<string, PayloadComponent> Components { get; init; }
}

public sealed class PayloadManifest
{
    public int Schema { get; init; }
    public string? Owner { get; init; }
    public string? Repo { get; init; }
    public string? Tag { get; init; }
    public required Dictionary<string, PayloadComponent> Components { get; init; }

    /// <summary>Further versions of a component, keyed by that component's name. Kept out of
    /// <see cref="Components"/> on purpose: an app from before this list downloads every component
    /// it finds there in its first-run wizard and installs the one version it knows, so what only a
    /// newer app can install lives here, where an older one never looks.</summary>
    public Dictionary<string, List<ComponentRelease>>? Releases { get; init; }

    public const string AddonComponent = "addon";
    public const string RuntimeComponent = "runtime";
    public const string X86ExtrasComponent = "x86-extras";
    public const string BridgeComponent = "bridge";
    public const string ReShadeComponent = "reshade";
    public const string ShaderComponent = "shader";

    /// <summary>The OptiScaler route: the release archive with the files taken out of it, and the
    /// runtime build OptiScaler recognises, which is not the one the add-on pins.</summary>
    public const string OptiScalerComponent = "optiscaler";
    public const string OptiRuntimeComponent = "opti-runtime";

    /// <summary>The lmxxf runtime's weights, which OptiScaler 0.2.0 and later can drive instead of
    /// the danielblnc runtime. Only ever inside a release: no version listed in Components uses them.</summary>
    public const string LmxxfWeightsComponent = "lmxxf-weights";

    /// <summary>Components fetched only when a route that uses them is installed. The OptiScaler
    /// archive alone is 132 MB, so downloading it in the first-run wizard for everybody, most of
    /// whom run the ReShade route, would be the largest download the app makes, spent on nothing.</summary>
    public static readonly IReadOnlySet<string> OnDemand =
        new HashSet<string>(StringComparer.Ordinal) { OptiScalerComponent, OptiRuntimeComponent, LmxxfWeightsComponent };

    /// <summary>The components worth having before anybody asks: everything but <see cref="OnDemand"/>.</summary>
    public IEnumerable<KeyValuePair<string, PayloadComponent>> Everyday =>
        Components.Where(pair => !OnDemand.Contains(pair.Key));

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static PayloadManifest Parse(string json)
    {
        PayloadManifest? m;
        try { m = JsonSerializer.Deserialize<PayloadManifest>(json, Options); }
        catch (JsonException e) { throw new InstallException($"The payload manifest is not readable: {e.Message}"); }

        Engine.Require(m is not null, "The payload manifest is empty.");
        Engine.Require(m!.Schema == 1, $"Payload manifest schema {m.Schema} is newer than this app understands. Update it.");
        Engine.Require(m.Components.Count > 0, "The payload manifest lists no components.");

        foreach (var (name, component) in m.Components) Check(name, component);

        foreach (var (name, releases) in m.Releases ?? new Dictionary<string, List<ComponentRelease>>())
            foreach (var release in releases)
            {
                Engine.Require(AddonReleases.Version(release.Version) is not null,
                    $"A release of {name} has an unusable version: '{release.Version}'");
                Engine.Require(release.Components.TryGetValue(name, out var own) && own.Version == release.Version,
                    $"Release {release.Version} of {name} does not carry {name} {release.Version} itself.");
                foreach (var (inner, component) in release.Components) Check(inner, component);
            }
        return m;
    }

    private static void Check(string name, PayloadComponent component)
    {
        Engine.Require(component.Files.Count > 0, $"Component {name} lists no files.");
        foreach (var file in component.Files.Concat(component.Extract ?? []))
        {
            Engine.Require(file.Url is null || file.Url.StartsWith("https://", StringComparison.Ordinal),
                $"Component {name} gives {file.Name} an address that is not https.");
            foreach (var mirror in file.Mirrors ?? [])
                Engine.Require(mirror.StartsWith("https://", StringComparison.Ordinal),
                    $"Component {name} gives {file.Name} a mirror that is not https.");
            // The manifest is remote data and it names the path this writes to, so the path is
            // checked here. Whether a file may be copied into a *game* folder is a separate
            // question, answered by Transaction.Apply against Engine.IsAllowed -- the cache also
            // holds files that are only ever read, like the bridge's payload.sha256.
            CheckRelativePath(name, file.RelativePath);
            CheckPlainName(name, file.AssetName);
            Engine.Require(Engine.IsHex(file.Sha256, 64),
                $"Component {name} has no usable SHA-256 for {file.Name}");
            Engine.Require(file.Size > 0, $"Component {name} gives {file.Name} a size of zero");
        }
    }

    /// <summary>Every version of a component this manifest can install, newest first: the one
    /// <see cref="Components"/> pins, and each one in <see cref="Releases"/>. Empty when the
    /// manifest has no such component at all.</summary>
    public IReadOnlyList<ComponentRelease> Offered(string component)
    {
        var offered = new List<ComponentRelease>();
        if (Components.TryGetValue(component, out var own))
            offered.Add(new ComponentRelease
            {
                Version = own.Version,
                Components = new Dictionary<string, PayloadComponent>(StringComparer.Ordinal) { [component] = own },
            });
        if (Releases?.TryGetValue(component, out var more) == true)
            offered.AddRange(more.Where(r => offered.All(o => o.Version != r.Version)));
        offered.Sort((a, b) => (AddonReleases.Version(b.Version) ?? new Version()).CompareTo(
            AddonReleases.Version(a.Version) ?? new Version()));
        return offered;
    }

    /// <summary>This manifest with one release's components in place of its own.</summary>
    public PayloadManifest With(ComponentRelease release)
    {
        var components = new Dictionary<string, PayloadComponent>(Components, StringComparer.Ordinal);
        foreach (var (name, component) in release.Components) components[name] = component;
        return new PayloadManifest
        {
            Schema = Schema,
            Owner = Owner,
            Repo = Repo,
            Tag = Tag,
            Components = components,
            Releases = Releases,
        };
    }

    /// <summary>This manifest at the newest version of a component: what an install gets when
    /// nobody picked a version, and what "out of date" is measured against.</summary>
    public PayloadManifest Newest(string component) =>
        Offered(component) is { Count: > 0 } offered ? With(offered[0]) : this;

    /// <summary>One or two plain segments, forward slashes only, nothing that climbs.</summary>
    private static void CheckRelativePath(string component, string path)
    {
        Engine.Require(path.Length is > 0 and <= 128, $"Component {component} has an unusable path: '{path}'");
        Engine.Require(!path.Contains('\\'), $"Component {component} path must use forward slashes: '{path}'");
        Engine.Require(!System.IO.Path.IsPathRooted(path), $"Component {component} path must be relative: '{path}'");
        var segments = path.Split('/');
        Engine.Require(segments.Length <= 2, $"Component {component} path is too deep: '{path}'");
        foreach (var segment in segments) CheckPlainName(component, segment);
    }

    private static void CheckPlainName(string component, string name)
    {
        Engine.Require(name.Length is > 0 and <= 96, $"Component {component} has an unusable name: '{name}'");
        Engine.Require(name is not ("." or ".."), $"Component {component} has an unusable name: '{name}'");
        Engine.Require(name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) < 0,
            $"Component {component} has an unusable name: '{name}'");
    }

    /// <summary>Whether this manifest carries a component at all. Component() throws, which is
    /// right for one an install cannot do without, and wrong for one added later: a manifest
    /// published before the companion effect has no 'shader' entry, and asking for it by name
    /// failed the whole install rather than skipping a file the add-on works without.</summary>
    public bool Has(string name) => Components.ContainsKey(name);

    public PayloadComponent Component(string name)
    {
        Engine.Require(Components.TryGetValue(name, out var component),
            $"The payload manifest has no '{name}' component.");
        return Components[name];
    }

    /// <summary>Every address one file can be fetched from, best first: its own, then its mirrors,
    /// then the release asset it would be published as. All of them are checked against the same
    /// SHA-256, so trying the next one costs nothing but time.</summary>
    public IReadOnlyList<Uri> DownloadUrls(string componentName, PayloadFile file)
    {
        var urls = new List<Uri>();
        if (file.Url is not null) urls.Add(new Uri(file.Url));
        foreach (var mirror in file.Mirrors ?? []) urls.Add(new Uri(mirror));

        var component = Component(componentName);
        var owner = component.Owner ?? Owner;
        var repo = component.Repo ?? Repo;
        var tag = component.Tag ?? Tag;
        if (!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo) && !string.IsNullOrWhiteSpace(tag))
            urls.Add(new Uri($"https://github.com/{owner}/{repo}/releases/download/{tag}/{file.AssetName}"));

        Engine.Require(urls.Count > 0, $"The payload manifest does not say where '{componentName}' is published.");
        return urls;
    }

    /// <summary>Where one file is published, best address first.</summary>
    public Uri DownloadUrl(string componentName, PayloadFile file) => DownloadUrls(componentName, file)[0];

    /// <summary>The hashes and sizes an install checks against, taken from the manifest rather than
    /// from constants.</summary>
    public PayloadPins Pins()
    {
        var addon = Single(AddonComponent, Work.AddonName);
        var runtime = Single(RuntimeComponent, Work.RuntimeName);
        var weights = Single(RuntimeComponent, Work.WeightsName);
        var optionalShader = Components.TryGetValue(ShaderComponent, out var sh)
            ? sh.Files.FirstOrDefault(f => f.Name == Work.ShaderName)
            : null;
        // Optional like the shader: a manifest published before the OptiScaler route has neither,
        // and the other routes must keep installing from it.
        // The lmxxf weights go in with OptiScaler itself, so they are pinned the same way.
        var optiFiles = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var name in new[] { OptiScalerComponent, LmxxfWeightsComponent })
            if (Components.TryGetValue(name, out var component))
                foreach (var f in component.Installed)
                    optiFiles[f.RelativePath] = Engine.Lower(f.Sha256);
        var optiRuntime = Components.TryGetValue(OptiRuntimeComponent, out var optiRt) ? optiRt.Files.FirstOrDefault() : null;
        return new PayloadPins
        {
            AddonSha = addon.Sha256,
            AddonSize = addon.Size,
            RuntimeSha = runtime.Sha256,
            RuntimeSize = runtime.Size,
            WeightsSha = weights.Sha256,
            WeightsSize = weights.Size,
            // Optional on purpose: a manifest published before the companion effect was
            // installable has no shader component, and an install from one must still work.
            ShaderSha = optionalShader?.Sha256 ?? string.Empty,
            ShaderSize = optionalShader?.Size ?? 0,
            OptiFiles = optiFiles,
            OptiRuntimeName = optiRuntime?.RelativePath ?? string.Empty,
            OptiRuntimeSha = optiRuntime is null ? string.Empty : Engine.Lower(optiRuntime.Sha256),
            OptiRuntimeSize = optiRuntime?.Size ?? 0,
        };
    }

    private PayloadFile Single(string componentName, string fileName)
    {
        var file = Component(componentName).Files.FirstOrDefault(f => f.Name == fileName);
        Engine.Require(file is not null, $"The payload manifest's '{componentName}' does not include {fileName}.");
        return file!;
    }
}
