using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The timeline's zoom ladder.
///
/// All of it is arithmetic an operator will notice immediately if it is wrong: a timeline that
/// opens on the wrong span, a key press that moves by less than a pixel, or a deepest zoom
/// that cannot actually address one frame.
/// </summary>
public class TimelineZoomTests
{
    private const int Rate = 25;

    [Fact]
    public void It_opens_on_one_hour()
    {
        ZoomLevel level = TimelineZoom.Ladder(Rate)[TimelineZoom.DefaultIndex(Rate)];

        Assert.Equal("1 hour", level.Name);
        Assert.Equal(3600 * Rate, level.SpanFrames);
    }

    [Fact]
    public void The_widest_it_goes_is_a_day_and_the_deepest_is_frames()
    {
        IReadOnlyList<ZoomLevel> ladder = TimelineZoom.Ladder(Rate);

        Assert.Equal(TimelineZoom.MaxSpanSeconds * (long)Rate, ladder[0].SpanFrames);
        Assert.Equal(1, ladder[^1].StepFrames);
        Assert.Equal(1, ladder[^1].TickFrames);
    }

    /// <summary>The thing the whole ladder exists for: at the bottom, one press is one frame.</summary>
    [Fact]
    public void At_full_zoom_an_arrow_key_moves_exactly_one_frame()
    {
        Assert.Equal(1, TimelineZoom.Ladder(Rate)[^1].StepFrames);
        Assert.Equal(1, TimelineZoom.Ladder(50)[^1].StepFrames);
    }

    /// <summary>At the hour view a frame is a fraction of a pixel; the step has to be visible.</summary>
    [Fact]
    public void At_the_hour_view_an_arrow_key_moves_a_minute()
    {
        ZoomLevel hour = TimelineZoom.Ladder(Rate)[TimelineZoom.DefaultIndex(Rate)];

        Assert.Equal(60 * Rate, hour.StepFrames);
        Assert.Equal("1 min", hour.StepName);
    }

    [Fact]
    public void Every_rung_is_narrower_than_the_one_above_and_steps_no_further()
    {
        IReadOnlyList<ZoomLevel> ladder = TimelineZoom.Ladder(Rate);

        for (int i = 1; i < ladder.Count; i++)
        {
            Assert.True(ladder[i].SpanFrames < ladder[i - 1].SpanFrames, $"span at {i}");
            Assert.True(ladder[i].StepFrames <= ladder[i - 1].StepFrames, $"step at {i}");
            Assert.True(ladder[i].StepFrames >= 1, $"step at {i} is at least a frame");
            Assert.True(ladder[i].TickFrames >= 1, $"tick at {i}");
        }
    }

    [Fact]
    public void Zooming_past_either_end_stays_on_the_ladder()
    {
        int last = TimelineZoom.Ladder(Rate).Count - 1;

        Assert.Equal(0, TimelineZoom.Step(0, -5, Rate));
        Assert.Equal(last, TimelineZoom.Step(last, 5, Rate));
    }

    [Fact]
    public void The_ladder_is_built_for_the_frame_rate_it_is_asked_about()
    {
        Assert.Equal(3600L * 50, TimelineZoom.Ladder(50)[TimelineZoom.DefaultIndex(50)].SpanFrames);
    }

    // ------------------------------------------------------------------ the window

    [Fact]
    public void A_recording_shorter_than_the_window_starts_at_the_beginning()
    {
        Assert.Equal(0, TimelineZoom.WindowStart(playhead: 500, spanFrames: 1000, totalFrames: 800));
    }

    [Fact]
    public void The_playhead_is_centred_once_there_is_recording_on_both_sides_of_it()
    {
        Assert.Equal(4500, TimelineZoom.WindowStart(playhead: 5000, spanFrames: 1000, totalFrames: 20000));
    }

    [Fact]
    public void The_window_pins_to_the_ends_rather_than_scrolling_into_nothing()
    {
        Assert.Equal(0, TimelineZoom.WindowStart(playhead: 10, spanFrames: 1000, totalFrames: 20000));
        Assert.Equal(19000, TimelineZoom.WindowStart(playhead: 19990, spanFrames: 1000, totalFrames: 20000));
    }

    // ------------------------------------------------------------------ pixels

    [Fact]
    public void A_frame_in_the_window_has_a_place_and_one_outside_it_does_not()
    {
        Assert.Equal(0, TimelineZoom.XOf(100, 100, 1000, 500));
        Assert.Equal(250, TimelineZoom.XOf(600, 100, 1000, 500));
        Assert.Null(TimelineZoom.XOf(99, 100, 1000, 500));
        Assert.Null(TimelineZoom.XOf(1101, 100, 1000, 500));
    }

