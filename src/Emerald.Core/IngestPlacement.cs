namespace Emerald.Core;

/// <summary>How a piece of ingest media is laid against the recording it is dropped on.</summary>
public enum IngestMode
{
    /// <summary>
    /// Plays over the captured media, replacing its video and audio, for as long as the ingest
    /// media runs. The recording underneath is not moved and nothing is sped up — the item
    /// simply covers part of it.
    /// </summary>
    Overlay,

    /// <summary>
    /// Fills the marked range exactly, changing speed to do it. Longer material is sped up,
    /// shorter material slowed down, so the range is covered end to end with no gap and no
    /// overrun. The one mode that alters the media's own rate.
    /// </summary>
    FitToFill,

    /// <summary>
    /// Covers the marked range at the media's own speed, starting at its own first frame. A
    /// shorter range cuts it off early; a longer range leaves the rest of the recording playing
    /// after it finishes.
    /// </summary>
    Replace,

    /// <summary>
    /// Goes in before the recording rather than over it: the item plays, then the captured media
    /// resumes. Everything after the insertion point moves later by the item's length, so the
    /// timeline — and the delay — grows.
    /// </summary>
    Insert,

    /// <summary>
    /// A scene taken out: the marked range of the recording does not go to air at all, and the
    /// recording after it follows straight on. The opposite of an insert - it takes no time on
    /// the timeline, and everything after it moves <i>earlier</i> by the range's length, so the
    /// delay shrinks by that much. No media: the placement's path is empty.
    /// </summary>
    Cut,
}

/// <summary>
/// Where a dropped item lands and what it does to the recording.
///
/// <paramref name="Frames"/> is how long the item occupies the timeline, which is not always its
/// own length: fit-to-fill stretches it and replace can cut it short.
/// </summary>
/// <param name="Covers">
/// The stretch of recording this placement governs. The same as <paramref name="Frames"/> except
/// under <see cref="IngestMode.Replace"/> with a range longer than the media, where the rest of
/// the range is the original playing on.
/// </param>
/// <param name="Speed">
/// What the media's own rate is multiplied by. 1 everywhere but fit-to-fill. Above 1 is faster
/// than recorded, below 1 slower.
/// </param>
/// <param name="ShiftsAfterBy">
/// How many frames everything after this placement moves later. Zero for the three modes that
/// lay over the recording, and the item's length for an insert.
/// </param>
public readonly record struct IngestPlan(
    IngestMode Mode,
    long StartFrame,
    long Frames,
    long Covers,
    double Speed,
    long ShiftsAfterBy)
{
    public long EndFrame => StartFrame + Frames;

    /// <summary>True when the media is not playing at its recorded rate.</summary>
    public bool Stretched => Math.Abs(Speed - 1) > 0.0001;
}

