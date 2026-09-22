// The transactional half of an install, shared by both routes. It records ownership, backs up
// anything it is about to displace, writes the journal *before* touching a target, and restores
// everything it moved if any write fails.

using System.Text;

namespace AmdNr.Core;

public static class Transaction
{
    /// <summary>Everything about the folder that would make the transaction fail halfway, checked
    /// before the journal is written so a refusal costs nothing and says why.
    ///
    /// This runs inside <see cref="Apply"/>, which is what folds it in front of both routes at once
    /// rather than leaving it as advice on one screen. The common case by far is the third one: the
    /// game is still open, and the old failure for that was an access-denied error from somewhere
    /// inside a copy.</summary>
    public static void Guard(string dir, SortedDictionary<string, byte[]> desired)
    {
        Engine.Require(Directory.Exists(dir), $"{dir} is not a folder.");
        Engine.Require(Engine.FolderIsWritable(dir),
            "That folder cannot be written to. It is either read-only or somewhere that needs "
            + "administrator rights -- run this installer as administrator, or move the game.");

        var held = desired.Keys.Where(n => Engine.IsLocked(Path.Combine(dir, n))).ToList();
        Engine.Require(held.Count == 0,
            $"{string.Join(", ", held)} {(held.Count == 1 ? "is" : "are")} open by another program. "
            + "The game or emulator is almost certainly still running -- close it and this line goes away.");

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
        public required byte[] After { get; init; }
        public required bool Existed { get; init; }
    }

    /// <summary><paramref name="desired"/> is whatever the route's own planner decided to write;
    /// this function is deliberately ignorant of what those files mean.</summary>
    public static void Apply(string dir, string preset, Route route,
        SortedDictionary<string, byte[]> desired, List<string> log)
    {
        Guard(dir, desired);
        var manifestPath = Path.Combine(dir, route.ManifestFileName());
        Engine.SafePath(manifestPath);

        var m = new Manifest(preset, route);
        if (File.Exists(manifestPath))
        {
            m = Manifest.Decode(Encoding.UTF8.GetString(Engine.Read(manifestPath)));
            Engine.Require(m.State == "installed",
                "Interrupted transaction: run uninstall/recovery before reinstall");
            Engine.Require(m.Preset == preset, "Uninstall previous preset before changing API");
            Engine.Require(m.Route == route,
                "That folder already has an install for the other architecture; uninstall it first");
        }

        var changes = new List<Change>();
        var stamp = ((ulong)(DateTime.UtcNow - DateTime.UnixEpoch).Ticks * 100).ToString();

        foreach (var (name, data) in desired)
        {
            Engine.Require(Engine.Allowed.Contains(name),
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
                throw new InstallException($"File changed since install; preserved: {name}");
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

        // Journal precedes target writes. Uninstall can recover interrupted installs using hashes.
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
            m.State = "installed";
            try { Manifest.WriteAtomic(dir, m); }
            catch (InstallException e) { failure = e; }
        }

        if (failure is null) return;

        for (var i = changes.Count - 1; i >= 0; i--)
        {
            var c = changes[i];
            try
            {
                if (c.Existed) Engine.Write(Path.Combine(dir, c.Name), c.Before);
                else File.Delete(Path.Combine(dir, c.Name));
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

    /// <summary>Undo an install using its own manifest. Files the installer did not own are left
    /// alone, files the user changed afterwards are kept with a warning, and anything displaced at
    /// install time is put back from its backup.</summary>
    public static void Uninstall(string dir, Route route, bool removeConfigs, List<string> log,
        bool force = false)
    {
        dir = Engine.Absolute(dir);
        Engine.SafePath(dir);
        var manifestPath = Path.Combine(dir, route.ManifestFileName());
        Engine.SafePath(manifestPath);
        Engine.Require(File.Exists(manifestPath), "No install manifest");

        var m = Manifest.Decode(Encoding.UTF8.GetString(Engine.Read(manifestPath)));
        var keep = new List<Entry>();

        foreach (var e in m.Entries)
        {
            var dst = Path.Combine(dir, e.Name);
            Engine.SafePath(dst);

            if (!e.Owned)
            {
                log.Add($"PRESERVED pre-existing identical file: {e.Name}");
                continue;
            }

            var backupPath = Path.Combine(dir, e.Backup);
            if (e.Backup.Length > 0)
            {
                Engine.SafePath(backupPath);
                Engine.HashIs(Engine.Read(backupPath), e.BackupHash, "Original backup");
            }

            var exists = File.Exists(dst);
            if (exists)
            {
                var current = Engine.HashFile(dst);
                if (current != e.Hash)
                {
                    if (m.State == "installing" && e.Backup.Length > 0 && current == e.BackupHash)
                    {
                        TryDelete(backupPath);
                        continue;
                    }
                    // A file that no longer hashes to what was written is somebody else's work
                    // now, so it is kept and the folder goes on counting as installed. Forced only
                    // by a caller that has been told exactly that and asked again -- and never for
                    // personal configuration, which has its own switch in removeConfigs.
                    if (!force || e.Configuration)
                    {
                        log.Add($"WARNING modified after install; retained with backup: {e.Name}");
                        keep.Add(e.Clone());
                        continue;
                    }
                    log.Add($"FORCED: {e.Name}");
                }
            }
            else if (e.Backup.Length == 0)
            {
                continue;
            }

            if (e.Configuration && e.Backup.Length == 0 && !removeConfigs)
            {
                log.Add($"PRESERVED personal/default configuration: {e.Name}");
                keep.Add(e.Clone());
                continue;
            }

            if (e.Backup.Length > 0)
            {
                Engine.Write(dst, Engine.Read(backupPath));
                TryDelete(backupPath);
                log.Add($"RESTORED: {e.Name}");
            }
            else if (TryDelete(dst))
            {
                log.Add($"REMOVED: {e.Name}");
            }
            else
            {
                // The file is still there, so the manifest goes on owning it. Dropping the entry
                // here was the whole bug: a DLL the running game still had open could not be
                // deleted, the log said REMOVED anyway, the entry went, and from then on every
                // uninstall found nothing to do while the add-on was still in the folder.
                log.Add($"WARNING {StillOpen}: {e.Name}. Close the game and uninstall again.");
                keep.Add(e.Clone());
            }
        }

        if (keep.Count == 0)
        {
            TryDelete(manifestPath);
        }
        else
        {
            m.Entries = keep;
            m.State = "installed";
            Manifest.WriteAtomic(dir, m);
        }
        log.Add("Uninstall complete; retained files/backups are listed above.");
    }

    /// <summary>The words a caller can look for to tell "the game still has it open" apart from
    /// "somebody changed it": one is waited out, the other is a decision.</summary>
    public const string StillOpen = "still open, so it is still here";

    /// <summary>True when the file is gone, which includes it never having been there. A file that
    /// will not delete does not fail the whole uninstall -- the rest still comes out -- but it is
    /// never reported as removed either, and the caller keeps its manifest entry so the next
    /// uninstall can finish the job.</summary>
    private static bool TryDelete(string path)
    {
        try
        {
            File.Delete(path);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
