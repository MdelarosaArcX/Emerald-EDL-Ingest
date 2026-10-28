using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// A deleted scene: a marked range of the recording that does not go to air.
///
/// It is an insert turned round. An insert takes time on the ruler and pushes everything after
/// it later; a cut takes none and pulls everything after it earlier - which is what shrinks the
/// delay. The mapping has to agree in all four directions (ruler to recording, recording to
/// ruler, where a placement sits, how a segment is split) or the player and the transmitter
/// will each be right on their own and disagree with each other.
/// </summary>
public sealed class SceneCutTests
{
    private static IngestItem Cut(long from, long to)
    {
        Assert.True(IngestPlacement.TryPlanCut(from, to, out IngestPlan plan, out string? problem), problem);
        return new IngestItem("", plan);
    }

    private static IngestItem Insert(long at, long frames)
    {
        Assert.True(IngestPlacement.TryPlan(IngestMode.Insert, frames, at, null, null,
                                            out IngestPlan plan, out string? problem), problem);
        return new IngestItem("insert.mov", plan);
    }

    [Fact]
    public void A_cut_takes_no_time_on_the_ruler_and_removes_its_range()
    {
        IngestPlan plan = Cut(1000, 1500).Plan;

        Assert.Equal(IngestMode.Cut, plan.Mode);
        Assert.Equal(0, plan.Frames);
        Assert.Equal(500, plan.Covers);
        Assert.Equal(-500, plan.ShiftsAfterBy);
    }

    [Fact]
    public void Before_a_cut_the_recording_plays_as_it_is()
    {
        TimelineAt at = IngestTimeline.At(999, new[] { Cut(1000, 1500) });

        Assert.False(at.OnIngest);
        Assert.Equal(999, at.CapturedFrame);
    }

    [Fact]
    public void From_a_cut_on_the_recording_resumes_after_the_removed_range()
    {
        var items = new[] { Cut(1000, 1500) };

        Assert.Equal(1500, IngestTimeline.At(1000, items).CapturedFrame);
        Assert.Equal(1600, IngestTimeline.At(1100, items).CapturedFrame);
    }

    [Fact]
    public void A_recording_frame_after_a_cut_is_earlier_on_the_ruler()
    {
        var items = new[] { Cut(1000, 1500) };

        Assert.Equal(999, IngestTimeline.RulerFrameOf(999, items));
        Assert.Equal(1100, IngestTimeline.RulerFrameOf(1600, items));
    }

    /// <summary>A frame inside the removed range is nowhere on the ruler; it is put at the join.</summary>
    [Fact]
    public void A_recording_frame_inside_a_cut_lands_at_the_join()
    {
        var items = new[] { Cut(1000, 1500) };

        Assert.Equal(1000, IngestTimeline.RulerFrameOf(1200, items));
    }

    [Fact]
    public void A_cut_shortens_the_timeline_by_its_range()
    {
        Assert.Equal(-500, IngestTimeline.AddedFrames(new[] { Cut(1000, 1500) }));
    }

    /// <summary>The delay arithmetic: a three-minute insert and a one-minute cut leave two minutes added.</summary>
    [Fact]
    public void Inserts_and_cuts_add_up()
    {
        var items = new[] { Insert(1000, 4500), Cut(10_000, 11_500) };

        Assert.Equal(4500 - 1500, IngestTimeline.AddedFrames(items));
    }

    [Fact]
    public void A_segment_across_a_cut_is_drawn_either_side_of_it()
    {
        var items = new[] { Cut(1000, 1500) };

        IReadOnlyList<IngestTimeline.Piece> pieces = IngestTimeline.Split(0, 3000, items);

        Assert.Equal(2, pieces.Count);

        Assert.Equal(new IngestTimeline.Piece(0, 1000, 0), pieces[0]);
        Assert.Equal(new IngestTimeline.Piece(1500, 1500, 1000), pieces[1]);
    }

    [Fact]
    public void A_segment_wholly_inside_a_cut_is_not_drawn()
    {
        Assert.Empty(IngestTimeline.Split(1100, 200, new[] { Cut(1000, 1500) }));
    }

    [Fact]
    public void A_placement_after_a_cut_moves_earlier_on_the_ruler()
    {
        IngestItem insert = Insert(5000, 100);
        var items = new[] { Cut(1000, 1500), insert };

        Assert.Equal(4500, IngestTimeline.RulerStart(insert, items));
    }

    [Fact]
    public void A_cut_needs_a_range()
    {
        Assert.False(IngestPlacement.TryPlanCut(null, 100, out _, out string? problem));
        Assert.NotNull(problem);

        Assert.False(IngestPlacement.TryPlanCut(500, 500, out _, out _));
    }
}
