using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The buffers a recording's frames go into are used again, and the whole risk of that is one
/// thing: a buffer handed to the next frame while something is still reading the last one out of
/// it. On this plant that is a torn picture in the delay ring, and so on air.
///
/// So these hold the rule the pool exists by - a buffer comes back only when every reader has let
/// it go - and then run frames through real delay rings to see that what lands in each slot is the
/// frame that was sent and nothing else.
/// </summary>
public sealed class FramePoolTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "emerald-pool-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_folder)) Directory.Delete(_folder, recursive: true); }
        catch (IOException) { /* a ring still closing on its own thread */ }
    }

    [Fact]
    public void A_buffer_let_go_is_the_next_frames()
    {
        var pool = new FramePool(1024);

        FrameLease first = pool.Rent(1024);
        byte[] buffer = first.Buffer;
        first.Release();

        Assert.Same(buffer, pool.Rent(1024).Buffer);
        Assert.Equal(1, pool.Made);
        Assert.Equal(1, pool.Reused);
    }

    [Fact]
    public void A_buffer_somebody_still_holds_is_not_given_to_the_next_frame()
    {
        var pool = new FramePool(1024);

        FrameLease frame = pool.Rent(1024);
        frame.Retain();             // a queue takes it
        frame.Release();            // the capture loop moves on

        Assert.NotSame(frame.Buffer, pool.Rent(1024).Buffer);
        Assert.Equal(0, pool.Waiting);

        frame.Release();            // the queue's reader has copied it

        Assert.Equal(1, pool.Waiting);
    }

    [Fact]
    public void A_release_too_many_is_counted_and_does_not_put_the_buffer_back_twice()
    {
        var pool = new FramePool(1024);

        FrameLease frame = pool.Rent(1024);
        frame.Release();
        frame.Release();

        Assert.Equal(1, pool.OverReleased);
        Assert.Equal(1, pool.Waiting);

        // And the frame that has the buffer now is not affected by the old lease at all.
        FrameLease next = pool.Rent(1024);
        frame.Release();

        Assert.Same(frame.Buffer, next.Buffer);
        Assert.Equal(0, pool.Waiting);
    }

    [Fact]
    public void A_frame_nobody_holds_cannot_be_taken_up_again()
    {
        var pool = new FramePool(1024);

        FrameLease frame = pool.Rent(1024);
        frame.Release();

        Assert.Throws<InvalidOperationException>(frame.Retain);
    }

    [Fact]
    public void A_short_read_gets_an_array_of_its_own_length_and_is_never_pooled()
    {
        var pool = new FramePool(1024);

        FrameLease partial = pool.Rent(600);

        Assert.Equal(600, partial.Buffer.Length);

        partial.Release();

        Assert.Equal(0, pool.Waiting);
        Assert.Equal(1024, pool.Rent(1024).Buffer.Length);
    }

    [Fact]
    public void The_pool_keeps_no_more_idle_than_its_limit()
    {
        var pool = new FramePool(64);

        List<FrameLease> burst = Enumerable.Range(0, FramePool.KeepAtMost + 10).Select(_ => pool.Rent(64)).ToList();
        foreach (FrameLease frame in burst) frame.Release();

        Assert.Equal(FramePool.KeepAtMost, pool.Waiting);
    }

    [Fact]
    public void Frames_through_two_rings_at_once_arrive_whole_and_in_their_own_slots()
    {
        // The capture loop's hand-off, as fast as it will go: rent, fill, offer to each ring,
        // let go. Each ring's writer copies on its own thread and lets go in its turn. Every
        // byte of a frame is its number, so a buffer reused too early shows as a slot holding
        // two frames' bytes, or the wrong frame's.
        var format = new CaptureFormat("test", 160, 90, 25);
        const int Frames = 1500;

        using DelayLine one = DelayLine.Create(_folder, format, TimeSpan.FromSeconds(Frames / 25.0));
        using DelayLine two = DelayLine.Create(_folder, format, TimeSpan.FromSeconds(Frames / 25.0));

        var pool = new FramePool(format.FrameBytes);

        for (long n = 0; n < Frames; n++)
        {
            FrameLease picture = pool.Rent(format.FrameBytes);
            Array.Fill(picture.Buffer, (byte)(n % 251));

            var frame = new CapturedFrame(picture.Buffer, format.FrameBytes, null, 0, false, n, picture);

            one.Offer(frame);
            two.Offer(frame);

            picture.Release();

            // Now and then the loop outruns the writers, which is the case that matters: the
            // queues fill, frames are refused, and a refusal has to give its claim back too.
            if (n % 97 != 0) Thread.Yield();
        }

        one.Seal();
        two.Seal();

        foreach (DelayLine line in new[] { one, two })
        {
            Assert.True(SpinWait.SpinUntil(() => line.FramesWritten >= line.SealedAt, TimeSpan.FromSeconds(20)),
                        "the ring's writer did not finish");

            DelayFrameBuffer read = line.NewBuffer();
            long written = line.FramesWritten;

            Assert.True(written > 0);

            for (long sequence = 0; sequence < written; sequence++)
            {
                Assert.Equal(DelayReadResult.Ok, line.TryRead(sequence, read));

                byte expected = (byte)(read.RecordingFrame % 251);

                Assert.Equal(format.FrameBytes, read.VideoBytes);
                Assert.True(read.Video.AsSpan(0, read.VideoBytes).IndexOfAnyExcept(expected) < 0,
                            $"slot {sequence} (recording frame {read.RecordingFrame}) holds bytes of another frame");
            }
        }

        Assert.Equal(0, pool.OverReleased);

        // Every buffer came back: none is still out with a writer that has finished.
        Assert.True(SpinWait.SpinUntil(() => pool.Waiting == Math.Min(pool.Made, FramePool.KeepAtMost),
                                       TimeSpan.FromSeconds(5)),
                    $"{pool.Made} buffers were made and {pool.Waiting} came back");

        // And far fewer were made than frames sent, which is the point of the pool.
        Assert.True(pool.Made < 64, $"{pool.Made} buffers for {Frames} frames");
    }

    [Fact]
    public void A_frame_offered_to_a_ring_that_has_been_let_go_gives_its_claim_back()
    {
        var format = new CaptureFormat("test", 64, 36, 25);
        var pool = new FramePool(format.FrameBytes);

        DelayLine line = DelayLine.Create(_folder, format, TimeSpan.FromSeconds(1));
        line.Dispose();

        FrameLease picture = pool.Rent(format.FrameBytes);

        // Must not throw: a throw here stops the frame reaching the rings subscribed after this one.
        line.Offer(new CapturedFrame(picture.Buffer, format.FrameBytes, null, 0, false, 0, picture));

        picture.Release();

        Assert.Equal(1, pool.Waiting);
        Assert.Equal(0, pool.OverReleased);
    }
}