/// <summary>
/// Working out what a dropped ingest item does.
///
/// Pure, and separate from the dropping, because these four rules are the whole substance of the
/// feature and the differences between them are easy to get subtly wrong — three of them cover
/// the recording and one lengthens it; two take their span from the marks and two from where the
/// pointer was; exactly one changes the speed. A mistake in any of that is a transmission that
/// runs to the wrong length.
/// </summary>
public static class IngestPlacement
{
    /// <summary>
    /// Plans a placement, or says why it cannot be made.
    ///
    /// <paramref name="dropFrame"/> is where the pointer was, used by the modes that start
    /// there. <paramref name="markIn"/> and <paramref name="markOut"/> are the marked range,
    /// required by the two modes defined in terms of it.
    /// </summary>
    public static bool TryPlan(IngestMode mode, long ingestFrames, long dropFrame,
                              long? markIn, long? markOut,
                              out IngestPlan plan, out string? problem)
    {
        plan = default;
        problem = null;

        if (ingestFrames <= 0)
        {
            problem = "The length of that file could not be read, so it cannot be placed.";
            return false;
        }

        if (dropFrame < 0) dropFrame = 0;

        long range = 0;

        // The two modes that are defined by the marked range cannot be planned without one, and
        // saying so is better than quietly falling back to the drop point - which would put
        // material on air at a time nobody chose.
        if (mode is IngestMode.FitToFill or IngestMode.Replace)
        {
            if (markIn is not { } from || markOut is not { } to || to <= from)
            {
                problem = mode == IngestMode.FitToFill
                    ? "Fit-to-fill needs a marked range to fill. Mark in and out first."
                    : "Replace needs a marked range to replace. Mark in and out first.";
                return false;
            }

            range = to - from;
        }

        plan = mode switch
        {
            IngestMode.Overlay => new IngestPlan(
                mode, dropFrame, ingestFrames, ingestFrames, 1, 0),

            // The range is covered exactly, and the speed is whatever makes that true. More
            // material than room means faster than recorded.
            IngestMode.FitToFill => new IngestPlan(
                mode, markIn!.Value, range, range, ingestFrames / (double)range, 0),

            // Its own speed, cut off by the end of the range. What it does not cover of the
            // range is the original carrying on, which is why Covers and Frames differ here.
            IngestMode.Replace => new IngestPlan(
                mode, markIn!.Value, Math.Min(ingestFrames, range), range, 1, 0),

            // Laid in rather than over, so everything after it moves later by its length.
            IngestMode.Insert => new IngestPlan(
                mode, dropFrame, ingestFrames, ingestFrames, 1, ingestFrames),

            _ => default,
        };

        return true;
    }

    /// <summary>
    /// Plans taking a scene out: the marked range of the recording, in recording frames, will not
    /// go to air. Zero length on the timeline, and it pulls everything after it earlier by the
    /// range - so <see cref="IngestPlan.ShiftsAfterBy"/> is negative.
    /// </summary>
    public static bool TryPlanCut(long? markIn, long? markOut, out IngestPlan plan, out string? problem)
    {
        plan = default;
        problem = null;

        if (markIn is not { } from || markOut is not { } to || to <= from)
        {
            problem = "Deleting a scene needs a marked range. Mark in and out first.";
            return false;
        }

        plan = new IngestPlan(IngestMode.Cut, Math.Max(0, from), 0, to - Math.Max(0, from), 1,
                              -(to - Math.Max(0, from)));
        return true;
    }

    /// <summary>
    /// What a plan does, in a sentence, for the timeline block and the log.
    ///
    /// Written from the plan rather than from the mode, so it states what will actually happen
    /// to this item at this length — "sped up to 1.42x" is the fact an operator needs, and the
    /// mode's name alone does not carry it.
    /// </summary>
    public static string Describe(IngestPlan plan, int rate)
    {
        string length = $"{plan.Frames:N0} frames ({new Timecode(plan.Frames, rate)})";

        return plan.Mode switch
        {
            IngestMode.Overlay =>
                $"over the recording for {length}",

            IngestMode.FitToFill when plan.Speed > 1 =>
                $"filling {length}, sped up to {plan.Speed:0.###}x",

            IngestMode.FitToFill when plan.Speed < 1 =>
                $"filling {length}, slowed to {plan.Speed:0.###}x",

            IngestMode.FitToFill =>
                $"filling {length} at its own speed",

            IngestMode.Replace when plan.Covers > plan.Frames =>
                $"replacing {length} of a {plan.Covers:N0}-frame range; " +
                $"the last {plan.Covers - plan.Frames:N0} frames are the original",

            IngestMode.Replace =>
                $"replacing {length}",

            IngestMode.Insert =>
                $"inserted for {length}; everything after moves {plan.ShiftsAfterBy:N0} frames later",

            IngestMode.Cut =>
                $"{plan.Covers:N0} frames ({new Timecode(plan.Covers, rate)}) taken out; " +
                $"everything after moves {plan.Covers:N0} frames earlier",

            _ => length,
        };
    }
}
