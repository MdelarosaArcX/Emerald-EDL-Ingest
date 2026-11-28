using System.Diagnostics;
using System.IO;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// Inserts past the end of memory: frames compressed as they leave it, and decoded again for air.
///
/// At the real raster, because DNxHD has no small one, so these take a gigabyte or two and some seconds.
/// On their own (<see cref="ProcessWideCollection"/>), and nothing without ffmpeg.
/// </summary>
[Collection(ProcessWideCollection.Name)]
public sealed class DelayOverflowTests : IDisposable
{
    private static readonly CaptureFormat Hd = new("1080p25", 1920, 1080, 25, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "emerald-overflow-tests", Guid.NewGuid().ToString("N"));

    public DelayOverflowTests()
    {
        Directory.CreateDirectory(_folder);
        DelayLine.MemoryReserve = 0;
        DelayOverflow.Reserve = 0;
        DelayOverflow.Floor = 0;
    }

    public void Dispose()
    {
        DelayOverflow.Reserve = 0;
        DelayOverflow.Floor = 0;
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>A gradient whose level and phase say which frame it is.</summary>
    private static byte[] Picture(long n, byte[]? into = null)
    {
        byte[] p = into ?? new byte[Hd.FrameBytes];
        byte luma = Level(n);

        for (int i = 0; i < p.Length; i += 2)
        {
            p[i] = 128;
            p[i + 1] = luma;
        }

        return p;
    }

    /// <summary>Distinct for any two frames within 200 of each other.</summary>
    private static byte Level(long n) => (byte)(20 + n * 7 % 211);

    private static double LevelOf(byte[] picture)
    {
        long sum = 0;
        int n = 0;
        for (int i = 1; i < picture.Length; i += 2002, n++) sum += picture[i | 1];
        return sum / (double)n;
    }

    /// <summary>Four pairs, which is what this plant records.</summary>
    private const int Channels = 8;

    private static byte[] Sound(long n) => Enumerable.Range(0, 1920 * 2 * Channels).Select(i => (byte)((n * 31 + i) % 251)).ToArray();

    /// <summary>Twelve seconds in memory - room for a 4 s delay - of a 60 s ring.</summary>
    private DelayLine? Open(string? ffmpeg = null)
    {
        ffmpeg ??= Ffmpeg.Locate();
        if (ffmpeg is null) return null;

        return DelayLine.Create(_folder, Hd, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(56), TimeSpan.FromSeconds(12),
                                required: TimeSpan.FromSeconds(4), ffmpeg: ffmpeg);
    }

    [Fact]
    public async Task This_ffmpeg_gives_back_every_frame_in_order()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;
        Assert.Null(await DelayOverflow.Probe(ffmpeg, Hd));
    }

    [Fact]
    public void Room_is_the_whole_ring_once_it_is_proved_and_memory_until_then()
    {
        using DelayLine? line = Open();
        if (line is null) return;

        Assert.Equal(300, line.MemorySlots);
        Assert.Equal(300 - 175, line.MemoryRoomFrames);
        Assert.True(line.WaitForOverflow(TimeSpan.FromSeconds(20)));
        Assert.Equal(line.SlotCount - 50, line.RoomFrames);
    }

    [Fact]
    public void Without_a_working_ffmpeg_the_room_is_what_memory_holds()
    {
        string fake = Path.Combine(_folder, "ffmpeg.exe");
        File.WriteAllText(fake, "not a program");

        using DelayLine? line = Open(fake);

        Assert.False(line!.WaitForOverflow(TimeSpan.FromSeconds(20)));
        Assert.Equal(line.MemoryRoomFrames, line.RoomFrames);
    }

