using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The mastering codec.
///
/// Emerald wrote ProRes 422 masters and now writes DNxHD 145. DNxHD is not a quality setting
/// with a codec attached — it is a table of frame size, rate and bitrate combinations, and the
/// bitrate <i>is</i> the tier. So what matters here is that the arguments name exactly the
/// combination that is in the table, and that a raster the table does not contain is refused
/// in words rather than by an encoder dying a few frames in.
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
    public void The_master_is_written_as_DNxHD_at_one_four_five()
    {
        Assert.Equal("dnxhd", RecordingProfile.HighRes.VideoCodec);
        Assert.Equal("145M", RecordingProfile.HighRes.VideoBitrate);
        Assert.Equal("DNxHD 145", RecordingProfile.HighRes.VideoLabel);
    }

    [Fact]
    public void No_profile_is_named_because_that_would_select_DNxHR_instead()
    {
        // dnxhd is the encoder for both families. Naming a profile puts it into DNxHR, which
        // is a different codec with a different fourcc and no bitrate table at all.
        Assert.Null(RecordingProfile.HighRes.Profile);
    }

    [Fact]
    public void The_master_is_eight_bit_four_two_two()
    {
        Assert.Equal("yuv422p", RecordingProfile.HighRes.PixelFormat);
    }

    [Fact]
    public void The_bitrate_reaches_the_encoder()
    {
        List<string> args = Args();

        // The proxy is on the encoder's own rate control by default, so the master's is the
        // only -b:v in the command.
        Assert.Equal("145M", After(args, "-b:v"));
        Assert.Equal(1, args.Count(a => a == "-b:v"));
    }

    [Fact]
    public void The_proxy_is_left_exactly_as_it_was()
    {
        List<string> args = Args();

        Assert.Equal("libx264", RecordingProfile.LowRes.VideoCodec);
        Assert.Null(RecordingProfile.LowRes.VideoBitrate);
        Assert.Contains("libx264", args);
        Assert.Contains("ultrafast", args);
    }

    [Fact]
    public void A_chosen_proxy_bitrate_still_reaches_the_proxy_and_not_the_master()
    {
        List<string> args = new RecordingProfile(5000, 192, 48000, 120)
            .EncoderArguments(Hd, "pipe", @"C:\out", "clip", 48000, singleFile: true)
            .ToList();

        Assert.Contains("5000k", args);
        Assert.Contains("145M", args);
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
        // AVdn is DNxHD; AVdh would be DNxHR. Getting this wrong means a clip on disk is
        // labelled as the codec it is not.
        Assert.Equal("AVdn", RecordingProfile.HighRes.FourCc);
        Assert.Equal("avc1", RecordingProfile.LowRes.FourCc);
    }

    [Fact]
    public void Both_halves_are_still_written_from_one_pass()
    {
        List<string> args = Args();

        Assert.Equal(2, RecordingProfile.Outputs.Count);
        Assert.Equal(2, args.Count(a => a == "-c:v"));
        Assert.Equal(1, args.Count(a => a == "pipe:0"));
    }

    // ------------------------------------------------------------------ the raster table

    [Theory]
    [InlineData(1920, 1080)]
    [InlineData(1440, 1080)]
    [InlineData(1280, 720)]
    public void The_rasters_in_the_table_are_accepted(int width, int height)
    {
        Assert.True(RecordingProfile.HighRes.Supports(width, height));
    }

    [Theory]
    [InlineData(720, 576)]     // PAL
    [InlineData(720, 486)]     // NTSC
    [InlineData(3840, 2160)]   // UHD
    public void A_raster_outside_the_table_is_refused_by_name(int width, int height)
    {
        Assert.False(RecordingProfile.HighRes.Supports(width, height));

        string why = RecordingProfile.HighRes.Refuses(width, height);

        // The message has to name both what is supported and what arrived, or the operator
        // cannot tell what to change.
        Assert.Contains($"{width}x{height}", why);
        Assert.Contains("1920x1080", why);
    }

    [Fact]
    public void The_proxy_takes_any_raster_at_all()
    {
        // H.264 has no table. Only the master can refuse, which is why the check names the
        // output that did it rather than saying the recording is impossible.
        Assert.True(RecordingProfile.LowRes.Supports(720, 576));
        Assert.True(RecordingProfile.LowRes.Supports(3840, 2160));
    }
}
