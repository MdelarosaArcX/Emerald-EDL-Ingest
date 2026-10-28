using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Emerald.Core;
using Emerald.Video;

namespace Emerald.Media;

/// <summary>One segment a marked range touches, and how much of it the range wants.</summary>
public readonly record struct RangePart(string Path, long FromFrame, long Frames)
{
    public long ToFrame => FromFrame + Frames;
}

/// <summary>
/// What a marked range actually comes to on the disk: which files, and where in the first and
/// last of them it begins and ends.
///
/// Pure, and separate from the exporting, because this is the part that is wrong in a way nobody
/// notices until they watch the result. A range that crosses a segment boundary — which at two
/// minutes a segment nearly all of them do — has to take the tail of one file, the whole of the
/// ones between, and the head of the last.
/// </summary>
public static class RangePlan
{
    /// <summary>
    /// The parts of a range, in order, given the segments laid out on the clock.
    ///
    /// <paramref name="segments"/> is each file with the frame of the day it starts at and how
    /// many frames it holds, ordered. Segments the range does not touch are left out; a range
    /// that touches nothing comes back empty rather than as a zero-length part, because an export
    /// of nothing should be refused and not attempted.
    /// </summary>
    public static IReadOnlyList<RangePart> Parts(
        IReadOnlyList<(string Path, long StartFrame, long Frames)> segments,
        long inFrame, long outFrame)
    {
        var parts = new List<RangePart>();

        if (outFrame <= inFrame) return parts;

        foreach ((string path, long start, long frames) in segments)
        {
            if (frames <= 0) continue;

            long end = start + frames;

            // Wholly before or after the range.
            if (end <= inFrame || start >= outFrame) continue;

            long from = Math.Max(0, inFrame - start);
            long to = Math.Min(frames, outFrame - start);

            if (to > from) parts.Add(new RangePart(path, from, to - from));
        }

        return parts;
    }

    /// <summary>
    /// A name for an exported range that an operator does not have to think about.
    ///
    /// The recording it came from and the timecode it starts at, which together are unique and
    /// say what it is. Characters a filename cannot hold become underscores rather than the name
    /// being refused.
    /// </summary>
    public static string SuggestName(string recording, Timecode inAt)
    {
        string stamp = inAt.ToString().Replace(':', '-');
        string raw = $"{recording}_{stamp}";

        return new string(raw.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)
                             .ToArray());
    }
}

/// <summary>What came of an export.</summary>
public readonly record struct ExportResult(bool Ok, string Path, string Problem);

/// <summary>
/// Cuts a marked range out of the store and writes it, with whatever the operator said about it,
/// into the ingest folder.
///
/// <b>From the masters, not the proxies.</b> The proxy exists so the timeline can scrub; what
/// comes out of an export is something somebody will transmit or hand on, so it is cut from the
/// DNxHD beside it. Where a master is missing the proxy is used and the log says so, because an
/// export that silently produced the low-resolution copy would be found out on air.
///
/// <b>Stream copy, not re-encode</b>, so no generation is lost and the work is seconds rather
/// than minutes — 500 frames of DNxHD out of two segments took 1.6s on this plant.
///
/// Because DNxHD is all-intra, every frame of a master is a keyframe and the cut is exact. The
/// keyframe caveat applies only to the proxy fallback: an H.264 proxy has a 250-frame GOP, so a
/// range cut from proxies starts at the first keyframe at or after the mark.
/// </summary>
public static class RangeExport
{
    /// <summary>Where exported ranges go, beside the store rather than inside it.</summary>
    public static string FolderFor(string storeRoot) => Path.Combine(storeRoot, "ingest");

