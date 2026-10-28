using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The rule that keeps a recording's sound against its picture while frames are being lost.
///
/// The recorder writes two raw pipes with no timestamps in them, so the alignment between sound
/// and picture <i>is</i> how many of each were written. Every case below is a way that count can
/// go wrong, and each one shows up as lip sync walking away over a long recording rather than as
/// anything failing at the time.
/// </summary>
public sealed class CaptureSyncTests
{
    [Fact]
    public void A_slot_whose_video_was_written_gets_its_audio()
    {
        // The ordinary case, and the one that happens tens of thousands of times an hour.
        Assert.Equal(1, CaptureSync.AudioChunksDue(videoFramesIn: 1, audioChunksIn: 0));
        Assert.Equal(1, CaptureSync.AudioChunksDue(videoFramesIn: 5000, audioChunksIn: 4999));
    }

    [Fact]
    public void A_slot_whose_video_was_dropped_gets_no_audio()
    {
        // The fix. The video count did not move, so neither does the audio - a frame of sound
        // with no picture behind it is exactly what pushes the two apart.
        Assert.Equal(0, CaptureSync.AudioChunksDue(videoFramesIn: 4999, audioChunksIn: 4999));
    }

    [Fact]
    public void A_long_stall_costs_both_streams_equally_and_nothing_drifts()
    {
        // A hundred slots in a row dropped: the audio for all hundred goes with them, and the
        // two counts come out of it still equal.
        long video = 1000, audio = 1000;

        for (int slot = 0; slot < 100; slot++)
        {
            // video dropped, so no increment
            audio += CaptureSync.AudioChunksDue(video, audio);
        }

        Assert.Equal(0, CaptureSync.Imbalance(video, audio));
        Assert.Equal(0, CaptureSync.DriftMs(video, audio, 25));
    }

    [Fact]
    public void Audio_that_could_not_be_queued_is_caught_up_afterwards()
    {
        // The other direction: the audio pipe was momentarily full, so a slot's sound was owed.
        // The next slots pay it back rather than leaving the file permanently short.
        long video = 10, audio = 7;

        int due = CaptureSync.AudioChunksDue(video, audio);

        Assert.Equal(3, due);

        audio += due;
        Assert.Equal(0, CaptureSync.Imbalance(video, audio));
    }

    [Fact]
    public void Catching_up_is_limited_so_the_receiver_is_never_held_up()
    {
        // A second of owed audio must not all be written in one slot: that loop is reading the
        // card, and stalling it would turn a sync error into lost frames.
        Assert.Equal(3, CaptureSync.AudioChunksDue(videoFramesIn: 1000, audioChunksIn: 900));

        Assert.Equal(1, CaptureSync.AudioChunksDue(videoFramesIn: 1000, audioChunksIn: 900,
                                                   catchUpLimit: 1));

        // And a nonsense limit still makes progress rather than stalling for ever.
        Assert.Equal(1, CaptureSync.AudioChunksDue(videoFramesIn: 1000, audioChunksIn: 900,
                                                   catchUpLimit: 0));
    }

    [Fact]
    public void Audio_is_never_written_ahead_of_the_picture()
    {
        // If the counts ever said audio was ahead, writing more would make it worse. Nothing is
        // due, and the imbalance reads negative so it can be reported as the fault it would be.
        Assert.Equal(0, CaptureSync.AudioChunksDue(videoFramesIn: 100, audioChunksIn: 105));
        Assert.Equal(-5, CaptureSync.Imbalance(100, 105));
    }

    [Fact]
    public void The_drift_is_reported_in_the_units_somebody_watching_it_back_would_notice()
    {
        // Twenty-five frames at 25 fps is a second of lip-sync error.
        Assert.Equal(1000, CaptureSync.DriftMs(videoFramesIn: 1025, audioChunksIn: 1000, frameRate: 25));
        Assert.Equal(40, CaptureSync.DriftMs(videoFramesIn: 1001, audioChunksIn: 1000, frameRate: 25));

        // The same count of frames is less time at a higher rate.
        Assert.Equal(500, CaptureSync.DriftMs(videoFramesIn: 1025, audioChunksIn: 1000, frameRate: 50));
    }

    [Fact]
    public void An_unknown_frame_rate_reports_no_drift_rather_than_dividing_by_zero()
    {
        Assert.Equal(0, CaptureSync.DriftMs(1025, 1000, frameRate: 0));
        Assert.Equal(0, CaptureSync.DriftMs(1025, 1000, frameRate: -25));
    }

    [Fact]
    public void A_whole_recording_of_mixed_drops_comes_out_aligned()
    {
        // The property that matters end to end: whatever pattern of drops a recording suffers,
        // the two streams finish with the same number of frames in them.
        long video = 0, audio = 0;

        var rng = new Random(20261012);

        for (int slot = 0; slot < 100_000; slot++)
        {
            // One slot in fifty is dropped, in bursts of up to twenty.
            bool drop = rng.Next(50) == 0;

            int burst = drop ? rng.Next(1, 20) : 1;

            for (int i = 0; i < burst; i++)
            {
                if (!drop) video++;

                audio += CaptureSync.AudioChunksDue(video, audio);
            }
        }

        Assert.Equal(0, CaptureSync.Imbalance(video, audio));
        Assert.Equal(0, CaptureSync.DriftMs(video, audio, 25));
        Assert.True(video > 90_000, "the simulation should have written most slots");
    }
}
