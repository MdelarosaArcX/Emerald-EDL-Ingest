using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// Where a level lands on the bar, and what "this pair is carrying a language" means.
///
/// The second one is the interesting half. A DELTACAST receiver hands back a full buffer of
/// zeros for a channel nothing is embedded on rather than saying the group is absent, so
/// "the card returned data" is true for all sixteen on every feed. The deck's first run on
/// real hardware reported eight pairs against a single-language source because of exactly
/// that, and a recording would have written seven silent tracks.
/// </summary>
public sealed class AudioLevelScaleTests
{
    [Fact]
    public void Nothing_is_at_the_bottom_of_the_bar_and_full_scale_at_the_top()
    {
        Assert.Equal(0, new AudioLevel(AudioLevel.Silence, AudioLevel.Silence, true).PeakFraction);
        Assert.Equal(1, new AudioLevel(0, 0, true).PeakFraction);
    }

    [Fact]
    public void Halfway_down_the_scale_is_halfway_along_the_bar()
    {
        Assert.Equal(0.5, new AudioLevel(AudioLevel.Silence / 2, AudioLevel.Silence / 2, true).PeakFraction,
                     precision: 3);
    }

    [Fact]
    public void A_level_past_full_scale_does_not_run_off_the_end_of_the_bar()
    {
        Assert.Equal(1, new AudioLevel(6, 6, true).PeakFraction);
    }

    [Fact]
    public void An_absent_channel_reads_as_nothing_rather_than_as_a_number()
    {
        Assert.False(AudioLevel.Absent.Present);
        Assert.Equal(0, AudioLevel.Absent.PeakFraction);
    }

    [Fact]
    public void A_meter_fed_silence_is_present_but_empty()
    {
        // Present and silent is a real state - a language between words - and it must not
        // read the same as a pair the feed does not carry at all.
        var meter = new AudioMeter();
        meter.Push(new short[960], 960, present: true);

        Assert.True(meter.Level.Present);
        Assert.Equal(0, meter.Level.PeakFraction);
    }

    [Fact]
    public void A_meter_told_the_channel_is_absent_says_so_however_loud_the_buffer_is()
    {
        // The buffer handed in is deliberately full-scale: presence is the caller's decision,
        // taken from whether the channel has carried signal, not from what is in the array.
        var loud = new short[960];
        for (int i = 0; i < loud.Length; i++) loud[i] = short.MaxValue;

        var meter = new AudioMeter();
        meter.Push(loud, 960, present: false);

        Assert.False(meter.Level.Present);
    }
}
