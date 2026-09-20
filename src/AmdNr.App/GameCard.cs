using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using AmdNr.Core;

namespace AmdNr.App;

/// <summary>One tile in the grid. The persisted entry is the truth; this is what it looks like,
/// including the parts that arrive later -- the cover downloads in the background and the install
/// state is re-read from the folder whenever something touches it.</summary>
public sealed class GameCard(GameEntry entry) : INotifyPropertyChanged
{
    public GameEntry Entry { get; } = entry;

    public string Name => Entry.Display;
    public string Path => Entry.Path;
    public string Platform => Entry.Platform.ToString();

    /// <summary>Added by hand. The tile says "folder" in the current language instead of the enum name.</summary>
    public bool IsManual => Entry.Platform == GamePlatform.Manual;
    public bool IsLauncher => !IsManual;

    /// <summary>Two letters for the tile that stands in for a cover. Words like "The" carry no
    /// information, so the first two that do are used.</summary>
    public string Initials
    {
        get
        {
            var words = Name.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(w => char.IsLetterOrDigit(w[0]))
                .Where(w => w.Length > 2 || words_keep.Contains(w, StringComparer.OrdinalIgnoreCase))
                .Take(2).ToList();
            if (words.Count == 0) return Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";
            return string.Concat(words.Select(w => char.ToUpperInvariant(w[0])));
        }
    }

    private static readonly string[] words_keep = ["GTA", "NFS", "DMC"];

    /// <summary>A colour derived from the name, so a game keeps the same tile every time without
    /// anything being stored. A diagonal fade reads as a designed placeholder, where a flat fill
    /// read as a cover that failed to load.</summary>
    public IBrush Tile
    {
        get
        {
            var hash = Name.Aggregate(17, (acc, c) => acc * 31 + c);
            var hue = Math.Abs(hash) % 360;
            return new LinearGradientBrush
            {
                StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
                EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(new HslColor(1, hue, 0.38, 0.34).ToRgb(), 0),
                    new GradientStop(new HslColor(1, (hue + 30) % 360, 0.40, 0.13).ToRgb(), 1),
                },
            };
        }
    }

    private Bitmap? _cover;
    public Bitmap? Cover
    {
        get => _cover;
        set
        {
            _cover = value;
            Raise();
            Raise(nameof(HasCover));
            Raise(nameof(NoCover));
        }
    }

    public bool HasCover => _cover is not null;
    public bool NoCover => _cover is null;

    private bool _installed;
    public bool Installed
    {
        get => _installed;
        set
        {
            _installed = value;
            Raise();
        }
    }

    private bool _selected;

    /// <summary>The tile whose drawer is open, outlined so the grid and the drawer read as one.</summary>
    public bool IsSelected
    {
        get => _selected;
        set { _selected = value; Raise(); }
    }

    private bool _pulsing;
    public bool Pulsing
    {
        get => _pulsing;
        private set { _pulsing = value; Raise(); }
    }

    /// <summary>Plays the badge's pulse once. The class is dropped again afterwards so the next
    /// install can play it a second time.</summary>
    public async void Pulse()
    {
        Pulsing = true;
        await Task.Delay(800);
        Pulsing = false;
    }

    private GraphicsDetection? _graphics;

    /// <summary>What the game renders with, once it has been read. Null until then, which the tile
    /// shows as "…" rather than a guess.</summary>
    public GraphicsDetection? Graphics
    {
        get => _graphics;
        set
        {
            _graphics = value;
            Raise();
            Raise(nameof(RouteLabel));
            Raise(nameof(RouteTags));
            Raise(nameof(UnknownApi));
            Raise(nameof(NoRoute));
            RefreshInstalled();
        }
    }

    public string RouteLabel => _graphics?.Tag ?? "…";

    /// <summary>The same label as separate chips: each API on its own, the architecture quieter, all
    /// of them red when none has a route. Nothing yet while detection is still running.</summary>
    public IReadOnlyList<RouteTag> RouteTags => _graphics switch
    {
        null => [new RouteTag("…", Quiet: true, Bad: false)],
        { All.Count: 0 } => [],
        var g => [
            .. g.All.Select(a => new RouteTag(GraphicsDetection.Short(a), Quiet: false, Bad: NoRoute)),
            .. g.Width == Route.X86 ? [new RouteTag("32-bit", Quiet: true, Bad: false)] : Array.Empty<RouteTag>(),
        ],
    };

    /// <summary>Read, and nothing recognisable found. Shown as a word in the current language.</summary>
    public bool UnknownApi => _graphics is { All.Count: 0 };

    /// <summary>Detected, and nothing it supports has a route: a 64-bit D3D9 game, a 32-bit
    /// OpenGL or D3D12 one.</summary>
    public bool NoRoute => _graphics is { Preset: null } g && g.All.Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Looked for beside the folder the install writes into as well as at the root: an
    /// Unreal install lands in Binaries\Win64 and a Source one in bin\, and neither is the root.</summary>
    public void RefreshInstalled() =>
        Installed = GameScanner.IsInstalled(Entry.Path)
                    || (_graphics?.Target is { } target
                        && GameScanner.IsInstalled(System.IO.Path.GetDirectoryName(target)!));

    public void RefreshRoute()
    {
        Raise(nameof(RouteLabel));
        Raise(nameof(RouteTags));
    }
}

public sealed record RouteTag(string Text, bool Quiet, bool Bad);
