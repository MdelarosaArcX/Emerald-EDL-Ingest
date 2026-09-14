using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The mastering codec.
///
/// Emerald wrote ProRes 422 masters and now writes DNxHR HQ. What matters here is that the
/// arguments ffmpeg is handed actually describe DNxHR — the encoder is picky enough that a
/// wrong profile or pixel format is a recording that fails on the first frame rather than a
/// file that looks slightly different.
/// </summary>
public sealed class RecordingMasterTests
{
    private static readonly CaptureFormat Hd = new("1080p25", 1920, 1080, 25, 0);

    private static List<string> Args() =>
        RecordingProfile.Default
            .EncoderArguments(Hd, "pipe", @"C:\out", "clip", 48000, singleFile: true)
            .ToList();

    /// <summary>The value immediately after a flag, taking the Nth occurrence.</summary>
    private static string? After(List<string> args, string flag, int occurrence = 0)
    {
        int seen = 0;

        for (int i = 0; i < args.Count - 1; i++)
        {
            if (args[i] != flag) continue;
            if (seen++ == occurrence) return args[i + 1];
        }

        return null;
    }

    [Fact]
    public void The_master_is_written_as_DNxHR()
    {
        Assert.Equal("dnxhd", RecordingProfile.HighRes.VideoCodec);
        Assert.Equal("dnxhr_hq", RecordingProfile.HighRes.Profile);
    }

    [Fact]
    public void The_master_is_eight_bit_four_two_two()
    {
        // dnxhr_hq is the 8-bit tier. Handing it yuv422p10le is how you silently get either a
        // refusal or the wrong tier; the 10-bit tier is a different profile name entirely.
        Assert.Equal("yuv422p", RecordingProfile.HighRes.PixelFormat);
    }

    [Fact]
    public void The_profile_reaches_the_encoder()
    {
        List<string> args = Args();

        // Two outputs, proxy first. The master's -profile:v is the second one, because the
        // proxy has none.
        Assert.Contains("-profile:v", args);
        Assert.Equal("dnxhr_hq", After(args, "-profile:v"));
    }

    [Fact]
    public void The_proxy_is_left_exactly_as_it_was()
    {
        List<string> args = Args();

        // Nothing about this change touches the proxy: it is still half-size H.264, and it is
        // what the clip strip lists and the stage plays.
        Assert.Equal("libx264", RecordingProfile.LowRes.VideoCodec);
        Assert.Null(RecordingProfile.LowRes.Profile);
        Assert.Contains("libx264", args);
        Assert.Contains("ultrafast", args);
    }

    [Fact]
    public void The_master_is_still_full_raster_and_the_proxy_still_half()
    {
        Assert.False(RecordingProfile.HighRes.HalfSize);
        Assert.True(RecordingProfile.LowRes.HalfSize);
    }

    [Fact]
    public void Each_output_declares_the_code_it_writes()
    {
        // What lets a file on disk be recognised later without probing it.
        Assert.Equal("AVdh", RecordingProfile.HighRes.FourCc);
        Assert.Equal("avc1", RecordingProfile.LowRes.FourCc);
    }

    [Fact]
    public void Both_halves_are_still_written_from_one_pass()
    {
        List<string> args = Args();

        // One receiver read once, two files out. Two passes would mean the pair were not
        // frame-for-frame the same picture, which is the whole basis of proxy editing.
        Assert.Equal(2, RecordingProfile.Outputs.Count);
        Assert.Equal(2, args.Count(a => a == "-c:v"));
        Assert.Equal(1, args.Count(a => a == "pipe:0"));
    }
}
