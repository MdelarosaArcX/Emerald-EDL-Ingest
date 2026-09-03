using Emerald.Core;
using Xunit;

namespace Emerald.Ingest.Tests;

/// <summary>
/// The SOM / EOM / Duration convention, pinned down.
///
/// Every other test in this project, and every schedule the operator reads, rests on these
/// four sums being what the module says they are. They are pure arithmetic, so there is no
/// excuse for finding out on air that they were not.
/// </summary>
public class TimecodeCalculationTests
{
    private readonly ITimecodeCalculationService _calc = TimecodeCalculationService.Instance;

    private static Timecode Tc(string text, int rate = 25)
    {
        Assert.True(Timecode.TryParse(text, rate, out Timecode value, out string? error), error);
        return value;
    }

    [Fact]
    public void Eom_is_the_reference_plus_the_duration()
    {
        Timecode eom = _calc.CalculateEomFromDuration(Tc("20:57:26:00"), Tc("00:15:00:00"));
        Assert.Equal("21:12:26:00", eom.ToString());
    }

    [Fact]
    public void Editing_the_duration_moves_the_eom_with_it()
    {
        Timecode reference = Tc("20:57:26:00");

        Assert.Equal("21:12:26:00", _calc.CalculateEomFromDuration(reference, Tc("00:15:00:00")).ToString());
        Assert.Equal("21:17:26:00", _calc.CalculateEomFromDuration(reference, Tc("00:20:00:00")).ToString());
    }

    [Fact]
    public void Duration_and_eom_are_inverses_of_each_other()
    {
        Timecode reference = Tc("20:57:26:00");
        Timecode duration = Tc("00:15:00:00");

        Timecode eom = _calc.CalculateEomFromDuration(reference, duration);
        Assert.Equal(duration.TotalFrames, _calc.CalculateDurationFromEom(reference, eom).TotalFrames);
    }

    [Fact]
    public void A_recording_across_midnight_rolls_the_eom_forward()
    {
        Timecode eom = _calc.CalculateEomFromDuration(Tc("23:50:00:00"), Tc("00:20:00:00"));
        Assert.Equal("00:10:00:00", eom.ToString());
    }

    [Fact]
    public void An_eom_earlier_in_the_day_than_the_reference_is_tomorrows()
    {
        // 23:50 -> 00:10 is twenty minutes, not twenty-three hours and forty in reverse.
        Timecode duration = _calc.CalculateDurationFromEom(Tc("23:50:00:00"), Tc("00:10:00:00"));
        Assert.Equal("00:20:00:00", duration.ToString());
    }

    [Fact]
    public void Frames_until_never_counts_backwards()
    {
        Assert.Equal(0, _calc.FramesUntil(Tc("10:00:00:00"), Tc("10:00:00:00")));
        Assert.Equal(25, _calc.FramesUntil(Tc("10:00:00:00"), Tc("10:00:01:00")));

        // A second before midnight to a second after is fifty frames forward, not a day back.
        Assert.Equal(50, _calc.FramesUntil(Tc("23:59:59:00"), Tc("00:00:01:00")));
    }

    [Theory]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(60)]
    public void The_arithmetic_holds_at_every_supported_rate(int rate)
    {
        Timecode reference = Tc("20:57:26:00", rate);
        Timecode duration = Tc("00:15:00:00", rate);

        Timecode eom = _calc.CalculateEomFromDuration(reference, duration);

        Assert.Equal("21:12:26:00", eom.ToString());
        Assert.Equal(15 * 60 * rate, duration.TotalFrames);
        Assert.Equal(duration.TotalFrames, _calc.CalculateDurationFromEom(reference, eom).TotalFrames);
    }

    [Fact]
    public void A_long_recording_is_counted_in_frames_not_hours()
    {
        // Eleven hours at 50 fps is just short of two million frames; nothing here overflows
        // or rounds, because it is all integer frame arithmetic.
        Timecode duration = Tc("11:00:00:00", 50);
        Assert.Equal(11L * 3600 * 50, duration.TotalFrames);
        Assert.Equal("07:00:00:00", _calc.CalculateEomFromDuration(Tc("20:00:00:00", 50), duration).ToString());
    }
}
