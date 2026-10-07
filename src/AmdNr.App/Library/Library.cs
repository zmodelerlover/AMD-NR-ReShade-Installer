// The list of games: games.json, checked against the disk every time the app opens and every time
// its window comes back to the front, so a game that was uninstalled meanwhile drops out of the list
// on its own instead of sitting there offering to install into a folder that is not there.

using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AmdNr.Core;

namespace AmdNr.App;

public sealed class Library(Session session)
{
    private readonly List<GameCard> _cards = [];

    /// <summary>Games on a drive that is not there right now: kept in games.json with everything that
    /// was chosen for them, and not shown, because a tile that cannot be installed into or opened is
    /// clutter. They come back by themselves when the drive does.</summary>
    private readonly List<GameEntry> _away = [];

    public IReadOnlyList<GameCard> Cards => _cards;
    public int Away => _away.Count;

    /// <summary>Raised whenever a game is added, removed or put back.</summary>
    public event Action? Changed;

    /// <summary>Reads games.json and sorts it against the disk: the games that are still there are
    /// listed, the ones on a drive that is not connected are set aside, and the ones that have gone
    /// are dropped and named in the return value, so the window can say so rather than let a list
    /// shrink in silence. Checking a folder is a stat or two, but a few hundred of them on a slow
    /// drive is not something to do on the thread that draws the window.</summary>
    public async Task<IReadOnlyList<string>> LoadAsync()
    {
        var entries = GameStore.Load();
        var presence = await Task.Run(() => entries.Select(e => GameScanner.PresenceOf(e.Path)).ToList());

        _cards.Clear();
        _away.Clear();
        var gone = new List<string>();
        for (var i = 0; i < entries.Count; i++)
        {
            switch (presence[i])
            {
                case Presence.Gone: gone.Add(entries[i].Display); break;
                case Presence.Unreachable: _away.Add(entries[i]); break;
                default: _cards.Add(new GameCard(entries[i])); break;
            }
        }
        Sort();
        foreach (var card in _cards) card.RefreshInstalled();
        if (gone.Count > 0) Save();
        Changed?.Invoke();
        return gone;
    }

    /// <summary>The same check for games already on screen, when the window comes back to the front:
    /// somebody who switched to Steam and uninstalled one should not have to restart this to see it
    /// go. The game whose sheet is open, and anything while work is running, is left alone -- unless
    /// the work is the scan asking for this, which is <paramref name="scanning"/>.</summary>
    public async Task<IReadOnlyList<string>> PruneAsync(GameCard? open, bool scanning = false)
    {
        if ((session.Busy && !scanning) || _cards.Count + _away.Count == 0) return [];
        var cards = _cards.Where(c => c != open).ToList();
        var away = _away.ToList();
        var (presence, back) = await Task.Run(() => (
            cards.Select(c => GameScanner.PresenceOf(c.Path)).ToList(),
            away.Select(e => GameScanner.PresenceOf(e.Path)).ToList()));
        if (session.Busy && !scanning) return [];

        var gone = new List<string>();
        var changed = false;
        for (var i = 0; i < cards.Count; i++)
        {
            if (presence[i] == Presence.Here || !_cards.Remove(cards[i])) continue;
            changed = true;
            if (presence[i] == Presence.Unreachable) _away.Add(cards[i].Entry);
            else gone.Add(cards[i].Name);
        }
        // And the other way: a drive plugged back in while the app was open brings its games back
        // now, with everything chosen for them, not on the next start.
        for (var i = 0; i < away.Count; i++)
        {
            if (back[i] == Presence.Unreachable || !_away.Remove(away[i])) continue;
            changed = true;
            if (back[i] == Presence.Gone) gone.Add(away[i].Display);
            else
            {
                var card = new GameCard(away[i]);
                _cards.Add(card);
                card.RefreshInstalled();
            }
        }
        if (changed)
        {
            Sort();
            Save();
            Changed?.Invoke();
        }
        return gone;
    }

