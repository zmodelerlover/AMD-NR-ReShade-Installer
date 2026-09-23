// The choice the sheet exists for: ReShade or OptiScaler, and inside ReShade which API, under which
// name and at which add-on version. The two routes are alternatives -- one manifest per folder, and
// the engine refuses a second preset over it -- so they are two cards, not two entries in one list.

using Avalonia.Controls;
using Avalonia.Interactivity;
using AmdNr.Core;

namespace AmdNr.App;

/// <summary>One installable version of the add-on, as the sheet offers it. A null release means
/// the version the payload manifest pins, which is the one this build was published with.</summary>
public sealed record VersionChoice(Version Version, string Label, AddonRelease? Release);

public partial class GameSheet
{
    private bool _setting;
    private string? _proxy;
    private VersionChoice? _version;
    private readonly List<VersionChoice> _versions = [];

    /// <summary>The ReShade preset the API list was last on for this game, so going to OptiScaler and
    /// back does not lose it.</summary>
    private Preset? _lastReShade;

    private static readonly Preset[] Wide = [Preset.Dx11, Preset.Dx12, Preset.Vulkan, Preset.OpenGL];
    private static readonly Preset[] Narrow = [Preset.X86Dx11, Preset.X86Dx9, Preset.X86Dx8];
    private static readonly Preset[] Emulated = [Preset.Pcsx2, Preset.Rpcs3];

    private void ShowRoutes(GameCard card, GraphicsDetection graphics)
    {
        _lastReShade = card.Entry.Preset.IsOptiScaler() ? graphics.ReShadeRoute : card.Entry.Preset;

        // Recommended is what detection would pick; installed is what is in the folder. A game can
        // carry both on different cards, and that is the case that most needs saying.
        var recommended = graphics.Preset?.Family();
        ReShadeRecommended.IsVisible = recommended == RouteFamily.ReShade;
        OptiRecommended.IsVisible = recommended == RouteFamily.OptiScaler;
        ReShadeInstalled.IsVisible = card.InstalledVia == RouteFamily.ReShade;
        OptiInstalled.IsVisible = card.InstalledVia == RouteFamily.OptiScaler;

        // A route that does not fit stays clickable -- the detection can be wrong about which file is
        // the game -- but it looks it, and says why in place of its summary.
        var reshadeFits = graphics.All.Count == 0 || graphics.ReShadeRoute is not null;
        RouteReShade.Classes.Set("misfit", !reshadeFits);
        ReShadeSummary.Text = reshadeFits
            ? Ui.Text("Str.RouteReShadeSummary")
            : Ui.Format("Str.RouteReShadeMisfit", graphics.Tag);
        RouteOpti.Classes.Set("misfit", !graphics.CanRunOptiScaler);
        OptiSummary.Text = graphics.CanRunOptiScaler
            ? graphics.Upscalers.Count > 0
                ? Ui.Format("Str.RouteOptiFound", string.Join(", ", graphics.Upscalers.Take(2)))
                : Ui.Text("Str.RouteOptiSummary")
            : Ui.Format("Str.RouteOptiMisfit", graphics.Tag);
        OptiMismatchText.Text = OptiSummary.Text;
        OptiMismatchBox.IsVisible = !graphics.CanRunOptiScaler;

        _setting = true;
        RouteOpti.IsChecked = card.Entry.Preset.IsOptiScaler();
        RouteReShade.IsChecked = !card.Entry.Preset.IsOptiScaler();
        _setting = false;
        FillPresets(graphics);
        ShowChosenRoute();
    }

