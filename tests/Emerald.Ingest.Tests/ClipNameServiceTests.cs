using Emerald.Core;
using Xunit;

namespace Emerald.Ingest.Tests;

public class ClipNameServiceTests
{
    private readonly ClipNameService _names = new();

    [Fact]
    public void A_generated_name_carries_the_date_the_time_and_the_frame()
    {
        var at = new DateTime(2026, 9, 2, 14, 50, 11, 800);
        var timecode = new Timecode(0, 25).AddFrames(20);   // frame 20

        Assert.Equal("CLIP_20260902_14501120", _names.Generate(timecode, at));
    }

    [Fact]
    public void Without_a_clock_the_tail_comes_from_the_wall_time()
    {
        var at = new DateTime(2026, 9, 2, 14, 50, 11, 800);
        Assert.Equal("CLIP_20260902_14501180", _names.Generate(null, at));
    }

    [Fact]
    public void A_generated_name_is_always_a_valid_one() =>
        Assert.True(_names.IsValid(_names.Generate(), out _));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("clip/name")]
    [InlineData("clip:name")]
    [InlineData("clip%name")]      // ffmpeg reads % as a format specifier in an output path
    [InlineData("trailing dot.")]
    public void Names_a_filename_cannot_hold_are_refused(string name)
    {
        Assert.False(_names.IsValid(name, out string? error));
        Assert.False(string.IsNullOrWhiteSpace(error));
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_rather_than_refused()
    {
        // The form hands the name over trimmed, so a stray space either side is not an
        // error the operator should be stopped for - a trailing dot still is.
        Assert.True(_names.IsValid("  CLIP_TEST  ", out _));
        Assert.False(_names.IsValid("  CLIP_TEST.  ", out _));
    }

    [Fact]
    public void An_overlong_name_is_refused_rather_than_quietly_truncated()
    {
        Assert.False(_names.IsValid(new string('a', ClipNameService.MaxLength + 1), out string? error));
        Assert.Contains("longer than", error);
    }

    [Fact]
    public void Sanitising_replaces_what_it_cannot_keep_and_caps_the_length()
    {
        Assert.Equal("clip_name", _names.Sanitise("clip/name"));
        Assert.Equal(ClipNameService.MaxLength, _names.Sanitise(new string('a', 200)).Length);
        Assert.Equal("", _names.Sanitise("   "));
    }
}
