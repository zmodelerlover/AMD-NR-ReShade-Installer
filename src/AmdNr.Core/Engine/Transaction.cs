// The transactional half of an install, shared by both routes. It records ownership, backs up
// anything it is about to displace, writes the journal *before* touching a target, and restores
// everything it moved if any write fails.

using System.Text;

namespace AmdNr.Core;

public static partial class Transaction
{
    /// <summary>Everything about the folder that would make the transaction fail halfway, checked
    /// before the journal is written so a refusal costs nothing and says why.
    ///
    /// This runs inside <see cref="Apply"/>, which is what folds it in front of both routes at once
    /// rather than leaving it as advice on one screen. The common case by far is the third one: the
    /// game is still open, and the old failure for that was an access-denied error from somewhere
    /// inside a copy.</summary>
    public static void Guard(string dir, SortedDictionary<string, byte[]> desired, IEnumerable<string>? displace = null)
    {
        Engine.Require(Directory.Exists(dir), $"{dir} is not a folder.");
        Engine.Require(Engine.FolderIsWritable(dir),
            "That folder cannot be written to. It is either read-only or somewhere that needs "
            + "administrator rights -- run this installer as administrator, or move the game.");

        var held = desired.Keys.Concat(displace ?? []).Where(n => Engine.IsLocked(Path.Combine(dir, n))).ToList();
        if (held.Count > 0) throw new InstallException(Engine.OpenElsewhere(dir, held));

        // Size is a cheap stand-in for "already the file we want": hashing every payload here would
        // read the 141 MB of weights twice, once to decide and once to install. A file whose size
        // already matches needs no new room; anything else needs its own bytes plus a backup of
        // whatever it displaces.
        ulong need = 0;
        foreach (var (name, data) in desired)
        {
            var existing = Engine.SizeOf(Path.Combine(dir, name));
            if (existing != (ulong)data.LongLength) need += (ulong)data.LongLength + (existing ?? 0);
        }
        foreach (var name in (displace ?? []).Where(n => !desired.ContainsKey(n)))
            need += Engine.SizeOf(Path.Combine(dir, name)) ?? 0;

        var free = Engine.FreeBytes(dir);
        if (free is { } available)
        {
            Engine.Require(need == 0 || available >= need,
                $"Not enough room: {available / 1_048_576} MB free, and this needs {need / 1_048_576} MB "
                + "including the backup of what it replaces.");
        }
    }

    private sealed class Change
    {
        public required string Name { get; init; }
        public required byte[] Before { get; init; }
        /// <summary>What goes there, or null when the file comes out (see <see cref="PlanRetire"/>).</summary>
        public required byte[]? After { get; init; }
        public required bool Existed { get; init; }
    }

