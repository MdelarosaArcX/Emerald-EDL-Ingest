using Emerald.Media;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// A segment's length is its picture's, not its longest track's. The recorder's sound runs a few
/// milliseconds past the last frame of every file, and segments laid end to end by the longer
/// figure put the timeline 5.45 s behind air over 685 of them.
///
/// Against a file ffmpeg actually wrote, with its sound deliberately longer than its picture.
/// </summary>
public sealed class PictureDurationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emerald-picture", Guid.NewGuid().ToString("N"));

    private static string? Ffmpeg => Emerald.Video.Ffmpeg.Locate(null);

    public PictureDurationTests() => Directory.CreateDirectory(_root);

    private static void Encode(string path, double pictureSeconds, double soundSeconds)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Ffmpeg!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (string a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", $"testsrc=size=64x48:rate=25:duration={pictureSeconds:0.###}",
                     "-f", "lavfi", "-i", $"sine=frequency=1000:sample_rate=48000:duration={soundSeconds:0.###}",
                     "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p",
                     "-c:a", "aac", path,
                 })
            psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)!;
        p.ReadToEndQuietly();
        p.WaitForExit();
    }

    [Fact]
    public void The_picture_length_ignores_sound_that_runs_on_past_the_last_frame()
    {
        if (Ffmpeg is null) return;

        string path = Path.Combine(_root, "media_0102261252_101.mov");
        Encode(path, 2, 2.2);

        TimeSpan? movie = MediaCodec.ReadDuration(path);
        TimeSpan? picture = MediaCodec.ReadPictureDuration(path);

        Assert.NotNull(picture);
        Assert.Equal(2.0, picture!.Value.TotalSeconds, 3);
        Assert.True(movie > picture, $"movie {movie}, picture {picture}");
    }

    [Fact]
    public void Probed_info_runs_for_the_picture()
    {
        if (Ffmpeg is null || MediaProbe.LocateFfprobe(Ffmpeg) is not { } ffprobe) return;

        string path = Path.Combine(_root, "media_0102261252_102.mov");
        Encode(path, 2, 2.2);

        MediaInfo info = MediaProbe.Probe(ffprobe, path, 25)!;

        Assert.True(info.Duration.TotalSeconds > 2.1);
        Assert.Equal(2.0, info.Runs.TotalSeconds, 3);
    }

    [Fact]
    public void A_file_with_no_picture_runs_for_its_whole_length()
    {
        var info = new MediaInfo("x.wav", TimeSpan.FromSeconds(3), default, false, true, "?", 0, 0);

        Assert.Equal(TimeSpan.FromSeconds(3), info.Runs);
    }

    [Fact]
    public void An_unfinished_file_has_no_picture_length()
    {
        if (Ffmpeg is null) return;

        string path = Path.Combine(_root, "media_0102261252_103.mov");
        Encode(path, 1, 1);

        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(bytes.Length / 2)]);

        Assert.Null(MediaCodec.ReadPictureDuration(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}

internal static class ProcessQuiet
{
    /// <summary>Drains stderr so a chatty ffmpeg cannot fill the pipe and hang the wait.</summary>
    public static void ReadToEndQuietly(this System.Diagnostics.Process p) => p.StandardError.ReadToEnd();
}
