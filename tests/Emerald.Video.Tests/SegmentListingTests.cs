using System.Diagnostics;
using Emerald.Media;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// Listing the store without a process per file. The container read is only worth having if it
/// says what ffprobe says, so each is checked against ffprobe on a real file. Skipped quietly on a
/// machine without ffmpeg, as the other tests that need it are.
/// </summary>
public sealed class SegmentListingTests : IDisposable
{
    private static string? Ffmpeg => Emerald.Video.Ffmpeg.Locate(null);
    private static string? Ffprobe => MediaProbe.LocateFfprobe(Ffmpeg);

    private readonly string _folder = Path.Combine(Path.GetTempPath(), "emerald-list-" + Guid.NewGuid().ToString("N"));

    public SegmentListingTests() => Directory.CreateDirectory(_folder);

    public void Dispose() => Directory.Delete(_folder, recursive: true);

    private string Make(string name, string arguments)
    {
        string path = Path.Combine(_folder, name);

        var info = new ProcessStartInfo(Ffmpeg!, $"-hide_banner -loglevel error -y {arguments} \"{path}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using Process ffmpeg = Process.Start(info)!;
        ffmpeg.WaitForExit();
        Assert.Equal(0, ffmpeg.ExitCode);

        return path;
    }

    private const string Picture = "-f lavfi -i testsrc2=s=320x180:r=25 -t 1";
    private const string Sound = "-f lavfi -i sine=r=48000 -t 1";

    [Theory]
    [InlineData("-c:v libx264 -pix_fmt yuv420p -c:a aac -ac 2")]
    [InlineData("-c:v libx264 -pix_fmt yuv420p -c:a aac -ac 1")]
    [InlineData("-c:v libx265 -tag:v hvc1 -pix_fmt yuv420p -c:a aac -ac 2")]
    public void The_container_read_says_what_ffprobe_says(string codecs)
    {
        if (Ffprobe is null) return;

        string path = Make("a.mp4", $"{Picture} {Sound} {codecs}");

        MediaCodec.SegmentLayout read = MediaCodec.ReadSegment(path)!;
        MediaInfo probed = MediaProbe.Probe(Ffprobe!, path, 25)!;

        Assert.Equal(probed.VideoCodec, read.VideoCodec);
        Assert.Equal(probed.Width, read.Width);
        Assert.Equal(probed.Height, read.Height);
        Assert.Equal(probed.HasAudio, read.HasAudio);
        Assert.Equal(probed.Audio[0].Channels, read.AudioChannels);
        Assert.Equal(probed.Audio[0].Language, read.AudioLanguage);
        Assert.Equal(probed.Duration.TotalSeconds, read.Duration.TotalSeconds, 2);
        Assert.Equal(probed.Runs.TotalSeconds, read.Picture.TotalSeconds, 2);
    }

    [Fact]
    public void A_silent_segment_is_read_as_silent()
    {
        if (Ffprobe is null) return;

        MediaCodec.SegmentLayout read = MediaCodec.ReadSegment(Make("a.mp4", $"{Picture} -c:v libx264 -pix_fmt yuv420p"))!;

        Assert.False(read.HasAudio);
    }

    [Fact]
    public void Several_audio_tracks_are_left_to_ffprobe()
    {
        if (Ffprobe is null) return;

        string path = Make("a.mp4", $"{Picture} {Sound} {Sound} -map 0 -map 1 -map 2 -c:v libx264 -pix_fmt yuv420p -c:a aac");

        Assert.Null(MediaCodec.ReadSegment(path));
    }

    [Fact]
    public void A_timecode_track_is_left_to_ffprobe()
    {
        if (Ffprobe is null) return;

        string path = Make("a.mp4", $"{Picture} -c:v libx264 -pix_fmt yuv420p -timecode 01:00:00:00");

        Assert.Null(MediaCodec.ReadSegment(path));
    }

    [Fact]
    public void A_codec_it_does_not_know_is_left_to_ffprobe()
    {
        if (Ffprobe is null) return;

        Assert.Null(MediaCodec.ReadSegment(Make("a.mov", $"{Picture} -c:v dnxhd -b:v 36M -s 1920x1080 -pix_fmt yuv422p")));
    }

    [Fact]
    public void A_segment_still_being_written_has_nothing_to_read()
    {
        string path = Path.Combine(_folder, "open.mp4");

        // ftyp and the start of mdat, as the recorder leaves them until it closes the file.
        byte[] open = new byte[64];
        new byte[] { 0, 0, 0, 24, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }.CopyTo(open, 0);
        new byte[] { 0, 0, 0, 40, (byte)'m', (byte)'d', (byte)'a', (byte)'t' }.CopyTo(open, 24);
        File.WriteAllBytes(path, open);

        Assert.Null(MediaCodec.ReadSegment(path));
    }

    [Fact]
    public void A_file_gone_from_under_a_listing_is_null_and_not_the_end_of_it()
    {
        // The storage limit deletes the oldest segment, which is the first one a listing reads.
        // This used to throw out of the loop, and took every file after it along.
        Assert.Null(MediaLibrary.ClipAt(Path.Combine(_folder, "gone.mp4"), null, null, 25));
    }
}