    /// <summary>
    /// A transmitter thirty seconds behind with twelve in memory, at the real rate: every frame it reads is the
    /// one it asked for - by its picture, its sound and its place in the recording - none is lost, and it
    /// is never kept waiting. The first frames are raw out of memory, the rest have been through DNxHD.
    /// </summary>
    [Fact]
    public void A_transmitter_past_the_end_of_memory_gets_every_frame_in_order()
    {
        using DelayLine? line = Open();
        if (line is null) return;

        Assert.True(line.WaitForOverflow(TimeSpan.FromSeconds(20)));

        const int rate = 25, behind = 30 * rate, total = 50 * rate;
        using DelayHold hold = line.Hold(0);
        DelayFrameBuffer buffer = line.NewBuffer();
        byte[] picture = Picture(0);

        int read = 0, waits = 0, late = 0;
        var clock = Stopwatch.StartNew();

        for (int n = 0; n < total; n++)
        {
            byte[] sound = Sound(n);
            line.Write(Picture(n, picture), Hd.FrameBytes, sound, sound.Length, audioReal: n % 2 == 0, recordingFrame: 5000 + n, audioChannels: Channels);

            long at = n + 1 - behind;
            hold.Move(Math.Max(0, at));
            line.PrefetchAhead(Math.Max(0, at));

            if (at >= 0)
            {
                DelayReadResult result;
                var waited = Stopwatch.StartNew();

                while ((result = line.TryRead(at, buffer)) == DelayReadResult.NotReady && waited.ElapsedMilliseconds < 2000)
                {
                    waits++;
                    Thread.Sleep(1);
                }

                if (waited.ElapsedMilliseconds > OverflowWait.QuietMs) late++;

                Assert.True(result == DelayReadResult.Ok, $"frame {at}: {result}, {line.CompressedLost} lost");
                Assert.InRange(LevelOf(buffer.Video), Level(at) - 3, Level(at) + 3);
                Assert.Equal(Sound(at), buffer.Audio.AsSpan(0, buffer.AudioBytes).ToArray());
                Assert.Equal(Channels, buffer.AudioChannels);
                Assert.Equal(at % 2 == 0, buffer.AudioWasReal);
                Assert.Equal(5000 + at, buffer.RecordingFrame);
                Assert.Equal(5000 + at, line.RecordingFrameAt(at));
                read++;
            }

            // The recorder's own pace.
            long due = (long)(n + 1) * 1000 / rate;
            if (due > clock.ElapsedMilliseconds) Thread.Sleep((int)(due - clock.ElapsedMilliseconds));
        }

        Assert.Equal(total - behind + 1, read);
        Assert.Equal(0, line.CompressedLost);
        Assert.Equal(0, line.Spilled);
        Assert.True(line.Compressed > 500, $"{line.Compressed} compressed");
        Assert.True(late == 0, $"{late} frames were later than the card could hide, {waits} waits in all");
    }

    /// <summary>Runs the line at the recorder's pace, <paramref name="behind"/> frames back, for so many frames from where it is.</summary>
    private static void Run(DelayLine line, DelayHold hold, long behind, int frames, byte[] picture, int msPerFrame = 20)
    {
        var clock = Stopwatch.StartNew();

        for (int n = 0; n < frames; n++)
        {
            line.Write(picture, Hd.FrameBytes, null, 0, false);

            long at = Math.Max(0, line.FramesWritten - behind);
            hold.Move(at);
            line.PrefetchAhead(at);

            long due = (long)(n + 1) * msPerFrame;
            if (due > clock.ElapsedMilliseconds) Thread.Sleep((int)(due - clock.ElapsedMilliseconds));
        }
    }