    /// <summary>Raised when the games a scan passes over change.</summary>
    public event Action? IgnoredChanged;

    public IReadOnlyList<IgnoredGame> Ignored => Settings.Load().Ignored;

    private void Unignore(string path)
    {
        var settings = Settings.Load();
        if (settings.Ignored.RemoveAll(i => Engine.SamePath(i.Path, path)) == 0) return;
        settings.Save();
        IgnoredChanged?.Invoke();
    }

    /// <summary>An ignored game back in the list, found and detected as a new one would be. False when its
    /// folder is gone: it only leaves the ignored list then.</summary>
    public bool Restore(IgnoredGame game)
    {
        Unignore(game.Path);
        if (!Directory.Exists(game.Path)) return false;
        var card = Add(new GameEntry { Path = game.Path, Name = game.Name, Preset = GameScanner.GuessPreset(game.Path) });
        Redetect(card);
        _ = LoadCoversAsync();
        return true;
    }

    public void Save() => GameStore.Save(_cards.Select(c => c.Entry).Concat(_away));

    public GameCard? Find(string path) => _cards.FirstOrDefault(c => Engine.SamePath(c.Path, path));

    /// <summary>A game added by hand, or found again. One on a drive that came back is the entry that
    /// was set aside, with everything chosen for it, not a new one.</summary>
    public GameCard Add(GameEntry entry)
    {
        if (Find(entry.Path) is { } existing) return existing;
        // Added by hand: whatever was said about this folder before, it is wanted now.
        Unignore(entry.Path);
        var away = _away.FirstOrDefault(e => Engine.SamePath(e.Path, entry.Path));
        if (away is not null) _away.Remove(away);
        entry.Added ??= DateTime.Now;
        var card = new GameCard(away ?? entry);
        _cards.Add(card);
        Sort();
        card.RefreshInstalled();
        Save();
        Changed?.Invoke();
        return card;
    }

    /// <summary>Everything a scan turned up, folded in. A folder already in the list keeps the route
    /// the person chose for it. Returns how many were new.</summary>
    public int Merge(IReadOnlyList<ScannedGame> found)
    {
        var added = 0;
        var ignored = Settings.Load().Ignored;
        foreach (var game in found)
        {
            if (Find(game.InstallPath) is not null
                || ignored.Any(i => Engine.SamePath(i.Path, game.InstallPath))) continue;
            var away = _away.FirstOrDefault(e => Engine.SamePath(e.Path, game.InstallPath));
            if (away is not null) _away.Remove(away);
            var entry = away ?? GameEntry.From(game);
            entry.Added ??= DateTime.Now;
            _cards.Add(new GameCard(entry));
            added++;
        }
        Sort();
        foreach (var card in _cards) card.RefreshInstalled();
        Save();
        Changed?.Invoke();
        return added;
    }

    /// <summary>Takes a game out of the list for good: a scan passes over its folder from then on, until
    /// it is brought back from Settings or added by hand.</summary>
    public void Remove(GameCard card)
    {
        _cards.Remove(card);
        Save();
        var settings = Settings.Load();
        if (!settings.Ignored.Any(i => Engine.SamePath(i.Path, card.Path)))
            settings.Ignored.Add(new IgnoredGame { Path = card.Path, Name = card.Name });
        settings.Save();
        IgnoredChanged?.Invoke();
        Changed?.Invoke();
        DeleteCover(card.Entry.CustomCover);
        DeleteCover(card.Entry.CustomHero);
    }

