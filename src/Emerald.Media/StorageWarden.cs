using System.IO;
using Emerald.Video;

namespace Emerald.Media;

/// <summary>What a sweep of the store did, so it can be reported rather than done quietly.</summary>
public readonly record struct StorageSweep(
    int Deleted,
    long FreedBytes,
    long BeforeBytes,
    long AfterBytes,
    long LimitBytes)
{
    public bool DidAnything => Deleted > 0;

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 40 => $"{bytes / (double)(1L << 40):F2} TB",
        >= 1L << 30 => $"{bytes / (double)(1L << 30):F1} GB",
        _ => $"{bytes / (double)(1L << 20):F0} MB",
    };

    public override string ToString() =>
        $"{Deleted} segment(s) deleted, {Size(FreedBytes)} freed - " +
        $"store now {Size(AfterBytes)} of {Size(LimitBytes)}";
}

/// <summary>
/// Keeps the capture store under a size the operator set, by deleting the oldest recordings
/// to make room for the newest.
///
/// A rolling window rather than a wall: a recording that stops because the disk is full has
/// lost whatever came after it, which for a plant that records continuously is the wrong
/// failure. Keeping the last N hundred gigabytes and letting the rest go is what an operator
/// actually wants from a loop recorder.
///
/// <b>This deletes the operator's recordings.</b> So it will only ever do so when a limit has
/// been set deliberately, it takes the oldest first, it never touches a file that is still
/// being written, and every deletion is reported. It also stops rather than emptying the
/// store: there is a floor below which it refuses to keep cutting, because a limit set far
/// too low should leave the operator with their recent material and a warning, not with
/// nothing.
/// </summary>
public static class StorageWarden
{
    /// <summary>
    /// A file this recently written is assumed to be the one the recorder has open.
    ///
    /// Generous on purpose: the cost of waiting another sweep before deleting something is
    /// nothing, and the cost of deleting the segment being recorded is a hole in the middle
    /// of the recording.
    /// </summary>
    public static readonly TimeSpan TooRecent = TimeSpan.FromMinutes(2);

    /// <summary>
    /// The fewest segment pairs to keep whatever the limit says.
    ///
    /// A limit smaller than a single segment would otherwise delete each file as it was
    /// finished and leave the store permanently empty, which is not a rolling window — it is
    /// a recording that goes nowhere.
    /// </summary>
    public const int AlwaysKeep = 3;

    /// <summary>Total bytes of both halves of the store.</summary>
    public static long Size(string root) =>
        Half(root, RecordingProfile.LowRes) + Half(root, RecordingProfile.HighRes);

    private static long Half(string root, RecordingOutput output)
    {
        try
        {
            string folder = RecordingProfile.FolderFor(output, root);
            if (!Directory.Exists(folder)) return 0;

            long total = 0;

            foreach (string path in Directory.EnumerateFiles(folder))
            {
                try { total += new FileInfo(path).Length; }
                catch (IOException) { }
            }

            return total;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    /// <summary>
    /// Deletes the oldest recordings until the store fits, and reports what it did.
    ///
    /// <paramref name="limitBytes"/> of zero or less means no limit, and nothing is touched.
    /// A pair is deleted together — the proxy and the master are one recording, and keeping
    /// half of it would leave a clip that cannot be edited and a strip entry that cannot be
    /// played.
    /// </summary>
    public static StorageSweep Enforce(string root, long limitBytes, DateTime now)
    {
        long before = Size(root);

        if (limitBytes <= 0 || before <= limitBytes)
            return new StorageSweep(0, 0, before, before, limitBytes);

        List<Segment> segments = Oldest(root, now);

        long freed = 0;
        int deleted = 0;
        long size = before;

        foreach (Segment segment in segments)
        {
            if (size <= limitBytes) break;

            // Never below the floor, however small the limit is.
            if (segments.Count - deleted <= AlwaysKeep) break;

            long went = segment.Delete();
            if (went == 0) continue;

            freed += went;
            size -= went;
            deleted++;
        }

        return new StorageSweep(deleted, freed, before, size, limitBytes);
    }

    /// <summary>
    /// Every recording in the store, oldest first, with both halves gathered together.
    ///
    /// Paired by the timestamp in the name rather than by exact filename, for the same reason
    /// <see cref="MediaLibrary"/> does: ffmpeg stamps each output when that muxer opens it, so
    /// the two halves of one recording routinely differ by a second.
    /// </summary>
    private static List<Segment> Oldest(string root, DateTime now)
    {
        var byTime = new SortedDictionary<DateTime, Segment>();

        Gather(RecordingProfile.FolderFor(RecordingProfile.LowRes, root));
        Gather(RecordingProfile.FolderFor(RecordingProfile.HighRes, root));

        return byTime.Values.Where(s => s.Written + TooRecent < now).ToList();

        void Gather(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;

                foreach (string path in Directory.EnumerateFiles(folder))
                {
                    if (!MediaScanner.IsPlayable(path)) continue;

                    var info = new FileInfo(path);
                    DateTime key = Rounded(info.LastWriteTimeUtc);

                    // Within a couple of seconds of one already seen: the other half of it.
                    Segment? existing = null;

                    foreach (DateTime near in new[] { key, key.AddSeconds(-1), key.AddSeconds(1) })
                    {
                        if (byTime.TryGetValue(near, out Segment? found)) { existing = found; break; }
                    }

                    if (existing is null)
                    {
                        existing = new Segment(info.LastWriteTimeUtc);
                        byTime[key] = existing;
                    }

                    existing.Add(path, info.Length, info.LastWriteTimeUtc);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static DateTime Rounded(DateTime when) =>
            new(when.Ticks - when.Ticks % TimeSpan.TicksPerSecond, when.Kind);
    }

    /// <summary>One recording: its proxy, its master, and what deleting both would free.</summary>
    private sealed class Segment
    {
        private readonly List<(string Path, long Bytes)> _files = new();

        public Segment(DateTime written) => Written = written;

        public DateTime Written { get; private set; }

        public void Add(string path, long bytes, DateTime written)
        {
            _files.Add((path, bytes));

            // A pair is as old as its younger half, not its older one: the master is usually
            // closed a moment after the proxy, and judging the pair on the proxy alone could
            // delete a master the recorder had only just finished writing.
            if (written > Written) Written = written;
        }

        /// <summary>Deletes both halves and returns what was actually freed.</summary>
        public long Delete()
        {
            long freed = 0;

            foreach ((string path, long bytes) in _files)
            {
                try
                {
                    File.Delete(path);
                    freed += bytes;
                }
                catch (IOException)
                {
                    // Open in a player, or on a drive that went away. Left alone; the next
                    // sweep will find it again.
                }
                catch (UnauthorizedAccessException) { }
            }

            return freed;
        }
    }
}
