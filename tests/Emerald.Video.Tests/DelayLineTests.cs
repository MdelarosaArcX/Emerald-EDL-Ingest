using System.IO;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The ring that holds the delay.
///
/// Every one of these runs without a card and without ffmpeg, because the ring is nothing
/// but arithmetic over a file — which is the point of having built it that way. What is
/// being protected here is the property that matters on air: a read either produces the
/// frame that was asked for, or produces nothing and leaves the caller's buffer alone.
/// </summary>
public sealed class DelayLineTests : IDisposable
{
    private readonly string _folder;

    /// <summary>A tiny raster, so a whole ring is a few megabytes and the tests are quick.</summary>
    private static readonly CaptureFormat Tiny = new("test64", 64, 64, 25, 0);

    public DelayLineTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "emerald-delay-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    private DelayLine Open(TimeSpan? delay = null) =>
        DelayLine.Create(_folder, Tiny, delay ?? TimeSpan.FromSeconds(1));

    /// <summary>A frame whose every byte identifies which frame it is.</summary>
    private static byte[] Pattern(int frameBytes, long sequence)
    {
        var frame = new byte[frameBytes];
        for (int i = 0; i < frame.Length; i++) frame[i] = (byte)((sequence + i) & 0xFF);
        return frame;
    }

    // ------------------------------------------------------------------ round trip

    [Fact]
    public void A_frame_comes_back_exactly_as_it_went_in()
    {
        using DelayLine line = Open();
        DelayFrameBuffer buffer = line.NewBuffer();

        for (long i = 0; i < 40; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            var audio = new byte[] { 1, 2, 3, 4 };
            line.Write(frame, frame.Length, audio, audio.Length, audioReal: true);

            Assert.Equal(DelayReadResult.Ok, line.TryRead(i, buffer));
            Assert.Equal(i, buffer.Sequence);
            Assert.Equal(line.FrameBytes, buffer.VideoBytes);
            Assert.Equal(frame, buffer.Video);
            Assert.Equal(4, buffer.AudioBytes);
            Assert.True(buffer.AudioWasReal);
        }
    }

    [Fact]
    public void Frames_are_still_intact_after_the_ring_has_wrapped()
    {
        using DelayLine line = Open();
        DelayFrameBuffer buffer = line.NewBuffer();

        long total = line.SlotCount + 20;
        for (long i = 0; i < total; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, audioReal: false);
        }