    private void Sort() =>
        _cards.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Reads every game's graphics API that has not been read yet, a few at a time and off
    /// the thread that draws the window. It only opens files -- headers and import tables -- so a
    /// library of a few hundred games takes seconds, and it works offline.</summary>
    public async Task DetectAsync()
    {
        var pending = _cards.Where(c => c.Graphics is null).ToList();
        if (pending.Count == 0) return;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(pending.Select(async card =>
        {
            await gate.WaitAsync();
            try
            {
                var detection = await Task.Run(() => Detect(card));
                // A sheet opened meanwhile read it already, and shows the route that came of it: a
                // second answer landing under it changed the route the Install button would use
                // while the sheet still showed the first.
                if (card.Graphics is null) Apply(card, detection);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // A folder that cannot be read keeps its tile; the sheet reads it again when opened.
            }
            finally
            {
                gate.Release();
            }
        }));
        Save();
    }

    /// <summary>Detection again for one game, after the person pointed at another executable.</summary>
    public void Redetect(GameCard card)
    {
        Apply(card, Detect(card));
        Save();
    }

    /// <summary>Whether the OptiScaler this game would get also runs on games without an upscaler (from
    /// 0.5.0 on, see <see cref="Work.OptiRunsEverywhere"/>): the version picked for it, else the newest the
    /// payload offers. That decides whether OptiScaler is recommended on every API it runs on, or only for
    /// D3D12 games with an upscaler.</summary>
    public bool OptiEverywhere(GameCard card) =>
        Work.OptiRunsEverywhere(card.Entry.OptiScalerVersion
                                ?? session.Manifest?.Offered(PayloadManifest.OptiScalerComponent).FirstOrDefault()?.Version);

    /// <summary>What the game renders with: its own files first, then what the API database knows
    /// about it. Only reads, so it runs on any thread.</summary>
    public GraphicsDetection Detect(GameCard card) =>
        GraphicsDetector.Detect(card.Path, card.Entry.LookupName, card.Entry.Executable)
            .With(session.ApiDb?.Lookup(card.Entry.AppId, card.Entry.LookupName));

    /// <summary>A detection taken as the game's, and the route following it unless the person chose
    /// one. Every reader goes through here, so the tile, the sheet and the install agree.</summary>
    public void Apply(GameCard card, GraphicsDetection detection)
    {
        card.Graphics = detection;
        if (!card.Entry.PresetChosen && detection.PresetFor(OptiEverywhere(card)) is { } preset) card.Entry.Preset = preset;
        // Nothing recommends a route and nobody chose one: the route installed there is the game's.
        else if (!card.Entry.PresetChosen && card.InstalledVia == RouteFamily.OptiScaler) card.Entry.Preset = Preset.OptiScaler;
    }

    /// <summary>Cover art, a few at a time, and only for what has none yet. Steam publishes it on its
    /// own CDN, by the app id the scan read or the one the game's title finds (CoverCache); what Steam
    /// does not have keeps its tile. Each cover is decoded off the thread that draws the window and at
    /// the size a tile shows it, not at the 600x900 it was published at: a few hundred full-size covers
    /// was a few hundred megabytes.</summary>
    public async Task LoadCoversAsync()
    {
        var covers = new CoverCache(session.Http, session.ApiDb);
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(_cards.Where(c => c.Cover is null).ToList().Select(async card =>
        {
            await gate.WaitAsync();
            try
            {
                var file = card.Entry.CustomCover is { } own && File.Exists(own)
                    ? own
                    : await covers.EnsureAsync(card.Entry.AppId, card.Entry.LookupName, card.Path);
                if (file is null) return;
                var bitmap = await Decode(file);
                Dispatcher.UIThread.Post(() => card.Cover = bitmap);
            }
            catch (Exception e) when (e is IOException or HttpRequestException or ArgumentException
                                          or UnauthorizedAccessException or InvalidOperationException)
            {
                // A cover that will not load is a tile, which is what it already is.
            }
            finally
            {
                gate.Release();
            }
        }));
    }
    /// <summary>The game's banner for its page in the list view, once. Games Steam has no banner for
    /// show their cover there instead.</summary>
    public async Task LoadHeroAsync(GameCard card)
    {
        if (card.HeroLooked) return;
        card.HeroLooked = true;
        try
        {
            var file = card.Entry.CustomHero is { } own && File.Exists(own)
                ? own
                : await new CoverCache(session.Http, session.ApiDb)
                    .EnsureHeroAsync(card.Entry.AppId, card.Entry.LookupName, card.Path);
            if (file is null) return;
            var bitmap = await Decode(file, HeroWidth);
            Dispatcher.UIThread.Post(() => card.Hero = bitmap);
        }
        catch (Exception e) when (e is IOException or HttpRequestException or ArgumentException
                                      or UnauthorizedAccessException or InvalidOperationException)
        {
            // No banner: the page shows the cover.
        }
    }

