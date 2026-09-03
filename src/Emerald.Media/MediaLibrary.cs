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
    string? MasterPath = null)
{
    /// <summary>True when this is the proxy of a pair and the ProRes master is on disk beside it.</summary>
    public bool HasMaster => MasterPath is not null;


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

        // The proxies first, then anything sitting loose in the store — recordings made
        // before the store grew its two halves. The ProRes masters are deliberately not
        // listed: they are the same pictures, at a size that would make the strip crawl,
        // and each one is reachable from the proxy that stands for it.
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

                    clips.Add(new CapturedClip(path, file.Name, file.LastWriteTime, file.Length, info,
                                               isProxy ? FindMaster(folder, path) : null));
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

    /// <summary>The master written alongside a proxy: same name, the master's own extension.</summary>
    private static string? FindMaster(string root, string proxyPath)
    {
        string master = Path.Combine(
            RecordingProfile.FolderFor(RecordingProfile.HighRes, root),
            $"{Path.GetFileNameWithoutExtension(proxyPath)}.{RecordingProfile.HighRes.Extension}");

        return File.Exists(master) ? master : null;
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
