using System.Collections.Concurrent;
using System.IO;

namespace Emerald.Media;

/// <summary>
/// How long each finished segment's picture runs, read once per file for the whole application.
///
/// Live Edit's store index and the storage limit both need every segment's length - the index to
/// lay segments end to end, the limit to know which master holds which proxies - and each used
/// to read every file for itself: twice the opening of every master, each a seek to its index at
/// the end of a 4 GB file, on the disk the recorder is writing to. Kept here, the second asker
/// gets the first one's answer.
///
/// Keyed on length and write time as well as path, so a file that changes is read again. Only a
/// finished file is kept: one still being written has no length yet and is asked again.
/// </summary>
public static class MediaLengths
{
    private static readonly ConcurrentDictionary<string, (long Bytes, long Ticks, TimeSpan Runs)> Known =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Beyond this the oldest knowledge is let go of all at once; a store this large is not one.</summary>
    private const int Most = 200_000;

    private static int _reads;

    /// <summary>Lengths remembered, for the memory monitor.</summary>
    public static int Remembered => Known.Count;

    /// <summary>Files actually opened to read a length, for the tests.</summary>
    public static int Reads => _reads;

    /// <summary>
    /// The picture's own length - not the movie's, whose sound runs a few milliseconds past the
    /// last frame - or the movie's when the picture does not say. Null while still being written.
    /// </summary>
    public static TimeSpan? Picture(FileInfo file)
    {
        if (Known.TryGetValue(file.FullName, out var known) &&
            known.Bytes == file.Length && known.Ticks == file.LastWriteTimeUtc.Ticks)
            return known.Runs;

        Interlocked.Increment(ref _reads);

        TimeSpan? runs = MediaCodec.ReadPictureDuration(file.FullName) ?? MediaCodec.ReadDuration(file.FullName);
        if (runs is not { Ticks: > 0 } finished) return null;

        if (Known.Count >= Most) Known.Clear();
        Known[file.FullName] = (file.Length, file.LastWriteTimeUtc.Ticks, finished);

        return finished;
    }

    /// <summary>Lets go of every file not in <paramref name="present"/>: deleted files leave nothing behind.</summary>
    public static void KeepOnly(IReadOnlySet<string> present)
    {
        foreach (string path in Known.Keys)
            if (!present.Contains(path)) Known.TryRemove(path, out _);
    }
}
