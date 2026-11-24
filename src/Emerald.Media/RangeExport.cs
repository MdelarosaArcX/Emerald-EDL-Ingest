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
    /// The parts of a range cut from a recording's masters, which run longer than its proxies.
    ///
    /// <paramref name="masters"/> is the recording's masters laid out on the clock by their own
    /// lengths; one with no frames is still being written. The range is first narrowed to
    /// <paramref name="coveredFrom"/>..<paramref name="coveredTo"/>, the span the recording's
    /// proxies cover, so two recordings overlapping on the clock are never cut twice - and the
    /// range is narrowed rather than the masters, so each part's offset is still from the start of
    /// its own file.
    ///
    /// <paramref name="waitsForOpen"/> is true when the range reaches into the master still being
    /// written. The parts are then what is ready so far, and an export should wait.
    /// </summary>
    public static IReadOnlyList<RangePart> FromMasters(
        IReadOnlyList<(string Path, long StartFrame, long Frames)> masters,
        long coveredFrom, long coveredTo, long inFrame, long outFrame, out bool waitsForOpen)
    {
        long from = Math.Max(inFrame, coveredFrom);
        long to = Math.Min(outFrame, coveredTo);

        waitsForOpen = to > from && masters.Any(m => m.Frames <= 0 && m.StartFrame < to);

        return Parts(masters.Where(m => m.Frames > 0).ToList(), from, to);
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

/// <summary>How far an export has got: what it is doing, and the whole job's fraction from 0 to 1.</summary>
public readonly record struct ExportProgress(string Stage, double Fraction);

/// <summary>
/// Cuts a marked range out of the store and writes it, with whatever the operator said about it,
/// into the recording's folder on the ingest media path: the cut in high\, its 960x540 low-res
/// copy in low\ (see <see cref="IngestLibrary"/>).
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

    /// <summary>Where exported ranges go with the high-res and low-res folders chosen apart (see <see cref="StorePaths.Exports"/>).</summary>
    public static string FolderFor(StorePaths store) => store.Exports;

    // Of the whole job, how much is the cut and how much the low-res copy. The cut is a stream copy
    // but reads and writes every byte of DNxHD; the copy decodes and encodes, on the card when
    // there is one.
    private const double CutShare = 0.35;

    /// <summary>
    /// Writes the range into a recording's ingest folder (<see cref="IngestLibrary.FolderFor"/>):
    /// the cut itself and a sidecar carrying the metadata in high\, and its low-res copy in low\.
    ///
    /// Synchronous and slow — it shells out to ffmpeg — so callers run it off the UI thread.
    /// </summary>
    /// <param name="progress">
    /// Told what is happening and how far through the whole job it is, on the calling thread, a
    /// few times a second - from ffmpeg's own progress, so the bar is a real fraction.
    /// </param>
    public static ExportResult Write(string ffmpegPath, string folder, string name,
                                     string metadata, IReadOnlyList<RangePart> parts,
                                     int frameRate, string extension,
                                     Action<ExportProgress>? progress = null,
                                     CancellationToken cancel = default)
    {
        if (parts.Count == 0) return new ExportResult(false, "", "The range covers no media.");
        if (name.Length == 0) return new ExportResult(false, "", "The export has no name.");

        string high = IngestLibrary.HighOf(folder);
        string output = Path.Combine(high, $"{name}.{extension}");

        try
        {
            Directory.CreateDirectory(high);
            Directory.CreateDirectory(IngestLibrary.LowOf(folder));

            progress?.Invoke(new ExportProgress($"cutting {parts.Count} segment(s)", 0));

            // A concat list rather than one -i per part: the parts are the same codec from the
            // same encoder, so they join without being decoded.
            string list = Path.Combine(high, $".{name}.concat.txt");

            var text = new StringBuilder();
            foreach (RangePart part in parts)
                text.AppendLine($"file '{part.Path.Replace("'", "'\\''")}'");

            File.WriteAllText(list, text.ToString(), new UTF8Encoding(false));

            // Where the wanted range sits inside the joined parts: the first part's offset, for
            // however long the whole range runs.
            double from = parts[0].FromFrame / (double)frameRate;
            double length = parts.Sum(p => p.Frames) / (double)frameRate;

            // -ss goes AFTER -i, which is not the usual advice and is measured.
            //
            // Before -i, the concat demuxer does not seek: it hands back the whole of the first
            // file, and because -t is then counted against timestamps that already start at the
            // offset, nothing is trimmed from either end. A 20-second range asked for that way
            // came out as 130 seconds - the offset plus the length - on this plant's own
            // segments. After -i, the same range comes out at 500 frames, 20.005s, and decodes
            // end to end without a complaint. The cost is the usual one for a stream copy: the
            // picture starts at the first keyframe at or after the mark.
            string[] cut =
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
            };

            // Called off - its window closed, or the operator pressed Cancel: the cut is stopped
            // rather than left to run on, and what it had written is not left as an export.
            FfmpegOutcome cutting = FfmpegRun.Run(ffmpegPath, cut, length,
                f => progress?.Invoke(new ExportProgress("cutting the high-res", f * CutShare)), cancel);

            try { File.Delete(list); } catch (IOException) { }

            if (cutting.Cancelled)
            {
                try { File.Delete(output); } catch (IOException) { }
                return new ExportResult(false, "", "Called off.");
            }

            if (!cutting.Ok || !File.Exists(output))
                return new ExportResult(false, "", cutting.Problem.Length > 0 ? cutting.Problem : "ffmpeg wrote nothing.");

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

            // And the low-res copy, which is what the Ingest list shows and the timeline plays.
            // After the sidecar, because the export itself is the thing that must be right.
            FfmpegOutcome low = IngestProxy.Make(ffmpegPath, output, IngestLibrary.LowPathFor(output), frameRate,
                f => progress?.Invoke(new ExportProgress("making the low-res copy", CutShare + f * (1 - CutShare))), cancel);

            if (low.Cancelled)
            {
                foreach (string done in new[] { output, Path.ChangeExtension(output, ".json") })
                    try { File.Delete(done); } catch (IOException) { }

                return new ExportResult(false, "", "Called off.");
            }

            // A failed low-res copy is still a good export: it goes to air from high\. The low-res
            // maker in Live Edit tries again for any high-res left without one.
            if (!low.Ok) return new ExportResult(true, output, $"The low-res copy was not made: {low.Problem}");

            progress?.Invoke(new ExportProgress("done", 1));
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
        // Ingest media in its recording folder: the low-res beside it, always - it is small and
        // made to be scrubbed, whatever the high-res is.
        if (IngestLibrary.LowFor(exportPath) is { } low) return low;

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
}
