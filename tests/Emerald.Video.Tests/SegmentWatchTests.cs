using Emerald.Media;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// Noticing a segment the moment ffmpeg finishes it, and not a moment before.
///
/// The test is done against files ffmpeg actually wrote rather than hand-made atoms, because
/// the thing being relied on — that the movie header is the last thing written — is a fact
/// about ffmpeg, not about this code.
/// </summary>
public sealed class SegmentWatchTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "emerald-segments", Guid.NewGuid().ToString("N"));
    private readonly string _low;
    private readonly string _high;

    private static string? Ffmpeg => Emerald.Video.Ffmpeg.Locate(null);

    public SegmentWatchTests()
    {
        _low = RecordingProfile.FolderFor(RecordingProfile.LowRes, _root);
        _high = RecordingProfile.FolderFor(RecordingProfile.HighRes, _root);
        Directory.CreateDirectory(_low);
        Directory.CreateDirectory(_high);
    }

    /// <summary>A real, closed file of the given length, straight from ffmpeg.</summary>
    private static void Encode(string path, double seconds)
    {
        var psi = new System.Diagnostics.ProcessStartInfo(Ffmpeg!)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        };
        foreach (string a in new[]
                 {
                     "-hide_banner", "-loglevel", "error", "-y",
                     "-f", "lavfi", "-i", $"testsrc=size=64x48:rate=25:duration={seconds:0.###}",
                     "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", path,
                 })
            psi.ArgumentList.Add(a);

        using var p = System.Diagnostics.Process.Start(psi)!;
        p.WaitForExit();
    }

    /// <summary>What a segment looks like while it is still being written: no movie header.</summary>
    private static void Truncate(string path, double keep)
    {
        byte[] bytes = File.ReadAllBytes(path);
        File.WriteAllBytes(path, bytes[..(int)(bytes.Length * keep)]);
    }

    [Fact]
    public void A_finished_segment_is_announced_with_its_length_and_both_sizes()
    {
        if (Ffmpeg is null) return;

        string proxy = Path.Combine(_low, "clip_2026-09-18_12-00-00.mp4");
        string master = Path.Combine(_high, "clip_2026-09-18_12-00-00.mov");
        Encode(proxy, 2);
        Encode(master, 2);

        var watch = new SegmentWatch(_root, "clip", DateTime.UtcNow.AddMinutes(-1));
        IReadOnlyList<CompletedSegment> done = watch.Poll();

        CompletedSegment segment = Assert.Single(done);
        Assert.Equal(1, segment.Number);
        Assert.Equal(master, segment.MasterPath);
        Assert.InRange(segment.Duration!.Value.TotalSeconds, 1.9, 2.1);
        Assert.Equal(new FileInfo(proxy).Length, segment.ProxyBytes);
        Assert.Equal(new FileInfo(master).Length, segment.MasterBytes);
    }

    [Fact]
    public void A_segment_still_being_written_is_not_announced_and_is_announced_once_finished()
    {
        if (Ffmpeg is null) return;

        string proxy = Path.Combine(_low, "clip_2026-09-18_12-02-00.mp4");
        Encode(proxy, 1);
        byte[] whole = File.ReadAllBytes(proxy);
        Truncate(proxy, 0.6);

        var watch = new SegmentWatch(_root, "clip", DateTime.UtcNow.AddMinutes(-1));

        Assert.Empty(watch.Poll());

        File.WriteAllBytes(proxy, whole);

        Assert.Single(watch.Poll());
        Assert.Empty(watch.Poll());   // and never twice
    }

    /// <summary>The master closes a moment after the proxy. Announcing then would halve the size.</summary>
    [Fact]
    public void A_segment_waits_for_its_master_to_close()
    {
        if (Ffmpeg is null) return;

        string proxy = Path.Combine(_low, "clip_2026-09-18_12-04-00.mp4");
        string master = Path.Combine(_high, "clip_2026-09-18_12-04-00.mov");
        Encode(proxy, 1);
        Encode(master, 1);
        byte[] whole = File.ReadAllBytes(master);
        Truncate(master, 0.6);

        var watch = new SegmentWatch(_root, "clip", DateTime.UtcNow.AddMinutes(-1));

        Assert.Empty(watch.Poll());

        File.WriteAllBytes(master, whole);

        Assert.Equal(whole.Length, Assert.Single(watch.Poll()).MasterBytes);
    }

    [Fact]
    public void Files_from_before_the_recording_or_under_another_name_are_not_this_recordings()
    {
        if (Ffmpeg is null) return;

        string old = Path.Combine(_low, "clip_2026-09-17_09-00-00.mp4");
        string other = Path.Combine(_low, "news_2026-09-18_12-00-00.mp4");
        Encode(old, 1);
        Encode(other, 1);
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-3));

        var watch = new SegmentWatch(_root, "clip", DateTime.UtcNow.AddMinutes(-1));

        Assert.Empty(watch.Poll());
    }

    [Fact]
    public void The_duration_reader_says_nothing_about_a_file_without_its_header()
    {
        if (Ffmpeg is null) return;

        string path = Path.Combine(_root, "x.mp4");
        Encode(path, 1);
        Assert.NotNull(MediaCodec.ReadDuration(path));

        Truncate(path, 0.5);
        Assert.Null(MediaCodec.ReadDuration(path));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
