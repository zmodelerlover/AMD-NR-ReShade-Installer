// The sheet a game opens: what was detected about it, which of the two routes to install it with and
// what that route needs to know, and the install itself. Split by what each part does -- the routes
// (GameSheet.Routes.cs), the install and uninstall (GameSheet.Actions.cs), the smaller things around
// them (GameSheet.Tools.cs) and what the sheet says about all of it (GameSheet.Verdict.cs); this
// part opens and closes it and reads the game.

using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AmdNr.Core;

namespace AmdNr.App;

public partial class GameSheet : UserControl
{
    private MainWindow _shell = null!;
    private GameCard? _card;
    private readonly ObservableCollection<ReportLine> _report = [];

    /// <summary>What the width detection said about the open game, kept so the route panel can say
    /// when the chosen route disagrees with it.</summary>
    private Detected _detected = Detected.Unknown;

    public GameSheet()
    {
        InitializeComponent();
        ReportList.ItemsSource = _report;
        _report.CollectionChanged += (_, _) =>
        {
            ReportEmpty.IsVisible = _report.Count == 0;
            DetailsCount.Text = _report.Count == 0 ? "" : Ui.Format("Str.DetailsCount", _report.Count);
        };
        DrawerPanel.SizeChanged += (_, e) =>
            // A short window cannot fit the cover and the choice both, and the choice is the point.
            CoverBox.IsVisible = e.NewSize.Height >= 640;
    }

    private Session Session => _shell.Session;
    private Library Library => _shell.Library;

    /// <summary>The game whose sheet is open, or null.</summary>
    public GameCard? Card => _card;

    public bool IsOpen => Drawer.Classes.Contains("open");

    public void Attach(MainWindow shell)
    {
        _shell = shell;
        Session.BusyChanged += () => Lock(Session.Busy);
        Session.ManifestChanged += () =>
        {
            if (_card is { } card && !Session.Busy) Show(card);
        };
    }

    public void Open(GameCard card)
    {
        if (_card is not null && _card != card) _card.IsSelected = false;
        _card = card;
        card.IsSelected = true;
        DrawerHeader.DataContext = card;
        if (!IsOpen)
        {
            IsVisible = true;
            // One layout pass while visible before the class lands, or the transition has no
            // starting frame and the sheet just appears.
            Dispatcher.UIThread.Post(() =>
            {
                Drawer.Classes.Set("open", true);
                // Focus goes into the sheet, so Tab walks its controls rather than the grid behind it.
                CloseButton.Focus();
            }, DispatcherPriority.Render);
        }
        Show(card);
    }

