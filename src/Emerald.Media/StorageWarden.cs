using System.IO;
using Emerald.Core;
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
    /// The fewest segments to keep whatever the limit says. A master and the proxies it holds
    /// count as one, so for a recording in folders this is fifteen minutes.
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

            foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
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
    /// Every recording in the store, oldest first, each master gathered with the proxies whose
    /// time falls inside it.
    ///
    /// The master is cut at five minutes and the proxy shorter, so "the two halves" of a segment
    /// are one master and the several proxies it holds the picture of. Gathered by time - each
    /// laid end to end on its own lengths, as the timeline lays them - and not by when the files
    /// were last written, which put a master with whichever single proxy happened to close in the
    /// same second and left the rest to go on their own, ahead of the master that held them.
    /// </summary>
    private static List<Segment> Oldest(string root, DateTime now)
    {
        var all = new List<Segment>(Loose(root));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        string low = RecordingProfile.FolderFor(RecordingProfile.LowRes, root);
        string high = RecordingProfile.FolderFor(RecordingProfile.HighRes, root);

        foreach (string name in RecordingFolders(low).Union(RecordingFolders(high), StringComparer.OrdinalIgnoreCase))
        {
            DateTime rolled = RecordingSequence.FolderStartedAt(name)!.Value;

            List<Piece> proxies = Pieces(Path.Combine(low, name), rolled, seen);
            List<Piece> masters = Pieces(Path.Combine(high, name), rolled, seen);

            var groups = masters.Select(m =>
            {
                var group = new Segment(m.Written);
                group.Add(m.Path, m.Bytes, m.Written, m.Runs);
                return (Master: m, Group: group);
            }).ToList();

            foreach (Piece proxy in proxies)
            {
                // The master its start falls in. One straddling a boundary goes with the earlier.
                int at = groups.FindLastIndex(g => g.Master.Starts <= proxy.Starts);

                if (at >= 0 && (groups[at].Master.Runs is not { } runs || proxy.Starts < groups[at].Master.Starts + runs))
                {
                    groups[at].Group.Add(proxy.Path, proxy.Bytes, proxy.Written, proxy.Runs);
                    continue;
                }

                // A proxy with no master: recorded without one, or its master already gone.
                var alone = new Segment(proxy.Written);
                alone.Add(proxy.Path, proxy.Bytes, proxy.Written, proxy.Runs);
                all.Add(alone);
            }

            all.AddRange(groups.Select(g => g.Group));
        }

        // What was read of files that are gone is no use to anybody.
        lock (Lengths)
            foreach (string gone in Lengths.Keys.Where(k => !seen.Contains(k)).ToList())
                Lengths.Remove(gone);

        return all.Where(s => s.Written + TooRecent < now).OrderBy(s => s.Written).ToList();
    }

    /// <summary>One file of a recording folder, placed on the clock.</summary>
    private sealed record Piece(string Path, long Bytes, DateTime Written, DateTime Starts, TimeSpan? Runs);

    /// <summary>
    /// The picture lengths read so far, so a sweep reads only a file it has not seen. A sweep runs
    /// every time a segment closes, over a store of tens of thousands of files.
    /// </summary>
    private static readonly Dictionary<string, (long Bytes, long Ticks, TimeSpan? Runs)> Lengths =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>A recording folder's files in numbered order, each placed end to end on its own length.</summary>
    private static List<Piece> Pieces(string directory, DateTime rolled, HashSet<string> seen)
    {
        var files = new List<(FileInfo File, TimeSpan? Runs)>();

        try
        {
            if (!Directory.Exists(directory)) return new List<Piece>();

            foreach (FileInfo file in new DirectoryInfo(directory).EnumerateFiles())
            {
                if (!MediaScanner.IsPlayable(file.Name)) continue;

                seen.Add(file.FullName);
                files.Add((file, LengthOf(file)));
            }
        }
        catch (IOException) { return new List<Piece>(); }
        catch (UnauthorizedAccessException) { return new List<Piece>(); }

        files.Sort((a, b) => (RecordingSequence.NumberIn(a.File.Name) ?? 0).CompareTo(RecordingSequence.NumberIn(b.File.Name) ?? 0));

        DateTime[] starts = StoreIndex.LayEndToEnd(
            files.Select(f => (f.File.FullName, f.Runs ?? TimeSpan.Zero)).ToList(), rolled);

        return files.Select((f, i) => new Piece(f.File.FullName, f.File.Length, f.File.LastWriteTimeUtc, starts[i], f.Runs))
                    .ToList();
    }

    private static TimeSpan? LengthOf(FileInfo file)
    {
        lock (Lengths)
            if (Lengths.TryGetValue(file.FullName, out var known) &&
                known.Bytes == file.Length && known.Ticks == file.LastWriteTimeUtc.Ticks)
                return known.Runs;

        // The picture's length, as the timeline lays segments end to end by (see MediaInfo.Runs).
        TimeSpan? runs = MediaCodec.ReadPictureDuration(file.FullName) ?? MediaCodec.ReadDuration(file.FullName);
        if (runs is { } r && r <= TimeSpan.Zero) runs = null;

        lock (Lengths) Lengths[file.FullName] = (file.Length, file.LastWriteTimeUtc.Ticks, runs);
        return runs;
    }

    private static IEnumerable<string> RecordingFolders(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return Array.Empty<string>();

            return Directory.EnumerateDirectories(directory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => RecordingSequence.FolderStartedAt(n) is not null)
                .ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>
    /// The old flat files, from before recordings had folders, with both halves gathered together.
    ///
    /// Paired by the timestamp in the name rather than by exact filename, for the same reason
    /// <see cref="MediaLibrary"/> does: ffmpeg stamps each output when that muxer opens it, so
    /// the two halves of one recording routinely differ by a second.
    /// </summary>
    private static IEnumerable<Segment> Loose(string root)
    {
        var byTime = new SortedDictionary<DateTime, Segment>();

        Gather(RecordingProfile.FolderFor(RecordingProfile.LowRes, root));
        Gather(RecordingProfile.FolderFor(RecordingProfile.HighRes, root));

        return byTime.Values;

        void Gather(string folder)
        {
            try
            {
                if (!Directory.Exists(folder)) return;

                foreach (string path in Directory.EnumerateFiles(folder, "*", SearchOption.TopDirectoryOnly))
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

                    existing.Add(path, info.Length, info.LastWriteTimeUtc, null);
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        static DateTime Rounded(DateTime when) =>
            new(when.Ticks - when.Ticks % TimeSpan.TicksPerSecond, when.Kind);
    }

    /// <summary>One recording: its master, the proxies it holds, and what deleting them would free.</summary>
    private sealed class Segment
    {
        private readonly List<(string Path, long Bytes, TimeSpan? Runs)> _files = new();

        public Segment(DateTime written) => Written = written;

        public DateTime Written { get; private set; }

        public void Add(string path, long bytes, DateTime written, TimeSpan? runs)
        {
            _files.Add((path, bytes, runs));

            // A pair is as old as its younger half, not its older one: the master is usually
            // closed a moment after the proxy, and judging the pair on the proxy alone could
            // delete a master the recorder had only just finished writing.
            if (written > Written) Written = written;
        }

        /// <summary>Deletes every file of it and returns what was actually freed.</summary>
        public long Delete()
        {
            long freed = 0;

            // Oldest first within each folder, so the ledger, which keeps only the last one
            // deleted, moves forward through them.
            var files = _files
                .OrderBy(f => Path.GetDirectoryName(f.Path), StringComparer.OrdinalIgnoreCase)
                .ThenBy(f => RecordingSequence.NumberIn(Path.GetFileName(f.Path)) ?? 0)
                .ToList();

            foreach ((string path, long bytes, TimeSpan? known) in files)
            {
                // Read before the file goes: once it has, nothing knows how long it ran, and every
                // file after it in its folder would be placed that much too early. Each file's own
                // length - the master's is not the proxy's any more.
                TimeSpan? length = known ?? MediaCodec.ReadPictureDuration(path) ?? MediaCodec.ReadDuration(path);

                try
                {
                    File.Delete(path);
                    freed += bytes;

                    if (length is { } ran && ran > TimeSpan.Zero &&
                        Path.GetDirectoryName(path) is { } folder &&
                        RecordingSequence.FolderStartedAt(Path.GetFileName(folder)) is not null &&
                        RecordingSequence.NumberIn(Path.GetFileName(path)) is { } number)
                        PruneLedger.Record(folder, number, ran);
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
