using Emerald.Core;
using System.IO;
using Emerald.Video;

namespace Emerald.Media;

/// <summary>
/// One file of a continuous recording, once ffmpeg has finished it: a proxy segment, or a master.
///
/// The two are announced apart. A master runs five minutes and a proxy far less, so a proxy has
/// no master of its own to wait for - holding it back until "its" master closed would have kept
/// the newest proxies unannounced for up to five minutes.
/// </summary>
public sealed record CompletedSegment(
    /// <summary>Counted from 1 for this recording, proxies and masters each on their own count.</summary>
    int Number,

    /// <summary>The number in the filename.</summary>
    long Sequence,
    string Path,
    long Bytes,
    TimeSpan? Duration,
    bool IsMaster = false)
{
    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);

    public static string Size(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):F2} GB"
        : $"{bytes / (double)(1L << 20):F0} MB";
}

/// <summary>
/// Notices each file of a running recording as ffmpeg finishes it.
///
/// The recorder itself cannot say when a segment is done: it hands frames to one ffmpeg that
/// cuts the files on its own clock, and nothing comes back. So this looks at the store instead.
/// A file is finished when its movie header is on the disk — the header is written last, so
/// its presence is the one thing that cannot be true of a file still being recorded. That
/// makes the test structural rather than a guess from timestamps or from a newer sibling
/// having appeared, and it is eight milliseconds a file.
/// </summary>
public sealed class SegmentWatch
{
    private readonly StorePaths _paths;
    private readonly string _prefix;
    private readonly DateTime _sinceUtc;

    private readonly HashSet<string> _announced = new(StringComparer.OrdinalIgnoreCase);

    private const int ForgetEvery = 2000;
    private int _forgetAt = ForgetEvery;
    private int _number;
    private int _masterNumber;

    /// <param name="root">The store: the folder the low and high subfolders live in.</param>
    /// <param name="namePrefix">What the recording's files are called before the timestamp.</param>
    /// <param name="sinceUtc">When the recording started. Older files are somebody else's.</param>
    /// <param name="recordingFolder">
    /// The per-recording folder to watch, or null for the flat store an older recording wrote to.
    /// </param>
    public SegmentWatch(string root, string namePrefix, DateTime sinceUtc, string? recordingFolder = null)
        : this(StorePaths.Under(root), namePrefix, sinceUtc, recordingFolder)
    {
    }

    /// <param name="paths">The masters' folder and the proxies' folder, which may be on different drives.</param>
    public SegmentWatch(StorePaths paths, string namePrefix, DateTime sinceUtc, string? recordingFolder = null)
    {
        _paths = paths;
        _prefix = namePrefix;
        _sinceUtc = sinceUtc;
        _recordingFolder = recordingFolder;
    }

    private readonly string? _recordingFolder;

    /// <summary>The highest number written so far, for carrying the year's counter forward.</summary>
    public long HighestSequence { get; private set; } = -1;

    /// <summary>
    /// The files finished since the last call, proxies first and then masters, each oldest first.
    /// Cheap enough to call on a tick: only files not yet announced are opened, and a file
    /// without its header yet is rejected in the first few atoms.
    /// </summary>
    public IReadOnlyList<CompletedSegment> Poll()
    {
        var done = new List<CompletedSegment>();

        foreach (string proxy in Candidates(RecordingProfile.LowRes))
        {
            // Not finished: the header is not there yet. Look again next time.
            if (MediaCodec.ReadDuration(proxy) is not { } duration) continue;

            long sequence = Announce(proxy);
            if (sequence > HighestSequence) HighestSequence = sequence;

            done.Add(new CompletedSegment(++_number, sequence, proxy, SafeLength(proxy), duration));
        }

        foreach (string master in Candidates(RecordingProfile.HighRes))
        {
            if ((MediaCodec.ReadPictureDuration(master) ?? MediaCodec.ReadDuration(master)) is not { } duration)
                continue;

            long sequence = Announce(master);

            done.Add(new CompletedSegment(++_masterNumber, sequence, master, SafeLength(master), duration,
                                          IsMaster: true));
        }

        return done;
    }

    /// <summary>This recording's files of one output not yet announced, in name order.</summary>
    private List<string> Candidates(RecordingOutput output)
    {
        string folder = _paths.For(output);

        // Inside this recording's own folder when there is one, so a second recorder writing
        // at the same time into its own folder is never mistaken for this one's segments.
        if (_recordingFolder is not null) folder = Path.Combine(folder, _recordingFolder);

        try
        {
            if (!Directory.Exists(folder)) return new List<string>();

            return Directory.EnumerateFiles(folder, $"{_prefix}_*.{output.Extension}")
                .Where(p => !_announced.Contains(p))
                .Where(p => SafeWriteTimeUtc(p) >= _sinceUtc.AddSeconds(-2))
                .OrderBy(p => RecordingSequence.NumberIn(Path.GetFileName(p)) ?? 0)
                .ThenBy(p => p, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (IOException) { return new List<string>(); }
        catch (UnauthorizedAccessException) { return new List<string>(); }
    }

    private long Announce(string path)
    {
        _announced.Add(path);

        // A recording that runs for days announces thousands, and the storage limit has long
        // since deleted most of them. Those can never be candidates again.
        if (_announced.Count >= _forgetAt)
        {
            _announced.RemoveWhere(p => !File.Exists(p));
            _forgetAt = Math.Max(ForgetEvery, _announced.Count * 2);
        }

        return RecordingSequence.NumberIn(Path.GetFileName(path)) ?? -1;
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
