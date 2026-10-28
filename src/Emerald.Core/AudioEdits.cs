namespace Emerald.Core;

/// <summary>
/// A level set on a stretch of the recording's own sound: louder, quieter, or taken out.
///
/// In frames, and only ever from <see cref="StartFrame"/> up to but not including
/// <see cref="EndFrame"/>. Live Edit keeps these in frames of its day; the transmitter is handed
/// them in frames of the recording, like the cues.
/// </summary>
/// <param name="GainDb">The change in decibels. <see cref="double.NegativeInfinity"/> is silence - a deleted stretch of audio.</param>
public sealed record AudioEdit(long StartFrame, long Frames, double GainDb)
{
    public long EndFrame => StartFrame + Frames;

    public bool Muted => double.IsNegativeInfinity(GainDb);

    public bool Holds(long frame) => frame >= StartFrame && frame < EndFrame;
}

/// <summary>
/// The rules for audio edits. Pure, so they can be tested without a card: Live Edit uses them to
/// lay edits down, the transmitter to find the level of the frame it is about to send.
/// </summary>
public static class AudioEdits
{
    public const double MinDb = -60;
    public const double MaxDb = 12;

    /// <summary>
    /// The edits with <paramref name="gainDb"/> laid over [<paramref name="from"/>, <paramref name="to"/>),
    /// replacing whatever was there.
    ///
    /// Whatever stuck out either side of the range is kept, trimmed to the range's edge, so the
    /// edits never overlap and a frame has one level. Zero decibels is no edit at all and is not
    /// stored. Sorted by start, so a reader can stop early.
    /// </summary>
    public static IReadOnlyList<AudioEdit> Set(IReadOnlyList<AudioEdit> edits, long from, long to, double gainDb)
    {
        if (to <= from) return edits;

        var result = new List<AudioEdit>();

        foreach (AudioEdit edit in edits)
        {
            if (edit.EndFrame <= from || edit.StartFrame >= to)
            {
                result.Add(edit);
                continue;
            }

            if (edit.StartFrame < from)
                result.Add(edit with { Frames = from - edit.StartFrame });

            if (edit.EndFrame > to)
                result.Add(new AudioEdit(to, edit.EndFrame - to, edit.GainDb));
        }

        if (gainDb != 0) result.Add(new AudioEdit(from, to - from, Clamp(gainDb)));

        return Merge(result);
    }

    /// <summary>The level at a frame, in decibels: zero where nothing has been set.</summary>
    public static double GainDbAt(long frame, IReadOnlyList<AudioEdit> edits)
    {
        foreach (AudioEdit edit in edits)
        {
            if (edit.StartFrame > frame) break;
            if (edit.Holds(frame)) return edit.GainDb;
        }

        return 0;
    }

    /// <summary>The multiplier for a level in decibels. Silence is zero.</summary>
    public static double Linear(double gainDb) =>
        double.IsNegativeInfinity(gainDb) ? 0 : Math.Pow(10, gainDb / 20);

    /// <summary>
    /// Inside the range the operator can still change, and silence kept as silence. Anything
    /// else is held to <see cref="MinDb"/> and <see cref="MaxDb"/>.
    /// </summary>
    public static double Clamp(double gainDb) =>
        double.IsNegativeInfinity(gainDb) ? gainDb : Math.Clamp(gainDb, MinDb, MaxDb);

    /// <summary>
    /// The stretch between the cuts either side of <paramref name="frame"/>: what one click on the
    /// audio lane picks up, the way a razor cut divides a clip in an editor. Bounded by
    /// <paramref name="first"/> and <paramref name="end"/> - the recording - where there is no cut.
    /// </summary>
    public static (long From, long To) PieceAt(long frame, IEnumerable<long> cuts, long first, long end)
    {
        long from = first, to = end;

        foreach (long cut in cuts)
        {
            if (cut <= frame && cut > from) from = cut;
            if (cut > frame && cut < to) to = cut;
        }

        return (from, to);
    }

    /// <summary>
    /// The first frame at which two sets of edits disagree, or null when they set the same level
    /// everywhere. What an undo changes, so it can be refused where air has already gone by.
    /// </summary>
    public static long? FirstDifference(IReadOnlyList<AudioEdit> a, IReadOnlyList<AudioEdit> b)
    {
        // A level is constant between boundaries, so checking every boundary of both is enough.
        foreach (long frame in a.Concat(b).SelectMany(e => new[] { e.StartFrame, e.EndFrame }).Distinct().OrderBy(f => f))
            if (!GainDbAt(frame, a).Equals(GainDbAt(frame, b)))
                return frame;

        return null;
    }

    /// <summary>Sorted, with neighbours at the same level joined into one.</summary>
    private static IReadOnlyList<AudioEdit> Merge(List<AudioEdit> edits)
    {
        edits.Sort((x, y) => x.StartFrame.CompareTo(y.StartFrame));

        var merged = new List<AudioEdit>(edits.Count);

        foreach (AudioEdit edit in edits)
        {
            if (merged.Count > 0 && merged[^1] is var last &&
                last.EndFrame == edit.StartFrame && last.GainDb.Equals(edit.GainDb))
            {
                merged[^1] = last with { Frames = last.Frames + edit.Frames };
                continue;
            }

            merged.Add(edit);
        }

        return merged;
    }
}