    /// <summary>
    /// The point of it: memory rises by what the insert adds - a second of it about 16 MB - holds steady while the
    /// delay is that long, is more for a longer one, and is given back when the delay is short again.
    /// </summary>
    [Fact]
    public void Memory_rises_with_the_length_of_the_insert_and_comes_back()
    {
        using DelayLine? line = Open();
        if (line is null) return;

        Assert.True(line.WaitForOverflow(TimeSpan.FromSeconds(20)));

        byte[] picture = Picture(5);
        double second = DelayOverflow.StrideFor(Hd) * 25.0;
        Assert.InRange(second / 1e6, 15, 17);

        using DelayHold hold = line.Hold(0);

        // The delay alone, for longer than memory: nothing taken.
        Run(line, hold, 4 * 25, 400, picture);
        Assert.Equal(0, line.GrownBytes);

        // Held back as if by an insert, to 20 s behind with 12 in memory, and then kept there.
        for (long behind = 4 * 25; behind < 20 * 25; behind += 25) Run(line, hold, behind, 25, picture);
        Run(line, hold, 20 * 25, 200, picture);
        double ten = line.GrownBytes / second;
        Run(line, hold, 20 * 25, 200, picture);
        double still = line.GrownBytes / second;

        Assert.InRange(ten, 8, 17);
        Assert.InRange(still, ten - 2, ten + 2);

        // Another ten seconds.
        for (long behind = 20 * 25; behind < 30 * 25; behind += 25) Run(line, hold, behind, 25, picture);
        Run(line, hold, 30 * 25, 100, picture);
        Assert.InRange(line.GrownBytes / second, ten + 7, ten + 13);

        // Back to the delay alone.
        Run(line, hold, 4 * 25, 100, picture);
        Assert.Equal(0, line.GrownBytes);
        Assert.Equal(0, line.CompressedLost);
    }

    /// <summary>The delay and its guard exactly, as it is held on air, with the pacer's half second of drift: nothing is compressed.</summary>
    [Fact]
    public void The_memory_kept_for_a_delay_is_enough_for_it()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        Assert.True(DelayLine.CanGrow(ffmpeg, Hd, TimeSpan.FromSeconds(20)));

        TimeSpan delay = TimeSpan.FromSeconds(4);
        using DelayLine line = DelayLine.Create(_folder, Hd, delay, TimeSpan.FromSeconds(56), DelayLine.MemoryFor(delay),
                                                required: delay, ffmpeg: ffmpeg);

        Assert.Equal((4 + 7) * 25, line.MemorySlots);
        Assert.True(line.WaitForOverflow(TimeSpan.FromSeconds(20)));

        using DelayHold hold = line.Hold(0);
        Run(line, hold, 4 * 25 + 12 + 2, 900, Picture(9), msPerFrame: 10);