    /// <summary>The widths art is decoded at: what a tile shows a cover at, and what the widest page shows a banner at.</summary>
    private const int CoverWidth = 440, HeroWidth = 1600;

    private static Task<Bitmap> Decode(string file, int width = CoverWidth) => Task.Run(() =>
    {
        using var stream = File.OpenRead(file);
        return Bitmap.DecodeToWidth(stream, width, BitmapInterpolationMode.HighQuality);
    });

    /// <summary>A game pinned to the top of the library, or unpinned.</summary>
    public void SetFavorite(GameCard card, bool favorite)
    {
        card.Favorite = favorite;
        Save();
    }

    /// <summary>The name the person gives a game here. Empty, or the name it already had, goes back to that
    /// one. A tile still without a cover looks again under the new name.</summary>
    public void Rename(GameCard card, string? name)
    {
        name = name?.Trim();
        card.Entry.CustomName = string.IsNullOrEmpty(name) || name == card.Entry.Name ? null : name;
        card.RaiseName();
        Sort();
        Save();
        Changed?.Invoke();
        if (card.Cover is null) _ = LoadCoversAsync();
    }

    /// <summary>A picture the person picked, as the game's cover; null goes back to the one found online.
    /// The picture is copied into the cache, and read before anything is saved: a file that is not an image
    /// throws here and the cover stays as it was.</summary>
    public async Task SetCoverAsync(GameCard card, string? picked)
    {
        var old = card.Entry.CustomCover;
        if (picked is null)
        {
            card.Entry.CustomCover = null;
            card.Cover = null;
            Save();
            _ = LoadCoversAsync();
        }
        else
        {
            var bitmap = await Decode(picked);
            card.Entry.CustomCover = await CopyInAsync(picked);
            card.Cover = bitmap;
            Save();
        }
        if (old != card.Entry.CustomCover) DeleteCover(old);
    }

    /// <summary>The same for the banner over the game's page in the list view; null goes back to Steam's.</summary>
    public async Task SetHeroAsync(GameCard card, string? picked)
    {
        var old = card.Entry.CustomHero;
        if (picked is null)
        {
            card.Entry.CustomHero = null;
            card.Hero = null;
            card.HeroLooked = false;
            Save();
            _ = LoadHeroAsync(card);
        }
        else
        {
            var bitmap = await Decode(picked, HeroWidth);
            card.Entry.CustomHero = await CopyInAsync(picked);
            card.HeroLooked = true;
            card.Hero = bitmap;
            Save();
        }
        if (old != card.Entry.CustomHero) DeleteCover(old);
    }

    /// <summary>A picture the person picked, copied into the cache under a name of its own.</summary>
    private static async Task<string> CopyInAsync(string picked)
    {
        var folder = Path.Combine(CoverCache.Folder, "custom");
        Directory.CreateDirectory(folder);
        var copy = Path.Combine(folder, $"{Guid.NewGuid():N}{Path.GetExtension(picked).ToLowerInvariant()}");
        await Task.Run(() => File.Copy(picked, copy));
        return copy;
    }

    private static void DeleteCover(string? file)
    {
        if (file is null) return;
        try { File.Delete(file); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a stray file in the cache */ }
    }
}