        // The newest frame is readable and is the right one, having gone round at least once.
        long newest = total - 1;
        Assert.Equal(DelayReadResult.Ok, line.TryRead(newest, buffer));
        Assert.Equal(Pattern(line.FrameBytes, newest), buffer.Video);
    }

    // ------------------------------------------------------------------ refusals

    [Fact]
    public void A_frame_the_writer_has_not_reached_is_refused()
    {
        using DelayLine line = Open();
        DelayFrameBuffer buffer = line.NewBuffer();

        byte[] frame = Pattern(line.FrameBytes, 0);
        line.Write(frame, frame.Length, null, 0, false);

        Assert.Equal(DelayReadResult.NotYetWritten, line.TryRead(1, buffer));
    }

    [Fact]
    public void A_frame_the_writer_has_lapped_is_refused()
    {
        using DelayLine line = Open();
        DelayFrameBuffer buffer = line.NewBuffer();

        for (long i = 0; i < line.SlotCount + 10; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false);
        }

        Assert.Equal(DelayReadResult.Lapped, line.TryRead(0, buffer));
    }

    /// <summary>
    /// The property the whole design rests on. A transmitter that has just been refused a
    /// frame falls back on the last one it held — so a refusal must not have scribbled on it.
    /// </summary>
    [Fact]
    public void A_refused_read_leaves_the_buffer_untouched()
    {
        using DelayLine line = Open();
        DelayFrameBuffer buffer = line.NewBuffer();

        byte[] good = Pattern(line.FrameBytes, 7);
        line.Write(good, good.Length, null, 0, false);

        Assert.Equal(DelayReadResult.Ok, line.TryRead(0, buffer));
        byte[] held = (byte[])buffer.Video.Clone();

        Assert.Equal(DelayReadResult.NotYetWritten, line.TryRead(1, buffer));
        Assert.Equal(held, buffer.Video);
        Assert.Equal(0, buffer.Sequence);          // still describing the frame it holds
    }

    // ------------------------------------------------------------------ audio

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(7680)]
    public void Audio_of_any_length_up_to_capacity_round_trips(int bytes)
    {
        using DelayLine line = Open();
        DelayFrameBuffer buffer = line.NewBuffer();

        var audio = new byte[bytes];
        for (int i = 0; i < bytes; i++) audio[i] = (byte)(i & 0xFF);

        byte[] frame = Pattern(line.FrameBytes, 0);
        line.Write(frame, frame.Length, bytes == 0 ? null : audio, bytes, audioReal: bytes > 0);

        Assert.Equal(DelayReadResult.Ok, line.TryRead(0, buffer));
        Assert.Equal(bytes, buffer.AudioBytes);
        Assert.Equal(audio, buffer.Audio.AsSpan(0, bytes).ToArray());
    }

    [Fact]
    public void Audio_longer_than_the_slot_is_clamped_rather_than_overrunning()
    {
        using DelayLine line = Open();
        DelayFrameBuffer buffer = line.NewBuffer();

        int capacity = DelayLine.AudioCapacityFor(Tiny.FrameRate);
        var oversized = new byte[capacity * 2];

        byte[] frame = Pattern(line.FrameBytes, 0);
        line.Write(frame, frame.Length, oversized, oversized.Length, true);

        Assert.Equal(DelayReadResult.Ok, line.TryRead(0, buffer));
        Assert.Equal(capacity, buffer.AudioBytes);
    }

    // ------------------------------------------------------------------ sealing

    [Fact]
    public void A_sealed_line_reads_out_and_then_ends()
    {
        using DelayLine line = Open();
        DelayFrameBuffer buffer = line.NewBuffer();

        for (long i = 0; i < 5; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false);
        }

        line.Seal();

        Assert.True(line.IsSealed);
        Assert.Equal(5, line.SealedAt);
        Assert.Equal(DelayReadResult.Ok, line.TryRead(4, buffer));
        Assert.Equal(DelayReadResult.EndOfLine, line.TryRead(5, buffer));
    }

    [Fact]
    public void Sealing_twice_keeps_the_first_answer()
    {
        using DelayLine line = Open();

        byte[] frame = Pattern(line.FrameBytes, 0);
        line.Write(frame, frame.Length, null, 0, false);
        line.Seal();

        line.Write(frame, frame.Length, null, 0, false);
        line.Seal();

        Assert.Equal(1, line.SealedAt);
    }

    // ------------------------------------------------------------------ sizing

    [Fact]
    public void A_minute_of_1080p25_is_about_six_gigabytes()
    {
        var hd = new CaptureFormat("1080p25", 1920, 1080, 25, 0);
        long size = DelayLine.SizeFor(hd, TimeSpan.FromMinutes(1));

        // 1500 frames plus two seconds of guard, each slot a page-aligned 4 MB.
        Assert.Equal(1550, DelayLine.SlotCountFor(hd, TimeSpan.FromMinutes(1)));
        Assert.InRange(size, 6.0 * (1L << 30), 6.2 * (1L << 30));
    }

    [Fact]
    public void Every_slot_starts_on_a_page_boundary()
    {
        foreach (int rate in new[] { 24, 25, 30, 50, 60 })
        {
            var hd = new CaptureFormat($"1080p{rate}", 1920, 1080, rate, 0);
            Assert.Equal(0, DelayLine.SlotStrideFor(hd) % 4096);
        }
    }

    [Fact]
    public void The_ring_holds_the_delay_plus_a_guard_of_at_least_two_seconds()
    {
        var hd = new CaptureFormat("1080p25", 1920, 1080, 25, 0);

        long slots = DelayLine.SlotCountFor(hd, TimeSpan.FromSeconds(10));
        Assert.True(slots >= 10 * 25 + 2 * 25,
            "the writer must not be able to reach the slot the reader is on");
    }

    [Fact]
    public void An_impossible_delay_is_refused_with_the_sizes_named()
    {
        var hd = new CaptureFormat("1080p25", 1920, 1080, 25, 0);

        Assert.False(DelayLine.Validate(_folder, hd, TimeSpan.FromHours(24), out long size, out string? problem));
        Assert.True(size > 0);
        Assert.Contains("free", problem);
    }

    // ------------------------------------------------------------------ concurrency

    /// <summary>
    /// A writer and a reader running flat out against each other, the reader a fixed distance
    /// behind. Every frame carries a checksum of itself, so a torn read is detectable rather
    /// than merely unlikely. This is the test that stands in for the on-air case.
    /// </summary>
    [Fact]
    public void A_reader_running_behind_a_writer_never_sees_a_torn_frame()
    {
        using DelayLine line = Open(TimeSpan.FromSeconds(1));
        DelayFrameBuffer[] buffers = { line.NewBuffer(), line.NewBuffer() };

        const long total = 4000;
        long behind = line.TargetFrames;
        int torn = 0, lapped = 0;
        long read = 0;

        var writer = new Thread(() =>
        {
            for (long i = 0; i < total; i++)
            {
                byte[] frame = Pattern(line.FrameBytes, i);
                frame[0] = Checksum(frame);
                line.Write(frame, frame.Length, null, 0, false);
            }

            line.Seal();
        });

        writer.Start();

        int turn = 0;
        while (true)
        {
            DelayFrameBuffer into = buffers[turn];
            DelayReadResult result = line.TryRead(read, into);

            if (result == DelayReadResult.EndOfLine) break;

            if (result == DelayReadResult.Lapped) { lapped++; read++; continue; }
            if (result == DelayReadResult.NotYetWritten) { Thread.SpinWait(50); continue; }

            // Rebuild what the checksum should be and compare: a frame stitched from two
            // different writes will not match.
            byte held = into.Video[0];
            into.Video[0] = (byte)((into.Sequence + 0) & 0xFF);
            if (Checksum(into.Video) != held) torn++;

            read++;
            turn ^= 1;

            // Stay roughly the design distance behind, so the writer is genuinely lapping
            // around the ring rather than the reader trailing at the very edge.
            while (line.FramesWritten - read < behind && !line.IsSealed) Thread.SpinWait(20);
        }

        writer.Join();

        Assert.Equal(0, torn);
        Assert.Equal(0, lapped);
        Assert.Equal(total, read);
    }

    private static byte Checksum(byte[] frame)
    {
        int sum = 0;
        for (int i = 1; i < frame.Length; i++) sum += frame[i];
        return (byte)(sum & 0xFF);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp folder */ }
    }
}
