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
    public string Platform => Entry.Platform == GamePlatform.Manual ? "Folder" : Entry.Platform.ToString();

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
    /// anything being stored.</summary>
    public IBrush Tile
    {
        get
        {
            var hash = Name.Aggregate(17, (acc, c) => acc * 31 + c);
            var hue = Math.Abs(hash) % 360;
            return new SolidColorBrush(new HslColor(1, hue, 0.32, 0.30).ToRgb());
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
            Raise(nameof(StateLabel));
        }
    }

    public string StateLabel => _installed ? "ON" : "—";

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
            Raise(nameof(NoRoute));
            RefreshInstalled();
        }
    }

    public string RouteLabel => _graphics?.Tag ?? "…";

    /// <summary>Detected, and nothing it supports has a route: OpenGL, a 64-bit D3D9 game.</summary>
    public bool NoRoute => _graphics is { Preset: null } g && g.All.Count > 0;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Looked for beside the game's executable as well as at the root, because that is where
    /// an Unreal install lands.</summary>
    public void RefreshInstalled() =>
        Installed = GameScanner.IsInstalled(Entry.Path)
                    || (_graphics?.Executable is { } exe && GameScanner.IsInstalled(System.IO.Path.GetDirectoryName(exe)!));

    public void RefreshRoute() => Raise(nameof(RouteLabel));
}
