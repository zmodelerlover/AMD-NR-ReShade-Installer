// Replacing the running executable with a newer one.
//
// The obstacle is that Windows will not let anything write over a running image. It will, however,
// let it be *renamed* -- the lock is on the file's contents, not on its directory entry. So the
// swap is: move the running exe aside, put the new one where it was, start it, exit. The old one
// goes on running from its new name until the process ends, and the next start sweeps it up.
//
// Nothing here downloads. The bytes arrive already verified against the hash the release publishes,
// because this is the one place in the app that writes something and then runs it: an unverified
// update is a remote code execution with extra steps, and "the connection looked fine" is not a
// check. Verify is here rather than beside the download so it cannot be skipped by a caller.

using System.Security.Cryptography;

namespace AmdNr.Core;

public static class AppUpdater
{
    /// <summary>The suffix the outgoing executable is parked under. Left in the same folder on
    /// purpose: moving across volumes can fail, and the folder is one this app already writes to.
    /// </summary>
    public const string OldSuffix = ".old";

    /// <summary>Whether these bytes are what the release says that file is. `sums` is the
    /// SHA256SUMS.txt the release publishes, in the shape sha256sum writes.</summary>
    public static bool Verify(byte[] bytes, string sums, string name)
    {
        var want = ParseSums(sums).TryGetValue(name, out var h) ? h : null;
        if (want is null) return false;
        return string.Equals(Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), want,
            StringComparison.Ordinal);
    }

    /// <summary>sha256sum output: the hash, then the name, with or without the asterisk that means
    /// binary. A line that is not that is not a pin and is skipped rather than guessed at.</summary>
    internal static Dictionary<string, string> ParseSums(string text)
    {
        var found = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Split('\n'))
        {
            var parts = raw.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2 || parts[0].Length != 64) continue;
            if (!parts[0].All(Uri.IsHexDigit)) continue;
            found[Path.GetFileName(parts[1].TrimStart('*'))] = parts[0].ToLowerInvariant();
        }
        return found;
    }

    /// <summary>Puts <paramref name="staged"/> where <paramref name="current"/> is and returns the
    /// path the outgoing one was parked at, so the caller can say so. Throws rather than half-doing
    /// it: if the new file cannot be put in place the old one is moved back, because an app that
    /// deleted itself and failed to land the replacement is the worst outcome here.</summary>
    public static string Swap(string current, string staged)
    {
        Engine.Require(File.Exists(staged), "The downloaded update is not where it was put.");
        Engine.Require(new FileInfo(staged).Length > 0, "The downloaded update is empty.");

        var parked = current + OldSuffix;
        if (File.Exists(parked)) TryDelete(parked);       // a sweep that could not run last time
        File.Move(current, parked);                        // allowed while it is running
        try
        {
            File.Move(staged, current);
        }
        catch (Exception)
        {
            File.Move(parked, current);                    // put it back; nothing was lost
            throw;
        }
        return parked;
    }

    /// <summary>Removes what the last swap parked. Called at startup, where the previous process has
    /// exited and the file is finally deletable; a copy still held by something is left for the next
    /// run rather than reported, because there is nothing for anyone to do about it.
    ///
    /// That one file, by name. It was every *.old in the folder, and the folder is wherever the exe
    /// was put -- a desktop, Downloads -- so every start deleted other people's .old files there,
    /// past the Recycle Bin.</summary>
    public static void SweepOld(string current) => TryDelete(current + OldSuffix);

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}
