using System.Diagnostics;
using System.IO;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The sound of a placed item on Live Edit air, read a video frame at a time as the transmitter
/// reads it. It used to be asked for by frame number where samples were wanted, so every frame
/// got nearly the same fortieth of a second again.
///
/// Through a real ffmpeg, on a file whose every sample says where in the file it is. Skipped
/// quietly on a machine without one, as the other decode tests are.
/// </summary>
public sealed class CueSoundTests : IDisposable
{
    private static string? Ffmpeg => Emerald.Video.Ffmpeg.Locate(null);

    private const int FrameRate = 25;
    private const int PerFrame = AudioSource.SampleRate / FrameRate;

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "emerald-cuesound-tests", Guid.NewGuid().ToString("N"));

    public CueSoundTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    /// <summary>
    /// Stereo 48 kHz, sample n carrying n itself: its low fifteen bits on the left, the rest on
    /// the right. Counted from one, so no sample of it is silence.
    /// </summary>
    private string Counted(double seconds)
    {
        string path = Path.Combine(_folder, "counted.wav");
        int samples = (int)(seconds * AudioSource.SampleRate);

        using var file = new BinaryWriter(File.Create(path));

        file.Write("RIFF"u8); file.Write(36 + samples * 4); file.Write("WAVE"u8);
        file.Write("fmt "u8); file.Write(16); file.Write((short)1); file.Write((short)2);
        file.Write(AudioSource.SampleRate); file.Write(AudioSource.SampleRate * 4);
        file.Write((short)4); file.Write((short)16);
        file.Write("data"u8); file.Write(samples * 4);

        for (int n = 1; n <= samples; n++)
        {
            file.Write((short)(n & 0x7FFF));
            file.Write((short)(n >> 15));
        }

        return path;
    }

    /// <summary>Which sample of the file this one is, counted from nothing; -1 for silence.</summary>
    private static long Position(short left, short right) => ((long)right << 15 | (ushort)left) - 1;

    /// <summary>One frame as the transmitter takes it, waited for while the decoder gets there.</summary>
    private static void Take(AudioSource sound, long frame, short[] left, short[] right)
    {
        var patience = Stopwatch.StartNew();

        do
        {
            sound.ReadVideoFrame(left, right, frame);
            if (Position(left[^1], right[^1]) >= 0) return;

            Thread.Sleep(5);
        }
        while (patience.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void Each_frame_of_a_cue_carries_the_sound_that_belongs_to_it()
    {
        if (Ffmpeg is not { } ffmpeg) return;

        using AudioSource sound = AudioSource.Open(ffmpeg, Counted(3), FrameRate);

        var left = new short[PerFrame];
        var right = new short[PerFrame];

        // Two seconds on air, a frame after a frame.
        for (long frame = 0; frame < 2 * FrameRate; frame++)
        {
            Take(sound, frame, left, right);

            for (int i = 0; i < PerFrame; i++)
                Assert.True(Position(left[i], right[i]) == frame * PerFrame + i,
                    $"frame {frame}, sample {i}: sample {Position(left[i], right[i])} of the file, " +
                    $"where {frame * PerFrame + i} belongs");
        }
    }

    [Fact]
    public void A_cue_joined_late_starts_its_sound_where_its_picture_starts()
    {
        if (Ffmpeg is not { } ffmpeg) return;

        // A second into its media, as the transmitter opens a cue it is late for.
        using AudioSource sound = AudioSource.Open(ffmpeg, Counted(3), FrameRate, -1, TimeSpan.FromSeconds(1));

        var left = new short[PerFrame];
        var right = new short[PerFrame];

        for (long frame = 0; frame < FrameRate; frame++)
        {
            Take(sound, frame, left, right);

            Assert.Equal(AudioSource.SampleRate + frame * PerFrame, Position(left[0], right[0]));
            Assert.Equal(AudioSource.SampleRate + frame * PerFrame + PerFrame - 1, Position(left[^1], right[^1]));
        }
    }

    [Fact]
    public void Past_the_end_of_its_media_a_cue_is_silent()
    {
        if (Ffmpeg is not { } ffmpeg) return;

        using AudioSource sound = AudioSource.Open(ffmpeg, Counted(1), FrameRate);

        var left = new short[PerFrame];
        var right = new short[PerFrame];

        for (long frame = 0; frame < FrameRate; frame++) Take(sound, frame, left, right);

        Assert.Equal(AudioSource.SampleRate - 1, Position(left[^1], right[^1]));

        sound.ReadVideoFrame(left, right, FrameRate);

        Assert.All(left, sample => Assert.Equal(0, sample));
        Assert.All(right, sample => Assert.Equal(0, sample));
        Assert.True(sound.Exhausted);
    }
}
