using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The gate that stops the wrong picture reaching the card.
///
/// This matters more than it looks. `VideoFormat.ForFrameRate` ends in a silent fallback to
/// 1080p25, which is correct where it is used — the EDL scales everything through ffmpeg —
/// and catastrophic on a raw path, where a 720p frame would be copied into the top of a
/// 1080-line raster and transmitted as garbage. Every standard the SDK can report is checked
/// here, so no new one can quietly acquire a fallback.
/// </summary>
public class DelayFormatsTests
{
    [Theory]
    [InlineData(8, 24)]     // 1080p24
    [InlineData(0, 25)]     // 1080p25
    [InlineData(1, 30)]     // 1080p30
    [InlineData(10, 50)]    // 1080p50
    [InlineData(9, 60)]     // 1080p60
    public void Progressive_1080_is_transmitted_as_itself(uint std, int rate)
    {
        CaptureFormat capture = CaptureFormat.FromStandard(std, 25);

        Assert.True(DelayFormats.TryMatch(capture, out var format, out string? problem), problem);
        Assert.NotNull(format);
        Assert.Equal(1920, format!.Width);
        Assert.Equal(1080, format.Height);
        Assert.Equal(rate, format.FrameRate);
    }

    [Theory]
    [InlineData(2)]     // 1080i50 - 1920x1080 counted at 25, indistinguishable by raster alone
    [InlineData(3)]     // 1080i60
    public void Interlaced_is_refused_rather_than_sent_as_progressive(uint std)
    {
        CaptureFormat capture = CaptureFormat.FromStandard(std, 25);

        // The trap: raster and rate match a progressive standard exactly.
        Assert.Equal(1920, capture.Width);
        Assert.Equal(1080, capture.Height);
        Assert.True(capture.IsInterlaced);

        Assert.False(DelayFormats.TryMatch(capture, out var format, out string? problem));
        Assert.Null(format);
        Assert.Contains("interlaced", problem);
    }

    [Theory]
    [InlineData(4)]     // 720p50
    [InlineData(5)]     // 720p60
    [InlineData(6)]     // PAL
    [InlineData(7)]     // NTSC
    public void A_raster_the_transmitter_cannot_carry_is_refused(uint std)
    {
        CaptureFormat capture = CaptureFormat.FromStandard(std, 25);

        Assert.False(DelayFormats.TryMatch(capture, out var format, out string? problem));
        Assert.Null(format);
        Assert.False(string.IsNullOrWhiteSpace(problem));
        Assert.Contains(capture.Name, problem);
    }

    [Fact]
    public void An_unknown_standard_is_refused_rather_than_assumed_to_be_1080p25()
    {
        // FromStandard's fallback arm invents 1920x1080 at whatever rate it was handed. That
        // guess must not be allowed to reach the card.
        CaptureFormat capture = CaptureFormat.FromStandard(99, 25);

        Assert.Equal(1920, capture.Width);
        Assert.Equal(25, capture.FrameRate);

        Assert.False(DelayFormats.TryMatch(capture, out _, out string? problem));
        Assert.Contains("does not recognise", problem);
    }

    [Fact]
    public void Every_standard_the_sdk_can_report_is_either_matched_or_explained()
    {
        for (uint std = 0; std <= 12; std++)
        {
            CaptureFormat capture = CaptureFormat.FromStandard(std, 25);
            bool ok = DelayFormats.TryMatch(capture, out var format, out string? problem);

            if (ok) Assert.NotNull(format);
            else Assert.False(string.IsNullOrWhiteSpace(problem), $"standard {std} refused without a reason");
        }
    }

    [Fact]
    public void A_format_built_by_hand_without_a_standard_is_refused()
    {
        // Nothing should be able to sidestep the table by constructing a CaptureFormat.
        var invented = new CaptureFormat("made up", 1920, 1080, 25);

        Assert.Equal(CaptureFormat.UnknownStandard, invented.Standard);
        Assert.False(DelayFormats.TryMatch(invented, out _, out _));
    }
}
