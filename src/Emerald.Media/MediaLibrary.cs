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
    string? MasterCodec = null)
{
    /// <summary>True when this is the proxy of a pair and its master is on disk beside it.</summary>
    public bool HasMaster => MasterPath is not null;

    /// <summary>
    /// What the master is, for a column: the codec when one was found, or why not.
    ///
    /// This is the answer to "has this one been converted yet" - a clip recorded before the
    /// change reads ProRes 422, one recorded after it reads DNxHD.
    /// </summary>
    public string MasterText => MasterPath is null ? "no master" : MasterCodec ?? "master, codec unread";


    public string SizeText => Bytes >= 1L << 30
        ? $"{Bytes / (double)(1L << 30):F1} GB"
        : $"{Bytes / (double)(1L << 20):F0} MB";

    public string FormatText => Info is null
        ? "not probed"
        : $"{Info.Width}x{Info.Height} {Info.VideoCodec}{(Info.HasAudio ? " + audio" : ", silent")}";

    public string DurationText => Info is null
        ? "-"
        : $"{(int)Info.Duration.TotalMinutes:00}:{Info.Duration.Seconds:00}";
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

        // The proxies first, then anything sitting loose in the store — recordings made
        // before the store grew its two halves. The masters are deliberately not listed:
        // they are the same pictures, at a size that would make the strip crawl, and each
        // one is reachable from the proxy that stands for it.
        Collect(RecordingProfile.FolderFor(RecordingProfile.LowRes, folder), isProxy: true);
        Collect(folder, isProxy: false);

        clips.Sort((a, b) => b.Recorded.CompareTo(a.Recorded));
        return clips;

        void Collect(string from, bool isProxy)
        {
            try
            {
                if (!Directory.Exists(from)) return;

                foreach (string path in Directory.EnumerateFiles(from))
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
                             folder, $"*.{RecordingProfile.HighRes.Extension}"))
                {
                    _byName[Path.GetFileNameWithoutExtension(path)] = path;

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
        public string? For(string proxyPath)
        {
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
        private static DateTime? StampIn(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            int underscore = name.LastIndexOf('_');

            if (underscore <= 0 || underscore < 11) return null;

            string stamp = name[(underscore - 10)..];

            return DateTime.TryParseExact(stamp, "yyyy-MM-dd_HH-mm-ss", null,
                                          System.Globalization.DateTimeStyles.None, out DateTime when)
                ? when
                : null;
        }
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