        Assert.Equal(0, line.Compressed);
        Assert.Equal(0, line.GrownBytes);
    }

    /// <summary>
    /// A soak, only when asked for (EMERALD_OVERFLOW_SOAK=1; 9 minutes, 6 GB): two minutes of delay grown by five of inserts, at
    /// the recorder's pace with pictures of noise, reading every frame as air does.
    /// </summary>
    [Fact]
    public void Soak_five_minutes_of_inserts_at_the_recorders_pace()
    {
        if (Environment.GetEnvironmentVariable("EMERALD_OVERFLOW_SOAK") != "1" || Ffmpeg.Locate() is not { } ffmpeg) return;

        // The ring in memory is kept small, so it all fits beside the five minutes; what is measured is the rest.
        using DelayLine line = DelayLine.Create(_folder, Hd, TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(360), TimeSpan.FromSeconds(12),
                                                required: TimeSpan.FromSeconds(4), ffmpeg: ffmpeg);
        Assert.True(line.WaitForOverflow(TimeSpan.FromSeconds(20)));

        // Noise, with the frame's number where it survives: a block of one level along the top.
        var random = new Random(1);
        var noise = new byte[8][];
        for (int i = 0; i < noise.Length; i++) random.NextBytes(noise[i] = new byte[Hd.FrameBytes]);

        void Mark(byte[] p, long n)
        {
            for (int i = 0; i < 1920 * 2 * 32; i += 2) { p[i] = 128; p[i + 1] = Level(n); }
        }

        double Read(byte[] p)
        {
            long sum = 0; int n = 0;
            for (int i = 1920 * 2 * 8 + 1; i < 1920 * 2 * 24; i += 202, n++) sum += p[i | 1];
            return sum / (double)n;
        }

        using DelayHold hold = line.Hold(0);
        DelayFrameBuffer buffer = line.NewBuffer();
        const int rate = 25, behind = 300 * rate, total = 520 * rate;
        long late = 0, worst = 0, read = 0, slowWrites = 0, peak = 0;
        var clock = Stopwatch.StartNew();

        for (int n = 0; n < total; n++)
        {
            byte[] p = noise[n % noise.Length];
            Mark(p, n);

            var writing = Stopwatch.StartNew();
            line.Write(p, Hd.FrameBytes, Sound(n % 250), 1920 * 2 * Channels, true, recordingFrame: n, audioChannels: Channels);
            if (writing.ElapsedMilliseconds > 40) slowWrites++;

            long at = n + 1 - behind;
            hold.Move(Math.Max(0, at));
            line.PrefetchAhead(Math.Max(0, at));
            peak = Math.Max(peak, line.GrownBytes);

            if (at >= 0)
            {
                var waited = Stopwatch.StartNew();
                DelayReadResult result;
                while ((result = line.TryRead(at, buffer)) == DelayReadResult.NotReady && waited.ElapsedMilliseconds < 3000) Thread.Sleep(1);

                worst = Math.Max(worst, waited.ElapsedMilliseconds);
                if (waited.ElapsedMilliseconds > OverflowWait.QuietMs) late++;

                Assert.True(result == DelayReadResult.Ok, $"frame {at}: {result}, {line.CompressedLost} lost");
                Assert.InRange(Read(buffer.Video), Level(at) - 4, Level(at) + 4);
                Assert.Equal(at, buffer.RecordingFrame);
                read++;
            }

            long due = (long)(n + 1) * 1000 / rate;
            if (due > clock.ElapsedMilliseconds) Thread.Sleep((int)(due - clock.ElapsedMilliseconds));
        }

        string said = $"read {read}, late {late}, worst wait {worst} ms, writes over 40 ms {slowWrites}, lost {line.CompressedLost}, " +
                      $"peak {peak / 1e9:F2} GB, {clock.Elapsed.TotalSeconds - total / (double)rate:F1} s behind the clock";

        if (Environment.GetEnvironmentVariable("EMERALD_OVERFLOW_SOAK_REPORT") is { Length: > 0 } report) File.WriteAllText(report, said);

        Assert.True(late == 0 && line.CompressedLost == 0, said);
    }

    /// <summary>Little free memory: the room is what is kept for the delay, so an insert is refused before it is placed.</summary>
    [Fact]
    public void With_no_memory_to_spare_there_is_no_room_for_inserts()
    {
        using DelayLine? line = Open();
        if (line is null) return;

        Assert.True(line.WaitForOverflow(TimeSpan.FromSeconds(20)));
        DelayOverflow.Reserve = long.MaxValue / 4;
        Assert.Equal(line.MemoryRoomFrames, line.RoomFrames);
    }

    /// <summary>Memory gone after an insert was let in: frames are lost and said to be, and nothing falls over.</summary>
    [Fact]
    public void Memory_running_out_part_way_loses_frames_and_nothing_else()
    {
        using DelayLine? line = Open();
        if (line is null) return;

        Assert.True(line.WaitForOverflow(TimeSpan.FromSeconds(20)));
        DelayOverflow.Floor = long.MaxValue / 4;

        using DelayHold hold = line.Hold(0);
        Run(line, hold, 1000, 420, Picture(4), msPerFrame: 15);
        Thread.Sleep(500);

        Assert.Equal(0, line.GrownBytes);
        Assert.True(line.CompressedLost > 0);
        Assert.Equal(DelayReadResult.Missing, line.TryRead(60, line.NewBuffer()));
        Assert.Null(line.Fault);
    }

    /// <summary>Nothing is compressed for a delay that fits in memory, and no ffmpeg runs for it.</summary>
    [Fact]
    public void A_delay_inside_memory_is_never_compressed()
    {
        using DelayLine? line = Open();
        if (line is null) return;

        Assert.True(line.WaitForOverflow(TimeSpan.FromSeconds(20)));

        using DelayHold hold = line.Hold(0);
        byte[] picture = Picture(0);

        for (int n = 0; n < 700; n++)
        {
            line.Write(Picture(n, picture), Hd.FrameBytes, null, 0, false);
            long at = Math.Max(0, n + 1 - line.MemoryRoomFrames);
            hold.Move(at);
            line.PrefetchAhead(at);
            if (n % 10 == 9) Thread.Sleep(60);
        }

        Thread.Sleep(300);
        Assert.Equal(0, line.Compressed);
        Assert.Equal(0, line.Spilled);
    }

    /// <summary>
    /// A line written far faster than anything can compress: frames are lost and said to be, the writer is
    /// not held up, and a read of a lost one says so rather than "not yet" or "lapped".
    /// </summary>
    [Fact]
    public void The_writer_never_waits_for_the_compressor()
    {
        using DelayLine? line = Open();
        if (line is null) return;

        Assert.True(line.WaitForOverflow(TimeSpan.FromSeconds(20)));

        using DelayHold hold = line.Hold(0);
        DelayFrameBuffer buffer = line.NewBuffer();
        byte[] picture = Picture(7);
        long worst = 0;

        for (int n = 0; n < 900; n++)
        {
            var one = Stopwatch.StartNew();
            line.Write(picture, Hd.FrameBytes, null, 0, false);
            worst = Math.Max(worst, one.ElapsedMilliseconds);
        }

        Assert.True(line.CompressedLost > 0, "nothing was lost, so this tested nothing");
        Assert.True(worst < 500, $"the writer waited {worst} ms");

        var seen = new HashSet<DelayReadResult>();
        for (long s = 0; s < 500; s++) seen.Add(line.TryRead(s, buffer));
        Assert.Contains(DelayReadResult.Missing, seen);
        Assert.DoesNotContain(DelayReadResult.Lapped, seen);
    }

    /// <summary>Closed in the middle of all of it, it leaves no ffmpeg running and no file behind.</summary>
    [Fact]
    public void Closing_it_at_work_leaves_nothing_behind()
    {
        DelayLine? line = Open();
        if (line is null) return;

        Assert.True(line.WaitForOverflow(TimeSpan.FromSeconds(20)));

        int before = Process.GetProcessesByName("ffmpeg").Length;
        byte[] picture = Picture(3);

        using (DelayHold hold = line.Hold(0))
            for (int n = 0; n < 420; n++)
            {
                line.Write(picture, Hd.FrameBytes, null, 0, false);
                line.PrefetchAhead(0);
                Thread.Sleep(15);
            }

        Assert.True(line.Compressed > 0);

        var closing = Stopwatch.StartNew();
        line.Release();

        Assert.True(closing.Elapsed < TimeSpan.FromSeconds(20), $"took {closing.Elapsed} to close");
        Assert.Empty(Directory.GetFiles(_folder, "*.ring"));

        Thread.Sleep(500);
        Assert.True(Process.GetProcessesByName("ffmpeg").Length <= before, "an ffmpeg was left running");
    }
}

public sealed class OverflowWaitTests
{
    [Fact]
    public void A_short_wait_holds_nothing_then_a_picture_is_held_then_the_frame_is_given_up()
    {
        long now = 1000;
        var wait = new OverflowWait(() => now, ms => now += ms);

        int quiet = 0, held = 0;
        bool push;

        while (!wait.GiveUp(out push))
        {
            if (push) held++; else quiet++;
        }

        Assert.InRange(now - 1000, OverflowWait.GiveUpMs, OverflowWait.GiveUpMs + 40);
        Assert.True(quiet * 3 >= OverflowWait.QuietMs);

        // About a picture a frame's time, so the card neither runs dry nor fills with repeats.
        Assert.InRange(held, 20, 26);
    }

    [Fact]
    public void Each_frame_has_its_own_wait()
    {
        long now = 0;
        var wait = new OverflowWait(() => now, ms => now += ms);

        Assert.False(wait.GiveUp(out _));
        now += 900;
        Assert.False(wait.GiveUp(out bool push));
        Assert.True(push);

        wait.Reset();
        now += 900;
        Assert.False(wait.GiveUp(out push));
        Assert.False(push);
    }
}
