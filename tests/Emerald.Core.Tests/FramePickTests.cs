using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// Naming one frame of the day.
///
/// This exists because the timeline cannot be clicked precisely at any useful zoom — an hour
/// across a thousand pixels is ninety frames a pixel — so typing is the only way to reach a
/// chosen frame while still being able to see the shape of the recording. Which means the forms
/// an operator actually types all have to work, including the lazy ones.
/// </summary>
public sealed class FramePickTests
{
    private const int Rate = 25;
    private const long Day = 24L * 60 * 60 * Rate;      // 2,160,000

    private static long Pick(string text, long from = 0)
    {
        Assert.True(FramePick.TryParse(text, Rate, Day, from, out long frame, out string? error),
                    error);
        return frame;
    }

    private static string Refuse(string text, long from = 0)
    {
        Assert.False(FramePick.TryParse(text, Rate, Day, from, out _, out string? error));
        Assert.NotNull(error);
        return error!;
    }

    [Fact]
    public void A_timecode_is_the_frame_of_the_day_it_names()
    {
        Assert.Equal(0, Pick("00:00:00:00"));
        Assert.Equal(1, Pick("00:00:00:01"));
        Assert.Equal(Rate, Pick("00:00:01:00"));
        Assert.Equal(21 * 90_000 + 5 * 1500 + 11 * 25 + 4, Pick("21:05:11:04"));
    }

    [Fact]
    public void A_bare_number_is_a_frame_of_the_day()
    {
        Assert.Equal(0, Pick("0"));
        Assert.Equal(1234, Pick("1234"));
        Assert.Equal(Day - 1, Pick((Day - 1).ToString()));
    }

    [Fact]
    public void A_plus_or_minus_moves_from_where_the_playhead_is()
    {
        Assert.Equal(1050, Pick("+50", from: 1000));
        Assert.Equal(950, Pick("-50", from: 1000));
        Assert.Equal(1000, Pick("+0", from: 1000));
    }

    [Fact]
    public void A_relative_move_can_be_given_in_seconds()
    {
        Assert.Equal(1000 + 2 * Rate, Pick("+2s", from: 1000));
        Assert.Equal(1000 - 2 * Rate, Pick("-2s", from: 1000));
        Assert.Equal(1000 + 2 * Rate, Pick("+2S", from: 1000));
    }

    [Fact]
    public void A_relative_move_off_the_end_of_the_day_lands_on_the_end()
    {
        // Holding a key down at midnight means the start of the day, not an error message.
        Assert.Equal(0, Pick("-500", from: 10));
        Assert.Equal(Day - 1, Pick("+500", from: Day - 10));
    }

    [Fact]
    public void A_time_with_the_frames_left_off_is_that_second()
    {
        Assert.Equal(10 * 90_000 + 23 * 1500 + 45 * 25, Pick("10:23:45"));
    }

    [Fact]
    public void Minutes_and_seconds_alone_are_within_the_hour_the_playhead_is_in()
    {
        // The playhead sits at 10:00:00:00; "23:45" means 10:23:45.
        long tenOClock = 10 * 90_000;

        Assert.Equal(tenOClock + 23 * 1500 + 45 * 25, Pick("23:45", from: tenOClock));

        // And from a different hour it means a different frame, which is the whole point of it
        // being relative rather than a timecode missing its left-hand pieces.
        long twoOClock = 2 * 90_000;

        Assert.Equal(twoOClock + 23 * 1500 + 45 * 25, Pick("23:45", from: twoOClock));
    }

    [Fact]
    public void Semicolons_and_dots_separate_a_timecode_as_well_as_colons()
    {
        // Operators type what their other kit shows them, and drop-frame notation uses a
        // semicolon. The value is the same either way; nothing here is drop-frame.
        long expected = Pick("01:02:03:04");

        Assert.Equal(expected, Pick("01;02;03;04"));
        Assert.Equal(expected, Pick("01.02.03.04"));
    }

    [Fact]
    public void Surrounding_space_is_ignored()
    {
        Assert.Equal(Pick("1234"), Pick("  1234  "));
        Assert.Equal(Pick("+50", from: 1000), Pick("  +50 ", from: 1000));
    }

    [Fact]
    public void Nothing_typed_is_refused_without_moving_the_playhead()
    {
        Assert.False(FramePick.TryParse("", Rate, Day, 4321, out long frame, out _));
        Assert.Equal(4321, frame);

        Assert.False(FramePick.TryParse("   ", Rate, Day, 4321, out frame, out _));
        Assert.Equal(4321, frame);

        Assert.False(FramePick.TryParse(null, Rate, Day, 4321, out frame, out _));
        Assert.Equal(4321, frame);
    }

    [Fact]
    public void A_frame_past_the_end_of_the_day_is_refused_and_says_so()
    {
        Assert.Contains("frames in the day", Refuse(Day.ToString()));
    }

    [Fact]
    public void Nonsense_is_refused_rather_than_read_as_zero()
    {
        Refuse("tomorrow");
        Refuse("12:xx:00:00");
        Refuse("-");
        Refuse("+abc");
        Refuse("99:99:99:99");
    }

    [Fact]
    public void An_out_of_range_piece_of_a_partial_time_is_refused()
    {
        Refuse("10:99");
        Refuse("25:00:00");
    }

    [Fact]
    public void A_frame_number_at_or_past_the_rate_is_refused_in_a_timecode()
    {
        // 25 frames at 25 fps is the next second, and accepting it silently would mean two
        // spellings of one frame.
        Refuse("00:00:00:25");
    }

    [Fact]
    public void An_unknown_frame_rate_is_refused_rather_than_guessed()
    {
        Assert.False(FramePick.TryParse("1000", 0, Day, 0, out _, out string? error));
        Assert.Contains("rate", error);
    }
}
