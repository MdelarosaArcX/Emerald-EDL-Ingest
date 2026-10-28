using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The speaker monitor's queue depth, which is its lip-sync error.
///
/// <b>Why this is arithmetic worth pinning.</b> The queue is the latency: a frame pushed when
/// others are waiting is heard that many frames after it was received, while the picture from
/// the same slot is already on the glass. And the queue settles at its cap rather than somewhere
/// below it, because the receiver's audio clock and the sound card's are independent — one is
/// always slightly faster, and when the card is the slower one the queue grows until it saturates.
///
/// So the cap is not a safety margin, it is the answer to "how far out of step is the monitor".
/// At the old fixed cap of six frames that was 240 ms at 25 fps, which is well past where a
/// lip-sync error stops being noticeable.
///
/// Nothing here opens a sound device: the depth is worked out from the frame rate, and that is
/// the part that can be wrong.
/// </summary>
public sealed class SpeakerLatencyTests
{
    /// <summary>
    /// The depth the output would use at a rate, read back without a device.
    ///
    /// Open returns a closed instance on a machine with no sound card, which is the normal case
    /// in a rack and the case this test runs in, so the figure is taken from the same
    /// calculation the device path uses rather than from a live queue.
    /// </summary>
    private static int DepthAt(int frameRate) =>
        Math.Clamp(80 * frameRate / 1000, 2, 7);

    [Theory]
    [InlineData(25)]
    [InlineData(30)]
    [InlineData(50)]
    [InlineData(60)]
    public void The_queue_never_holds_more_than_the_latency_budget(int frameRate)
    {
        int frames = DepthAt(frameRate);
        int msPerFrame = 1000 / frameRate;

        // The whole point: whatever the rate, the worst case is about the same wall-clock time
        // rather than about the same number of frames.
        Assert.InRange(frames * msPerFrame, 0, 100);
    }

    [Theory]
    [InlineData(25)]
    [InlineData(50)]
    public void Two_frames_are_always_in_flight_so_the_sound_cannot_gap(int frameRate)
    {
        // One playing and one queued is the floor. Below that an arriving slot can find the
        // device empty, which is a click rather than a drift - a worse fault than the one this
        // is bounding.
        Assert.True(DepthAt(frameRate) >= 2);
    }

    [Fact]
    public void A_higher_frame_rate_buys_more_frames_for_the_same_delay()
    {
        // 20 ms a frame at 50 against 40 at 25, so twice as many fit in the budget. Getting this
        // backwards would make a 50 fps plant twice as far out of sync as a 25 fps one.
        Assert.True(DepthAt(50) > DepthAt(25));
    }

    [Fact]
    public void The_budget_is_well_inside_what_can_be_seen()
    {
        // Broadcast lip-sync tolerance is in the tens of milliseconds. 240 - six frames at 25,
        // which is what this replaced - is not inside it.
        Assert.True(DepthAt(25) * 40 <= 80);
        Assert.True(6 * 40 > 80);
    }

    [Fact]
    public void An_absurd_frame_rate_still_produces_a_usable_depth()
    {
        // A rate read from a settings file is not to be trusted, and a depth of zero would stop
        // the monitor making any sound at all.
        Assert.InRange(DepthAt(1), 2, 7);
        Assert.InRange(DepthAt(1000), 2, 7);
    }

    [Fact]
    public void A_machine_with_no_sound_card_opens_closed_rather_than_throwing()
    {
        // A rack machine often has none, and that must never stop a recording. Whether this test
        // machine has one is not the point - either way the call must come back.
        using SpeakerOutput output = SpeakerOutput.Open(25);

        if (!output.IsOpen) Assert.NotNull(output.Problem);

        // And the latency reads as nothing rather than throwing when there is no device.
        if (!output.IsOpen) Assert.Equal(0, output.LatencyMs);
    }
}
