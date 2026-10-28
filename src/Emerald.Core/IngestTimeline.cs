namespace Emerald.Core;

/// <summary>A piece of ingest media and how it was placed.</summary>
public readonly record struct IngestItem(string Path, IngestPlan Plan);

/// <summary>
/// What should be playing at one frame of the timeline.
/// </summary>
/// <param name="Path">
/// The ingest file to play, or null when the captured recording is what plays here.
/// </param>
/// <param name="Offset">
/// How far into <paramref name="Path"/> to be, in frames of that file at its own rate. Only
/// meaningful when <paramref name="Path"/> is set.
/// </param>
/// <param name="Speed">What to multiply the file's rate by. 1 unless a fit-to-fill is stretching it.</param>
/// <param name="CapturedFrame">
/// Which frame of the captured recording this timeline frame corresponds to.
///
/// The two differ once anything has been <see cref="IngestMode.Insert"/>ed: an insert adds its
/// own length to the timeline, so everything after it shows captured material from earlier than
/// its position on the ruler would suggest. This is the number the recording is looked up by.
/// </param>
public readonly record struct TimelineAt(string? Path, long Offset, double Speed, long CapturedFrame)
{
    /// <summary>True when an ingest item is covering the recording here.</summary>
    public bool OnIngest => Path is not null;
}

/// <summary>
/// Turns a frame of the timeline into what plays there.
///
/// This is what makes the four placement modes mean something rather than being a label on a
/// block. All four come down to two questions at any given frame — is an ingest item covering
/// this moment, and how much has the timeline been lengthened by inserts before it — and both
/// are answered here, once, so the player and the position readout cannot disagree.
///
/// <b>Coordinates.</b> A plan's StartFrame is in captured frames, which is what the operator
/// pointed at when they dropped it. An insert pushes everything after it along, so a placement's
/// position on the ruler is its planned start plus the length of every insert that finished
/// before it. That accumulation is why this walks the list in order rather than testing each
/// placement independently.
/// </summary>
public static class IngestTimeline
{
    /// <summary>
    /// What plays at <paramref name="frame"/> of the timeline.
    ///
    /// Placements are taken in start order. Overlapping ones are resolved in favour of the first
    /// that covers the frame, which is the one laid down earliest — two items over the same
    /// moment is a mistake, and picking consistently is better than picking by whichever
    /// happened to sort last.
    /// </summary>
    public static TimelineAt At(long frame, IReadOnlyList<IngestItem> items)
    {
        long shift = 0;

        foreach (IngestItem item in Ordered(items))
        {
            IngestPlan plan = item.Plan;

            // Where this placement actually sits on the ruler, once the inserts and cuts before
            // it have moved it.
            long start = plan.StartFrame + shift;

            if (frame < start) break;              // ordered, so nothing later can cover it

            // A cut takes no time on the ruler: from its point on, the recording carries on from
            // the far side of the range it took out.
            if (plan.Mode == IngestMode.Cut)
            {
                shift -= plan.Covers;
                continue;
            }

            if (frame < start + plan.Frames)
            {
                long into = frame - start;

                // Frames into the file itself. A stretched item covers Frames of timeline with
                // Frames * Speed of its own material, so the offset runs faster than the ruler.
                long offset = (long)Math.Round(into * plan.Speed);

                return new TimelineAt(item.Path, offset, plan.Speed, frame - shift);
            }

            // Past it. An insert has lengthened the timeline, so everything from here on shows
            // captured material from earlier than the ruler says.
            if (plan.Mode == IngestMode.Insert) shift += plan.Frames;
        }

        return new TimelineAt(null, 0, 1, frame - shift);
    }

    /// <summary>
    /// How much longer the timeline is than the recording, which is how much the delay grows -
    /// negative when more has been cut out than inserted.
    ///
    /// Inserts add their length and cuts take theirs away; the other three modes cover the
    /// recording rather than changing its length.
    /// </summary>
    public static long AddedFrames(IReadOnlyList<IngestItem> items) =>
        items.Sum(i => i.Plan.Mode switch
        {
            IngestMode.Insert => i.Plan.Frames,
            IngestMode.Cut => -i.Plan.Covers,
            _ => 0L,
        });

