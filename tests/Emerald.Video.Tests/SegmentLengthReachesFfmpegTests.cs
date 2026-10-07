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

    /// <summary>The value given to ffmpeg's -segment_time, read off the command itself.</summary>
    private static string SegmentTimeArgument(RecordingProfile profile)
    {
        List<string> args = profile.EncoderArguments(
            new CaptureFormat("1080i50", 1920, 1080, 25),
            pipeName: "emerald-test",
            folder: "C:\\store",
            namePrefix: "media",
            inputSampleRate: 48000,
            names: RecordingSequence.Next(new DateTime(2026, 10, 8, 9, 0, 0), "media")).ToList();

        int at = args.IndexOf("-segment_time");

        Assert.True(at >= 0 && at + 1 < args.Count, "the command has no -segment_time");

        return args[at + 1];
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
}