    /// <summary>Carries a manifest written before v0.6.5 over to the name this version reads.
    ///
    /// The manifest moved with everything else, and nothing read the old name any more -- so an
    /// upgrade would have found no manifest, treated a folder that already had an install as a
    /// fresh one, and recorded every file already there as Owned=false. Uninstall only removes
    /// what it owns, so ReShade and the runtime would have been left behind for good, with the
    /// backups of whatever they displaced orphaned beside them.
    ///
    /// Entries for the add-on's own files are dropped: those are swept by name on install and
    /// written fresh. Every other entry is kept exactly as it was, backup paths included -- those
    /// point into the old backup directory, which is still on disk under its old name and is
    /// still where the originals actually are.</summary>
    public static void MigrateLegacyManifest(string dir, Route route)
    {
        var now = Path.Combine(dir, route.ManifestFileName());
        var was = Path.Combine(dir, route == Route.X86
            ? "dlss5-x86bridge.install.json"
            : "dlss5-neural.install.json");
        Engine.SafePath(now);
        Engine.SafePath(was);
        if (File.Exists(now) || !File.Exists(was)) return;

        var m = Manifest.Decode(Encoding.UTF8.GetString(Engine.Read(was)));
        m.Entries.RemoveAll(e => Engine.Legacy.Contains(e.Name, StringComparer.Ordinal));
        Manifest.WriteAtomic(dir, m);
        try { File.Delete(was); } catch (IOException) { /* the new one is authoritative either way */ }
    }
    /// <summary><paramref name="desired"/> is whatever the route's own planner decided to write;
    /// this function is deliberately ignorant of what those files mean. <paramref name="retire"/>
    /// names recorded files the route no longer wants here, taken out in the same transaction
    /// (<see cref="PlanRetire"/>); a name that is also in <paramref name="desired"/> is not one.
    /// <paramref name="displace"/> names files somebody else put here that the route takes out: each
    /// goes to the backup, and uninstall puts it back. <paramref name="reShade"/> is what the manifest records
    /// as the ReShade in use, the pinned build unless told.</summary>
    public static void Apply(string dir, string preset, Route route,
        SortedDictionary<string, byte[]> desired, List<string> log, IEnumerable<string>? retire = null,
        IEnumerable<string>? displace = null, string? reShade = null)
    {
        Guard(dir, desired, displace);
        MigrateLegacyManifest(dir, route);
        var manifestPath = Path.Combine(dir, route.ManifestFileName());
        Engine.SafePath(manifestPath);

        var m = new Manifest(preset, route);
        if (File.Exists(manifestPath))
        {
            m = Manifest.Decode(Encoding.UTF8.GetString(Engine.Read(manifestPath)));
            // What is about to be written is this build's pair, whatever the folder had before.
            m.BridgeProtocol = Manifest.Current;
            Engine.Require(m.State == "installed",
                "Interrupted transaction: run uninstall/recovery before reinstall");
            // What uninstall leaves is this manifest holding only the configuration it keeps on
            // purpose, still named after the preset it came from. That is not an install, so any
            // preset may take it over, and the kept entries ride along for the next uninstall to
            // keep again. Refusing it left a folder that ever had one route or API unable to take
            // another -- with nothing installed to uninstall first.
            if (!m.Entries.Any(e => e.Owned && !e.Configuration && File.Exists(Path.Combine(dir, e.Name))))
                m.Preset = preset;
            Engine.Require(m.Preset == preset, "Uninstall previous preset before changing API");
            Engine.Require(m.Route == route,
                "That folder already has an install for the other architecture; uninstall it first");
        }
        m.ReShade = reShade ?? Manifest.PinnedReShade;

        var changes = new List<Change>();
        var superseded = new List<string>();
        var stamp = ((ulong)(DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100).ToString();

        foreach (var (name, data) in desired)
        {
            Engine.Require(Engine.IsAllowed(name),
                $"Refusing to install an unmanaged filename: {name}");
            var dst = Path.Combine(dir, name);
            Engine.SafePath(dst);
            var exists = File.Exists(dst);
            var wanted = Engine.Sha(data);
            var old = exists ? Engine.HashFile(dst) : string.Empty;
            var index = m.Entries.FindIndex(x => x.Name == name);

            if (exists && old == wanted)
            {
                log.Add($"IDENTICAL: {name}");
                if (index < 0)
                {
                    m.Entries.Add(new Entry
                    {
                        Name = name,
                        Hash = wanted,
                        Owned = false,
                        Configuration = Engine.IsConfig(name),
                    });
                }
                continue;
            }

            if (index >= 0 && exists && old != m.Entries[index].Hash)
            {
                if (Engine.IsConfig(name))
                {
                    log.Add($"PRESERVED user-modified config: {name}");
                    continue;
                }
                // What the runtime made of a file an install recorded: kept while the version is the
                // same one, and replaced by the new version's. The entry says what was there when it
                // was recorded -- written by this app, or an identical copy somebody had put in by
                // hand -- and either way those were the bytes this app pinned, so there is nothing of
                // anybody's to back up: the new list is this app's from here on.
                if (Engine.IsRuntimeMaintained(name))
                {
                    if (m.Entries[index].Hash == wanted)
                    {
                        log.Add($"KEPT as the runtime updated it: {name}");
                        continue;
                    }
                    m.Entries[index].Owned = true;
                }
                else
                {
                    // Replaced since the install (a build copied in by hand, another ReShade): refusing
                    // left a folder no install could update. That copy is the backup now, and the earlier
                    // one is spent, as when a displaced file is put back (see displace below).
                    var entry = m.Entries[index];
                    if (entry.Backup.Length > 0) superseded.Add(Path.Combine(dir, entry.Backup));
                    entry.Backup = $"{Engine.BackupDir}/{stamp}/{name}";
                    entry.BackupHash = old;
                    entry.Owned = true;
                    var bp = Path.Combine(dir, entry.Backup);
                    Engine.SafePath(bp);
                    Engine.MakeParent(bp);
                    Engine.Write(bp, Engine.Read(dst));
                    Engine.HashIs(Engine.Read(bp), old, "Backup");
                    log.Add($"REPLACED changed since the install, backed up: {name}");
                }
            }

            var before = exists ? Engine.Read(dst) : [];
            if (index < 0)
            {
                var item = new Entry
                {
                    Name = name,
                    Hash = wanted,
                    Owned = true,
                    Configuration = Engine.IsConfig(name),
                };
                if (exists)
                {
                    item.Backup = $"{Engine.BackupDir}/{stamp}/{name}";
                    item.BackupHash = old;
                    var bp = Path.Combine(dir, item.Backup);
                    Engine.SafePath(bp);
                    Engine.MakeParent(bp);
                    Engine.Write(bp, before);
                    Engine.HashIs(Engine.Read(bp), old, "Backup");
                    log.Add($"EXTERNAL backed up: {name}");
                }
                else
                {
                    log.Add($"CREATE: {name}");
                }
                m.Entries.Add(item);
            }
            else
            {
                if (!m.Entries[index].Owned && exists)
                {
                    var backup = $"{Engine.BackupDir}/{stamp}/{name}";
                    var bp = Path.Combine(dir, backup);
                    Engine.SafePath(bp);
                    Engine.MakeParent(bp);
                    Engine.Write(bp, before);
                    Engine.HashIs(Engine.Read(bp), old, "Upgrade backup");
                    m.Entries[index].Backup = backup;
                    m.Entries[index].BackupHash = old;
                }
                m.Entries[index].Hash = wanted;
                m.Entries[index].Owned = true;
            }

            changes.Add(new Change { Name = name, Before = before, After = data, Existed = exists });
        }

        // Taken out the way a write displaces a file, with nothing written in its place: the entry owns
        // the name and holds the backup, which is what uninstall puts back.
        foreach (var name in (displace ?? []).Distinct(StringComparer.Ordinal).Where(n => !desired.ContainsKey(n)))
        {
            Engine.Require(Engine.IsAllowed(name) || Engine.IsPlainDll(name), $"Refusing to move an unmanaged filename: {name}");
            var dst = Path.Combine(dir, name);
            Engine.SafePath(dst);
            if (!File.Exists(dst)) continue;
            var before = Engine.Read(dst);
            var old = Engine.Sha(before);
            var backup = $"{Engine.BackupDir}/{stamp}/{name}";
            var bp = Path.Combine(dir, backup);
            Engine.SafePath(bp);
            Engine.MakeParent(bp);
            Engine.Write(bp, before);
            Engine.HashIs(Engine.Read(bp), old, "Backup");
            // Put back by the person after an earlier install took theirs out: this one is the original now.
            if (m.Entries.Find(x => x.Name == name) is { } earlier)
            {
                if (earlier.Backup.Length > 0) superseded.Add(Path.Combine(dir, earlier.Backup));
                m.Entries.Remove(earlier);
            }
            // The hash of nothing: no file is written under the name.
            m.Entries.Add(new Entry { Name = name, Hash = Engine.Sha([]), Owned = true, Backup = backup, BackupHash = old });
            changes.Add(new Change { Name = name, Before = before, After = null, Existed = true });
            log.Add($"MOVED to the backup: {name}");
        }

        var (retired, spent) = PlanRetire(dir, m, desired, retire ?? [], changes, log);
        spent.AddRange(superseded);

        // Journal precedes target writes. Uninstall can recover interrupted installs using hashes.
        // The journal still lists what is being taken out, so an uninstall after a crash here
        // finds it whether or not it went already.
        var hadManifest = File.Exists(manifestPath);
        var oldManifest = hadManifest ? Engine.Read(manifestPath) : [];
        m.State = "installing";
        Manifest.WriteAtomic(dir, m);

        InstallException? failure = null;
        foreach (var c in changes)
        {
            try
            {
                // Not every destination is in the game's root any more: the companion effect
                // goes into reshade-shaders\Shaders, which may not exist yet.
                var dst = Path.Combine(dir, c.Name);
                if (c.After is null)
                {
                    Remove(dst);
                    continue;
                }
                Engine.MakeParent(dst);
                Engine.Write(dst, c.After);
            }
            catch (InstallException e)
            {
                failure = e;
                break;
            }
        }

        if (failure is null)
        {
            m.Entries.RemoveAll(retired.Contains);
            m.State = "installed";
            try { Manifest.WriteAtomic(dir, m); }
            catch (InstallException e) { failure = e; }
        }

        if (failure is null)
        {
            // The originals are back where they were; their copies are not needed any more.
            foreach (var backup in spent) TryDelete(backup);
            return;
        }

        for (var i = changes.Count - 1; i >= 0; i--)
        {
            var c = changes[i];
            try
            {
                if (c.Existed) Engine.Write(Path.Combine(dir, c.Name), c.Before);
                else
                {
                    Engine.Writable(Path.Combine(dir, c.Name));
                    File.Delete(Path.Combine(dir, c.Name));
                }
            }
            catch
            {
                // Rollback is best effort by design: one file that cannot be put back must not stop
                // the rest from being put back.
            }
        }
        try
        {
            if (hadManifest) Engine.Write(manifestPath, oldManifest);
            else File.Delete(manifestPath);
        }
        catch
        {
            // Same: the failure that matters is the one being rethrown.
        }
        throw failure;
    }
}
