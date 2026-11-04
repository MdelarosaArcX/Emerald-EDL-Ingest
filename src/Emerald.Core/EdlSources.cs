namespace Emerald.Core;

/// <summary>One captured file, as it sits on the day: where it starts and how many frames it holds.</summary>
public readonly record struct EdlSegment(string Path, long StartFrame, long Frames)
{
    public long EndFrame => StartFrame + Frames;
}

/// <summary>
/// The part of one capture file an edit covers. Every number is inclusive: the first and the last
/// frame actually selected, so <see cref="Frames"/> is <c>Last - First + 1</c> and no reader has
/// to guess which end is open.
/// </summary>
/// <param name="File">The capture file, or null for a stretch of the day nothing was recorded in.</param>
/// <param name="FirstFileFrame">The first selected frame, counted from the head of that file.</param>
/// <param name="LastFileFrame">The last selected frame, counted from the head of that file.</param>
/// <param name="FirstFrame">The same first frame, in frames of the day.</param>
/// <param name="LastFrame">The same last frame, in frames of the day.</param>
public sealed record EdlSourceRange(string? File, long FirstFileFrame, long LastFileFrame, long FirstFrame, long LastFrame)
{
    public long Frames => LastFrame - FirstFrame + 1;
}

/// <summary>
/// A marked range, as everything that logs or sends it needs it: the marks, the frames between
/// them, and which capture files those frames are in.
///
/// <paramref name="MarkIn"/> is the first selected frame and <paramref name="MarkOut"/> the frame
/// the out mark was set on, which is not selected - the range is <c>[MarkIn, MarkOut)</c>, as on
/// the timeline, so the duration is <c>MarkOut - MarkIn</c> with no plus one to forget.
/// </summary>
public sealed record EdlSelection(long MarkIn, long MarkOut, int Rate, IReadOnlyList<EdlSourceRange> Sources, string Audio)
{
    public long FirstFrame => MarkIn;
    public long LastFrame => MarkOut - 1;
    public long DurationFrames => MarkOut - MarkIn;
    public double DurationSeconds => Rate > 0 ? DurationFrames / (double)Rate : 0;

    public string MarkInTimecode => new Timecode(MarkIn, Rate).ToString();
    public string MarkOutTimecode => new Timecode(MarkOut, Rate).ToString();

    public IReadOnlyList<string> SourceFiles =>
        Sources.Where(s => s.File is not null).Select(s => s.File!).Distinct().ToList();

    public bool SpansFiles => SourceFiles.Count > 1;
}

/// <summary>Which capture files a range of the day falls in.</summary>
public static class EdlSources
{
    /// <summary>
    /// The files that hold frames <paramref name="first"/> to <paramref name="last"/> of the day,
    /// inclusive, in order - one entry per file touched, so a range across a segment boundary names
    /// both files and exactly which frames of each. A stretch no file covers is listed with no file
    /// rather than left out, so the entries always add up to the whole range.
    /// </summary>
    public static IReadOnlyList<EdlSourceRange> Resolve(long first, long last, IEnumerable<EdlSegment> segments)
    {
        var ranges = new List<EdlSourceRange>();

        if (last < first) return ranges;

        long at = first;

        foreach (EdlSegment segment in segments.Where(s => s.Frames > 0).OrderBy(s => s.StartFrame))
        {
            if (segment.EndFrame <= at) continue;
            if (segment.StartFrame > last) break;

            if (segment.StartFrame > at)
            {
                ranges.Add(new EdlSourceRange(null, 0, segment.StartFrame - at - 1, at, segment.StartFrame - 1));
                at = segment.StartFrame;
            }

            long end = Math.Min(last, segment.EndFrame - 1);

            ranges.Add(new EdlSourceRange(segment.Path, at - segment.StartFrame, end - segment.StartFrame, at, end));
            at = end + 1;

            if (at > last) break;
        }

        if (at <= last) ranges.Add(new EdlSourceRange(null, 0, last - at, at, last));

        return ranges;
    }
}
