using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// Timecode is the one piece of arithmetic every module depends on — cue points, durations,
/// stop times and the recorder's tally all run through it — and it is pure, so it is worth
/// pinning down here rather than only on hardware.
/// </summary>
public class TimecodeTests
{
    [Theory]
    [InlineData("00:00:00:00", 25, 0)]
    [InlineData("00:00:01:00", 25, 25)]
    [InlineData("00:01:00:00", 25, 1500)]
    [InlineData("01:00:00:00", 25, 90_000)]
    [InlineData("23:59:59:24", 25, 2_159_999)]
    [InlineData("01:00:00:00", 50, 180_000)]
    public void Parses_to_a_frame_count(string text, int rate, long frames)
    {
        Assert.True(Timecode.TryParse(text, rate, out Timecode tc, out string? error), error);
        Assert.Equal(frames, tc.TotalFrames);
        Assert.Equal(text, tc.ToString());
    }

    [Theory]
    [InlineData("24:00:00:00", 25)]   // hours are 00-23
    [InlineData("00:60:00:00", 25)]
    [InlineData("00:00:60:00", 25)]
    [InlineData("00:00:00:25", 25)]   // frame number must be below the rate
    [InlineData("1:2:3", 25)]
    [InlineData("aa:bb:cc:dd", 25)]
    [InlineData("", 25)]
    public void Rejects_malformed_or_out_of_range(string text, int rate)
    {
        Assert.False(Timecode.TryParse(text, rate, out _, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Rejects_an_unknown_frame_rate()
    {
        Assert.False(Timecode.TryParse("00:00:01:00", 0, out _, out string? error));
        Assert.Equal("Frame rate is unknown.", error);
    }

    [Fact]
    public void Wraps_at_midnight()
    {
        Timecode.TryParse("23:59:59:24", 25, out Timecode end, out _);
        Assert.Equal("00:00:00:00", end.AddWrapping(1).ToString());
        Assert.Equal("00:00:01:00", end.AddWrapping(26).ToString());
    }

    [Fact]
    public void Wraps_backwards_past_midnight()
    {
        Timecode.TryParse("00:00:00:00", 25, out Timecode start, out _);
        Assert.Equal("23:59:59:24", start.AddWrapping(-1).ToString());
    }

    /// <summary>
    /// A failed TryParse leaves default(Timecode), which has Rate 0. That value used to reach
    /// AddWrapping while the operator was still typing and divide by zero.
    /// </summary>
    [Fact]
    public void Adding_to_a_rateless_timecode_does_not_divide_by_zero()
    {
        Timecode.TryParse("nonsense", 25, out Timecode bad, out _);
        Assert.Equal(0, bad.Rate);

        Timecode result = bad.AddWrapping(100);
        Assert.Equal(0, result.TotalFrames);
    }

    [Fact]
    public void AddFrames_does_not_go_negative()
    {
        Timecode.TryParse("00:00:00:10", 25, out Timecode tc, out _);
        Assert.Equal(0, tc.AddFrames(-100).TotalFrames);
    }

    [Theory]
    [InlineData(25, 50, "00:00:10:00", "00:00:10:00")]
    [InlineData(25, 30, "01:00:00:00", "01:00:00:00")]
    public void Rebase_keeps_the_same_wall_clock_position(int from, int to, string text, string expected)
    {
        Timecode.TryParse(text, from, out Timecode tc, out _);
        Timecode rebased = tc.Rebase(to);

        Assert.Equal(to, rebased.Rate);
        Assert.Equal(expected, rebased.ToString());
        Assert.Equal(tc.TotalSeconds, rebased.TotalSeconds, 3);
    }

    [Fact]
    public void Rebase_ignores_a_nonsense_rate()
    {
        Timecode.TryParse("00:00:10:00", 25, out Timecode tc, out _);
        Assert.Equal(tc, tc.Rebase(0));
    }
}
