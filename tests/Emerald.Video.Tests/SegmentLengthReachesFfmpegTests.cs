using Emerald.Core;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// That the segment length in the settings is the one ffmpeg is actually given.
///
/// The gap this covers: <see cref="RecordingProfile.From"/> runs the stored value through a list
/// of offered lengths and falls back when it is not on that list. Thirty seconds was not on it,
/// so a settings file asking for thirty would have been quietly overruled by the fallback — and
/// the fallback was two minutes. Both ends of that are pinned here.
/// </summary>
public sealed class SegmentLengthReachesFfmpegTests
{
    private static RecordingProfile For(int seconds) => RecordingProfile.From(new AppSettings
    {
        CaptureFrameRate = 25,
        RecordingSegmentSeconds = seconds,
        RecordingAudioSampleRate = 48000,
    });

    /// <summary>The proxy's -segment_time, read off the command itself. The proxy is the first output.</summary>
    private static string SegmentTimeArgument(RecordingProfile profile) => SegmentTimes(profile)[0];

    /// <summary>Every -segment_time on the command, in output order: proxy, then master.</summary>
    private static List<string> SegmentTimes(RecordingProfile profile)
    {
        List<string> args = profile.EncoderArguments(
            new CaptureFormat("1080i50", 1920, 1080, 25),
            pipeName: "emerald-test",
            folder: "C:\\store",
            namePrefix: "media",
            inputSampleRate: 48000,
            names: RecordingSequence.Next(new DateTime(2026, 10, 8, 9, 0, 0), "media")).ToList();

        List<string> found = args
            .Select((a, i) => (a, i))
            .Where(p => p.a == "-segment_time" && p.i + 1 < args.Count)
            .Select(p => args[p.i + 1])
            .ToList();

        Assert.True(found.Count == 2, $"expected a -segment_time for each output, found {found.Count}");

        return found;
    }

    [Fact]
    public void Thirty_seconds_is_an_offered_length()
    {
        // If it is not on this list, Pick discards it and the fallback wins.
        Assert.Contains(30, RecordingProfile.SegmentLengths.Select(o => o.Value));
    }

    [Fact]
    public void The_fallback_length_is_thirty_seconds()
    {
        Assert.Equal(30, RecordingProfile.Default.SegmentSeconds);
    }

    [Fact]
    public void A_setting_of_thirty_seconds_survives_into_the_profile()
    {
        Assert.Equal(30, For(30).SegmentSeconds);
    }

    [Fact]
    public void Thirty_seconds_is_what_reaches_ffmpeg()
    {
        // The end of the chain, and the only assertion here that could not pass while the
        // recorder still cut two-minute files.
        Assert.Equal("30", SegmentTimeArgument(For(30)));
    }

    [Fact]
    public void Another_offered_length_still_reaches_ffmpeg_unchanged()
    {
        Assert.Equal("300", SegmentTimeArgument(For(300)));
    }

    [Fact]
    public void A_length_nobody_offers_falls_back_to_thirty_rather_than_reaching_ffmpeg()
    {
        // A hand-edited or corrupt settings file must not put an arbitrary number on the
        // command line.
        Assert.Equal("30", SegmentTimeArgument(For(7)));
        Assert.Equal("30", SegmentTimeArgument(For(0)));
        Assert.Equal("30", SegmentTimeArgument(For(-1)));
    }

    [Theory]
    [InlineData(10)]
    [InlineData(30)]
    [InlineData(120)]
    [InlineData(300)]
    public void The_master_is_cut_at_five_minutes_whatever_the_proxy_is_set_to(int proxy)
    {
        List<string> times = SegmentTimes(For(proxy));

        Assert.Equal(proxy.ToString(), times[0]);
        Assert.Equal("300", times[1]);
    }

    [Fact]
    public void No_proxy_length_on_offer_is_longer_than_a_master()
    {
        // A proxy longer than a master would straddle several of them, and the export and the
        // storage limit both count on it lying inside at most two.
        Assert.All(RecordingProfile.SegmentLengths,
                   o => Assert.InRange(o.Value, 1, RecordingProfile.MasterSegmentSeconds));
    }

    [Fact]
    public void A_saved_ten_minute_proxy_falls_back_rather_than_outrunning_the_master()
    {
        Assert.Equal("30", SegmentTimeArgument(For(600)));
    }
}
