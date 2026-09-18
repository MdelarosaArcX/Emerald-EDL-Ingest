using System.IO;
using Emerald.Video;

namespace Emerald.Media;

/// <summary>One segment of a continuous recording, once both halves of it are on the disk.</summary>
public sealed record CompletedSegment(
    int Number,
    string ProxyPath,
    long ProxyBytes,
    string? MasterPath,
    long MasterBytes,
    TimeSpan? Duration)
{
    public string Name => Path.GetFileNameWithoutExtension(ProxyPath);

    public long TotalBytes => ProxyBytes + MasterBytes;

    public static string Size(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):F2} GB"
        : $"{bytes / (double)(1L << 20):F0} MB";
}

/// <summary>
/// Notices each segment of a running recording as ffmpeg finishes it.
///
/// The recorder itself cannot say when a segment is done: it hands frames to one ffmpeg that
/// cuts the files on its own clock, and nothing comes back. So this looks at the store instead.
/// A segment is finished when its movie header is on the disk — the header is written last, so
/// its presence is the one thing that cannot be true of a file still being recorded. That
/// makes the test structural rather than a guess from timestamps or from a newer sibling
/// having appeared, and it is eight milliseconds a file.
///
/// Both halves have to be there. The proxy and the master are cut by the same ffmpeg but
/// closed by two muxers, and the master — twenty times the size — can be a moment behind.
/// A segment announced with only its proxy would then be announced at half its size.
/// </summary>
public sealed class SegmentWatch
{
    private readonly string _root;
    private readonly string _prefix;
    private readonly DateTime _sinceUtc;

    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);
    private int _number;

    /// <param name="root">The store: the folder the low and high subfolders live in.</param>
    /// <param name="namePrefix">What the recording's files are called before the timestamp.</param>
    /// <param name="sinceUtc">When the recording started. Older files are somebody else's.</param>
    public SegmentWatch(string root, string namePrefix, DateTime sinceUtc)
    {
        _root = root;
        _prefix = namePrefix;
        _sinceUtc = sinceUtc;
    }

    /// <summary>
    /// The segments finished since the last call, oldest first. Cheap enough to call on a
    /// tick: only files not yet announced are opened, and a file without its header yet is
    /// rejected in the first few atoms.
    /// </summary>
    public IReadOnlyList<CompletedSegment> Poll()
    {
        string low = RecordingProfile.FolderFor(RecordingProfile.LowRes, _root);
        var done = new List<CompletedSegment>();

        IEnumerable<string> candidates;

        try
        {
            if (!Directory.Exists(low)) return done;

            candidates = Directory.EnumerateFiles(low, $"{_prefix}_*.{RecordingProfile.LowRes.Extension}")
                .Where(p => !_announced.Contains(p))
                .Where(p => SafeWriteTimeUtc(p) >= _sinceUtc.AddSeconds(-2))
                .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException) { return done; }
        catch (UnauthorizedAccessException) { return done; }

        MediaLibrary.MasterIndex? masters = null;

        foreach (string proxy in candidates)
        {
            // Not finished: the header is not there yet. Look again next time.
            if (MediaCodec.ReadDuration(proxy) is not { } duration) continue;

            masters ??= new MediaLibrary.MasterIndex(_root);
            string? master = masters.For(proxy);

            // The proxy is closed but the master is not. The next poll will find both.
            if (master is not null && MediaCodec.ReadFourCc(master) is null) continue;

            _announced.Add(proxy);

            done.Add(new CompletedSegment(
                Number: ++_number,
                ProxyPath: proxy,
                ProxyBytes: SafeLength(proxy),
                MasterPath: master,
                MasterBytes: master is null ? 0 : SafeLength(master),
                Duration: duration));
        }

        return done;
    }

    private static DateTime SafeWriteTimeUtc(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }

    private static long SafeLength(string path)
    {
        try { return new FileInfo(path).Length; }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }
}
