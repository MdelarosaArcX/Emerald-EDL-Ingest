using System.IO;
using Emerald.Deltacast;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// Decoding ahead of the transmitter, for the media a cue cuts into air.
///
/// Through a real ffmpeg, because that is the pipe the stalls came from. Skipped quietly on a
/// machine without one, as the other decode tests are.
/// </summary>
public sealed class FrameReadaheadTests : IDisposable
{
    private static string? Ffmpeg => Emerald.Video.Ffmpeg.Locate(null);

    private readonly string _folder =
        Path.Combine(Path.GetTempPath(), "emerald-readahead-tests", Guid.NewGuid().ToString("N"));

    private static readonly VideoFormat Format = VideoFormat.ForFrameRate(25);

    public FrameReadaheadTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }

    private string Clip(double seconds)
    {
        string path = Path.Combine(_folder, "clip.mp4");

        var psi = new System.Diagnostics.ProcessStartInfo(Ffmpeg!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
        };

        foreach (string a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", $"testsrc=size=320x180:rate=25:duration={seconds:0.###}",
                     "-pix_fmt", "yuv420p", path,
                 })
            psi.ArgumentList.Add(a);

        using var ff = System.Diagnostics.Process.Start(psi)!;
        ff.StandardError.ReadToEnd();
        ff.WaitForExit();

        return path;
    }

    /// <summary>Every frame of the media comes through, in order, and then it says it is finished.</summary>
    [Fact]
    public void Every_frame_arrives_and_then_it_is_finished()
    {
        if (Ffmpeg is null) return;

        using var ahead = new FrameReadahead(FrameSource.Open(Ffmpeg, Clip(2), Format), Format.FrameBytes, 10);

        int frames = 0;

        while (true)
        {
            if (ahead.TryTake(out byte[] frame, TimeSpan.FromSeconds(5)))
            {
                Assert.Equal(Format.FrameBytes, frame.Length);
                ahead.Return(frame);
                frames++;
                continue;
            }

            if (ahead.Finished) break;
        }

        Assert.Equal(50, frames);
    }

    /// <summary>
    /// The point of it: frames decoded before they are asked for, so the first ones of a cue are
    /// in hand the moment it cuts in.
    /// </summary>
    [Fact]
    public void Frames_are_decoded_before_they_are_asked_for()
    {
        if (Ffmpeg is null) return;

        using var ahead = new FrameReadahead(FrameSource.Open(Ffmpeg, Clip(2), Format), Format.FrameBytes, 10);

        ahead.WaitFor(10, TimeSpan.FromSeconds(10));

        Assert.Equal(10, ahead.Buffered);
    }

    /// <summary>A queue that is full and not drained must not lose frames or run the decoder away.</summary>
    [Fact]
    public void A_full_queue_waits_for_buffers_to_come_back()
    {
        if (Ffmpeg is null) return;

        using var ahead = new FrameReadahead(FrameSource.Open(Ffmpeg, Clip(2), Format), Format.FrameBytes, 4);

        ahead.WaitFor(4, TimeSpan.FromSeconds(10));
        Thread.Sleep(200);

        Assert.Equal(4, ahead.Buffered);
        Assert.False(ahead.Finished);
    }
}