    /// <summary>The ReShade APIs, grouped by what they are for. The group the detected width says can
    /// work comes first, and the others stay reachable below it, because a wrong width is wrong about
    /// which file is the game and a list that hides every working route leaves nowhere to go.</summary>
    private void FillPresets(GraphicsDetection graphics)
    {
        IEnumerable<(string Title, Preset[] Presets)> groups =
        [
            ("Str.Group64", Wide), ("Str.Group32", Narrow), ("Str.GroupEmulators", Emulated),
        ];
        groups = graphics.Emulator is not null ? groups.OrderBy(g => g.Presets != Emulated)
            : _detected.Route == Route.X86 ? groups.OrderBy(g => g.Presets != Narrow)
            : groups;

        var items = new List<ComboBoxItem>();
        foreach (var (title, presets) in groups)
        {
            var header = new ComboBoxItem { Content = Ui.Text(title), IsEnabled = false, Focusable = false };
            header.Classes.Add("group");
            items.Add(header);
            foreach (var preset in presets)
                items.Add(new ComboBoxItem
                {
                    Tag = preset,
                    Content = PresetLabel(preset)
                              + (preset == graphics.ReShadeRoute ? $"  —  {Ui.Text("Str.RecommendedLower")}" : ""),
                });
        }

        _setting = true;
        PresetBox.ItemsSource = items;
        var wanted = _lastReShade ?? graphics.ReShadeRoute ?? Preset.Dx11;
        PresetBox.SelectedItem = items.FirstOrDefault(i => i.Tag is Preset p && p == wanted)
                                 ?? items.First(i => i.Tag is Preset);
        _setting = false;
    }

    /// <summary>Everything that follows from the route now chosen: which panel shows, its notes, the
    /// names it can load as, the versions it can install at, and what the Install button says.</summary>
    private void ShowChosenRoute()
    {
        if (_card is not { } card) return;
        var preset = card.Entry.Preset;
        var opti = preset.IsOptiScaler();
        ReShadePanel.IsVisible = !opti;
        OptiPanel.IsVisible = opti;

        if (!opti)
        {
            PresetNote.Text = Ui.Translated($"Str.PresetNote.{preset}", preset.Note());
            // Said out loud next to the choice when it disagrees with the width read off the executable.
            MismatchBox.IsVisible = !preset.MatchesDetected(_detected);
            MismatchText.Text = Ui.Format("Str.RouteMismatch",
                preset.Route() == Route.X86 ? "32-bit" : "64-bit",
                _detected.Route == Route.X86 ? "32-bit" : "64-bit");
        }
        ShowProxies(preset);
        ShowVersions(preset);
        ShowInstallLabel();
    }

    private void OnRouteChanged(object? sender, RoutedEventArgs e)
    {
        if (_setting || _card is not { } card || sender is not RadioButton { IsChecked: true } button) return;
        var preset = button == RouteOpti
            ? Preset.OptiScaler
            : PresetBox.SelectedItem is ComboBoxItem { Tag: Preset p } ? p : _lastReShade ?? Preset.Dx11;
        Choose(card, preset);
    }

