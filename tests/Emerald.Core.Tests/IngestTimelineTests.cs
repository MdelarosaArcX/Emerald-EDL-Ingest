using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// What plays at a given frame once ingest media has been laid in.
///
/// This is where the four modes stop being labels and start changing what goes out, so the
/// cases that matter are the ones where the timeline and the recording stop agreeing: an insert
/// lengthens the ruler, so everything after it shows captured material from earlier than its
/// position suggests, and a stretched item runs through its own frames faster than the ruler
/// advances. Both are easy to get subtly wrong and neither is visible until something is on air
/// at the wrong moment.
/// </summary>
public sealed class IngestTimelineTests
{
    private static IngestItem Item(IngestMode mode, long ingest, long drop = 1000,
                                    long? markIn = null, long? markOut = null,
                                    string path = "item.mov")
    {
        Assert.True(IngestPlacement.TryPlan(mode, ingest, drop, markIn, markOut,
                                            out IngestPlan plan, out string? problem), problem);

        return new IngestItem(path, plan);
    }

    private static readonly IngestItem[] None = Array.Empty<IngestItem>();

    [Fact]
    public void With_nothing_placed_the_timeline_is_the_recording()
    {
        TimelineAt at = IngestTimeline.At(5000, None);

        Assert.False(at.OnIngest);
        Assert.Null(at.Path);
        Assert.Equal(5000, at.CapturedFrame);
        Assert.Equal(1, at.Speed);
    }

    // ------------------------------------------------------------------ overlay

    [Fact]
    public void Inside_an_overlay_the_item_plays_instead_of_the_recording()
    {
        var items = new[] { Item(IngestMode.Overlay, 500, drop: 1000) };

        TimelineAt at = IngestTimeline.At(1200, items);

        Assert.True(at.OnIngest);
        Assert.Equal("item.mov", at.Path);
        Assert.Equal(200, at.Offset);        // 200 frames into the file
        Assert.Equal(1, at.Speed);
    }

    [Fact]
    public void Either_side_of_an_overlay_the_recording_plays_unshifted()
    {
        var items = new[] { Item(IngestMode.Overlay, 500, drop: 1000) };

        Assert.False(IngestTimeline.At(999, items).OnIngest);
        Assert.Equal(999, IngestTimeline.At(999, items).CapturedFrame);

        // One past the end, so the overlay is over.
        Assert.False(IngestTimeline.At(1500, items).OnIngest);
        Assert.Equal(1500, IngestTimeline.At(1500, items).CapturedFrame);
    }

    [Fact]
    public void An_overlay_covers_its_first_frame_and_not_its_last_plus_one()
    {
        var items = new[] { Item(IngestMode.Overlay, 500, drop: 1000) };

        Assert.True(IngestTimeline.At(1000, items).OnIngest);
        Assert.True(IngestTimeline.At(1499, items).OnIngest);
        Assert.False(IngestTimeline.At(1500, items).OnIngest);
    }

    // ------------------------------------------------------------------ fit to fill

    [Fact]
    public void A_stretched_item_runs_through_its_own_frames_faster_than_the_ruler()
    {
        // 500 frames of media into a 250-frame range: double speed.
        var items = new[] { Item(IngestMode.FitToFill, 500, markIn: 2000, markOut: 2250) };

        Assert.Equal(2.0, IngestTimeline.At(2000, items).Speed, 4);

        // Half way along the range is half way through the media, which at double speed is 250
        // frames in rather than 125.
        Assert.Equal(250, IngestTimeline.At(2125, items).Offset);
    }

    [Fact]
    public void A_slowed_item_runs_through_its_frames_slower_than_the_ruler()
    {
        // 250 frames of media into a 500-frame range: half speed.
        var items = new[] { Item(IngestMode.FitToFill, 250, markIn: 2000, markOut: 2500) };

        Assert.Equal(0.5, IngestTimeline.At(2000, items).Speed, 4);
        Assert.Equal(125, IngestTimeline.At(2250, items).Offset);
    }

    // ------------------------------------------------------------------ replace

    [Fact]
    public void A_replace_shorter_than_its_range_hands_back_to_the_recording()
    {
        // 300 frames of media in a 500-frame range.
        var items = new[] { Item(IngestMode.Replace, 300, markIn: 2000, markOut: 2500) };

        Assert.True(IngestTimeline.At(2299, items).OnIngest);

        // Past the media but still inside the marked range: the original plays on, and it is
        // the original's own frame because replace does not move anything.
        TimelineAt after = IngestTimeline.At(2300, items);

        Assert.False(after.OnIngest);
        Assert.Equal(2300, after.CapturedFrame);
    }

