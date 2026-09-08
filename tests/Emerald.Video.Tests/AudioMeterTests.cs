using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// Turning samples into something a bar can draw.
///
/// The deck's meters were a fixed-width rectangle labelled "3 dB" that never moved, which is
/// worse than no meter: it says the audio is fine whatever is actually on the wire. These are
/// about the arithmetic behind a bar that means something — the scale it is drawn on, and the
/// ballistics that make it readable rather than a flicker.
/// </summary>
public sealed class AudioMeterTests
{
    /// <summary>A frame of a constant amplitude, which has a peak that is easy to reason about.</summary>
    private static short[] Tone(short amplitude, int samples = 960)
    {
        var frame = new short[samples];
        for (int i = 0; i < samples; i++) frame[i] = i % 2 == 0 ? amplitude : (short)-amplitude;
        return frame;
    }

    [Fact]
    public void Full_scale_reads_as_zero_dB()
    {
        var meter = new AudioMeter();
        meter.Push(Tone(short.MaxValue), 960, present: true);

        Assert.Equal(0, meter.Level.PeakDb, precision: 1);
        Assert.Equal(1, meter.Level.PeakFraction, precision: 2);
    }

    [Fact]
    public void Half_amplitude_is_about_six_dB_down()
    {
        var meter = new AudioMeter();
        meter.Push(Tone(16384), 960, present: true);

        // Halving the amplitude is -6.02 dB, which is the property that makes decibels the
        // right scale for this: a bar drawn from raw amplitude would still read nearly full.
        Assert.Equal(-6.0, meter.Level.PeakDb, precision: 1);
    }

    [Fact]
    public void Silence_bottoms_out_rather_than_going_to_minus_infinity()
    {
        var meter = new AudioMeter();
        meter.Push(new short[960], 960, present: true);

        Assert.Equal(AudioLevel.Silence, meter.Level.PeakDb);
        Assert.Equal(0, meter.Level.PeakFraction);
    }

    [Fact]
    public void The_bar_is_drawn_on_a_decibel_scale_not_an_amplitude_one()
    {
        // Half amplitude has to land near the middle of the bar, not near the top. This is the
        // whole reason the fraction is computed from dB: a linear bar spends all its travel in
        // the last few dB and reads as either full or empty.
        var meter = new AudioMeter();
        meter.Push(Tone(16384), 960, present: true);

        Assert.InRange(meter.Level.PeakFraction, 0.85, 0.95);
    }

    [Fact]
    public void A_channel_that_is_not_on_the_wire_is_absent_rather_than_quiet()
    {
        var meter = new AudioMeter();
        meter.Push(Array.Empty<short>(), 0, present: false);

        // Absent and silent look the same on a bar and mean quite different things: one is a
        // pair the feed does not carry, the other is a pair carrying nothing at this instant.
        Assert.False(meter.Level.Present);
    }

    [Fact]
    public void The_bar_jumps_to_a_rise_immediately()
    {
        var meter = new AudioMeter();

        meter.Push(Tone(1000), 960, present: true);
        meter.Push(Tone(short.MaxValue), 960, present: true);

        // No ramp on the way up: a transient that took several frames to show would be over
        // before the bar got there.
        Assert.Equal(0, meter.Level.PeakDb, precision: 1);
    }

    [Fact]
    public void The_bar_falls_back_gradually_rather_than_dropping_out()
    {
        var meter = new AudioMeter();
        meter.Push(Tone(short.MaxValue), 960, present: true);

        double loud = meter.Level.PeakDb;

        meter.Idle(TimeSpan.FromMilliseconds(100));
        double after = meter.Level.PeakDb;

        Assert.True(after < loud, "it must fall");
        Assert.True(after > AudioLevel.Silence, "but not all the way at once - that would be a flicker");
    }

    [Fact]
    public void Long_enough_with_nothing_arriving_and_it_empties()
    {
        var meter = new AudioMeter();
        meter.Push(Tone(short.MaxValue), 960, present: true);

        // A lost signal must not leave the bar frozen where it was, which looks exactly like
        // a signal that is still there.
        meter.Idle(TimeSpan.FromSeconds(10));

        Assert.Equal(AudioLevel.Silence, meter.Level.PeakDb);
    }

    [Fact]
    public void Hot_and_clipping_are_where_a_desk_puts_them()
    {
        var quiet = new AudioLevel(-20, -25, true);
        var hot = new AudioLevel(-6, -12, true);
        var over = new AudioLevel(-0.1, -6, true);

        Assert.False(quiet.IsHot);
        Assert.True(hot.IsHot);
        Assert.False(hot.IsClipping);
        Assert.True(over.IsClipping);
    }

    [Fact]
    public void Reset_puts_it_back_to_nothing()
    {
        var meter = new AudioMeter();
        meter.Push(Tone(short.MaxValue), 960, present: true);
        meter.Reset();

        Assert.Equal(AudioLevel.Silence, meter.Level.PeakDb);
        Assert.False(meter.Level.Present);
    }
}