    [Fact]
    public void Clicking_the_timeline_lands_on_a_frame_and_never_outside_the_recording()
    {
        Assert.Equal(600, TimelineZoom.FrameAt(250, 100, 1000, 500, 20000));
        Assert.Equal(0, TimelineZoom.FrameAt(-40, 0, 1000, 500, 20000));
        Assert.Equal(300, TimelineZoom.FrameAt(500, 0, 1000, 500, totalFrames: 300));
    }

    /// <summary>At full zoom a click has to be able to pick out one frame from its neighbour.</summary>
    [Fact]
    public void At_full_zoom_neighbouring_frames_are_separate_places_on_screen()
    {
        ZoomLevel deepest = TimelineZoom.Ladder(Rate)[^1];

        double? a = TimelineZoom.XOf(10, 0, deepest.SpanFrames, 1000);
        double? b = TimelineZoom.XOf(11, 0, deepest.SpanFrames, 1000);

        Assert.NotNull(a);
        Assert.NotNull(b);
        Assert.True(b - a >= 20, "a frame should be wide enough to click");
    }
}

/// <summary>
/// The timeline laid out on the station clock.
///
/// A segment that began at 11:34:52 belongs at 11:34:52, and what is on air belongs at the clock
/// less the delay it is running behind. Both have to land on the same ruler or the marker points
/// at the wrong media, which is the one thing this view exists to get right.
/// </summary>
public class TimelineDayTests
{
    private const int Rate = 25;

    [Fact]
    public void A_day_is_the_length_of_the_timeline()
    {
        Assert.Equal(24L * 60 * 60 * Rate, TimelineZoom.DayFrames(Rate));
        Assert.Equal(24L * 60 * 60 * 50, TimelineZoom.DayFrames(50));
    }

    [Fact]
    public void A_clip_lands_on_the_time_it_started()
    {
        // 11:34:52 exactly, which is where a segment named _11-34-52 belongs.
        long expected = ((11L * 3600) + (34 * 60) + 52) * Rate;

        Assert.Equal(expected, TimelineZoom.FrameOfDay(new TimeSpan(11, 34, 52), Rate));
        Assert.Equal(expected, TimelineZoom.FrameOfDay(new DateTime(2026, 9, 28, 11, 34, 52), Rate));
    }

    [Fact]
    public void Midnight_is_the_start_and_a_moment_before_it_is_the_end()
    {
        Assert.Equal(0, TimelineZoom.FrameOfDay(TimeSpan.Zero, Rate));
        Assert.Equal(TimelineZoom.DayFrames(Rate) - Rate,
                     TimelineZoom.FrameOfDay(new TimeSpan(23, 59, 59), Rate));
    }

    /// <summary>
    /// A delay reaching back over midnight is the case that would otherwise pile the marker up
    /// at the start of the day and have it point at the wrong media for a minute every night.
    /// </summary>
    [Fact]
    public void A_delay_that_reaches_back_over_midnight_wraps_rather_than_clamping()
    {
        long onAir = TimelineZoom.OnAirFrame(new TimeSpan(0, 0, 30), TimeSpan.FromMinutes(1), Rate);

        // Half a minute past midnight, a minute behind, is 23:59:30.
        Assert.Equal(((23L * 3600) + (59 * 60) + 30) * Rate, onAir);
    }

    [Fact]
    public void On_air_is_the_clock_less_the_delay()
    {
        long onAir = TimelineZoom.OnAirFrame(new TimeSpan(17, 45, 50), TimeSpan.FromMinutes(1), Rate);

        Assert.Equal(((17L * 3600) + (44 * 60) + 50) * Rate, onAir);
    }

    [Fact]
    public void With_no_delay_on_air_is_the_clock_itself()
    {
        var now = new TimeSpan(17, 44, 50);

        Assert.Equal(TimelineZoom.FrameOfDay(now, Rate),
                     TimelineZoom.OnAirFrame(now, TimeSpan.Zero, Rate));
    }

    [Fact]
    public void A_frame_of_the_day_reads_back_as_the_time_it_is()
    {
        var at = new TimeSpan(11, 34, 52);
        long frame = TimelineZoom.FrameOfDay(at, Rate);

        Assert.Equal(at, TimelineZoom.TimeOfFrame(frame, Rate));
    }
}
