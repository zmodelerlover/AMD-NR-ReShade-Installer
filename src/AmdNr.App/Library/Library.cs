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
    /// go. The game whose sheet is open, and anything while work is running, is left alone.</summary>
    public async Task<IReadOnlyList<string>> PruneAsync(GameCard? open)
    {
        if (session.Busy || _cards.Count == 0) return [];
        var cards = _cards.Where(c => c != open).ToList();
        var presence = await Task.Run(() => cards.Select(c => GameScanner.PresenceOf(c.Path)).ToList());
        if (session.Busy) return [];

        var gone = new List<string>();
        var changed = false;
        for (var i = 0; i < cards.Count; i++)
        {
            if (presence[i] == Presence.Here || !_cards.Remove(cards[i])) continue;
            changed = true;
            if (presence[i] == Presence.Unreachable) _away.Add(cards[i].Entry);
            else gone.Add(cards[i].Name);
        }
        if (changed)
        {
            Save();
            Changed?.Invoke();
        }
        return gone;
    }

    public void Save() => GameStore.Save(_cards.Select(c => c.Entry).Concat(_away));

    public GameCard? Find(string path) => _cards.FirstOrDefault(c => Engine.SamePath(c.Path, path));

    /// <summary>A game added by hand, or found again. One on a drive that came back is the entry that
    /// was set aside, with everything chosen for it, not a new one.</summary>
    public GameCard Add(GameEntry entry)
    {
        if (Find(entry.Path) is { } existing) return existing;
        var away = _away.FirstOrDefault(e => Engine.SamePath(e.Path, entry.Path));
        if (away is not null) _away.Remove(away);
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
        foreach (var game in found)
        {
            if (Find(game.InstallPath) is not null) continue;
            var away = _away.FirstOrDefault(e => Engine.SamePath(e.Path, game.InstallPath));
            if (away is not null) _away.Remove(away);
            _cards.Add(new GameCard(away ?? GameEntry.From(game)));
            added++;
        }
        Sort();
        foreach (var card in _cards) card.RefreshInstalled();
        Save();
        Changed?.Invoke();
        return added;
    }

    public void Remove(GameCard card)
    {
        _cards.Remove(card);
        Save();
        Changed?.Invoke();
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
        var db = session.ApiDb;
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(pending.Select(async card =>
        {
            await gate.WaitAsync();
            try
            {
                var detection = await Task.Run(() =>
                    GraphicsDetector.Detect(card.Path, card.Entry.Name, card.Entry.Executable)
                        .With(db?.Lookup(card.Entry.AppId, card.Entry.Name)));
                card.Graphics = detection;
                if (!card.Entry.PresetChosen && detection.Preset is { } preset) card.Entry.Preset = preset;
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
        card.Graphics = GraphicsDetector.Detect(card.Path, card.Entry.Name, card.Entry.Executable)
            .With(session.ApiDb?.Lookup(card.Entry.AppId, card.Entry.Name));
        if (!card.Entry.PresetChosen && card.Graphics.Preset is { } preset) card.Entry.Preset = preset;
        Save();
    }

    /// <summary>Cover art, a few at a time, and only for what has none yet. Steam publishes it on its
    /// own CDN keyed by the app id the scan already read; everything else keeps its tile. Each cover
    /// is decoded off the thread that draws the window and at the size a tile shows it, not at the
    /// 600x900 it was published at: a few hundred full-size covers was a few hundred megabytes.</summary>
    public async Task LoadCoversAsync()
    {
        var covers = new CoverCache(session.Http);
        using var gate = new SemaphoreSlim(4);
        await Task.WhenAll(_cards.Where(c => c.Cover is null && c.Entry.AppId is not null).ToList().Select(async card =>
        {
            await gate.WaitAsync();
            try
            {
                var file = await covers.EnsureAsync(card.Entry.AppId);
                if (file is null) return;
                var bitmap = await Task.Run(() =>
                {
                    using var stream = File.OpenRead(file);
                    return Bitmap.DecodeToWidth(stream, 440, BitmapInterpolationMode.HighQuality);
                });
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
}
