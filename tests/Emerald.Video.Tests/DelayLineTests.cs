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

        // Test rings are a few megabytes: hold them in RAM whatever the machine has free.
        DelayLine.MemoryReserve = 0;
    }

    /// <summary>A line on disk, read and written directly: what is opened when memory cannot hold the delay.</summary>
    private DelayLine OpenDirect(TimeSpan delay, TimeSpan headroom = default)
    {
        DelayLine.MemoryReserve = long.MaxValue / 4;
        DelayLine line = DelayLine.Create(_folder, Tiny, delay, headroom, TimeSpan.FromSeconds(12));
        DelayLine.MemoryReserve = 0;

        Assert.True(line.IsDirect);
        Assert.Equal(0, line.MemorySlots);
        return line;
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

    /// <summary>
    /// Live Edit's line: room for inserts to grow the delay into, but the countdown is still only
    /// the delay asked for - going live must not wait for the headroom to fill as well.
    /// </summary>
    [Fact]
    public void Headroom_makes_the_ring_bigger_but_not_the_wait()
    {
        using DelayLine plain = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(1));
        using DelayLine roomy = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(4));

        Assert.Equal(plain.TargetFrames, roomy.TargetFrames);
        Assert.Equal(plain.SlotCount + 4 * Tiny.FrameRate, roomy.SlotCount);
    }

    /// <summary>
    /// A line armed part-way through a recording. Its own count starts at nought, but the
    /// on-air marker is drawn from where the frame sits in the <i>recording</i> - and counting
    /// from the line instead is what put the marker fifteen minutes behind after a re-arm.
    /// </summary>
    [Fact]
    public void A_line_opened_mid_recording_still_knows_the_recording_frame()
    {
        using DelayLine line = Open();
        DelayFrameBuffer buffer = line.NewBuffer();

        const long armedAt = 22_500;   // fifteen minutes in, at 25

        for (long i = 0; i < 10; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false, recordingFrame: armedAt + i);

            Assert.Equal(DelayReadResult.Ok, line.TryRead(i, buffer));
            Assert.Equal(i, buffer.Sequence);
            Assert.Equal(armedAt + i, buffer.RecordingFrame);
        }
    }

    [Fact]
    public void Without_a_recording_frame_the_line_count_stands_in()
    {
        using DelayLine line = Open();
        DelayFrameBuffer buffer = line.NewBuffer();

        byte[] frame = Pattern(line.FrameBytes, 0);
        line.Write(frame, frame.Length, null, 0, false);

        Assert.Equal(DelayReadResult.Ok, line.TryRead(0, buffer));
        Assert.Equal(0, buffer.RecordingFrame);
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
    ///
    /// The reader here is under far more pressure than the real one: on air the pump reads a
    /// frame every output slot period and the ring holds seconds of slack, whereas this reads
    /// as fast as it can, checksums the whole frame each time, and is throttled only when it
    /// gets too <em>close</em>. So it does sometimes fall far enough behind to be lapped, and
    /// that is allowed. What is not allowed - ever, at any pressure - is a read reported as
    /// good that is stitched from two different frames.
    /// </summary>
    [Fact]
    public void A_reader_running_behind_a_writer_never_sees_a_torn_frame()
    {
        using DelayLine line = Open(TimeSpan.FromSeconds(1));
        NeverTorn(line);
    }

    /// <summary>The same, with the newest frames held in RAM: the ring the transmitter reads on air.</summary>
    [Fact]
    public void A_reader_running_behind_a_writer_in_memory_never_sees_a_torn_frame()
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(10),
                                                TimeSpan.FromSeconds(7));
        Assert.Equal(175, line.MemorySlots);
        NeverTorn(line);
    }

    /// <summary>The same on disk, read directly: a frame read while the writer comes round to its slot is never handed on.</summary>
    [Fact]
    public void A_reader_running_behind_a_writer_on_disk_never_sees_a_torn_frame()
    {
        using DelayLine line = OpenDirect(TimeSpan.FromSeconds(1));
        NeverTorn(line);
    }

    /// <summary>And through the read-ahead, which copies frames off the disk ahead of the reader.</summary>
    [Fact]
    public void A_reader_through_the_read_ahead_never_sees_a_torn_frame()
    {
        using DelayLine line = OpenDirect(TimeSpan.FromSeconds(1));
        NeverTorn(line, prefetch: true);
    }

    private static void NeverTorn(DelayLine line, bool prefetch = false)
    {
        DelayFrameBuffer[] buffers = { line.NewBuffer(), line.NewBuffer() };

        const long total = 4000;
        long behind = line.TargetFrames;
        int torn = 0, lapped = 0;
        long read = 0;
        long firstTorn = -1, firstTornAt = -1, firstTornWritten = -1;

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
            if (prefetch) line.PrefetchAhead(read);
            DelayReadResult result = line.TryRead(read, into);

            if (result == DelayReadResult.EndOfLine) break;

            if (result == DelayReadResult.Lapped) { lapped++; read++; continue; }
            if (result == DelayReadResult.NotYetWritten) { Thread.SpinWait(50); continue; }

            // Rebuild what the checksum should be and compare: a frame stitched from two
            // different writes will not match.
            byte held = into.Video[0];
            into.Video[0] = (byte)((into.Sequence + 0) & 0xFF);
            if (Checksum(into.Video) != held)
            {
                torn++;
                if (firstTorn < 0)
                {
                    firstTorn = into.Sequence;
                    firstTornAt = FirstDifference(into.Video, into.Sequence);
                    firstTornWritten = line.FramesWritten;
                }
            }

            read++;
            turn ^= 1;

            // Stay roughly the design distance behind, so the writer is genuinely lapping
            // around the ring rather than the reader trailing at the very edge.
            while (line.FramesWritten - read < behind && !line.IsSealed) Thread.SpinWait(20);
        }

        writer.Join();

        string detail = $"torn={torn} lapped={lapped} read={read} slots={line.SlotCount} " +
                        $"behind={behind} firstTorn={firstTorn} atByte={firstTornAt} " +
                        $"writtenThen={firstTornWritten}";

        Assert.True(torn == 0, detail);

        // Lapping is a reported outcome, not a hidden one - and the whole point is that it is
        // reported instead of returning the torn frame. It must still stay a small minority,
        // or the ring is sized wrong rather than the reader being unlucky.
        Assert.True(lapped < total / 10, detail);
        Assert.Equal(total, read);
    }

    /// <summary>Where a frame stopped matching its own pattern, for the failure message.</summary>
    private static long FirstDifference(byte[] frame, long sequence)
    {
        for (int i = 0; i < frame.Length; i++)
            if (frame[i] != (byte)((sequence + i) & 0xFF)) return i;

        return -1;
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

    // ------------------------------------------------------------------ by frame of the recording

    /// <summary>
    /// Live Edit shows the segment still being recorded from the line, by its frame of the
    /// recording - so a frame is found by that number, not by the line's own count.
    /// </summary>
    [Fact]
    public void A_frame_is_found_by_its_frame_of_the_recording()
    {
        using DelayLine line = Open();

        const long armedAt = 22_500;
        for (long i = 0; i < 20; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false, recordingFrame: armedAt + i);
        }

        Assert.Equal(5, line.SequenceOf(armedAt + 5));
        Assert.Equal(19, line.SequenceOf(armedAt + 19));
        Assert.Equal(-1, line.SequenceOf(armedAt + 20));     // not recorded yet
        Assert.Equal(-1, line.SequenceOf(armedAt - 1));      // before the line began
        Assert.Equal(armedAt + 7, line.RecordingFrameAt(7));
    }

    /// <summary>
    /// A frame the line could not keep up with leaves a step in the numbering; frames either side
    /// of it are still found exactly, and the missing one gives its neighbour.
    /// </summary>
    [Fact]
    public void A_dropped_frame_does_not_throw_the_lookup_out()
    {
        using DelayLine line = Open();

        long[] recorded = Enumerable.Range(0, 30).Select(i => (long)i).Where(i => i != 12).ToArray();
        foreach (long r in recorded)
        {
            byte[] frame = Pattern(line.FrameBytes, r);
            line.Write(frame, frame.Length, null, 0, false, recordingFrame: r);
        }

        Assert.Equal(11, line.SequenceOf(11));
        Assert.Equal(12, line.SequenceOf(13));    // one slot earlier than its number, past the drop
        Assert.Equal(28, line.SequenceOf(29));

        long nearest = line.SequenceOf(12);
        Assert.True(nearest is 11 or 12);
    }

    /// <summary>Lapped frames are not offered: the ring only reaches back so far.</summary>
    [Fact]
    public void A_frame_the_ring_has_gone_past_is_not_found()
    {
        using DelayLine line = Open();

        long total = line.SlotCount + 20;
        for (long i = 0; i < total; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false, recordingFrame: i);
        }

        Assert.Equal(-1, line.SequenceOf(5));
        Assert.Equal(total - 1, line.SequenceOf(total - 1));
    }

    /// <summary>A reader that found the line somewhere shared takes no claim on one already let go.</summary>
    [Fact]
    public void A_claim_is_refused_once_the_line_has_been_let_go()
    {
        DelayLine line = Open();

        Assert.True(line.TryAddRef());
        line.Release();
        line.Release();

        Assert.False(line.TryAddRef());
    }

    // ------------------------------------------------------------------ trimming

    /// <summary>
    /// Trimming only says the pages need not stay in memory. What is on air must not be able to
    /// tell: every frame the ring still holds reads back as written, round the end of the ring too.
    /// </summary>
    [Fact]
    public void Trimmed_frames_read_back_exactly_after_the_ring_has_wrapped()
    {
        using DelayLine line = Open(TimeSpan.FromSeconds(20));
        DelayFrameBuffer buffer = line.NewBuffer();

        long total = line.SlotCount + 100;
        for (long i = 0; i < total; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false);
        }

        Assert.True(line.TrimmedThrough > total - line.SlotCount, "the range read below must include trimmed frames");

        for (long i = total - line.SlotCount; i < total; i++)
        {
            Assert.Equal(DelayReadResult.Ok, line.TryRead(i, buffer));
            Assert.Equal(Pattern(line.FrameBytes, i), buffer.Video);
        }
    }

    /// <summary>
    /// A reader close behind the writer keeps the pages it is about to read. One a whole delay
    /// behind does not: holding everything between it and the writer is what put 12.7 GB in the
    /// working set on air. Its frames are read back from the file's cache instead.
    /// </summary>
    [Fact]
    public void Only_a_near_reader_holds_its_pages()
    {
        using DelayLine line = Open(TimeSpan.FromSeconds(30));
        DelayFrameBuffer buffer = line.NewBuffer();
        byte[] frame = Pattern(line.FrameBytes, 0);

        void Write(int count)
        {
            for (int i = 0; i < count; i++) line.Write(frame, frame.Length, null, 0, false);
        }

        // Four seconds at 25 is 100 frames kept, with nothing reading.
        Write(500);
        Assert.Equal(400, line.TrimmedThrough);

        // 200 behind (8 s, near) plus the two-second margin keeps 250, and the mark comes back to meet it.
        Assert.Equal(DelayReadResult.Ok, line.TryRead(300, buffer));
        Write(25);
        Assert.Equal(275, line.TrimmedThrough);

        Thread.Sleep(2100);
        Write(25);
        Assert.Equal(450, line.TrimmedThrough);

        // 450 behind (18 s) is far: it holds nothing, and still reads.
        Assert.Equal(DelayReadResult.Ok, line.TryRead(100, buffer));
        Write(25);
        Assert.Equal(475, line.TrimmedThrough);
        Assert.Equal(DelayReadResult.Ok, line.TryRead(126, buffer));
    }

    /// <summary>
    /// The ring is written to its file half a second behind the writer, so its pages are clean
    /// and Windows can count them as available rather than leaving them dirty in memory.
    /// </summary>
    [Fact]
    public void The_ring_is_written_ahead_to_disk()
    {
        using DelayLine line = Open(TimeSpan.FromSeconds(20));
        byte[] frame = Pattern(line.FrameBytes, 0);

        for (int i = 0; i < 200; i++) line.Write(frame, frame.Length, null, 0, false);

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (line.FlushedThrough < 200 - 12 && waited.ElapsedMilliseconds < 5000) Thread.Sleep(50);

        Assert.InRange(line.FlushedThrough, 200 - 12, 200);
    }

    // ------------------------------------------------------------------ disk given back

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern uint GetCompressedFileSizeW(string path, out uint high);

    /// <summary>What the file actually takes on disk, which for a sparse file is less than its length.</summary>
    private static long Allocated(string path)
    {
        uint low = GetCompressedFileSizeW(path, out uint high);
        return ((long)high << 32) | low;
    }

    private static void WaitFor(Func<bool> condition)
    {
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (!condition() && waited.ElapsedMilliseconds < 5000) Thread.Sleep(25);
    }

    /// <summary>
    /// A line made with headroom for inserts takes disk for its delay, not its whole length:
    /// frames more than the delay and a margin behind the writer are given back, and a read
    /// of one is turned away as if the ring had come round.
    /// </summary>
    [Fact]
    public void A_line_gives_back_the_disk_it_is_not_keeping()
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(28));
        line.AlwaysRelease = true;
        DelayFrameBuffer buffer = line.NewBuffer();
        Assert.True(line.IsSparse);

        for (int i = 0; i < 600; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false);
        }

        // Keeps the 2 s delay (50) and a 2 s margin (50) behind the writer at 600.
        WaitFor(() => line.ReleasedThrough >= 500);
        Assert.Equal(500, line.ReleasedThrough);
        Assert.Equal(500, line.OldestHeld);

        Assert.Equal(DelayReadResult.Lapped, line.TryRead(499, buffer));
        Assert.Equal(DelayReadResult.Ok, line.TryRead(500, buffer));
        Assert.Equal(Pattern(line.FrameBytes, 500), buffer.Video);
        Assert.Equal(-1, line.RecordingFrameAt(10));

        long length = new FileInfo(line.Path).Length;
        Assert.InRange(Allocated(line.Path), 0, length / 3);
    }

    /// <summary>
    /// A transmitter's hold keeps its frames on disk however far behind it is, and whether or
    /// not it is reading - it may be playing an insert and coming back. Let go, they are given back.
    /// </summary>
    [Fact]
    public void A_hold_keeps_its_frames_until_it_is_let_go()
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(28));
        line.AlwaysRelease = true;
        DelayFrameBuffer buffer = line.NewBuffer();
        byte[] frame = Pattern(line.FrameBytes, 0);

        using (DelayHold hold = line.Hold(100))
        {
            for (int i = 0; i < 600; i++) line.Write(frame, frame.Length, null, 0, false);

            // Two seconds short of the hold, and no further however long it waits.
            WaitFor(() => line.ReleasedThrough >= 50);
            Thread.Sleep(600);
            Assert.Equal(50, line.ReleasedThrough);
            Assert.Equal(DelayReadResult.Ok, line.TryRead(100, buffer));

            hold.Move(300);
            WaitFor(() => line.ReleasedThrough >= 250);
            Assert.Equal(250, line.ReleasedThrough);
        }

        WaitFor(() => line.ReleasedThrough >= 500);
        Assert.Equal(500, line.ReleasedThrough);
    }

    /// <summary>
    /// A ready line is as long as the longest delay with all its inserts, and keeps its whole
    /// length unless told otherwise; told how much a transmitter could start from, it keeps that.
    /// </summary>
    [Fact]
    public void A_ready_line_keeps_what_it_is_told_to()
    {
        using DelayLine line = Open(TimeSpan.FromSeconds(30));
        line.AlwaysRelease = true;
        byte[] frame = Pattern(line.FrameBytes, 0);

        for (int i = 0; i < 600; i++) line.Write(frame, frame.Length, null, 0, false);

        WaitFor(() => line.FlushedThrough >= 588);
        Thread.Sleep(600);
        Assert.Equal(0, line.ReleasedThrough);

        long keep = 100;
        line.KeepBehind(() => keep);
        WaitFor(() => line.ReleasedThrough >= 450);
        Assert.Equal(450, line.ReleasedThrough);

        // Asked for more later: what was given back stays given back, and nothing more goes.
        keep = 300;
        for (int i = 0; i < 50; i++) line.Write(frame, frame.Length, null, 0, false);
        Thread.Sleep(600);
        Assert.Equal(450, line.ReleasedThrough);
        Assert.Equal(450, line.OldestHeld);
    }

    /// <summary>
    /// On a volume with room to spare nothing is given back: punching holes in the ring and
    /// filling them again is what backed the writer up until frames were dropped, and there
    /// is no disk to save. Every frame the ring has not come round on stays readable.
    /// </summary>
    [Fact]
    public void A_line_on_a_roomy_volume_gives_nothing_back()
    {
        // The premise: the test volume has the 20 GB margin free. Without it, the line rightly
        // gives disk back and there is nothing here to show.
        if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(_folder))!).AvailableFreeSpace < 21L * 1024 * 1024 * 1024)
            return;

        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(28));
        DelayFrameBuffer buffer = line.NewBuffer();

        for (int i = 0; i < 600; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false);
        }

        WaitFor(() => line.FlushedThrough >= 588);
        Thread.Sleep(600);

        Assert.Equal(0, line.ReleasedThrough);
        Assert.Equal(0, line.OldestHeld);
        Assert.Equal(DelayReadResult.Ok, line.TryRead(10, buffer));
        Assert.Equal(Pattern(line.FrameBytes, 10), buffer.Video);
    }

    // ------------------------------------------------------------------ held in memory

    /// <summary>
    /// A line holding its delay in RAM: the newest frames read back from memory, a frame that left
    /// memory with nothing wanting it is gone as if lapped, and the disk is never written - the
    /// file takes no more than its header. This is the on-air case: nothing on the disk at all.
    /// </summary>
    [Fact]
    public void A_line_held_in_memory_reads_from_memory_and_leaves_the_disk_alone()
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(26),
                                                TimeSpan.FromSeconds(12));
        DelayFrameBuffer buffer = line.NewBuffer();
        Assert.Equal(300, line.MemorySlots);

        for (int i = 0; i < 600; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false, recordingFrame: 1000 + i);
        }

        WaitFor(() => line.FlushedThrough >= 588);
        Thread.Sleep(600);

        foreach (long sequence in new long[] { 300, 450, 599 })
        {
            Assert.Equal(DelayReadResult.Ok, line.TryRead(sequence, buffer));
            Assert.Equal(Pattern(line.FrameBytes, sequence), buffer.Video);
            Assert.Equal(1000 + sequence, buffer.RecordingFrame);
            Assert.Equal(1000 + sequence, line.RecordingFrameAt(sequence));
        }

        Assert.Equal(DelayReadResult.Lapped, line.TryRead(299, buffer));
        Assert.Equal(DelayReadResult.Lapped, line.TryRead(0, buffer));
        Assert.Equal(-1, line.RecordingFrameAt(0));
        Assert.Equal(300, line.OldestHeld);
        Assert.Equal(450, line.SequenceOf(1450));

        Assert.Equal(0, line.Spilled);
        Assert.InRange(Allocated(line.Path), 0, 128 * 1024);
    }

    /// <summary>
    /// A transmitter held back - an insert playing - keeps its frames when they leave memory: they
    /// are written to the file and read back from there, whole, while the newest stay in RAM.
    /// </summary>
    [Fact]
    public void A_held_frame_leaving_memory_is_spilled_and_read_back_from_disk()
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(26),
                                                TimeSpan.FromSeconds(12));
        DelayFrameBuffer buffer = line.NewBuffer();

        using DelayHold hold = line.Hold(100);

        for (int i = 0; i < 600; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false);
        }

        // Kept from two seconds short of the hold; everything before that left memory unwanted.
        Assert.Equal(50, line.OldestHeld);
        Assert.Equal(DelayReadResult.Lapped, line.TryRead(49, buffer));
        Assert.InRange(line.Spilled, 250, 350);

        foreach (long sequence in new long[] { 50, 100, 299, 300, 599 })
        {
            Assert.Equal(DelayReadResult.Ok, line.TryRead(sequence, buffer));
            Assert.Equal(Pattern(line.FrameBytes, sequence), buffer.Video);
        }
    }

    // ------------------------------------------------------------------ on disk, read directly

    /// <summary>
    /// On a drive with room, the delay is held on disk, read and written past the Windows cache:
    /// every frame comes back whole with its sound and its place in the recording, through the
    /// read-ahead as well as straight off the disk.
    /// </summary>
    [Fact]
    public void A_line_on_disk_reads_back_what_was_written()
    {
        using DelayLine line = OpenDirect(TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(26));
        DelayFrameBuffer buffer = line.NewBuffer();
        byte[] sound = Enumerable.Range(0, 1920 * 4).Select(i => (byte)(i % 199)).ToArray();

        for (int i = 0; i < 600; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, sound, sound.Length, audioReal: i % 2 == 0, recordingFrame: 1000 + i);
        }

        foreach (long sequence in new long[] { 0, 299, 450, 599 })
        {
            Assert.Equal(DelayReadResult.Ok, line.TryRead(sequence, buffer));
            Assert.Equal(Pattern(line.FrameBytes, sequence), buffer.Video);
            Assert.Equal(sound, buffer.Audio.AsSpan(0, buffer.AudioBytes).ToArray());
            Assert.Equal(sequence % 2 == 0, buffer.AudioWasReal);
            Assert.Equal(1000 + sequence, buffer.RecordingFrame);
            Assert.Equal(1000 + sequence, line.RecordingFrameAt(sequence));
        }

        Assert.Equal(450, line.SequenceOf(1450));
        Assert.Equal(DelayReadResult.NotYetWritten, line.TryRead(600, buffer));

        // Through the read-ahead: asked for, given a moment, and read from memory.
        line.PrefetchAhead(500);
        Thread.Sleep(300);
        Assert.Equal(DelayReadResult.Ok, line.TryRead(520, buffer));
        Assert.Equal(Pattern(line.FrameBytes, 520), buffer.Video);
    }

    /// <summary>Round the ring: a frame the writer has come back over is lapped, not handed on.</summary>
    [Fact]
    public void A_line_on_disk_that_has_come_round_turns_the_old_frame_away()
    {
        using DelayLine line = OpenDirect(TimeSpan.FromSeconds(1));
        DelayFrameBuffer buffer = line.NewBuffer();

        for (int i = 0; i < line.SlotCount + 10; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false);
        }

        Assert.Equal(DelayReadResult.Lapped, line.TryRead(5, buffer));
        Assert.Equal(-1, line.RecordingFrameAt(5));
        Assert.Equal(DelayReadResult.Ok, line.TryRead(line.SlotCount + 5, buffer));
        Assert.Equal(Pattern(line.FrameBytes, line.SlotCount + 5), buffer.Video);

        line.Seal();
        Assert.Equal(DelayReadResult.EndOfLine, line.TryRead(line.SlotCount + 10, buffer));
    }

    /// <summary>Given back to a drive running short, a frame on disk reads as gone, never as zeros.</summary>
    [Fact]
    public void A_line_on_disk_gives_back_what_it_is_not_keeping()
    {
        using DelayLine line = OpenDirect(TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(28));
        line.AlwaysRelease = true;
        DelayFrameBuffer buffer = line.NewBuffer();

        for (int i = 0; i < 600; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false);
        }

        WaitFor(() => line.ReleasedThrough >= 500);
        Assert.Equal(500, line.ReleasedThrough);
        Assert.Equal(DelayReadResult.Lapped, line.TryRead(499, buffer));
        Assert.Equal(DelayReadResult.Ok, line.TryRead(500, buffer));
        Assert.Equal(Pattern(line.FrameBytes, 500), buffer.Video);
    }

    // ------------------------------------------------------------------ RAM or disk, and how far back

    /// <summary>In RAM whenever the delay fits there, however much room the disk has.</summary>
    [Fact]
    public void A_delay_that_fits_in_memory_is_held_there()
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(26),
                                                TimeSpan.FromSeconds(12));

        Assert.False(line.IsDirect);
        Assert.Equal(300, line.MemorySlots);

        // As far back as a transmitter may be: five seconds short of the end of memory.
        Assert.Equal(300 - 125, line.RoomFrames);
    }

    /// <summary>Memory that cannot hold the delay and its guard is not used at all: the disk is.</summary>
    [Fact]
    public void A_delay_that_does_not_fit_in_memory_goes_to_the_disk()
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(26),
                                                TimeSpan.FromSeconds(8));

        Assert.True(line.IsDirect);
        Assert.Equal(0, line.MemorySlots);
        Assert.False(DelayLine.MemoryRingHeld);
        Assert.Equal(line.SlotCount - 50, line.RoomFrames);
    }

    /// <summary>The ready line's length is the delay and every insert; only the delay has to fit.</summary>
    [Fact]
    public void A_long_line_needs_only_what_is_required_of_it_in_memory()
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(60), TimeSpan.Zero,
                                                TimeSpan.FromSeconds(12), required: TimeSpan.FromSeconds(4));

        Assert.False(line.IsDirect);
        Assert.Equal(300, line.MemorySlots);
    }

    /// <summary>
    /// Writes round the line several times with a transmitter <paramref name="behind"/> frames back, at a pace
    /// the flusher keeps ahead of: it decides a frame two seconds before it leaves memory, which is
    /// the strict case. Flat out, the writer gets there first and decides late.
    /// </summary>
    private static void RunBehind(DelayLine line, long behind, int frames)
    {
        using DelayHold hold = line.Hold(0);
        DelayFrameBuffer buffer = line.NewBuffer();

        for (int i = 0; i < frames; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false);

            long at = line.FramesWritten - behind;
            hold.Move(Math.Max(0, at));
            if (i % 10 == 9) Thread.Sleep(60);
            if (at < 0 || i % 50 != 0) continue;

            Assert.Equal(DelayReadResult.Ok, line.TryRead(at, buffer));
            Assert.Equal(Pattern(line.FrameBytes, at), buffer.Video);
        }

        Thread.Sleep(600);
    }

    /// <summary>
    /// As far back as it may be, for several times round: not one frame reaches the disk. This is
    /// the whole of the delay with its insert room used up, on air.
    /// </summary>
    [Fact]
    public void A_transmitter_as_far_back_as_the_line_allows_puts_nothing_on_the_disk()
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(26),
                                                TimeSpan.FromSeconds(12));

        RunBehind(line, line.RoomFrames, 1000);

        Assert.Equal(0, line.Spilled);
        Assert.InRange(Allocated(line.Path), 0, 128 * 1024);
    }

    /// <summary>And two seconds further back does reach it, which is why it is not let.</summary>
    [Fact]
    public void A_transmitter_further_back_than_the_line_allows_is_written_out()
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(26),
                                                TimeSpan.FromSeconds(12));

        RunBehind(line, line.RoomFrames + 50, 1000);

        Assert.True(line.Spilled > 100, $"{line.Spilled} frames written out");
    }

    /// <summary>
    /// A line with no insert room is all in memory, ring and all. A transmitter exactly a delay behind
    /// is within it, and nothing is written out - there is nothing it could be read back for.
    /// </summary>
    [Fact]
    public void A_line_with_no_insert_room_puts_nothing_on_the_disk()
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(4), TimeSpan.Zero,
                                                TimeSpan.FromSeconds(14));

        Assert.Equal(line.SlotCount, line.MemorySlots);
        Assert.Equal(line.TargetFrames, line.RoomFrames);

        RunBehind(line, line.TargetFrames, 800);

        Assert.Equal(0, line.Spilled);
        Assert.InRange(Allocated(line.Path), 0, 128 * 1024);
    }

    /// <summary>Told to keep more than memory holds, an idle line writes it all out; kept to its room, none of it.</summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void An_idle_line_keeps_to_its_room(bool clamped)
    {
        using DelayLine line = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(30), TimeSpan.Zero,
                                                TimeSpan.FromSeconds(12), required: TimeSpan.FromSeconds(4));

        line.KeepBehind(() => clamped ? Math.Min(600, line.RoomFrames) : 600);

        for (int i = 0; i < 1500; i++)
        {
            byte[] frame = Pattern(line.FrameBytes, i);
            line.Write(frame, frame.Length, null, 0, false);
        }

        Thread.Sleep(600);

        if (clamped) Assert.Equal(0, line.Spilled);
        else Assert.True(line.Spilled > 500, $"{line.Spilled} frames written out");
    }

    /// <summary>
    /// One line holds its delay in RAM at a time. A second, opened while the first holds it, runs
    /// from disk - still a working delay - and once the first has closed, says so, and the next
    /// line opened gets the RAM.
    /// </summary>
    [Fact]
    public void Only_one_line_holds_its_delay_in_memory()
    {
        DelayLine first = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(3));
        bool closed = false;
        first.Closed += _ => closed = true;

        Assert.True(first.MemorySlots > 0);
        Assert.True(DelayLine.MemoryRingHeld);

        using (DelayLine second = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(1), TimeSpan.Zero,
                                                   TimeSpan.FromSeconds(3)))
        {
            Assert.Equal(0, second.MemorySlots);
            Assert.True(second.IsDirect);

            DelayFrameBuffer buffer = second.NewBuffer();
            for (int i = 0; i < 40; i++)
            {
                byte[] frame = Pattern(second.FrameBytes, i);
                second.Write(frame, frame.Length, null, 0, false);
            }

            Assert.Equal(DelayReadResult.Ok, second.TryRead(20, buffer));
            Assert.Equal(Pattern(second.FrameBytes, 20), buffer.Video);
        }

        first.Release();
        Assert.True(closed);
        Assert.False(DelayLine.MemoryRingHeld);

        using DelayLine third = DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(1), TimeSpan.Zero, TimeSpan.FromSeconds(3));
        Assert.True(third.MemorySlots > 0);
    }
}
