using Emerald.Core;
using Emerald.Edl;
using Xunit;

namespace Emerald.Edl.Tests;

/// <summary>
/// The sum behind the countdown in the queue.
///
/// It is the same function the engine waits on, deliberately: a countdown that reached zero
/// at a different moment from the cue would be worse than no countdown at all. So the
/// awkward part - a clock that repeats every 24 hours and cannot tell "five seconds late"
/// from "very nearly a day early" - is settled once, here.
/// </summary>
public sealed class QueueCountdownTests
{
    private const int Rate = 25;

    private static Timecode Tc(string text)
    {
        Assert.True(Timecode.TryParse(text, Rate, out Timecode parsed, out string? problem), problem);
        return parsed;
    }

    private static long Until(string target, string now) =>
        PlayoutService.FramesUntil(Tc(target), Tc(now), Rate);

    [Fact]
    public void A_start_ten_seconds_away_is_ten_seconds_of_frames()
    {
        Assert.Equal(10 * Rate, Until("20:00:10:00", "20:00:00:00"));
    }

    [Fact]
    public void A_start_on_the_current_frame_is_zero()
    {
        Assert.Equal(0, Until("20:00:00:00", "20:00:00:00"));
    }

    [Fact]
    public void A_single_frame_away_is_one_frame()
    {
        Assert.Equal(1, Until("20:00:00:01", "20:00:00:00"));
    }

    [Fact]
    public void A_start_that_has_just_gone_by_is_zero_rather_than_almost_a_day()
    {
        // The engine's rule, and the reason the display has to share it: a message five
        // seconds late rolls now. Counting down 23:59:55 to it would be nonsense on screen
        // and a day of dead air behind it.
        Assert.Equal(0, Until("20:00:00:00", "20:00:05:00"));
    }

    [Fact]
    public void A_start_just_before_midnight_counts_across_it()
    {
        // Ten seconds, over the wrap. A subtraction that did not wrap would give a negative
        // number here and a start that never comes.
        Assert.Equal(10 * Rate, Until("00:00:05:00", "23:59:55:00"));
    }

    [Fact]
    public void Just_under_half_a_day_ahead_still_counts_down()
    {
        long halfDay = 12L * 3600L * Rate;

        Assert.Equal(halfDay - 1, PlayoutService.FramesUntil(
            new Timecode(halfDay - 1, Rate), new Timecode(0, Rate), Rate));
    }

    [Fact]
    public void Just_over_half_a_day_ahead_is_read_as_already_gone()
    {
        long halfDay = 12L * 3600L * Rate;

        Assert.Equal(0, PlayoutService.FramesUntil(
            new Timecode(halfDay + 1, Rate), new Timecode(0, Rate), Rate));
    }

    [Fact]
    public void A_frame_rate_of_zero_never_divides_by_it()
    {
        // The clock is offline before its first sample, and the queue still draws.
        Assert.Equal(0, PlayoutService.FramesUntil(Tc("20:00:10:00"), Tc("20:00:00:00"), 0));
    }
}
