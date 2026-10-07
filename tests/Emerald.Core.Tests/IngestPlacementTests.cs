using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The four ways a dropped item sits against the recording.
///
/// These rules are the whole substance of the feature and they differ in ways that are easy to
/// confuse: three lay over the recording and one lengthens it, two take their span from the
/// marked range and two from where the pointer was, and exactly one changes the speed. Getting
/// any of it wrong is a transmission that runs to the wrong length, which is why each rule is
/// pinned separately here rather than being read off the implementation.
/// </summary>
public sealed class IngestPlacementTests
{
    private const int Rate = 25;

    private static IngestPlan Plan(IngestMode mode, long ingest, long drop = 1000,
                                   long? markIn = null, long? markOut = null)
    {
        Assert.True(IngestPlacement.TryPlan(mode, ingest, drop, markIn, markOut,
                                            out IngestPlan plan, out string? problem),
                    problem);
        return plan;
    }

    private static string Refuse(IngestMode mode, long ingest, long drop = 1000,
                                 long? markIn = null, long? markOut = null)
    {
        Assert.False(IngestPlacement.TryPlan(mode, ingest, drop, markIn, markOut, out _,
                                             out string? problem));
        Assert.NotNull(problem);
        return problem!;
    }

    // ------------------------------------------------------------------ overlay

    [Fact]
    public void Overlay_sits_where_it_was_dropped_for_its_own_length()
    {
        IngestPlan plan = Plan(IngestMode.Overlay, ingest: 500, drop: 1000);

        Assert.Equal(1000, plan.StartFrame);
        Assert.Equal(500, plan.Frames);
        Assert.Equal(500, plan.Covers);
        Assert.Equal(1, plan.Speed);
        Assert.False(plan.Stretched);
    }

    [Fact]
    public void Overlay_does_not_move_what_follows_it()
    {
        Assert.Equal(0, Plan(IngestMode.Overlay, 500).ShiftsAfterBy);
    }

    [Fact]
    public void Overlay_needs_no_marked_range()
    {
        Assert.True(IngestPlacement.TryPlan(IngestMode.Overlay, 500, 1000, null, null,
                                            out _, out _));
    }

    // ------------------------------------------------------------------ fit to fill

    [Fact]
    public void Fit_to_fill_covers_the_marked_range_exactly()
    {
        IngestPlan plan = Plan(IngestMode.FitToFill, ingest: 500, markIn: 2000, markOut: 2250);

        Assert.Equal(2000, plan.StartFrame);
        Assert.Equal(250, plan.Frames);
        Assert.Equal(2250, plan.EndFrame);
    }

    [Fact]
    public void Fit_to_fill_speeds_up_material_longer_than_the_range()
    {
        // 500 frames of media into 250 frames of room: twice as fast.
        IngestPlan plan = Plan(IngestMode.FitToFill, ingest: 500, markIn: 2000, markOut: 2250);

        Assert.Equal(2.0, plan.Speed, 4);
        Assert.True(plan.Stretched);
    }

    [Fact]
    public void Fit_to_fill_slows_material_shorter_than_the_range()
    {
        // 250 frames of media into 500 frames of room: half speed.
        IngestPlan plan = Plan(IngestMode.FitToFill, ingest: 250, markIn: 2000, markOut: 2500);

        Assert.Equal(0.5, plan.Speed, 4);
        Assert.True(plan.Stretched);
    }

    [Fact]
    public void Fit_to_fill_at_an_exact_length_changes_no_speed()
    {
        IngestPlan plan = Plan(IngestMode.FitToFill, ingest: 500, markIn: 2000, markOut: 2500);

        Assert.Equal(1.0, plan.Speed, 4);
        Assert.False(plan.Stretched);
    }

    [Fact]
    public void Fit_to_fill_ignores_where_the_pointer_was()
    {
        // It is defined by the range, so the drop point must not leak into it.
        Assert.Equal(2000, Plan(IngestMode.FitToFill, 500, drop: 99_999,
                                markIn: 2000, markOut: 2250).StartFrame);
    }

    // ------------------------------------------------------------------ replace

    [Fact]
    public void Replace_keeps_its_own_speed()
    {
        IngestPlan plan = Plan(IngestMode.Replace, ingest: 500, markIn: 2000, markOut: 2500);

        Assert.Equal(1, plan.Speed);
        Assert.False(plan.Stretched);
    }