    /// <summary>
    /// Writes the range as one file plus a sidecar carrying the metadata.
    ///
    /// Synchronous and slow — it shells out to ffmpeg — so callers run it off the UI thread.
    /// </summary>
    /// <param name="progress">
    /// Told what is happening, as it happens, on the calling thread.
    ///
    /// An export is two jobs of very different length: the cut is a stream copy and takes
    /// seconds, and the playable copy beside it is a real encode and can take minutes on a long
    /// range. Without this the window could only say "writing..." for the whole of it, which on
    /// the long one is indistinguishable from a hang.
    /// </param>
    public static ExportResult Write(string ffmpegPath, string storeRoot, string name,
                                     string metadata, IReadOnlyList<RangePart> parts,
                                     int frameRate, string extension,
                                     Action<string>? progress = null)
    {
        if (parts.Count == 0) return new ExportResult(false, "", "The range covers no media.");
        if (name.Length == 0) return new ExportResult(false, "", "The export has no name.");

        string folder = FolderFor(storeRoot);
        string output = Path.Combine(folder, $"{name}.{extension}");

        try
        {
            Directory.CreateDirectory(folder);

            progress?.Invoke($"cutting {parts.Count} segment(s) into {name}.{extension}");

            // A concat list rather than one -i per part: the parts are the same codec from the
            // same encoder, so they join without being decoded.
            string list = Path.Combine(folder, $".{name}.concat.txt");

            var text = new StringBuilder();
            foreach (RangePart part in parts)
                text.AppendLine($"file '{part.Path.Replace("'", "'\\''")}'");

            File.WriteAllText(list, text.ToString(), new UTF8Encoding(false));

            // Where the wanted range sits inside the joined parts: the first part's offset, for
            // however long the whole range runs.
            double from = parts[0].FromFrame / (double)frameRate;
            double length = parts.Sum(p => p.Frames) / (double)frameRate;

            var info = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            // -ss goes AFTER -i, which is not the usual advice and is measured.
            //
            // Before -i, the concat demuxer does not seek: it hands back the whole of the first
            // file, and because -t is then counted against timestamps that already start at the
            // offset, nothing is trimmed from either end. A 20-second range asked for that way
            // came out as 130 seconds - the offset plus the length - on this plant's own
            // segments. After -i, the same range comes out at 500 frames, 20.005s, and decodes
            // end to end without a complaint. The cost is the usual one for a stream copy: the
            // picture starts at the first keyframe at or after the mark.
            foreach (string a in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                         "-f", "concat", "-safe", "0",
                         "-i", list,
                         "-ss", Seconds(from),
                         "-t", Seconds(length),
                         "-c", "copy",
                         // The timestamps come off the middle of a recording. Without this the
                         // export can open on a negative or multi-hour offset and players report
                         // the wrong duration. A generic muxer option, so it is safe whatever
                         // container the master happens to be in.
                         "-avoid_negative_ts", "make_zero",
                         output,
                     })
                info.ArgumentList.Add(a);

            using Process? ffmpeg = Process.Start(info);

            if (ffmpeg is null)
                return new ExportResult(false, "", "ffmpeg could not be started.");

            string complaints = ffmpeg.StandardError.ReadToEnd();
            ffmpeg.WaitForExit();

            try { File.Delete(list); } catch (IOException) { }

            if (ffmpeg.ExitCode != 0 || !File.Exists(output))
                return new ExportResult(false, "",
                    complaints.Length > 0 ? complaints.Trim() : $"ffmpeg exited {ffmpeg.ExitCode}.");

            // The metadata beside it, as JSON rather than inside the container: a sidecar can be
            // read and corrected without rewriting a file that may be gigabytes.
            File.WriteAllText(
                Path.ChangeExtension(output, ".json"),
                JsonSerializer.Serialize(new
                {
                    name,
                    metadata,
                    exported = DateTimeOffset.Now,
                    frameRate,
                    frames = parts.Sum(p => p.Frames),
                    parts = parts.Select(p => new { p.Path, p.FromFrame, p.Frames }),
                }, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            // And the small playable copy, so the Ingest list can show what was just made. After
            // the sidecar, because the export itself is the thing that must be right and this is
            // a convenience on top of it.
            progress?.Invoke("writing the playable preview copy - this is the slow part");

            WritePreview(ffmpegPath, output, frameRate);

            return new ExportResult(true, output, "");
        }
        catch (Exception ex)
        {
            return new ExportResult(false, "", ex.Message);
        }
    }

    /// <summary>
    /// A time for ffmpeg. Invariant, because a machine set to a comma decimal separator would
    /// otherwise hand it "12,5" and it would read 12 seconds.
    /// </summary>
    private static string Seconds(double value) =>
        value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The suffix on the small playable copy written beside an export.
    ///
    /// An export is cut from the masters, so it is DNxHD in a .mov - which is right for something
    /// that will be transmitted or handed on, and unplayable in the application: WPF decodes
    /// through Media Foundation, which has no DNxHD decoder, so the preview came up blank with
    /// nothing to say for itself. A small H.264 copy beside it is what the Ingest list plays.
    ///
    /// The same relationship the store already has between a master and its proxy, for the same
    /// reason, named so it is obvious which is which on disk.
    /// </summary>
    public const string PreviewSuffix = ".preview.mp4";

    /// <summary>
    /// What to actually play for an export: the export itself when it can be played, and the
    /// copy written beside it when it cannot.
    ///
    /// An export cut from the masters is DNxHD in a .mov, which Media Foundation cannot decode —
    /// that is the whole reason the copy exists. An export cut from the proxies is already H.264
    /// in an .mp4 and plays perfectly well, so sending it through the copy would cost a
    /// generation of quality for nothing.
    ///
    /// Judged by extension rather than by probing, because the extension is not a guess here:
    /// the exporter chooses it from the source it cut, so .mov means masters and .mp4 means
    /// proxies.
    /// </summary>
    public static string? PlayablePathFor(string exportPath)
    {
        if (Path.GetExtension(exportPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase))
            return exportPath;

        return PreviewFor(exportPath);
    }

    /// <summary>The playable copy of an export, if one was written.</summary>
    public static string? PreviewFor(string exportPath)
    {
        string guess = PreviewPathFor(exportPath);

        return File.Exists(guess) ? guess : null;
    }

    private static string PreviewPathFor(string exportPath) =>
        Path.Combine(Path.GetDirectoryName(exportPath) ?? "",
                     Path.GetFileNameWithoutExtension(exportPath) + PreviewSuffix);

    /// <summary>True for the preview copies, which are not themselves exports.</summary>
    public static bool IsPreview(string path) =>
        path.EndsWith(PreviewSuffix, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Writes the small playable copy. Best effort: an export whose preview fails is still a
    /// good export, and saying it failed would send somebody looking for a problem with media
    /// that is fine.
    /// </summary>
    private static void WritePreview(string ffmpegPath, string exportPath, int frameRate)
    {
        try
        {
            var info = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
            };

            foreach (string a in new[]
                     {
                         "-hide_banner", "-loglevel", "error", "-nostdin", "-y",
                         "-i", exportPath,

                         // Full raster, and no scale filter at all.
                         //
                         // This was 640x360 on the reasoning that it only had to answer "is this
                         // the right material". That was true when the only thing playing it was
                         // the thumbnail beside the ingest list. It is not true now: an ingest
                         // placement plays this copy on the main viewer, because the export
                         // itself is DNxHD and nothing here can decode that - so this is the
                         // picture, and 360 lines blown up to fill a viewer is what "very low
                         // quality" looks like.
                         "-c:v", "libx264",
                         "-preset", "veryfast",
                         "-profile:v", "high",
                         "-crf", "20",
                         "-pix_fmt", "yuv420p",

                         // No B-frames and a keyframe every half second, for the same reason the
                         // capture proxy has them: this copy gets scrubbed and stepped through,
                         // and reordered pictures with eleven seconds between keyframes is what
                         // makes that lurch.
                         "-bf", "0",
                         "-g", Math.Max(1, frameRate / 2).ToString(),
                         "-sc_threshold", "0",

                         "-c:a", "aac", "-b:a", "192k",
                         PreviewPathFor(exportPath),
                     })
                info.ArgumentList.Add(a);

            using Process? ffmpeg = Process.Start(info);

            if (ffmpeg is null) return;

            ffmpeg.StandardError.ReadToEnd();
            ffmpeg.WaitForExit();
        }
        catch (Exception) { }
    }

    /// <summary>Everything already exported, newest first, for the Ingest Media list.</summary>
    public static IReadOnlyList<CapturedClip> List(string storeRoot, string? ffprobePath,
                                                   int frameRate = 25)
    {
        string folder = FolderFor(storeRoot);
        var clips = new List<CapturedClip>();

        try
        {
            if (!Directory.Exists(folder)) return clips;

            foreach (string path in Directory.EnumerateFiles(folder))
            {
                if (!MediaScanner.IsPlayable(path)) continue;

                // The preview copies belong to the exports beside them; listing them as well
                // would double every row.
                if (IsPreview(path)) continue;

                var file = new FileInfo(path);

                clips.Add(new CapturedClip(
                    path, file.Name, file.LastWriteTime, file.Length,
                    ffprobePath is null ? null : MediaProbe.Probe(ffprobePath, path, frameRate)));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        clips.Sort((a, b) => b.Recorded.CompareTo(a.Recorded));
        return clips;
    }
}