    // ------------------------------------------------------------------ insert

    [Fact]
    public void After_an_insert_the_recording_is_behind_the_ruler_by_its_length()
    {
        var items = new[] { Item(IngestMode.Insert, 500, drop: 1000) };

        // Before it: the two agree.
        Assert.Equal(999, IngestTimeline.At(999, items).CapturedFrame);

        // Inside it: the item plays.
        Assert.True(IngestTimeline.At(1200, items).OnIngest);

        // After it: the ruler has moved on 500 frames further than the recording has.
        TimelineAt after = IngestTimeline.At(1500, items);

        Assert.False(after.OnIngest);
        Assert.Equal(1000, after.CapturedFrame);
    }

    [Fact]
    public void Two_inserts_shift_by_both_their_lengths()
    {
        var items = new[]
        {
            Item(IngestMode.Insert, 500, drop: 1000, path: "a.mov"),
            Item(IngestMode.Insert, 300, drop: 2000, path: "b.mov"),
        };

        // The second insert's planned start is 2000 captured, which the first has pushed to 2500
        // on the ruler.
        Assert.True(IngestTimeline.At(2500, items).OnIngest);
        Assert.Equal("b.mov", IngestTimeline.At(2500, items).Path);

        // Past both: the recording is 800 frames behind the ruler.
        Assert.Equal(2200, IngestTimeline.At(3000, items).CapturedFrame);
    }

    [Fact]
    public void An_overlay_after_an_insert_is_pushed_along_with_everything_else()
    {
        var items = new[]
        {
            Item(IngestMode.Insert, 500, drop: 1000, path: "a.mov"),
            Item(IngestMode.Overlay, 200, drop: 3000, path: "b.mov"),
        };

        // Planned at 3000 captured, drawn and played at 3500 on the ruler.
        Assert.False(IngestTimeline.At(3000, items).OnIngest);
        Assert.True(IngestTimeline.At(3500, items).OnIngest);
        Assert.Equal("b.mov", IngestTimeline.At(3500, items).Path);
    }

    [Fact]
    public void Only_inserts_lengthen_the_timeline()
    {
        Assert.Equal(0, IngestTimeline.AddedFrames(new[] { Item(IngestMode.Overlay, 500) }));

        Assert.Equal(0, IngestTimeline.AddedFrames(
            new[] { Item(IngestMode.Replace, 500, markIn: 0, markOut: 250) }));

        Assert.Equal(0, IngestTimeline.AddedFrames(
            new[] { Item(IngestMode.FitToFill, 500, markIn: 0, markOut: 250) }));

        Assert.Equal(500, IngestTimeline.AddedFrames(new[] { Item(IngestMode.Insert, 500) }));
    }

    [Fact]
    public void The_added_length_is_the_sum_of_every_insert()
    {
        var items = new[]
        {
            Item(IngestMode.Insert, 500, drop: 1000, path: "a.mov"),
            Item(IngestMode.Overlay, 900, drop: 4000, path: "b.mov"),
            Item(IngestMode.Insert, 300, drop: 2000, path: "c.mov"),
        };

        Assert.Equal(800, IngestTimeline.AddedFrames(items));
    }

    // ------------------------------------------------------------------ drawing

    [Fact]
    public void A_placement_is_drawn_where_the_inserts_before_it_have_put_it()
    {
        var first = Item(IngestMode.Insert, 500, drop: 1000, path: "a.mov");
        var second = Item(IngestMode.Overlay, 200, drop: 3000, path: "b.mov");

        var items = new[] { first, second };

        Assert.Equal(1000, IngestTimeline.RulerStart(first, items));
        Assert.Equal(3500, IngestTimeline.RulerStart(second, items));
    }

    [Fact]
    public void Without_inserts_the_ruler_and_the_recording_are_the_same_thing()
    {
        // The identity in the ordinary case, which is what makes this safe to put in the drawing
        // path: three of the four modes change nothing about where the recording appears.
        var items = new[]
        {
            Item(IngestMode.Overlay, 500, drop: 1000, path: "a.mov"),
            Item(IngestMode.Replace, 300, markIn: 2000, markOut: 2500, path: "b.mov"),
        };

        foreach (long frame in new[] { 0L, 999, 1000, 2500, 100_000 })
            Assert.Equal(frame, IngestTimeline.RulerFrameOf(frame, items));

        Assert.Equal(4321, IngestTimeline.RulerFrameOf(4321, None));
    }

