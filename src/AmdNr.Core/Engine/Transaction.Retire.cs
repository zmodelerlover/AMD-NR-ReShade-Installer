// What an install takes back out: files an earlier install recorded that the route does not want in
// the folder any more -- the mochizuki runtime, once it is unticked. Without this an install only
// ever added, so a runtime left behind stayed recorded and was compared against every newer pin: a
// folder "out of date" that no install could bring up to date. It is part of the same transaction
// as what goes in, planned before the journal, written with it and rolled back with it.

namespace AmdNr.Core;

public static partial class Transaction
{
    /// <summary>Plans taking out each recorded file in <paramref name="retire"/> that
    /// <paramref name="desired"/> does not write, the way uninstall takes it: what the install wrote
    /// goes, and what it displaced comes back from its backup. A file changed since stays, entry and
    /// all, with a warning -- except one the runtime rewrites on its own, which is this app's either way.
    /// An entry the install only recorded -- an identical copy that was already there -- is dropped
    /// and its file left, since it was somebody's before it was recorded.
    ///
    /// Adds the writes to <paramref name="changes"/> and returns the entries to drop once everything is
    /// written, and the backups that are spent by then.</summary>
    private static (HashSet<Entry> Retired, List<string> Spent) PlanRetire(string dir, Manifest m,
        SortedDictionary<string, byte[]> desired, IEnumerable<string> retire, List<Change> changes, List<string> log)
    {
        var retired = new HashSet<Entry>(ReferenceEqualityComparer.Instance);
        var spent = new List<string>();
        foreach (var name in retire.Distinct(StringComparer.Ordinal).Where(n => !desired.ContainsKey(n)))
        {
            if (m.Entries.Find(x => x.Name == name) is not { Configuration: false } entry) continue;
            Engine.Require(Engine.IsAllowed(name), $"Refusing to remove an unmanaged filename: {name}");
            var dst = Path.Combine(dir, name);
            Engine.SafePath(dst);
            var exists = File.Exists(dst);
            if (!entry.Owned)
            {
                log.Add($"PRESERVED pre-existing identical file: {name}");
                retired.Add(entry);
                continue;
            }

            var maintained = Engine.IsRuntimeMaintained(name);
            if (exists && !maintained && Engine.HashFile(dst) != entry.Hash)
            {
                log.Add($"WARNING modified after install; retained with backup: {name}");
                continue;
            }
            Engine.Require(!exists || !Engine.IsLocked(dst),
                $"{name} is open by another program. The game is almost certainly still running -- "
                + "close it and this line goes away.");

            // What the runtime rewrites has nothing of anybody's in its backup either: uninstall
            // discards that copy too (Work.Uninstall's Ours).
            byte[]? original = null;
            if (entry.Backup.Length > 0)
            {
                var backup = Path.Combine(dir, entry.Backup);
                Engine.SafePath(backup);
                if (File.Exists(backup))
                {
                    if (!maintained)
                    {
                        original = Engine.Read(backup);
                        Engine.HashIs(original, entry.BackupHash, "Original backup");
                    }
                    spent.Add(backup);
                }
            }

            retired.Add(entry);
            if (!exists && original is null) continue;
            changes.Add(new Change { Name = name, Before = exists ? Engine.Read(dst) : [], After = original, Existed = exists });
            log.Add(original is null ? $"REMOVED: {name}" : $"RESTORED: {name}");
        }
        return (retired, spent);
    }

    /// <summary>Takes one file out, as an install write: a failure is the transaction's, and rolls
    /// back everything before it.</summary>
    private static void Remove(string path)
    {
        try
        {
            Engine.Writable(path);
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new InstallException($"Cannot remove {path}: {e.Message}");
        }
    }
}
