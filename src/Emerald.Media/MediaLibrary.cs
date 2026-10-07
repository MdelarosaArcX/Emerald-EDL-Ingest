using System.IO;
using Emerald.Core;
using Emerald.Video;

namespace Emerald.Media;

/// <summary>One recording in the store, with whatever ffprobe could tell us about it.</summary>
public sealed record CapturedClip(
    string Path,
    string Name,
    DateTime Recorded,
    long Bytes,
    MediaInfo? Info,
    string? MasterPath = null,

    /// <summary>
    /// What the master was encoded with - "ProRes 422", "DNxHD" - read from the container
    /// rather than probed, because a thousand ffprobe calls is three minutes and this is
    /// eight milliseconds a file.
    /// </summary>
    string? MasterCodec = null,

    /// <summary>
    /// When this segment begins, worked out across its recording folder rather than from its own
    /// name. Null for a clip that could not be placed. See <see cref="StartsAt"/>.
    /// </summary>
    DateTime? Starts = null)
{
    /// <summary>True when this is the proxy of a pair and its master is on disk beside it.</summary>
    public bool HasMaster => MasterPath is not null;

    /// <summary>
    /// What the master is, for a column: the codec when one was found, or why not.
    ///
    /// This is the answer to "has this one been converted yet" — a clip recorded before the
    /// change reads ProRes 422, one recorded after it reads DNxHD.
    ///
    /// A codec that cannot be read is almost always a recorder that has not finished: ffmpeg
    /// writes the index last, so the segment being written has no readable codec and no readable
    /// duration either. Saying "codec unread" of it was technically true and useless — it read as
    /// a fault when it is the ordinary state of the newest file in a running store. It says what
    /// is actually happening now, and keeps the old wording for the genuinely odd case of a
    /// finished file whose codec still cannot be read.
    /// </summary>
    public string MasterText =>
        StillBeingWritten ? "recording in-progress"
        : MasterPath is null ? "no master"
        : MasterCodec ?? "master, codec unread";


    public string SizeText => Bytes >= 1L << 30
        ? $"{Bytes / (double)(1L << 30):F1} GB"
        : $"{Bytes / (double)(1L << 20):F0} MB";

    public string FormatText => Info is null
        ? "not probed"
        : $"{Info.Width}x{Info.Height} {Info.VideoCodec}{(Info.HasAudio ? " + audio" : ", silent")}";

    public string DurationText => Info is null
        ? "-"
        : $"{(int)Info.Duration.TotalMinutes:00}:{Info.Duration.Seconds:00}";

    /// <summary>
    /// When this clip begins on the station clock, from the stamp in its name — or, for a file
    /// with no stamp, its last write less however long it runs.
    ///
    /// What a timeline laid out on the day is drawn from. Null only for a file that has neither,
    /// which is a clip that cannot be placed and is better left off a clock than put in the
    /// wrong place on it.
    /// </summary>
    public DateTime? StartsAt =>
        Starts
        ?? MediaLibrary.StartedAt(Path)
        ?? (Info is { } info && info.Duration > TimeSpan.Zero ? Recorded - info.Duration : null);

    /// <summary>
    /// True while the recorder still has this segment open.
    ///
    /// ffmpeg writes the index last, so a segment being recorded has no readable duration. It is
    /// on the disk, it is growing, and it cannot be played or scrubbed until the recorder moves
    /// on to the next one — which is exactly the state an operator needs told about rather than
    /// left to discover by clicking it.
    /// </summary>
    public bool StillBeingWritten => Info is null || Info.Duration <= TimeSpan.Zero;

    /// <summary>
    /// How many frames the clip holds, at a given rate — the unit Live Edit counts everything
    /// in, so it is worth having beside the duration rather than being re-derived at each use.
    /// </summary>
    public long FrameCount(int frameRate) =>
        Info is { } info && info.Duration > TimeSpan.Zero && frameRate > 0
            ? (long)Math.Round(info.Duration.TotalSeconds * frameRate)
            : 0;

    /// <summary>
    /// The frame count for a column, at the rate the store was listed with.
    ///
    /// Parameterless so it can be bound straight from XAML. The rate rides along on the probed
    /// timecode, which is the rate the listing was asked for in the first place.
    /// </summary>
    /// <summary>
    /// The name without its extension, for a list an operator reads.
    ///
    /// Every file in the store is one of two extensions and both are an implementation detail of
    /// the proxy/master pair — ".mp4" on every row is thirty characters of nothing.
    /// </summary>
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Name);

    public string FrameText =>
        Info is { } info && info.Duration > TimeSpan.Zero && info.StartTimecode.Rate > 0
            ? $"{FrameCount(info.StartTimecode.Rate):N0} frames"
            : "frames unknown";
}