    [Fact]
    public void An_insert_pushes_the_recording_along_the_ruler_from_its_own_position()
    {
        var items = new[] { Item(IngestMode.Insert, 500, drop: 1000) };

        Assert.Equal(999, IngestTimeline.RulerFrameOf(999, items));

        // At the insertion point the item plays first, so this captured frame appears after it.
        Assert.Equal(1500, IngestTimeline.RulerFrameOf(1000, items));
        Assert.Equal(2500, IngestTimeline.RulerFrameOf(2000, items));
    }

    [Fact]
    public void The_two_maps_are_inverses_of_each_other()
    {
        // The property that matters: a captured frame drawn at a ruler position must read back
        // as the same captured frame, or the blocks and the player disagree about what is where.
        var items = new[]
        {
            Item(IngestMode.Insert, 500, drop: 1000, path: "a.mov"),
            Item(IngestMode.Insert, 300, drop: 2000, path: "b.mov"),
        };

        foreach (long captured in new[] { 0L, 500, 999, 1000, 1500, 2000, 5000 })
        {
            long ruler = IngestTimeline.RulerFrameOf(captured, items);

            Assert.Equal(captured, IngestTimeline.At(ruler, items).CapturedFrame);
        }
    }

    // ------------------------------------------------------ cutting a segment for an insert

    [Fact]
    public void A_segment_with_nothing_inserted_in_it_is_one_piece_where_it_always_was()
    {
        var items = new[] { Item(IngestMode.Overlay, 500, drop: 100) };

        var pieces = IngestTimeline.Split(0, 750, items);

        IngestTimeline.Piece only = Assert.Single(pieces);

        Assert.Equal(0, only.CapturedStart);
        Assert.Equal(750, only.Frames);
        Assert.Equal(0, only.RulerStart);
    }

    [Fact]
    public void An_insert_inside_a_segment_cuts_it_in_two_with_the_item_between()
    {
        // A 30-second segment at 25 fps is 750 frames. Something inserted 15 seconds in - frame
        // 375 - splits it into 375 and 375, with the inserted length between them.
        var items = new[] { Item(IngestMode.Insert, 250, drop: 375) };

        var pieces = IngestTimeline.Split(0, 750, items);

        Assert.Equal(2, pieces.Count);

        Assert.Equal(0, pieces[0].CapturedStart);
        Assert.Equal(375, pieces[0].Frames);
        Assert.Equal(0, pieces[0].RulerStart);
        Assert.Equal(375, pieces[0].RulerEnd);

        // The second piece picks up after the inserted item, so there is a 250-frame gap on the
        // ruler between the two halves - which is where the item is drawn.
        Assert.Equal(375, pieces[1].CapturedStart);
        Assert.Equal(375, pieces[1].Frames);
        Assert.Equal(625, pieces[1].RulerStart);
    }

    [Fact]
    public void The_gap_between_the_halves_is_exactly_the_inserted_length()
    {
        var items = new[] { Item(IngestMode.Insert, 250, drop: 375) };

        var pieces = IngestTimeline.Split(0, 750, items);

        Assert.Equal(250, pieces[1].RulerStart - pieces[0].RulerEnd);
    }

    [Fact]
    public void The_pieces_still_add_up_to_the_whole_segment()
    {
        // Nothing of the recording may be lost or duplicated by the cutting, whatever is in it.
        var items = new[]
        {
            Item(IngestMode.Insert, 250, drop: 200, path: "a.mov"),
            Item(IngestMode.Insert, 100, drop: 500, path: "b.mov"),
        };

        var pieces = IngestTimeline.Split(0, 750, items);

        Assert.Equal(3, pieces.Count);
        Assert.Equal(750, pieces.Sum(p => p.Frames));

        // And they are contiguous in the recording.
        for (int i = 1; i < pieces.Count; i++)
            Assert.Equal(pieces[i - 1].CapturedStart + pieces[i - 1].Frames,
                         pieces[i].CapturedStart);
    }