    [Fact]
    public void Replace_is_cut_short_by_a_range_shorter_than_the_media()
    {
        IngestPlan plan = Plan(IngestMode.Replace, ingest: 500, markIn: 2000, markOut: 2200);

        Assert.Equal(200, plan.Frames);     // only what fits goes out
        Assert.Equal(200, plan.Covers);
    }

    [Fact]
    public void Replace_leaves_the_rest_of_the_range_to_the_original()
    {
        // 300 frames of media in a 500-frame range: it occupies 300 and the original plays the
        // remaining 200. Covers is the range; Frames is the item.
        IngestPlan plan = Plan(IngestMode.Replace, ingest: 300, markIn: 2000, markOut: 2500);

        Assert.Equal(300, plan.Frames);
        Assert.Equal(500, plan.Covers);
        Assert.Equal(200, plan.Covers - plan.Frames);
    }

    [Fact]
    public void Replace_does_not_move_what_follows_it()
    {
        Assert.Equal(0, Plan(IngestMode.Replace, 300, markIn: 2000, markOut: 2500).ShiftsAfterBy);
    }

    // ------------------------------------------------------------------ insert

    [Fact]
    public void Insert_goes_in_at_the_drop_point_and_pushes_everything_later()
    {
        IngestPlan plan = Plan(IngestMode.Insert, ingest: 500, drop: 1000);

        Assert.Equal(1000, plan.StartFrame);
        Assert.Equal(500, plan.Frames);
        Assert.Equal(500, plan.ShiftsAfterBy);
    }

    [Fact]
    public void Insert_is_the_only_mode_that_lengthens_the_timeline()
    {
        Assert.Equal(0, Plan(IngestMode.Overlay, 500).ShiftsAfterBy);
        Assert.Equal(0, Plan(IngestMode.FitToFill, 500, markIn: 0, markOut: 250).ShiftsAfterBy);
        Assert.Equal(0, Plan(IngestMode.Replace, 500, markIn: 0, markOut: 250).ShiftsAfterBy);
        Assert.Equal(500, Plan(IngestMode.Insert, 500).ShiftsAfterBy);
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void The_range_modes_are_refused_without_a_range()
    {
        Assert.Contains("marked range", Refuse(IngestMode.FitToFill, 500));
        Assert.Contains("marked range", Refuse(IngestMode.Replace, 500));

        // One mark alone is not a range.
        Assert.Contains("marked range", Refuse(IngestMode.FitToFill, 500, markIn: 100));
        Assert.Contains("marked range", Refuse(IngestMode.Replace, 500, markOut: 100));
    }

    [Fact]
    public void An_inverted_or_empty_range_is_refused_rather_than_dividing_by_zero()
    {
        Assert.Contains("marked range", Refuse(IngestMode.FitToFill, 500, markIn: 500, markOut: 500));
        Assert.Contains("marked range", Refuse(IngestMode.FitToFill, 500, markIn: 500, markOut: 400));
    }

    [Fact]
    public void Media_of_unknown_length_is_refused_in_every_mode()
    {
        foreach (IngestMode mode in Enum.GetValues<IngestMode>())
            Assert.Contains("could not be read",
                            Refuse(mode, ingest: 0, markIn: 0, markOut: 250));
    }

    [Fact]
    public void A_drop_before_the_start_of_the_day_is_pulled_back_to_it()
    {
        Assert.Equal(0, Plan(IngestMode.Overlay, 500, drop: -50).StartFrame);
    }

    // ------------------------------------------------------------------ what it says

    [Fact]
    public void A_stretched_plan_says_its_speed_rather_than_only_its_mode()
    {
        string said = IngestPlacement.Describe(
            Plan(IngestMode.FitToFill, 500, markIn: 2000, markOut: 2250), Rate);

        Assert.Contains("sped up", said);
        Assert.Contains("2x", said);

        string slowed = IngestPlacement.Describe(
            Plan(IngestMode.FitToFill, 250, markIn: 2000, markOut: 2500), Rate);

        Assert.Contains("slowed", slowed);
    }

    [Fact]
    public void A_replace_that_does_not_fill_the_range_says_what_the_original_keeps()
    {
        string said = IngestPlacement.Describe(
            Plan(IngestMode.Replace, 300, markIn: 2000, markOut: 2500), Rate);

        Assert.Contains("original", said);
        Assert.Contains("200", said);
    }

    [Fact]
    public void An_insert_says_how_far_it_moves_what_follows()
    {
        string said = IngestPlacement.Describe(Plan(IngestMode.Insert, 500), Rate);

        Assert.Contains("moves", said);
        Assert.Contains("500", said);
    }
}
