using Emerald.Core;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// What the proxy is encoded as.
///
/// <b>Why these are worth pinning.</b> The proxy is not a stand-in for something better the
/// operator can also watch: nothing in the application can decode the DNxHD master, so the proxy
/// is the picture Live Edit scrubs, every monitor shows, and every edit is judged on. A silent
/// regression in any of these settings degrades every recording from then on, and would be
/// noticed only as somebody saying the video looks soft.
///
/// Measured on this plant's ffmpeg before the settings were chosen: the old arguments produced a
/// 960x540 Constrained Baseline picture at 3.34 Mbps in 3.0 s per 30 s of video; these produce
/// 1920x1080 High profile at 5.38 Mbps in 2.3 s. Four times the pixels, and faster, because the
/// old path also had to software-scale every frame.
/// </summary>
public sealed class ProxyQualityTests
{
    private static List<string> ProxyArgs() => RecordingProfile.From(new AppSettings
        {
            CaptureFrameRate = 25,
            RecordingSegmentSeconds = 30,
            RecordingAudioSampleRate = 48000,
        })
        .EncoderArguments(
            new CaptureFormat("1080i50", 1920, 1080, 25),
            pipeName: "emerald-test",
            folder: "C:\\store",
            namePrefix: "media",
            inputSampleRate: 48000,
            names: RecordingSequence.Next(new DateTime(2026, 10, 12, 9, 0, 0), "media"))
        .ToList();

    /// <summary>The value following a flag on the command, or null.</summary>
    private static string? After(List<string> args, string flag)
    {
        int at = args.IndexOf(flag);
        return at >= 0 && at + 1 < args.Count ? args[at + 1] : null;
    }

    [Fact]
    public void The_proxy_is_not_scaled_down()
    {
        // No scale filter at all: the proxy is the receiver's own raster.
        Assert.DoesNotContain(ProxyArgs(), a => a.StartsWith("scale=", StringComparison.Ordinal));
    }

    [Fact]
    public void The_proxy_uses_a_preset_that_leaves_cabac_on()
    {
        // ultrafast turns off CABAC and most of the motion search. That is what made the old
        // proxy Constrained Baseline, and what spent 3.34 Mbps on a half-size picture.
        Assert.Equal("veryfast", After(ProxyArgs(), "-preset"));
    }

    [Fact]
    public void The_proxy_is_high_profile()
    {
        Assert.Equal("high", After(ProxyArgs(), "-profile:v"));
    }

    [Fact]
    public void The_proxy_carries_no_b_frames()
    {
        // Deliberate, and the one thing kept from the old settings. B-frames reorder the
        // stream, and stepping backwards a frame at a time through reordered pictures is what
        // makes an edit timeline lurch. The bitrate they would save is not worth it here.
        Assert.Equal("0", After(ProxyArgs(), "-bf"));
    }

    [Fact]
    public void The_proxy_is_seekable_half_a_second_at_a_time()
    {
        // The reason the proxy exists is to be scrubbed, and a player can only land on a
        // keyframe. Measured on this plant's own store, the original settings gave one keyframe
        // every eleven seconds.
        List<string> args = ProxyArgs();

        Assert.Equal("12", After(args, "-g"));           // 25 fps, so about half a second
        Assert.Equal("0", After(args, "-sc_threshold")); // on the interval, not on scene cuts
    }

    [Fact]
    public void The_low_latency_tuning_is_gone()
    {
        // -tune zerolatency was imposing no-B-frames as a side effect, along with no lookahead
        // and a worse rate control, for a latency guarantee a file writer does not need.
        Assert.DoesNotContain("zerolatency", ProxyArgs());
    }

    [Fact]
    public void The_master_is_untouched_by_any_of_this()
    {
        // The master is the thing that will be transmitted or handed on. None of the proxy's
        // settings may reach it.
        List<string> args = ProxyArgs();

        Assert.Contains("dnxhd", args);
        Assert.Contains("145M", args);
        Assert.Contains("yuv422p", args);
    }
}
