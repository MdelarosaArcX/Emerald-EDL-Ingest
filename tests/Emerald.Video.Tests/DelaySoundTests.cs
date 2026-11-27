using Emerald.Core;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The sound of the segment still being recorded, out of the delay: the samples the delay holds,
/// every pair, waited for when they are not recorded yet, and running on from the file before it.
/// </summary>
public sealed class DelaySoundTests : IDisposable
{
    private static readonly CaptureFormat Tiny = new("test64", 64, 64, 25, 0);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "emerald-delaysound", Guid.NewGuid().ToString("N"));

    public DelaySoundTests()
    {
        Directory.CreateDirectory(_folder);
        DelayLine.MemoryReserve = 0;
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    /// <summary>The value of sample <paramref name="sample"/> of channel <paramref name="channel"/>: different everywhere.</summary>
    private static short Value(long sample, int channel) => (short)(sample * 7 % 20000 - 10000 + channel * 3);

    /// <summary>Frames <paramref name="from"/> on of a recording, <paramref name="channels"/> channels each.</summary>
    private static void Record(DelayLine line, long from, int count, int channels)
    {
        var picture = new byte[Tiny.FrameBytes];
        var audio = new byte[1920 * channels * 2];

        for (long f = from; f < from + count; f++)
        {
            for (int i = 0; i < 1920; i++)
                for (int c = 0; c < channels; c++)
                    BitConverter.TryWriteBytes(audio.AsSpan(((i * channels) + c) * 2), Value(f * 1920 + i, c));

            line.Write(picture, picture.Length, audio, audio.Length, audioReal: true, recordingFrame: f, audioChannels: channels);
        }
    }

    private DelayLine Line() => DelayLine.Create(_folder, Tiny, TimeSpan.FromSeconds(4), TimeSpan.Zero, TimeSpan.FromSeconds(8));

    [Fact]
    public void The_sound_alone_is_what_the_whole_frame_holds()
    {
        using DelayLine line = Line();
        Record(line, 500, 10, 8);

        var whole = line.NewBuffer();
        var sound = line.NewBuffer();

        for (long seq = 0; seq < 10; seq++)
        {
            Assert.Equal(DelayReadResult.Ok, line.TryRead(seq, whole));
            Assert.Equal(DelayReadResult.Ok, line.TryReadAudio(seq, sound));

            Assert.Equal(whole.AudioBytes, sound.AudioBytes);
            Assert.Equal(whole.AudioChannels, sound.AudioChannels);
            Assert.Equal(whole.RecordingFrame, sound.RecordingFrame);
            Assert.True(whole.Audio.AsSpan(0, whole.AudioBytes).SequenceEqual(sound.Audio.AsSpan(0, sound.AudioBytes)));
        }

        Assert.Equal(DelayReadResult.NotYetWritten, line.TryReadAudio(10, sound));
    }

    [Fact]
    public void Every_pair_is_read_from_where_the_segment_starts()
    {
        using DelayLine line = Line();
        Record(line, 500, 40, 8);

        using var sound = new DelaySound();
        string key = DelaySound.KeyOf("segment.mp4");

        // The segment starts at frame 510 of the recording; read from part-way into its third frame.
        sound.Use(key, line, 510);

        var into = new byte[5000 * 16];
        Assert.True(sound.Read(key, 4, 2 * 1920 + 300, 5000, into));

        for (int i = 0; i < 5000; i++)
            for (int c = 0; c < 8; c++)
                Assert.Equal(Value(512 * 1920 + 300 + i, c), BitConverter.ToInt16(into, (i * 8 + c) * 2));
    }

    [Fact]
    public void Fewer_pairs_in_the_delay_leave_the_others_silent_and_more_are_left_out()
    {
        using DelayLine line = Line();
        Record(line, 0, 4, 2);

        using var sound = new DelaySound();
        string key = DelaySound.KeyOf("segment.mp4");
        sound.Use(key, line, 0);

        var into = new byte[1920 * 16];
        Assert.True(sound.Read(key, 4, 0, 1920, into));

        for (int i = 0; i < 1920; i++)
        {
            Assert.Equal(Value(i, 1), BitConverter.ToInt16(into, (i * 8 + 1) * 2));
            Assert.Equal(0, BitConverter.ToInt16(into, (i * 8 + 2) * 2));
        }

        var one = new byte[1920 * 4];
        Assert.True(sound.Read(key, 1, 1920, 1920, one));
        Assert.Equal(Value(1920, 0), BitConverter.ToInt16(one, 0));
    }

    [Fact]
    public void Not_recorded_yet_is_waited_for_and_gone_is_silence()
    {
        using DelayLine line = Line();
        Record(line, 1000, 20, 2);

        using var sound = new DelaySound();
        string key = DelaySound.KeyOf("segment.mp4");
        sound.Use(key, line, 1000);

        var into = new byte[1920 * 4];

        // Past the last frame recorded.
        Assert.False(sound.Read(key, 1, 25 * 1920, 1920, into));

        // Before the line reaches back: silence, not a wait.
        Assert.True(sound.Read(key, 1, -500 * 1920, 1920, into));
        Assert.All(into, b => Assert.Equal(0, b));

        // Unknown to it, or let go: silence.
        Assert.True(sound.Read(DelaySound.KeyOf("other.mp4"), 1, 0, 1920, into));
        sound.Drop();
        Assert.True(sound.Read(key, 1, 0, 1920, into));
        Assert.All(into, b => Assert.Equal(0, b));
    }

    [Fact]
    public void A_line_listened_to_is_let_go_when_it_is_not()
    {
        DelayLine line = Line();
        Record(line, 0, 5, 2);

        var sound = new DelaySound();
        sound.Use(DelaySound.KeyOf("a.mp4"), line, 0);

        // Released by its owner, it is still readable while the sound holds it.
        line.Release();
        var into = new byte[1920 * 4];
        Assert.True(sound.Read(DelaySound.KeyOf("a.mp4"), 1, 0, 1920, into));
        Assert.Equal(Value(0, 0), BitConverter.ToInt16(into, 0));

        sound.Drop();

        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (line.TryAddRef())
        {
            line.Release();
            Assert.True(waited.Elapsed.TotalSeconds < 5, "the line was never let go");
            Thread.Sleep(10);
        }
    }

    /// <summary>Files' sound for the feed, without ffmpeg: the same values, each file from the recording frame it starts at.</summary>
    private sealed class FileLike(long frames, Dictionary<string, long> starts) : ISoundSource
    {
        public bool Read(string path, int tracks, long sample, int count, byte[] into)
        {
            Array.Clear(into, 0, count * tracks * 4);
            long start = starts[path] * 1920;
            for (int i = 0; i < count; i++)
                if (sample + i >= 0 && sample + i < frames * 1920)
                    for (int c = 0; c < tracks * 2; c++)
                        BitConverter.TryWriteBytes(into.AsSpan(((i * tracks * 2) + c) * 2), Value(start + sample + i, c));
            return true;
        }

        public void Want(string path, int tracks, long sample) { }
        public long? Lead(string path, int tracks) => 0;
        public long? Length(string path, int tracks) => frames * 1920;
    }

    private sealed class Both(ISoundSource file, DelaySound delay) : ISoundSource
    {
        private ISoundSource For(string path) => DelaySound.IsKey(path) ? delay : file;
        public bool Read(string path, int tracks, long sample, int count, byte[] into) => For(path).Read(path, tracks, sample, count, into);
        public void Want(string path, int tracks, long sample) => For(path).Want(path, tracks, sample);
        public long? Lead(string path, int tracks) => For(path).Lead(path, tracks);
        public long? Length(string path, int tracks) => For(path).Length(path, tracks);
    }

    [Theory]
    [InlineData(-0.1)]
    [InlineData(0.1)]
    public void From_the_last_closed_file_into_the_delay_and_back_is_one_run_of_sound(double pictureLate)
    {
        using DelayLine line = Line();
        Record(line, 700, 80, 4);

        using var delay = new DelaySound();
        var feed = new SoundFeed(new Both(new FileLike(750, new() { ["closed.mp4"] = 0, ["open.mp4"] = 750 }), delay));

        string key = DelaySound.KeyOf("open.mp4");
        delay.Use(key, line, 750);

        var held = new PreviewHeld(line, 750);
        var file = new PreviewMoment("closed.mp4", 2, 0, 750, 25, 0, 0, 0, [], 1, new PreviewNext(key, 2, 750, held));
        var open = new PreviewMoment(key, 2, 750, 750, 25, 0, 0, 0, [], 1, null, held);

        // The open one closes part-way: the same sound from its file, on the same count.
        var nowClosed = open with { Path = "open.mp4", Held = null };

        long expected = -1;

        for (int i = 0; i < 50; i++)
        {
            double at = 29 + i * 0.04;
            PreviewMoment now = at < 30 + pictureLate ? file with { Position = at }
                              : i < 40 ? open with { Position = at - 30 }
                              : nowClosed with { Position = at - 30 };

            Assert.True(feed.Take(now, 0.1, 1920));
            if (i > 0) Assert.False(feed.Moved, $"moved at {at:F2}");

            // The file before is frame 0 on; the delay and the file after are counted from 750, which is the same.
            for (int s = 0; s < 1920; s += 97)
            {
                short got = BitConverter.ToInt16(feed.Buffer, s * 8);
                if (i == 0 && s == 0) expected = SampleOf(got);
                Assert.Equal(Value(expected + i * 1920 + s, 0), got);
            }
        }

        static long SampleOf(short value)
        {
            for (long s = 29 * 48000; s < 32 * 48000; s++) if (Value(s, 0) == value) return s;
            return -1;
        }
    }
}
