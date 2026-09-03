using Emerald.Core;
using Xunit;

namespace Emerald.Ingest.Tests;

/// <summary>
/// The shape a timecode field is always in.
///
/// Keystroke handling needs a live TextBox and cannot be exercised here, but every path
/// through the mask ends by putting the text into this one canonical form — so a field that
/// could be left holding something else is a field an operator could be shown something
/// that is not a timecode.
/// </summary>
public class TimecodeMaskTests
{
    [Theory]
    [InlineData("", "00:00:00:00")]                     // a freshly built TextBox
    [InlineData("   ", "00:00:00:00")]
    [InlineData("--:--:--:--", "00:00:00:00")]          // the old placeholder
    [InlineData("open-ended", "00:00:00:00")]           // the old open-ended wording
    [InlineData("00:00:00:00", "00:00:00:00")]
    [InlineData("20:57:26:00", "20:57:26:00")]
    [InlineData("00:01:00:00", "00:01:00:00")]
    public void Any_text_becomes_a_full_width_timecode(string input, string expected) =>
        Assert.Equal(expected, TimecodeMask.Canonical(input));

    [Fact]
    public void A_partial_value_set_in_code_is_padded_out_rather_than_left_ragged() =>
        Assert.Equal("10:00:00:00", TimecodeMask.Canonical("10"));

    [Fact]
    public void More_than_eight_digits_are_cut_rather_than_wrapped() =>
        Assert.Equal("12:34:56:78", TimecodeMask.Canonical("123456789012"));

    [Fact]
    public void Separators_of_any_kind_are_ignored_in_favour_of_the_digits() =>
        Assert.Equal("01:02:03:04", TimecodeMask.Canonical("01;02.03,04"));

    [Fact]
    public void The_result_is_always_eleven_characters()
    {
        foreach (string input in new[] { "", "1", "999", "20:57:26:00", "abc", "1234567890123" })
            Assert.Equal(11, TimecodeMask.Canonical(input).Length);
    }

    [Fact]
    public void Canonical_output_always_parses()
    {
        foreach (string input in new[] { "", "0", "00:01:00:00", "23:59:59:24" })
        {
            string canonical = TimecodeMask.Canonical(input);
            Assert.True(Timecode.TryParse(canonical, 25, out _, out string? error), $"{canonical}: {error}");
        }
    }

    [Fact]
    public void Canonical_is_idempotent()
    {
        foreach (string input in new[] { "", "7", "20:57:26:00", "--:--:--:--" })
        {
            string once = TimecodeMask.Canonical(input);
            Assert.Equal(once, TimecodeMask.Canonical(once));
        }
    }

    [Fact]
    public void An_out_of_range_value_is_kept_and_left_for_the_caller_to_reject()
    {
        // The mask never silently corrects a number somebody typed - 99 minutes stays 99
        // minutes, and shows up as an inline error rather than as a different timecode.
        Assert.Equal("99:99:99:99", TimecodeMask.Canonical("99999999"));
        Assert.False(Timecode.TryParse("99:99:99:99", 25, out _, out _));
    }
}