    private void OnPresetChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_setting || _card is not { } card || PresetBox.SelectedItem is not ComboBoxItem { Tag: Preset preset }) return;
        _lastReShade = preset;
        if (RouteReShade.IsChecked == true) Choose(card, preset);
    }

    private void Choose(GameCard card, Preset preset)
    {
        if (card.Entry.Preset == preset) return;
        card.Entry.Preset = preset;
        card.Entry.PresetChosen = true;
        card.RefreshRoute();
        Library.Save();
        ShowChosenRoute();
        _ = RefreshAsync();
    }

    /// <summary>The name ReShade -- or, on the other route, OptiScaler -- goes in as. Automatic is
    /// the first entry and what almost every game wants; the list under it is only the names that
    /// can actually be loaded for this API, because a name that exports nothing is a file nothing
    /// opens. A name chosen for one API is not carried into another, where it would be ignored.</summary>
    private void ShowProxies(Preset preset)
    {
        var choices = Work.ProxyChoicesFor(preset);
        var box = preset.IsOptiScaler() ? OptiProxyBox : ProxyBox;
        // Vulkan: ReShade is a layer there, so there is no name to choose.
        ProxySection.IsVisible = choices.Length > 0;
        if (!Work.ProxyAllowed(preset, _proxy)) _proxy = null;
        if (choices.Length == 0) return;

        _setting = true;
        box.ItemsSource = new[] { Ui.Text("Str.ProxyAuto") }.Concat(choices).ToList();
        box.SelectedIndex = _proxy is null ? 0 : Array.IndexOf(choices, _proxy) + 1;
        _setting = false;
    }

    private void OnProxyChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_setting || _card is not { } card || sender is not ComboBox box) return;
        var choices = Work.ProxyChoicesFor(card.Entry.Preset);
        var i = box.SelectedIndex;
        _proxy = i >= 1 && i - 1 < choices.Length ? choices[i - 1] : null;
        _ = RefreshAsync();
    }

    /// <summary>The versions this route can be installed at: every GitHub release that publishes
    /// what the route needs, plus the one the payload manifest pins. That last one is not
    /// decoration -- v0.5.0 published its 32-bit pair inside the archive rather than beside it,
    /// so for a bridge route the manifest is the only place those two files can be pinned from.
    /// The OptiScaler route installs no add-on, so there is no version to pick on it.</summary>
    private void ShowVersions(Preset preset)
    {
        _versions.Clear();
        if (preset.IsOptiScaler())
        {
            _version = null;
            return;
        }
        var route = preset.Route();
        foreach (var release in Session.Releases.Where(r => r.Covers(route)))
            _versions.Add(new VersionChoice(release.Version, release.Label, release));

        if (Session.Manifest is { } manifest)
        {
            var component = route == Route.X86 ? PayloadManifest.BridgeComponent : PayloadManifest.AddonComponent;
            if (manifest.Has(component)
                && AddonReleases.Version(manifest.Component(component).Version) is { } pinned
                && _versions.All(v => v.Version != pinned))
                _versions.Add(new VersionChoice(pinned, $"v{pinned} - {Ui.Text("Str.VersionShipped")}", null));
        }
        _versions.Sort((a, b) => b.Version.CompareTo(a.Version));

        // What this game was last installed with, then whatever the app is currently set to, then
        // the newest. The rule lives in Core so it can be asserted against.
        var index = AddonReleases.Preferred(_versions.Select(v => v.Version).ToList(),
            _card?.Entry.AddonVersion, _version?.Version);
        _setting = true;
        AddonVersionBox.ItemsSource = _versions.Select(v => v.Label).ToList();
        AddonVersionBox.SelectedIndex = index;
        _setting = false;

        _version = index >= 0 ? _versions[index] : null;
        VersionSection.IsVisible = _versions.Count > 0;
        VersionNote.Text = _version is null ? ""
            : _version.Release is null ? Ui.Text("Str.VersionNoteShipped")
            : Ui.Format("Str.VersionNoteRelease", _version.Version);
    }

    private void OnAddonVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_setting || _card is not { } card) return;
        if (AddonVersionBox.SelectedIndex < 0 || AddonVersionBox.SelectedIndex >= _versions.Count) return;
        _version = _versions[AddonVersionBox.SelectedIndex];
        card.Entry.AddonVersion = _version.Version.ToString();
        Library.Save();
        VersionNote.Text = _version.Release is null
            ? Ui.Text("Str.VersionNoteShipped")
            : Ui.Format("Str.VersionNoteRelease", _version.Version);
        // A different version is a different pair of hashes, so the pre-flight has to be redone:
        // "already installed" is only true of the version that is actually in the folder.
        _ = RefreshAsync();
    }

    /// <summary>What pressing Install will actually do here, said on the button: install, put the
    /// same thing in again, bring an old install up to date, or trade one route for the other.</summary>
    private void ShowInstallLabel()
    {
        if (_card is not { } card) return;
        var family = card.Entry.Preset.Family();
        var key = card.InstalledVia switch
        {
            null => family == RouteFamily.OptiScaler ? "Str.InstallOpti" : "Str.Install",
            var via when via == family => card.Outdated ? "Str.Update" : "Str.Reinstall",
            _ => family == RouteFamily.OptiScaler ? "Str.SwitchToOpti" : "Str.SwitchToReShade",
        };
        if (!InstallSpin.IsVisible) Ui.Localize(InstallLabel, key);
        UninstallButton.IsVisible = card.Installed;
    }

    /// <summary>A route's name in the current language. The engine carries it in English -- it has
    /// no resources -- so the translation lives here and falls back to its words.</summary>
    private static string PresetLabel(Preset preset) => Ui.Translated($"Str.Preset.{preset}", preset.Label());
}
