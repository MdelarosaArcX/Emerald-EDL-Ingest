using Emerald.Core;
using Emerald.Media;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// What a marked range comes to on disk.
///
/// The case worth protecting is the ordinary one: at two minutes a segment, almost every range
/// an operator marks crosses a boundary, so the answer is a tail, some whole files and a head.
/// Getting that wrong produces an export that looks plausible and is the wrong material, which
/// is the kind of fault found on air rather than at the desk.
/// </summary>
public sealed class RangePlanTests
{
    /// <summary>A store nothing is written to: every test here either refuses before touching
    /// the disk or asks only where a path would land.</summary>
    private static readonly string Store = System.IO.Path.Combine("C:", "emerald-store");

    // Three two-minute segments at 25, laid end to end from the top of the hour.
    private static readonly (string, long, long)[] Three =
    {
        ("a.mp4", 0, 3000),
        ("b.mp4", 3000, 3000),
        ("c.mp4", 6000, 3000),
    };

    [Fact]
    public void A_range_inside_one_segment_takes_only_that_segment()
    {
        var parts = RangePlan.Parts(Three, 3500, 4000);

        RangePart only = Assert.Single(parts);
        Assert.Equal("b.mp4", only.Path);
        Assert.Equal(500, only.FromFrame);
        Assert.Equal(500, only.Frames);
    }

    [Fact]
    public void A_range_across_a_boundary_takes_the_tail_of_one_and_the_head_of_the_next()
    {
        var parts = RangePlan.Parts(Three, 2900, 3100);

        Assert.Equal(2, parts.Count);

        Assert.Equal("a.mp4", parts[0].Path);
        Assert.Equal(2900, parts[0].FromFrame);
        Assert.Equal(100, parts[0].Frames);

        Assert.Equal("b.mp4", parts[1].Path);
        Assert.Equal(0, parts[1].FromFrame);
        Assert.Equal(100, parts[1].Frames);
    }

    [Fact]
    public void Segments_wholly_inside_the_range_are_taken_whole()
    {
        var parts = RangePlan.Parts(Three, 1000, 7000);

        Assert.Equal(3, parts.Count);
        Assert.Equal(3000, parts[1].Frames);   // b entire
        Assert.Equal(0, parts[1].FromFrame);
        Assert.Equal(1000, parts[2].Frames);   // head of c
    }

    [Fact]
    public void The_parts_add_up_to_the_length_of_the_range()
    {
        var parts = RangePlan.Parts(Three, 1234, 8765);

        Assert.Equal(8765 - 1234, parts.Sum(p => p.Frames));
    }

    [Fact]
    public void Segments_the_range_does_not_touch_are_left_out()
    {
        var parts = RangePlan.Parts(Three, 6000, 6500);

        Assert.Equal("c.mp4", Assert.Single(parts).Path);
    }

    [Fact]
    public void A_boundary_touched_exactly_does_not_produce_an_empty_part()
    {
        // The range ends on the first frame of b: b contributes nothing and should not appear,
        // or ffmpeg is handed a zero-length piece.
        var parts = RangePlan.Parts(Three, 2000, 3000);

        Assert.Equal("a.mp4", Assert.Single(parts).Path);
    }

    [Fact]
    public void A_range_with_the_marks_the_wrong_way_round_covers_nothing()
    {
        Assert.Empty(RangePlan.Parts(Three, 4000, 2000));
        Assert.Empty(RangePlan.Parts(Three, 4000, 4000));
    }

    [Fact]
    public void A_range_past_the_end_of_the_recording_covers_nothing()
    {
        Assert.Empty(RangePlan.Parts(Three, 9000, 9500));
    }

    [Fact]
    public void A_gap_between_segments_is_not_filled_in()
    {
        // A dropped segment leaves a hole. The export should contain what exists either side
        // and nothing invented for the middle.
        (string, long, long)[] gapped = { ("a.mp4", 0, 3000), ("c.mp4", 6000, 3000) };

        var parts = RangePlan.Parts(gapped, 2900, 6100);

        Assert.Equal(2, parts.Count);
        Assert.Equal(100, parts[0].Frames);
        Assert.Equal(100, parts[1].Frames);
    }

    [Fact]
    public void A_segment_whose_length_is_unknown_is_skipped()
    {
        // The segment being written has no readable duration, so it arrives as zero frames.
        (string, long, long)[] growing = { ("a.mp4", 0, 3000), ("b.mp4", 3000, 0) };

        Assert.Equal("a.mp4", Assert.Single(RangePlan.Parts(growing, 2000, 5000)).Path);
    }

    [Fact]
    public void The_suggested_name_carries_the_recording_and_the_timecode()
    {
        Assert.Equal("2026-10-02_14-30-00_21-05-11-04",
            RangePlan.SuggestName("2026-10-02_14-30-00", new Timecode(1_897_779, 25)));
    }

    [Fact]
    public void The_suggested_name_holds_nothing_a_filename_cannot()
    {
        string name = RangePlan.SuggestName(@"a/b\c:d", new Timecode(0, 25));

        Assert.DoesNotContain('/', name);
        Assert.DoesNotContain('\\', name);
        Assert.DoesNotContain(':', name);
    }

    [Fact]
    public void An_export_of_nothing_is_refused_rather_than_attempted()
    {
        ExportResult result = RangeExport.Write("ffmpeg", Store, "x", "", [], 25, "mp4");

        Assert.False(result.Ok);
        Assert.Contains("no media", result.Problem);
    }

    [Fact]
    public void An_export_with_no_name_is_refused()
    {
        ExportResult result = RangeExport.Write(
            "ffmpeg", Store, "", "", [new RangePart("a.mp4", 0, 10)], 25, "mp4");

        Assert.False(result.Ok);
        Assert.Contains("no name", result.Problem);
    }

    [Fact]
    public void Exports_land_beside_the_store_and_not_inside_it()
    {
        Assert.Equal(System.IO.Path.Combine(Store, "ingest"), RangeExport.FolderFor(Store));
    }

    [Fact]
    public void Listing_a_store_that_has_never_exported_anything_is_empty_and_not_an_error()
    {
        Assert.Empty(RangeExport.List(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "emerald-no-such-store"), null));
    }
}