/// <summary>
/// The store of everything Emerald has captured — full-resolution video with its audio,
/// exactly as it came off the wire.
///
/// The recorder writes here and Live Edit reads from here, so the folder is settled in one
/// place instead of being a path each module happens to be pointed at. Nothing is copied or
/// transcoded on the way in: what lands on disk is what the encoder produced.
/// </summary>
public static class MediaLibrary
{
    /// <summary>Captures alongside the solution, so the store travels with the install.</summary>
    public static string DefaultFolder { get; } = ResolveDefaultFolder();

    /// <summary>The store in use, honouring the operator's override in settings.</summary>
    public static string FolderFor(AppSettings settings) =>
        string.IsNullOrWhiteSpace(settings.CaptureFolder) ? DefaultFolder : settings.CaptureFolder;

    /// <summary>
    /// Everything in the store, newest first. <paramref name="ffprobePath"/> may be null, in
    /// which case clips are listed without format detail rather than not listed at all — the
    /// operator should still see their recordings when ffmpeg is missing.
    /// </summary>
    public static IReadOnlyList<CapturedClip> List(string folder, string? ffprobePath, int frameRate = 25)
    {
        var clips = new List<CapturedClip>();
        var masters = new MasterIndex(folder);

        // The proxies first, then anything sitting loose in the store — recordings made before
        // the store grew its two halves. The masters are deliberately not listed: they are the
        // same pictures at a size that would make the strip crawl, and each is reachable from
        // the proxy that stands for it.
        //
        // The proxy folder is walked all the way down, because a recording writes into a folder
        // of its own inside it. The store root is walked ONE level only: it contains the proxy
        // and master folders, and descending would list every proxy a second time and every
        // master as though it were a clip of its own.
        Collect(RecordingProfile.FolderFor(RecordingProfile.LowRes, folder), isProxy: true,
                SearchOption.AllDirectories);

        Collect(folder, isProxy: false, SearchOption.TopDirectoryOnly);

        clips.Sort((a, b) => b.Recorded.CompareTo(a.Recorded));
        return Accumulate(clips, frameRate);

        void Collect(string from, bool isProxy, SearchOption depth)
        {
            try
            {
                if (!Directory.Exists(from)) return;

                foreach (string path in Directory.EnumerateFiles(from, "*", depth))
                {
                    if (!MediaScanner.IsPlayable(path)) continue;

                    var file = new FileInfo(path);

                    // A segment still being written has no usable duration yet; probing it is
                    // harmless but pointless, so it is listed bare until the recorder moves on.
                    MediaInfo? info = ffprobePath is null ? null : MediaProbe.Probe(ffprobePath, path, frameRate);

                    string? master = isProxy ? masters.For(path) : null;

                    clips.Add(new CapturedClip(path, file.Name, file.LastWriteTime, file.Length, info,
                                               master,
                                               master is null ? null : MediaCodec.Read(master)));
                }
            }
            catch (IOException)
            {
                // A store on a disconnected drive lists as empty rather than taking the UI down.
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// When a recording's segment began.
    ///
    /// Two schemes, and the newer one is exact. A segment written into a per-recording folder
    /// takes its start from the folder's name plus the segments before it, which is frame-exact
    /// — the folder is stamped once when Record was pressed and the lengths are measured. The
    /// older flat files carry a timestamp in the name instead, written by <c>-strftime</c> to the
    /// second, so those are a second out at worst; they are still read, because a plant's
    /// existing recordings do not stop mattering when the naming changes.
    ///
    /// <paramref name="withinFolder"/> is how far into the recording this segment starts, which
    /// only the caller can know — it needs the lengths of the segments before it. Left out, the
    /// answer is the folder's own start, which is right for the first segment and early for the
    /// rest.
    /// </summary>
    public static DateTime? StartedAt(string path, TimeSpan? withinFolder = null)
    {
        // The newer scheme: the folder says when, the name says which.
        string? folder = Path.GetFileName(Path.GetDirectoryName(path));

        if (folder is not null &&
            RecordingSequence.FolderStartedAt(folder) is { } rolled &&
            RecordingSequence.NumberIn(Path.GetFileName(path)) is not null)
            return rolled + (withinFolder ?? TimeSpan.Zero);

        // The older scheme: a timestamp in the name, to the second.
        string name = Path.GetFileNameWithoutExtension(path);
        int underscore = name.LastIndexOf('_');

        if (underscore <= 0 || underscore < 11) return null;

        string stamp = name[(underscore - 10)..];

        return DateTime.TryParseExact(stamp, "yyyy-MM-dd_HH-mm-ss", null,
                                      System.Globalization.DateTimeStyles.None, out DateTime when)
            ? when
            : null;
    }

    /// <summary>The per-recording folder a segment belongs to, or null for a flat old-style file.</summary>
    public static string? RecordingFolder(string path)
    {
        string? folder = Path.GetFileName(Path.GetDirectoryName(path));

        return folder is not null && RecordingSequence.FolderStartedAt(folder) is not null
            ? folder
            : null;

    }


    /// <summary>
    /// Fills in each segment's exact start, folder by folder.
    ///
    /// Within a per-recording folder the segments are numbered, so the first begins when the
    /// folder says the recording rolled and every one after it begins where the last ended. That
    /// is frame-exact, and it is the reason the naming moved: a timestamp in a filename is
    /// written to the second and is therefore up to a second wrong, which shows at frame zoom.
    ///
    /// Done here, once per listing, rather than left to each caller — a start that is exact for
    /// the timeline and approximate for the clip list would be two answers to one question.
    /// Ordered by the number rather than by name, because a count past six digits is wider and
    /// would otherwise sort before the narrower ones.
    /// </summary>
    private static IReadOnlyList<CapturedClip> Accumulate(List<CapturedClip> clips, int frameRate)
    {
        var placed = new Dictionary<string, CapturedClip>();

        foreach (IGrouping<string, CapturedClip> group in clips
                     .Where(c => RecordingFolder(c.Path) is not null)
                     .GroupBy(c => RecordingFolder(c.Path)!))
        {
            if (RecordingSequence.FolderStartedAt(group.Key) is not { } rolled) continue;

            DateTime at = rolled;

            foreach (CapturedClip clip in group
                         .OrderBy(c => RecordingSequence.NumberIn(Path.GetFileName(c.Path)) ?? 0))
            {
                placed[clip.Path] = clip with { Starts = at };

                // A segment still open has no length to read, so the run stops being exact from
                // there on. It is the last one anyway, so there is nothing after it to be wrong.
                if (clip.Info is { } info && info.Duration > TimeSpan.Zero) at += info.Duration;
            }
        }

        return placed.Count == 0
            ? clips
            : clips.Select(c => placed.TryGetValue(c.Path, out CapturedClip? exact) ? exact : c).ToList();
    }

    /// <summary>
    /// The masters in a store, indexed so a proxy can find its own.
    ///
    /// Built once per listing rather than searched per proxy: a thousand proxies each scanning
    /// a thousand masters is a million comparisons for an answer that is the same every time.
    /// </summary>
    internal sealed class MasterIndex
    {
        private readonly Dictionary<string, string> _byName = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<(DateTime Stamp, string Path)> _byTime = new();

        public MasterIndex(string root)
        {
            string folder = RecordingProfile.FolderFor(RecordingProfile.HighRes, root);

            try
            {
                if (!Directory.Exists(folder)) return;

                foreach (string path in Directory.EnumerateFiles(
                             folder, $"*.{RecordingProfile.HighRes.Extension}",
                             SearchOption.AllDirectories))
                {
                    // Keyed by the recording folder as well as the name, because the numbers
                    // restart in no folder but two recordings can still hold the same number
                    // only if a counter was reset — and because the folder is what makes the
                    // pairing exact. The bare name stays keyed too, for the flat old files.
                    _byName[Key(path)] = path;
                    _byName.TryAdd(Path.GetFileNameWithoutExtension(path), path);

                    if (StampIn(path) is { } stamp) _byTime.Add((stamp, path));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }

            _byTime.Sort((a, b) => a.Stamp.CompareTo(b.Stamp));
        }

        /// <summary>
        /// The master belonging to a proxy.
        ///
        /// By name first, which is right whenever the two outputs opened their segment inside
        /// the same second. They often do not: ffmpeg stamps each output's filename when that
        /// muxer opens it, so a segment that straddles a second boundary is written as
        /// <c>high\..._17-26-33.mov</c> beside <c>low\..._17-26-32.mp4</c>. On this plant's own
        /// store that was 259 of 954 proxies reporting no master with the master sitting right
        /// there. So a near miss in time is accepted too — the pair are still frame-for-frame
        /// the same picture, they were simply named a moment apart.
        /// </summary>
        /// <summary>Recording folder plus name, which is what makes a new-scheme pairing exact.</summary>
        private static string Key(string path) =>
            $"{RecordingFolder(path) ?? ""}/{Path.GetFileNameWithoutExtension(path)}";

        public string? For(string proxyPath)
        {
            // The recording folder and the number together, first. Under the newer scheme this
            // always hits and is exact — the two outputs are cut by one encoder and given the
            // same number in the same folder — so none of the guesswork below is reached.
            if (_byName.TryGetValue(Key(proxyPath), out string? paired)) return paired;

            string stem = Path.GetFileNameWithoutExtension(proxyPath);

            if (_byName.TryGetValue(stem, out string? exact)) return exact;
            if (StampIn(proxyPath) is not { } want || _byTime.Count == 0) return null;

            // Segments are minutes apart, so anything inside a couple of seconds is the pair
            // and anything outside it is a different segment. There is no ambiguous middle.
            var tolerance = TimeSpan.FromSeconds(2);

            string? best = null;
            TimeSpan closest = TimeSpan.MaxValue;

            foreach ((DateTime stamp, string path) in _byTime)
            {
                TimeSpan gap = (stamp - want).Duration();

                if (gap > tolerance) continue;
                if (gap >= closest) continue;

                closest = gap;
                best = path;
            }

            return best;
        }

        /// <summary>
        /// The time in a segment's name — <c>capture_2026-09-09_13-02-41.mov</c>.
        ///
        /// From the name rather than the file's own timestamp: a copy or a restore rewrites
        /// the timestamp, and the pair would then no longer find each other.
        /// </summary>
        /// <summary>One rule for reading a segment's start out of its name.</summary>
        private static DateTime? StampIn(string path) => StartedAt(path);
    }



    /// <summary>
    /// Reads one file back into the store's own vocabulary. This is how a module that has
    /// just written a clip registers it: the file is on disk where the store already looks,
    /// and what comes back is the store's description of it — size, codec, raster, length —
    /// rather than the writer's own account of what it thinks it wrote.
    ///
    /// Returns null when the path is not there, which is the only honest answer to "did the
    /// recording produce a file". <paramref name="ffprobePath"/> may be null, in which case
    /// the clip is described without format detail.
    /// </summary>
    public static CapturedClip? Describe(string path, string? ffprobePath, int frameRate = 25)
    {
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists) return null;

            return new CapturedClip(
                Path: file.FullName,
                Name: file.Name,
                Recorded: file.LastWriteTime,
                Bytes: file.Length,
                Info: ffprobePath is null ? null : MediaProbe.Probe(ffprobePath, file.FullName, frameRate));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Convenience overload for callers that already hold settings.</summary>
    public static IReadOnlyList<CapturedClip> List(AppSettings settings, int frameRate = 25) =>
        List(FolderFor(settings), MediaProbe.LocateFfprobe(Ffmpeg.Locate(settings.FfmpegPath)), frameRate);

    private static string ResolveDefaultFolder()
    {
        // Walk up out of bin\<Config>\<tfm>\<rid> to the solution root when running from a
        // build tree; fall back to beside the executable for a copied install.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);

        for (int up = 0; up < 6 && dir is not null; up++, dir = dir.Parent)
        {
            if (dir.EnumerateFiles("Emerald.sln").Any())
                return Path.Combine(dir.FullName, "media");
        }

        return Path.Combine(AppContext.BaseDirectory, "media");
    }
}
