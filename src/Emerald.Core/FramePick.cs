namespace Emerald.Core;

/// <summary>
/// Naming a single frame of the day in writing.
///
/// At every zoom but the last, one pixel of the timeline is many frames — a minute of them at the
/// hour view — so there is no click that can land on a chosen frame, and no amount of care with
/// the mouse will change that. The way to reach an exact frame is to say which one, and this is
/// the parsing of what gets said.
///
/// Four things an operator might type, because all four are what they already have in front of
/// them: a timecode off the clock, a frame number off the counter, a nudge relative to where the
/// playhead is, and a plain time with the frames left off.
/// </summary>
public static class FramePick
{
    /// <summary>
    /// Reads a frame of the day out of what was typed.
    ///
    /// <paramref name="current"/> is where the playhead is, which only matters for the relative
    /// forms. The result is clamped to the day, so a nudge off either end lands on the end rather
    /// than being refused — an operator holding a key down at midnight means the start of the day,
    /// not a mistake.
    /// </summary>
    public static bool TryParse(string? text, int rate, long dayFrames, long current,
                               out long frame, out string? error)
    {
        frame = current;
        error = null;

        string input = (text ?? "").Trim();

        if (input.Length == 0)
        {
            error = "Nothing typed.";
            return false;
        }

        if (rate <= 0 || dayFrames <= 0)
        {
            error = "Frame rate is unknown.";
            return false;
        }

        // Relative: "+50", "-2" — frames from here. Written first because a leading sign
        // otherwise reads as a malformed number and the operator gets told off for the one form
        // that is quickest to type.
        if (input[0] is '+' or '-')
        {
            string digits = input[1..].Trim();

            // "+1s" / "-2s": seconds rather than frames. The suffix is there because two
            // seconds is a thought an operator has and fifty frames is arithmetic.
            bool seconds = digits.EndsWith("s", StringComparison.OrdinalIgnoreCase);
            if (seconds) digits = digits[..^1].Trim();

            if (!long.TryParse(digits, out long by) || by < 0)
            {
                error = "Expected +frames or -frames.";
                return false;
            }

            if (seconds) by *= rate;

            frame = Clamp(input[0] == '-' ? current - by : current + by, dayFrames);
            return true;
        }

        // A bare number: a frame of the day. Unambiguous, because a timecode always has colons.
        if (!input.Contains(':') && !input.Contains(';') && !input.Contains('.'))
        {
            if (!long.TryParse(input, out long absolute) || absolute < 0)
            {
                error = "Expected a frame number, a timecode, or +/- frames.";
                return false;
            }

            if (absolute >= dayFrames)
            {
                error = $"There are only {dayFrames:N0} frames in the day.";
                return false;
            }

            frame = absolute;
            return true;
        }

        // A full timecode, which is what the plant speaks.
        if (Timecode.TryParse(input, rate, out Timecode at, out string? why))
        {
            frame = Clamp(at.TotalFrames, dayFrames);
            return true;
        }

        // A time with the frames left off — "10:23:45", or "23:45" meaning minutes and seconds
        // past the current hour. Accepted because leaving the frames off is what somebody reading
        // a time off a clock or a log does, and refusing it taught them nothing.
        if (TryPartial(input, rate, dayFrames, current, out frame)) return true;

        error = why ?? "Expected HH:MM:SS:FF.";
        return false;
    }

    /// <summary>
    /// A timecode with pieces missing, filled in from the right: "45" is seconds, "23:45" is
    /// minutes and seconds, "10:23:45" is the hour too. Whatever is not given comes from where
    /// the playhead already is, so a partial time means "the nearest one to here".
    /// </summary>
    private static bool TryPartial(string input, int rate, long dayFrames, long current,
                                   out long frame)
    {
        frame = current;

        string[] parts = input.Split(new[] { ':', ';', '.' }, StringSplitOptions.None);

        if (parts.Length is < 2 or > 3) return false;

        var n = new int[parts.Length];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out n[i]) || n[i] < 0) return false;

        var here = new Timecode(current, rate);

        int hours = parts.Length == 3 ? n[0] : here.Hours;
        int minutes = parts.Length == 3 ? n[1] : n[0];
        int seconds = parts.Length == 3 ? n[2] : n[1];

        if (hours > 23 || minutes > 59 || seconds > 59) return false;

        frame = Clamp(((hours * 60L + minutes) * 60L + seconds) * rate, dayFrames);
        return true;
    }

    private static long Clamp(long frame, long dayFrames) =>
        Math.Clamp(frame, 0, dayFrames - 1);
}
