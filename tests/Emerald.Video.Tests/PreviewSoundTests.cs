using System.Diagnostics;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The preview's sound: read in pieces that join without a seam, every track from its own channels,
/// and kept in step with the picture. Real media where it matters; skipped without ffmpeg.
/// </summary>
public sealed class PreviewSoundTests : IDisposable
{
    private readonly string _temp = Path.Combine(Path.GetTempPath(), "emerald-sound", Guid.NewGuid().ToString("N"));

    public PreviewSoundTests() => Directory.CreateDirectory(_temp);

    public void Dispose()
    {
        try { Directory.Delete(_temp, recursive: true); } catch { }
    }

    private static byte[] Run(string ffmpeg, params string[] args)
    {
        var info = new ProcessStartInfo(ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string a in args) info.ArgumentList.Add(a);

        using Process p = Process.Start(info)!;
        Task<string> problem = p.StandardError.ReadToEndAsync();
        using var all = new MemoryStream();
        p.StandardOutput.BaseStream.CopyTo(all);
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, problem.Result);
        return all.ToArray();
    }

    /// <summary>As the recorder writes it: a picture with a keyframe every half second, and four stereo AAC tracks.</summary>
    private string Media(string ffmpeg, int seconds)
    {
        string path = Path.Combine(_temp, "media.mp4");
        var args = new List<string> { "-v", "error", "-y", "-f", "lavfi", "-i", $"testsrc=size=64x48:rate=25:duration={seconds}" };

        for (int t = 0; t < 4; t++)
            args.AddRange(new[] { "-f", "lavfi", "-i",
                $"aevalsrc=0.3*sin(2*PI*{220 * (t + 1)}*t)|0.2*sin(2*PI*{330 * (t + 1)}*t):s=48000:d={seconds}" });

        args.AddRange(new[] { "-map", "0:v", "-map", "1:a", "-map", "2:a", "-map", "3:a", "-map", "4:a", "-c:v", "libx264",
                              "-preset", "ultrafast", "-g", "12", "-pix_fmt", "yuv420p", "-c:a", "aac", "-b:a", "192k", path });
        Run(ffmpeg, args.ToArray());
        return path;
    }

    private static void ReadWhole(SegmentSound sound, string path, long sample, int count, byte[] into)
    {
        var waited = Stopwatch.StartNew();
        while (!sound.Read(path, 4, sample, count, into))
        {
            Assert.True(waited.Elapsed.TotalSeconds < 20, "the sound was never read");
            Thread.Sleep(5);
        }
    }

    [Fact]
    public void Every_track_is_read_whole_and_the_pieces_join_without_a_seam()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        const int seconds = 23;
        string path = Media(ffmpeg, seconds);

        // Each track decoded straight through, to compare with.
        byte[][] whole = Enumerable.Range(0, 4)
            .Select(t => Run(ffmpeg, "-v", "error", "-nostdin", "-i", path, "-map", $"0:a:{t}", "-af", "aresample=48000:async=1:first_pts=0", "-ac", "2", "-f", "s16le", "-"))
            .ToArray();

        using var sound = new SegmentSound(ffmpeg);
        const int count = 1920;
        var frame = new byte[count * 16];
        int worst = 0;
        long worstAt = -1;

        for (long at = 0; at + count <= (seconds - 1) * 48000L; at += count)
        {
            ReadWhole(sound, path, at, count, frame);

            for (int i = 0; i < count; i++)
                for (int c = 0; c < 8; c++)
                {
                    int got = BitConverter.ToInt16(frame, (i * 8 + c) * 2);
                    int want = BitConverter.ToInt16(whole[c / 2], (int)((at + i) * 4 + c % 2 * 2));
                    if (Math.Abs(got - want) <= worst) continue;
                    worst = Math.Abs(got - want);
                    worstAt = at + i;
                }
        }

        // A sample out at the steepest of these tones is over a thousand.
        Assert.True(worst < 150, $"out by {worst} at sample {worstAt} ({worstAt / 48000.0:F3} s)");
    }

    [Fact]
    public void Outside_the_file_is_silence()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        string path = Media(ffmpeg, 2);
        using var sound = new SegmentSound(ffmpeg);
        var frame = new byte[1920 * 16];

        ReadWhole(sound, path, 5 * 48000, 1920, frame);
        Assert.All(frame, b => Assert.Equal(0, b));

        ReadWhole(sound, path, -1920, 1920, frame);
        Assert.All(frame, b => Assert.Equal(0, b));

        ReadWhole(sound, path, 48000, 1920, frame);
        Assert.Contains(frame, b => b != 0);

        ReadWhole(sound, path, 500 * 48000L, 1920, frame);
        Assert.All(frame, b => Assert.Equal(0, b));
    }

    [Fact]
    public void Only_the_last_few_pieces_are_kept()
    {
        if (Ffmpeg.Locate() is not { } ffmpeg) return;

        string path = Media(ffmpeg, 2);
        using var sound = new SegmentSound(ffmpeg);
        var frame = new byte[1920 * 16];

        for (int i = 0; i < 12; i++)
        {
            string copy = Path.Combine(_temp, $"{i}.mp4");
            File.Copy(path, copy);
            ReadWhole(sound, copy, 0, 1920, frame);
        }

        Assert.InRange(sound.Bytes, 1, 8L * 2 * 48000 * 16);
    }

    [Fact]
    public void The_sound_is_left_alone_while_the_picture_only_wanders()
    {
        var cursor = new SoundCursor();
        Assert.True(cursor.Follow(10));
        int moves = 0;

        // A position that reads up to a frame out either way, for a minute.
        for (int i = 1; i <= 1500; i++)
        {
            cursor.Advance(1920);
            if (cursor.Follow(10 + i * 0.04 + (i % 3 - 1) * 0.04)) moves++;
        }

        Assert.Equal(0, moves);
    }

    [Fact]
    public void Drift_is_put_right_before_it_shows_and_rarely()
    {
        var cursor = new SoundCursor();
        cursor.Follow(0);
        int moves = 0;
        double worst = 0;

        // A card 200 parts in a million fast, for an hour.
        for (int i = 1; i <= 90000; i++)
        {
            cursor.Advance(1920);
            double picture = i * 0.04 * (1 - 200e-6);
            if (cursor.Follow(picture)) moves++;
            worst = Math.Max(worst, Math.Abs(picture - cursor.Sample!.Value / 48000.0));
        }

        Assert.InRange(moves, 5, 20);
        Assert.True(worst < 0.07, $"{worst * 1000:F0} ms");
    }

    [Fact]
    public void A_jump_of_the_picture_is_followed_at_once()
    {
        var cursor = new SoundCursor();
        cursor.Follow(10);
        cursor.Advance(1920);

        Assert.True(cursor.Follow(12));
        Assert.Equal(12 * 48000, cursor.Sample);
        Assert.True(cursor.Follow(3));
        Assert.Equal(3 * 48000, cursor.Sample);
    }
}
