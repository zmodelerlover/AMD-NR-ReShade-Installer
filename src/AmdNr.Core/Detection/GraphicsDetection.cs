// What the detection concluded about a game, with the evidence for it, and the route that follows
// from it. How it is read off the files is GraphicsDetector.

namespace AmdNr.Core;

public enum GraphicsApi
{
    Unknown,
    D3D8,
    D3D9,
    D3D11,
    D3D12,
    Vulkan,
    OpenGL,
}

public sealed record GraphicsDetection(
    string? Executable,
    Route? Width,
    GraphicsApi Api,
    bool AlsoD3D11,
    string Why)
{
    /// <summary>Every API the game can render with, best source first. Empty means only
    /// <see cref="Api"/> is known.</summary>
    public IReadOnlyList<GraphicsApi> Supported { get; init; } = [];

    /// <summary>"PCGamingWiki" when the supported list came from there, null when it came from the
    /// game's own files.</summary>
    public string? Source { get; init; }

    /// <summary>The emulator this folder holds, when it is one this app knows. An emulator links
    /// every renderer it can offer, so its import table cannot pick a route; this can, and it also
    /// carries the sentence naming the setting that actually decides it.</summary>
    public EmulatorInfo? Emulator { get; init; }

    /// <summary>The file whose folder the install has to write into, when that is not the folder the
    /// executable is in. Null means they are the same, which is the ordinary case.
    ///
    /// Source is why this exists. hl2.exe sits in the root and imports nothing; the module that
    /// creates the D3D9 device is bin\shaderapidx9.dll, and Source loads bin\ with
    /// LOAD_WITH_ALTERED_SEARCH_PATH, so that module resolves its own d3d9.dll against bin\ and
    /// never looks at the root. ReShade in the root is never loaded, and ReShade in bin\ searches
    /// bin\ for add-ons. Everything has to go there together.</summary>
    public string? InstallTarget { get; init; }

    /// <summary>The DLSS, FSR or XeSS files the game ships, by name. Empty when none
    /// were found, which is what decides whether a game that also runs D3D11 is recommended the
    /// OptiScaler route: OptiScaler only has something to do in a game that calls one of them.</summary>
    public IReadOnlyList<string> Upscalers { get; init; } = [];

    /// <summary>What an install is pointed at: the renderer's folder when those differ, the
    /// executable otherwise.</summary>
    public string? Target => InstallTarget ?? Executable;

    public IReadOnlyList<GraphicsApi> All =>
        Supported.Count > 0 ? Supported
        : Api == GraphicsApi.Unknown ? []
        : AlsoD3D11 ? [Api, GraphicsApi.D3D11] : [Api];

    /// <summary>The order this add-on would rather run on. D3D11 first because it is the only route
    /// where the game's own depth and motion reach the network; D3D12, Vulkan and OpenGL get colour
    /// only. OpenGL sits behind Vulkan because it is the newest of the three and has been proved on
    /// fewer hosts, not because it is worse per frame -- where a game offers both, Vulkan is the
    /// one with the miles on it.</summary>
    private static readonly GraphicsApi[] Preference =
    [
        GraphicsApi.D3D11, GraphicsApi.D3D12, GraphicsApi.Vulkan, GraphicsApi.OpenGL,
        GraphicsApi.D3D9, GraphicsApi.D3D8,
    ];

    public static Preset? RouteFor(Route? width, GraphicsApi api) => (width, api) switch
    {
        (Route.X64, GraphicsApi.D3D11) => Core.Preset.Dx11,
        (Route.X64, GraphicsApi.D3D12) => Core.Preset.Dx12,
        (Route.X64, GraphicsApi.Vulkan) => Core.Preset.Vulkan,
        // 64-bit only: the 32-bit pair has a D3D8, D3D9 and D3D11 frontend and no OpenGL one, so a
        // 32-bit OpenGL game falls through to null and is told so.
        (Route.X64, GraphicsApi.OpenGL) => Core.Preset.OpenGL,
        (Route.X86, GraphicsApi.D3D11) => Core.Preset.X86Dx11,
        (Route.X86, GraphicsApi.D3D9) => Core.Preset.X86Dx9,
        (Route.X86, GraphicsApi.D3D8) => Core.Preset.X86Dx8,
        _ => null,
    };

    /// <summary>The best route among everything the game supports, or null when none of it has one
    /// -- a 64-bit D3D9 game, a 32-bit D3D12 or OpenGL one, a software renderer.</summary>
    public Preset? Preset
    {
        get
        {
            // A known emulator names its own route. Without this the import table decides, and an
            // emulator links every renderer at once, so the answer was whichever this loop hit
            // first -- which is how the PCSX2 and RPCS3 routes were being overwritten with D3D11.
            if (Emulator is not null) return ReShadeRoute;

            // The OptiScaler route rather than a ReShade one for a game whose best route is D3D12,
            // and for one that runs D3D11 and D3D12 and ships an upscaler: on D3D12 the add-on sees
            // only the finished frame, while OptiScaler runs the network inside the game's upscaler
            // call with its depth and motion. A D3D11 game with no upscaler keeps the D3D11 ReShade
            // route, the one where the add-on gets depth and motion of its own; every ReShade route
            // stays in the list either way.
            return ReShadeRoute is { } route
                   && (route == Core.Preset.Dx12
                       || (route == Core.Preset.Dx11 && All.Contains(GraphicsApi.D3D12) && Upscalers.Count > 0))
                ? Core.Preset.OptiScaler
                : ReShadeRoute;
        }
    }

    /// <summary>The best ReShade route for this game, whichever route is recommended overall. The
    /// sheet asks for it when somebody picks ReShade on a game that was recommended OptiScaler: the
    /// API list then opens on the one this game would run best on, not on the first in the list.</summary>
    public Preset? ReShadeRoute
    {
        get
        {
            if (Emulator is { } emulator)
                return emulator.Route ?? RouteFor(Width, emulator.Best);
            foreach (var api in Preference)
                if (All.Contains(api) && RouteFor(Width, api) is { } route)
                    return route;
            return null;
        }
    }

    /// <summary>Whether this game can run the OptiScaler route at all: a 64-bit build that renders
    /// with D3D12. Unknown counts as yes -- a game nothing could be read from is not ruled out.</summary>
    public bool CanRunOptiScaler =>
        All.Count == 0 || (Width != Route.X86 && All.Contains(GraphicsApi.D3D12));

    /// <summary>The API that route runs on. OptiScaler runs the network on D3D12, so a game that
    /// offers both is told to switch to that one.</summary>
    public GraphicsApi Recommended =>
        Preset == Core.Preset.OptiScaler ? GraphicsApi.D3D12
        : Emulator?.Best ?? Preference.FirstOrDefault(api => All.Contains(api) && RouteFor(Width, api) is not null);

    /// <summary>When the game offers more than one API and the best route is not the only one, the
    /// game has to be told which to use -- which is the difference between "it works" and "it does
    /// nothing because the game started on D3D12".</summary>
    public bool NeedsRendererSwitch =>
        Preset is not null && All.Count(a => RouteFor(Width, a) is not null) > 1;

    /// <summary>A short label for a tile: "DX11 · DX12", "DX9 · 32-bit", "Vulkan".</summary>
    public string Tag
    {
        get
        {
            var apis = All.Count == 0 ? "?" : string.Join(" · ", All.Select(Short));
            return Width == Route.X86 ? $"{apis} · 32-bit" : apis;
        }
    }

    public static string Short(GraphicsApi api) => api switch
    {
        GraphicsApi.D3D8 => "DX8",
        GraphicsApi.D3D9 => "DX9",
        GraphicsApi.D3D11 => "DX11",
        GraphicsApi.D3D12 => "DX12",
        GraphicsApi.Vulkan => "Vulkan",
        GraphicsApi.OpenGL => "OpenGL",
        _ => "?",
    };

    /// <summary>What PCGamingWiki says replaces what the files suggested about *which* APIs exist;
    /// the executable, and so the bitness, still comes from the files, because only the installed
    /// copy can say which build is on this disk.</summary>
    public GraphicsDetection With(PcgwApi? wiki)
    {
        if (wiki is null || wiki.Supported.Count == 0) return this;

        // A record that says this build does not exist is not a record of this copy. The wiki page
        // for a remaster carries the same title as the original, and one of them being 64-bit D3D12
        // while the other is 32-bit D3D9 is exactly how a 32-bit game was told to install the D3D12
        // route. Where the record and the executable contradict each other, the executable is the
        // one that was measured on this disk, so it stands and the record is left out.
        if ((Width == Route.X86 && wiki.Has32Bit == false) ||
            (Width == Route.X64 && wiki.Has64Bit == false))
            return this with
            {
                Why = $"{Why} PCGamingWiki ({wiki.Page}) describes a build this is not, so what the "
                      + "files say stands.",
            };

        var ordered = wiki.Supported
            .OrderBy(a => Array.IndexOf(Preference, a) is var i && i < 0 ? 99 : i)
            .ToList();

        var width = Width ?? (wiki.Has64Bit == true ? Route.X64 : wiki.Has32Bit == true ? Route.X86 : null);
        var local = Api == GraphicsApi.Unknown ? "" : $" The executable itself links {Short(Api)}.";
        // A layout note is not something the wiki can know or replace: it says which folder the
        // files go in, which is the difference between an install that loads and one that does not.
        var layout = InstallTarget is null ? "" : $" {Why}";
        return this with
        {
            Width = width,
            Supported = ordered,
            Source = "PCGamingWiki",
            Why = $"PCGamingWiki ({wiki.Page}): {string.Join(", ", ordered.Select(Short))}.{local}{layout}",
        };
    }
}
