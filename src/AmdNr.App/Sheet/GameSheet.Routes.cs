// The choice the sheet exists for: ReShade or OptiScaler, and inside ReShade which API, under which
// name and at which add-on version. The two routes are alternatives -- one manifest per folder, and
// the engine refuses a second preset over it -- so they are two cards, not two entries in one list.

using Avalonia.Controls;
using Avalonia.Interactivity;
using AmdNr.Core;

namespace AmdNr.App;

/// <summary>One installable version, as the sheet offers it. For the add-on, a null release means
/// the version the payload manifest pins, which is the one this build was published with. For the
/// OptiScaler route, Opti is the version of it the payload manifest lists.</summary>
public sealed record VersionChoice(Version Version, string Label, AddonRelease? Release, ComponentRelease? Opti = null);

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
    private static readonly Preset[] FiveM = [Preset.FiveM];

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

        // Neither card is checked while the route is the person's to pick.
        var undecided = RouteUndecided(card);
        _setting = true;
        RouteOpti.IsChecked = !undecided && card.Entry.Preset.IsOptiScaler();
        RouteReShade.IsChecked = !undecided && !card.Entry.Preset.IsOptiScaler();
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
            ("Str.Group64", Wide), ("Str.Group32", Narrow), ("Str.GroupEmulators", Emulated), ("Str.GroupFiveM", FiveM),
        ];
        groups = graphics.Emulator?.Route == Preset.FiveM ? groups.OrderBy(g => g.Presets != FiveM)
            : graphics.Emulator is not null ? groups.OrderBy(g => g.Presets != Emulated)
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
        var undecided = RouteUndecided(card);
        PickRouteBox.IsVisible = undecided;
        PickRouteText.Text = Ui.Format("Str.PickRouteBody", Path.GetFileName(card.Graphics?.Executable ?? card.Path));
        ReShadePanel.IsVisible = !undecided && !opti;
        OptiPanel.IsVisible = !undecided && opti;

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
        ShowRuntime();
        ShowMochizuki();
        // What either route would bring waits for the route.
        if (undecided) RuntimeSection.IsVisible = MochizukiSection.IsVisible = false;
        ShowInstallLabel();
    }

    /// <summary>Whether the route is this game's to pick by hand, and not picked yet: see
    /// <see cref="Presets.RouteUndecided"/>. Install waits for it, and the pre-flight with it.</summary>
    private static bool RouteUndecided(GameCard card) =>
        Presets.RouteUndecided(card.Graphics, card.Entry.PresetChosen, card.InstalledVia);

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
        // The route shown by default is a choice too once it is clicked: a game whose API is unknown waits for it.
        if (card.Entry.Preset == preset && card.Entry.PresetChosen) return;
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

        var auto = preset.IsOptiScaler() && _card is { } card && WikiProxy(card) is { } wiki
            ? Ui.Format("Str.ProxyAutoWiki", wiki)
            : Ui.Text("Str.ProxyAuto");
        _setting = true;
        box.ItemsSource = new[] { auto }.Concat(choices).ToList();
        box.SelectedIndex = _proxy is null ? 0 : Array.IndexOf(choices, _proxy) + 1;
        _setting = false;
    }

    /// <summary>The name the OptiScaler wiki gives OptiScaler for this game, when it gives one this route can use.</summary>
    private string? WikiProxy(GameCard card) =>
        Session.ApiDb?.OptiScalerNames(card.Entry.AppId, card.Entry.CustomName, card.Entry.Name,
                Path.GetFileName(card.Path.TrimEnd('\\', '/')))
            .FirstOrDefault(n => Work.ProxyAllowed(Preset.OptiScaler, n));

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
    /// The OptiScaler route installs no add-on: what it picks is the OptiScaler version.</summary>
    private void ShowVersions(Preset preset)
    {
        _versions.Clear();
        if (preset.IsOptiScaler())
        {
            ShowOptiScalerVersions();
            return;
        }
        var route = preset.Route();
        foreach (var release in Session.Releases.Where(r => r.Covers(route)))
            _versions.Add(new VersionChoice(release.Version, release.Label, release));

        _updateTarget = null;
        if (Session.Manifest is { } manifest)
        {
            var component = route == Route.X86 ? PayloadManifest.BridgeComponent : PayloadManifest.AddonComponent;
            if (manifest.Has(component) && AddonReleases.Version(manifest.Component(component).Version) is { } pinned)
            {
                _updateTarget = pinned;
                if (_versions.All(v => v.Version != pinned))
                    _versions.Add(new VersionChoice(pinned, $"v{pinned} — {Ui.Text("Str.VersionShipped")}", null));
            }
        }
        _versions.Sort((a, b) => b.Version.CompareTo(a.Version));

        // What this game was last installed with, then whatever the app is currently set to, then
        // the newest. The rule lives in Core so it can be asserted against. A game that is out of
        // date opens on the version that brings it up to date, or Update would put the old one back.
        var index = AddonReleases.Preferred(_versions.Select(v => v.Version).ToList(),
            SavedVersion(_card?.Entry.AddonVersion), _version?.Opti is null ? _version?.Version : null);
        FillVersions(AddonVersionBox, VersionSection, VersionNote, index);
    }

    /// <summary>The version an out-of-date install is measured against: the one the payload list pins.
    /// Update means that one and nothing else.</summary>
    private Version? _updateTarget;

    private string? SavedVersion(string? saved) =>
        _card is { Outdated: true } && _updateTarget is { } target ? target.ToString() : saved;

    /// <summary>The OptiScaler versions the payload manifest lists, newest first. They come from
    /// the manifest rather than from GitHub, because each one is pinned file by file inside its
    /// archive, and a release's own sums cannot say where each file goes.</summary>
    private void ShowOptiScalerVersions()
    {
        foreach (var release in Session.Manifest?.Offered(PayloadManifest.OptiScalerComponent) ?? [])
        {
            if (AddonReleases.Version(release.Version) is not { } version) continue;
            var published = release.Components[PayloadManifest.OptiScalerComponent].Published;
            _versions.Add(new VersionChoice(version,
                $"v{release.Version}" + (published is null ? "" : $" — {published}"), null, release));
        }

        // Same rule as the add-on: this game's last version, then the session's, then the newest.
        // Out of date is measured against the newest here, so that is what Update installs.
        _updateTarget = _versions.Count > 0 ? _versions.Max(v => v.Version) : null;
        var index = AddonReleases.Preferred(_versions.Select(v => v.Version).ToList(),
            SavedVersion(_card?.Entry.OptiScalerVersion), _version?.Opti is null ? null : _version.Version);
        FillVersions(OptiVersionBox, OptiVersionSection, OptiVersionNote, index);
    }

    private void FillVersions(ComboBox box, Control section, TextBlock note, int index)
    {
        _setting = true;
        box.ItemsSource = _versions.Select(v => v.Label).ToList();
        box.SelectedIndex = index;
        _setting = false;

        _version = index >= 0 ? _versions[index] : null;
        section.IsVisible = _versions.Count > 0;
        note.Text = VersionNoteText();
    }

    private string VersionNoteText() => _version switch
    {
        null => "",
        { Opti: { } opti } => Ui.Format("Str.VersionNoteOpti", opti.Version)
            + (opti.Components.ContainsKey(PayloadManifest.LmxxfWeightsComponent) ? " " + Ui.Text("Str.VersionNoteOptiLmxxf") : "")
            + (CarriesMochizuki(opti) ? " " + Ui.Text("Str.VersionNoteOptiMochizuki") : ""),
        { Release: null } => Ui.Text("Str.VersionNoteShipped"),
        _ => Ui.Format("Str.VersionNoteRelease", _version.Version),
    };

    /// <summary>The notes of the version picked in the menu: the add-on's release, or the OptiScaler
    /// release its archive comes from.</summary>
    private void OnWhatsNew(object? sender, RoutedEventArgs e)
    {
        if (_version is not { } version) return;
        string owner, repo, tag, name;
        if (version.Opti is { } opti)
        {
            var url = opti.Components.TryGetValue(PayloadManifest.OptiScalerComponent, out var component)
                ? component.Files.FirstOrDefault()?.Url
                : null;
            if (ReleaseNotes.FromAssetUrl(url) is not { } release) return;
            (owner, repo, tag) = release;
            name = $"OptiScaler {opti.Version}";
        }
        else
        {
            (owner, repo) = (Session.Config.Addon.Owner, Session.Config.Addon.Repo);
            tag = version.Release?.Tag ?? $"v{version.Version}";
            name = $"{repo} v{version.Version}";
        }
        _shell.ShowNotes(name,
            ReleaseNotes.GetAsync(Session.Http, owner, repo, tag), $"https://github.com/{owner}/{repo}/releases/tag/{tag}");
    }

    private void OnAddonVersionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_setting || _card is not { } card || sender is not ComboBox box) return;
        if (box.SelectedIndex < 0 || box.SelectedIndex >= _versions.Count) return;
        _version = _versions[box.SelectedIndex];
        Remember(card, _version);
        Library.Save();
        (box == OptiVersionBox ? OptiVersionNote : VersionNote).Text = VersionNoteText();
        ShowRuntime();
        ShowMochizuki();
        ShowInstallLabel();
        // A different version is a different pair of hashes, so the pre-flight has to be redone:
        // "already installed" is only true of the version that is actually in the folder.
        _ = RefreshAsync();
    }

    /// <summary>Whether this OptiScaler version brings the mochizuki runtime: its runtime and its model.</summary>
    private static bool CarriesMochizuki(ComponentRelease opti) =>
        opti.Components.ContainsKey(PayloadManifest.MochizukiComponent)
        && opti.Components.ContainsKey(PayloadManifest.MochizukiModelComponent);

    /// <summary>The mochizuki choice: shown when the OptiScaler version chosen carries it, remembered
    /// per game, and not offered on a card known not to be RDNA4 -- where it could only stay off in
    /// game. A card this app could not read is given the benefit of the doubt, and the note says so.</summary>
    private void ShowMochizuki()
    {
        if (_card is not { } card) return;
        var machine = Session.Machine;
        var addon = !card.Entry.Preset.IsOptiScaler();
        MochizukiSection.IsVisible = OffersMochizuki(card);
        MochizukiCheckText.Text = Ui.Text(addon ? "Str.MochizukiCheckAddon" : "Str.MochizukiCheck");
        var note = Ui.Text(addon ? "Str.MochizukiNoteAddon" : "Str.MochizukiNote");
        _setting = true;
        MochizukiBox.IsEnabled = machine?.Rdna4 != false;
        MochizukiBox.IsChecked = ChoseMochizuki(card) && machine?.Rdna4 != false;
        _setting = false;
        MochizukiNote.Text = machine?.Rdna4 switch
        {
            false => Ui.Format("Str.MochizukiNotRdna4", machine!.Gpu),
            null => note + " " + Ui.Text("Str.MochizukiUnknownGpu"),
            _ => note,
        };
    }

    /// <summary>Whether an install from this sheet brings mochizuki: a version that carries it, ticked
    /// for this game, and not a card known not to be RDNA4. An install without it takes out what an
    /// earlier one put in, so this is also what stays in the folder.</summary>
    private bool WantsMochizuki(GameCard card) =>
        OffersMochizuki(card) && ChoseMochizuki(card) && Session.Machine?.Rdna4 != false;

    /// <summary>The first add-on that can drive the mochizuki runtime (its NR runtime choice).</summary>
    private static readonly Version MochizukiAddon = new(0, 6, 8);

    /// <summary>Whether the version chosen can take mochizuki: an OptiScaler release that carries it, or
    /// an add-on from <see cref="MochizukiAddon"/> on with the payload holding a mochizuki build.</summary>
    private bool OffersMochizuki(GameCard card)
    {
        if (card.Entry.Preset.IsOptiScaler()) return _version?.Opti is { } opti && CarriesMochizuki(opti);
        if (card.Entry.Preset == Preset.FiveM) return false;
        if (Selected() is not { } manifest) return false;
        var own = card.Entry.Preset.Route() == Route.X86 ? PayloadManifest.BridgeComponent : PayloadManifest.AddonComponent;
        var version = _version?.Version
                      ?? (manifest.Has(own) ? AddonReleases.Version(manifest.Component(own).Version) : null);
        return version >= MochizukiAddon && manifest.Pins().MochizukiFiles.Count > 0;
    }

    /// <summary>The box as the person last set it for this game, or, until they touch it, what the
    /// folder has: on where this app installed mochizuki. Read off the folder's manifest, like the
    /// tile's out-of-date badge, and wrapped the same way: a folder that cannot be read is "no".</summary>
    private static bool ChoseMochizuki(GameCard card)
    {
        if (card.Entry.Mochizuki is { } chosen) return chosen;
        try { return Work.HasMochizuki(TargetFor(card)); }
        catch (Exception) { return false; }
    }

    private void OnMochizukiChanged(object? sender, RoutedEventArgs e)
    {
        if (_setting || _card is not { } card) return;
        card.Entry.Mochizuki = MochizukiBox.IsChecked == true;
        Library.Save();
        // A different set of files to download and to install: the pre-flight says so again.
        _ = RefreshAsync();
    }

    /// <summary>Records a chosen version on the game, in the field for the kind of version it is.</summary>
    private static void Remember(GameCard card, VersionChoice choice)
    {
        if (choice.Opti is { } opti) card.Entry.OptiScalerVersion = opti.Version;
        else card.Entry.AddonVersion = choice.Version.ToString();
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
            // Update only when the version picked is the one that brings it up to date; an older one
            // picked by hand is put back as it is, and the button says so.
            var via when via == family => card.Outdated && (_updateTarget is null || _version?.Version == _updateTarget)
                ? "Str.Update"
                : "Str.Reinstall",
            _ => family == RouteFamily.OptiScaler ? "Str.SwitchToOpti" : "Str.SwitchToReShade",
        };
        if (!InstallSpin.IsVisible) Ui.Localize(InstallLabel, key);
        InstallButton.IsEnabled = !Session.Busy && !RouteUndecided(card);
        UninstallButton.IsVisible = card.Installed;
    }

    /// <summary>A route's name in the current language. The engine carries it in English -- it has
    /// no resources -- so the translation lives here and falls back to its words.</summary>
    private static string PresetLabel(Preset preset) => Ui.Translated($"Str.Preset.{preset}", preset.Label());
}