    [Fact]
    public void An_insert_at_the_very_start_of_a_segment_moves_it_rather_than_cutting_it()
    {
        var items = new[] { Item(IngestMode.Insert, 250, drop: 1000) };

        var pieces = IngestTimeline.Split(1000, 750, items);

        IngestTimeline.Piece only = Assert.Single(pieces);

        Assert.Equal(750, only.Frames);
        Assert.Equal(1250, only.RulerStart);
    }

    [Fact]
    public void An_insert_outside_a_segment_does_not_cut_it()
    {
        var items = new[] { Item(IngestMode.Insert, 250, drop: 5000) };

        Assert.Single(IngestTimeline.Split(0, 750, items));

        // Nor does one exactly at the end: that belongs to whatever comes next.
        Assert.Single(IngestTimeline.Split(0, 750, new[] { Item(IngestMode.Insert, 250, drop: 750) }));
    }

    [Fact]
    public void Only_inserts_cut_a_segment()
    {
        // The other three cover the recording rather than displacing it, so the block underneath
        // is still continuous - it is simply hidden for a while.
        foreach (IngestMode mode in new[] { IngestMode.Overlay, IngestMode.FitToFill, IngestMode.Replace })
        {
            var items = new[] { Item(mode, 250, drop: 375, markIn: 375, markOut: 500) };

            Assert.Single(IngestTimeline.Split(0, 750, items));
        }
    }

    [Fact]
    public void Each_piece_is_drawn_where_the_player_would_look_for_it()
    {
        // The property that ties the drawing to playback: a piece drawn at a ruler position must
        // map back to that piece's own captured frames, or the blocks and the picture disagree.
        var items = new[] { Item(IngestMode.Insert, 250, drop: 375) };

        foreach (IngestTimeline.Piece piece in IngestTimeline.Split(0, 750, items))
        {
            Assert.Equal(piece.CapturedStart,
                         IngestTimeline.At(piece.RulerStart, items).CapturedFrame);

            Assert.Equal(piece.CapturedStart + piece.Frames - 1,
                         IngestTimeline.At(piece.RulerEnd - 1, items).CapturedFrame);
        }
    }

    [Fact]
    public void The_frame_after_an_insert_hands_back_to_the_recording_and_not_to_the_item()
    {
        // The handover at the end of an inserted item. The frame the transport is sent to must
        // be off the item and on the recording, at the captured frame the insertion interrupted
        // - not back inside the item, which is what it appeared to do when the playhead was
        // being computed from a player that had not finished swapping sources.
        var items = new[] { Item(IngestMode.Insert, 250, drop: 375) };

        // Last frame of the item, then the first frame after it.
        Assert.True(IngestTimeline.At(624, items).OnIngest);

        TimelineAt handover = IngestTimeline.At(625, items);

        Assert.False(handover.OnIngest);
        Assert.Null(handover.Path);
        Assert.Equal(375, handover.CapturedFrame);
    }

    [Fact]
    public void Walking_forward_off_an_item_always_escapes_it()
    {
        // What the transport does at the end of an item: step forward until the placement no
        // longer covers the frame. It must terminate within the item's own length from any
        // frame inside it, or the handover would hunt.
        var items = new[] { Item(IngestMode.Insert, 250, drop: 375) };

        for (long from = 375; from < 625; from++)
        {
            long at = from;
            int steps = 0;

            while (IngestTimeline.At(at, items).OnIngest && steps < 1000) { at++; steps++; }

            Assert.False(IngestTimeline.At(at, items).OnIngest);
            Assert.Equal(625, at);
            Assert.True(steps <= 250);
        }
    }

    [Fact]
    public void A_segment_of_no_length_is_no_pieces()
    {
        Assert.Empty(IngestTimeline.Split(0, 0, None));
        Assert.Empty(IngestTimeline.Split(0, -5, None));
    }

    [Fact]
    public void Placements_given_out_of_order_still_resolve_in_time_order()
    {
        // The list is whatever order they were dropped in; the answer must not depend on it.
        var items = new[]
        {
            Item(IngestMode.Insert, 300, drop: 2000, path: "late.mov"),
            Item(IngestMode.Insert, 500, drop: 1000, path: "early.mov"),
        };

        Assert.Equal("early.mov", IngestTimeline.At(1100, items).Path);
        Assert.Equal(2200, IngestTimeline.At(3000, items).CapturedFrame);
    }
}
