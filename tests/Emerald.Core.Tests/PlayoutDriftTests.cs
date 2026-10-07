using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// Counting what did not reach air.
///
/// The two things worth protecting: running slightly ahead of the clock is normal and must not
/// read as a loss, and the threshold for saying so has to be loose enough that a healthy
/// transmitter stays quiet. A drop line every second on a plant that is fine is the same as no
/// drop lines at all.
/// </summary>
public sealed class PlayoutDriftTests
{
    [Fact]
    public void A_transmitter_at_rate_must_have_taken_rate_frames_a_second()
    {
        Assert.Equal(25, PlayoutDrift.Expected(TimeSpan.FromSeconds(1), 25));
        Assert.Equal(250, PlayoutDrift.Expected(TimeSpan.FromSeconds(10), 25));
        Assert.Equal(50, PlayoutDrift.Expected(TimeSpan.FromSeconds(1), 50));
    }

    [Fact]
    public void Nothing_is_expected_before_anything_has_happened()
    {
        Assert.Equal(0, PlayoutDrift.Expected(TimeSpan.Zero, 25));
        Assert.Equal(0, PlayoutDrift.Expected(TimeSpan.FromSeconds(-1), 25));
        Assert.Equal(0, PlayoutDrift.Expected(TimeSpan.FromSeconds(10), 0));
    }

    [Fact]
    public void Keeping_up_is_no_drop()
    {
        Assert.Equal(0, PlayoutDrift.Dropped(TimeSpan.FromSeconds(10), 25, framesOut: 250));
    }

    [Fact]
    public void Falling_behind_the_clock_is_the_drop()
    {
        Assert.Equal(10, PlayoutDrift.Dropped(TimeSpan.FromSeconds(10), 25, framesOut: 240));
        Assert.Equal(250, PlayoutDrift.Dropped(TimeSpan.FromSeconds(10), 25, framesOut: 0));
    }

    [Fact]
    public void Running_ahead_of_the_clock_is_not_a_negative_drop()
    {
        // The card's queue holds a few frames, so the engine is always slightly in front of what
        // is actually radiating. Reporting that as a loss of minus three would be nonsense.
        Assert.Equal(0, PlayoutDrift.Dropped(TimeSpan.FromSeconds(10), 25, framesOut: 253));
    }

    [Fact]
    public void A_frame_or_two_behind_is_not_worth_a_line()
    {
        Assert.False(PlayoutDrift.WorthSaying(dropped: 1, alreadySaid: 0, rate: 25));
        Assert.False(PlayoutDrift.WorthSaying(dropped: 4, alreadySaid: 0, rate: 25));
    }

    [Fact]
    public void A_fifth_of_a_second_lost_is_worth_a_line()
    {
        Assert.True(PlayoutDrift.WorthSaying(dropped: 5, alreadySaid: 0, rate: 25));
        Assert.True(PlayoutDrift.WorthSaying(dropped: 100, alreadySaid: 0, rate: 25));
    }

    [Fact]
    public void The_same_loss_is_not_said_twice()
    {
        // Said at 5; at 6 there is nothing new to report, and at 10 there is.
        Assert.False(PlayoutDrift.WorthSaying(dropped: 6, alreadySaid: 5, rate: 25));
        Assert.True(PlayoutDrift.WorthSaying(dropped: 10, alreadySaid: 5, rate: 25));
    }

    [Fact]
    public void The_threshold_follows_the_frame_rate()
    {
        // A fifth of a second is ten frames at 50 and five at 25, so a 50 fps plant does not get
        // twice as many lines for the same amount of lost time.
        Assert.False(PlayoutDrift.WorthSaying(dropped: 9, alreadySaid: 0, rate: 50));
        Assert.True(PlayoutDrift.WorthSaying(dropped: 10, alreadySaid: 0, rate: 50));

        // And a nonsense rate still reports something rather than dividing to zero and saying
        // everything.
        Assert.True(PlayoutDrift.WorthSaying(dropped: 1, alreadySaid: 0, rate: 0));
    }
}