    /// <summary>
    /// Where a frame of the captured recording appears on the ruler.
    ///
    /// The inverse of <see cref="TimelineAt.CapturedFrame"/>, and needed for drawing: the blocks
    /// are laid out from the recording's own frames, and an insert pushes everything at or after
    /// it along the ruler while a cut pulls it back. A frame inside a cut is not on the ruler at
    /// all; it is given the point the cut sits at, which is where the recording resumes. With
    /// neither this is the identity, so it costs nothing in the ordinary case.
    /// </summary>
    public static long RulerFrameOf(long capturedFrame, IReadOnlyList<IngestItem> items)
    {
        long shift = 0;

        foreach (IngestItem item in Ordered(items))
        {
            IngestPlan plan = item.Plan;

            if (plan.StartFrame > capturedFrame) break;

            // At or before this frame: the insert plays first, so the captured frame is after it.
            if (plan.Mode == IngestMode.Insert) shift += plan.Frames;

            if (plan.Mode == IngestMode.Cut)
                shift -= Math.Min(plan.Covers, capturedFrame - plan.StartFrame);
        }

        return capturedFrame + shift;
    }

    /// <summary>
    /// Where a placement sits on the ruler, which is not its planned start once inserts and cuts
    /// are in play. For drawing.
    /// </summary>
    public static long RulerStart(IngestItem item, IReadOnlyList<IngestItem> items)
    {
        long shift = 0;

        foreach (IngestItem other in Ordered(items))
        {
            if (ReferenceEquals(other.Path, item.Path) &&
                other.Plan.StartFrame == item.Plan.StartFrame)
                break;

            if (other.Plan.StartFrame >= item.Plan.StartFrame) break;

            if (other.Plan.Mode == IngestMode.Insert) shift += other.Plan.Frames;
            if (other.Plan.Mode == IngestMode.Cut) shift -= other.Plan.Covers;
        }

        return item.Plan.StartFrame + shift;
    }

    /// <summary>One continuous run of a segment, after inserts and cuts have cut it up.</summary>
    /// <param name="CapturedStart">Where this piece starts in the recording.</param>
    /// <param name="Frames">How long it runs.</param>
    /// <param name="RulerStart">Where it is drawn and played.</param>
    public readonly record struct Piece(long CapturedStart, long Frames, long RulerStart)
    {
        public long RulerEnd => RulerStart + Frames;
    }

    /// <summary>
    /// A segment broken at every insert that lands inside it, and with every cut taken out of it.
    ///
    /// <b>Why a segment has to be cut at all.</b> An insert does not cover the recording, it goes
    /// in between — so a two-minute segment with something inserted fifteen seconds in is no
    /// longer one continuous block on the ruler. It is fifteen seconds, then the inserted item,
    /// then the remaining hundred and five. Drawn as a single block it would run straight under
    /// the inserted item and claim time that now belongs to something else. A cut is the other
    /// way round: the range it took out is not on the ruler at all, so it is left out.
    ///
    /// A segment with neither inside it comes back as one piece at its own position, so the
    /// ordinary case is unchanged and costs one list.
    /// </summary>
    public static IReadOnlyList<Piece> Split(long capturedStart, long frames,
                                             IReadOnlyList<IngestItem> items)
    {
        var pieces = new List<Piece>();

        if (frames <= 0) return pieces;

        long end = capturedStart + frames;

        List<(long From, long To)> cuts = items
            .Where(i => i.Plan.Mode == IngestMode.Cut &&
                        i.Plan.StartFrame < end && i.Plan.StartFrame + i.Plan.Covers > capturedStart)
            .Select(i => (i.Plan.StartFrame, i.Plan.StartFrame + i.Plan.Covers))
            .ToList();

        // Every place the segment changes: an insert strictly inside it (one exactly at the start
        // pushes the whole segment along rather than cutting it, which RulerFrameOf already
        // handles), and both ends of every cut that reaches into it.
        List<long> bounds = items
            .Where(i => i.Plan.Mode == IngestMode.Insert)
            .Select(i => i.Plan.StartFrame)
            .Concat(cuts.SelectMany(c => new[] { c.From, c.To }))
            .Where(at => at > capturedStart && at < end)
            .Append(capturedStart)
            .Append(end)
            .Distinct()
            .OrderBy(at => at)
            .ToList();

        for (int i = 0; i + 1 < bounds.Count; i++)
        {
            long from = bounds[i], to = bounds[i + 1];

            if (cuts.Any(c => from >= c.From && to <= c.To)) continue;

            pieces.Add(new Piece(from, to - from, RulerFrameOf(from, items)));
        }

        return pieces;
    }

    private static IEnumerable<IngestItem> Ordered(IReadOnlyList<IngestItem> items) =>
        items.OrderBy(i => i.Plan.StartFrame);
}