    public async void Close()
    {
        if (_card is not null) _card.IsSelected = false;
        _card = null;
        Drawer.Classes.Set("open", false);
        await Task.Delay(220);
        // Reopened while it was sliding out: leave it be.
        if (!IsOpen) IsVisible = false;
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    /// <summary>A click on the dimmed grid around the sheet closes it, as it would any dialog.</summary>
    private void OnBackdropPressed(object? sender, PointerPressedEventArgs e) => Close();

    /// <summary>Everything the sheet says about a game, from what was detected to the pre-flight.
    /// Run again whenever something it depends on changes: the language, the payload list, the
    /// executable, the route.</summary>
    public void Show(GameCard card)
    {
        if (_card != card) return;
        var graphics = card.Graphics ?? GraphicsDetector.Detect(card.Path, card.Entry.Name, card.Entry.Executable);
        card.Graphics ??= graphics;

        ApiTag.Text = graphics.All.Count == 0 ? Ui.Text("Str.UnknownApi") : graphics.Tag;
        DetectedLine.Text = graphics.Why;
        ShowExecutable(graphics, card.Entry);
        ShowApiHint(graphics);

        Lock(Session.Busy);
        Ui.Localize(PlayLabel, graphics.Emulator is not null ? "Str.Launch" : "Str.Play");

        // The width comes from the executable the detection picked, so a folder holding a 32-bit
        // launcher beside a 64-bit game is decided by the game and not by the launcher.
        _detected = graphics.Width is { } width
            ? Detected.On(width, Path.GetFileName(graphics.Executable ?? card.Path))
            : Work.Detect(card.Path);

        ShowRoutes(card, graphics);
        _ = RefreshAsync();
    }

    /// <summary>Which file the width and the API were read off, and the way to change it. It is shown
    /// because it is the one input everything else is derived from and the one the app can get wrong
    /// on its own: a folder with a launcher in the root and the game in Bin64 is read as the
    /// launcher, and every route offered after that is for the wrong architecture.</summary>
    private void ShowExecutable(GraphicsDetection graphics, GameEntry entry)
    {
        var chosen = entry.Executable is { Length: > 0 };
        var exe = graphics.Executable;
        var width = exe is null ? null : Engine.MachineOfFile(exe) switch
        {
            Engine.MachineX86 => "32-bit",
            Engine.MachineX64 => "64-bit",
            _ => null,
        };
        var apis = graphics.All.Count == 0 ? Ui.Text("Str.UnknownApi") : string.Join(" · ", graphics.All.Select(GraphicsDetection.Short));

        ExeLine.Text = exe is null
            ? Ui.Text("Str.ExeNone")
            : string.Join("  ·  ", new[] { Path.GetFileName(exe), width, apis }.OfType<string>());
        DetectedHow.Text = Ui.Text(graphics.Source is null ? "Str.DetectedFromFiles" : "Str.DetectedFromWiki");
        ExeButton.Content = Ui.Text(chosen ? "Str.ExeUseDetected" : "Str.ExeChoose");

        // The long explanation only where it is news: nothing was found, or the person chose the file.
        ExeNote.Text = exe is null ? Ui.Text("Str.ExeNote") : chosen ? Ui.Text("Str.ExeNoteChosen") : "";
        ExeNote.IsVisible = ExeNote.Text.Length > 0;
    }

    private void ShowApiHint(GraphicsDetection graphics)
    {
        ApiHint.Text = graphics switch
        {
            { Preset: null, All.Count: > 0 } => Ui.Text("Str.NoRoute"),
            { NeedsRendererSwitch: true } => Ui.Format("Str.SwitchRenderer",
                    GraphicsDetection.Short(graphics.Recommended),
                    string.Join(", ", graphics.All.Select(GraphicsDetection.Short)))
                + (graphics.Executable?.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase) == true
                    ? " " + Ui.Format("Str.SwitchRendererUnreal", GraphicsDetection.Short(graphics.Recommended).ToLowerInvariant())
                    : ""),
            _ => "",
        };
        // An emulator's renderer is a setting inside it, and that sentence is the whole difference
        // between working and not, so it replaces the generic "switch the renderer" hint.
        if (graphics.Emulator is { } emulator)
            ApiHint.Text = $"{Ui.Text("Str.EmulatorSetting")}: " + Ui.Translated($"Str.Emulator.{emulator.Id}", emulator.Setting);
        ApiHintBox.IsVisible = ApiHint.Text.Length > 0;
    }

    /// <summary>While something runs, nothing that would change what it is doing can be touched: the
    /// route, the name, the version, the executable, and the row itself.
    ///
    /// Nothing here depends on which game is open. It did -- "and a game is open" -- and Busy going
    /// off while the sheet was closed, a Download all on the next page, left Install disabled on
    /// every sheet opened after it. The handlers check for a game themselves.</summary>
    private void Lock(bool busy)
    {
        Options.IsEnabled = !busy;
        RemoveButton.IsEnabled = !busy;
        // Steam can always start it; anything else needs an executable we actually found.
        PlayButton.IsEnabled = !busy && _card is { } card
                               && ((card.Entry.Platform == GamePlatform.Steam && card.Entry.AppId is { Length: > 0 })
                                   || (card.Graphics?.Executable is { } exe && File.Exists(exe)));
        ReportButton.IsEnabled = !busy;
        InstallButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        if (!busy) Progress.IsVisible = false;
    }
}
