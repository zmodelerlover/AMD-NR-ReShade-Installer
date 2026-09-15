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
}

public sealed class PayloadManifest
{
    public int Schema { get; init; }
    public string? Owner { get; init; }
    public string? Repo { get; init; }
    public string? Tag { get; init; }
    public required Dictionary<string, PayloadComponent> Components { get; init; }

    public const string AddonComponent = "addon";
    public const string RuntimeComponent = "runtime";
    public const string X86ExtrasComponent = "x86-extras";
    public const string BridgeComponent = "bridge";
    public const string ReShadeComponent = "reshade";

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

        foreach (var (name, component) in m.Components)
        {
            Engine.Require(component.Files.Count > 0, $"Component {name} lists no files.");
            foreach (var file in component.Files)
            {
                // The manifest is remote data and it names the path this writes to, so the path is
                // checked here. Whether a file may be copied into a *game* folder is a separate
                // question, answered by Transaction.Apply against Engine.Allowed -- the cache also
                // holds files that are only ever read, like the bridge's payload.sha256.
                CheckRelativePath(name, file.RelativePath);
                CheckPlainName(name, file.AssetName);
                Engine.Require(Engine.IsHex(file.Sha256, 64),
                    $"Component {name} has no usable SHA-256 for {file.Name}");
                Engine.Require(file.Size > 0, $"Component {name} gives {file.Name} a size of zero");
            }
        }
        return m;
    }

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

    public PayloadComponent Component(string name)
    {
        Engine.Require(Components.TryGetValue(name, out var component),
            $"The payload manifest has no '{name}' component.");
        return Components[name];
    }

    /// <summary>Where one file is published. A release asset URL, which is served by a CDN and
    /// supports Range requests, so an interrupted 141 MB download resumes instead of restarting.</summary>
    public Uri DownloadUrl(string componentName, PayloadFile file)
    {
        var component = Component(componentName);
        var owner = component.Owner ?? Owner;
        var repo = component.Repo ?? Repo;
        var tag = component.Tag ?? Tag;
        Engine.Require(!string.IsNullOrWhiteSpace(owner) && !string.IsNullOrWhiteSpace(repo)
                       && !string.IsNullOrWhiteSpace(tag),
            $"The payload manifest does not say where '{componentName}' is published.");
        return new Uri($"https://github.com/{owner}/{repo}/releases/download/{tag}/{file.AssetName}");
    }

    /// <summary>The hashes and sizes an install checks against, taken from the manifest rather than
    /// from constants.</summary>
    public PayloadPins Pins()
    {
        var addon = Single(AddonComponent, Work.AddonName);
        var runtime = Single(RuntimeComponent, Work.RuntimeName);
        var weights = Single(RuntimeComponent, Work.WeightsName);
        return new PayloadPins
        {
            AddonSha = addon.Sha256,
            AddonSize = addon.Size,
            RuntimeSha = runtime.Sha256,
            RuntimeSize = runtime.Size,
            WeightsSha = weights.Sha256,
            WeightsSize = weights.Size,
        };
    }

    private PayloadFile Single(string componentName, string fileName)
    {
        var file = Component(componentName).Files.FirstOrDefault(f => f.Name == fileName);
        Engine.Require(file is not null, $"The payload manifest's '{componentName}' does not include {fileName}.");
        return file!;
    }
}
