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
    /// <paramref name="current"/> is where the playhead is, which matters for the relative forms
    /// and for which day a time means. The result is clamped to the day, so a nudge off either end
    /// lands on the end rather than being refused — an operator holding a key down at midnight
    /// means the start of the day, not a mistake.
    ///
    /// <b>A recording that runs past midnight makes the ruler longer than a day</b>, and a time
    /// then names more than one frame, because the ruler reads 00:10:00:00 at both ends. It means
    /// the one nearest <paramref name="current"/>, so what is read off the ruler lands where it
    /// was read. <c>24:10:00:00</c> names the later one outright, where the ruler reaches that far.
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

        // Hours past 23: a place on a ruler that has run into the next day, stated outright.
        if (TryPastMidnight(input, rate, dayFrames, out frame, out string? beyond))
        {
            if (beyond is null) return true;

            frame = current;
            error = beyond;
            return false;
        }

        frame = current;

        // A full timecode, which is what the plant speaks.
        if (Timecode.TryParse(input, rate, out Timecode at, out string? why))
        {
            frame = Nearest(at.TotalFrames, rate, dayFrames, current);
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

        int minutes = parts.Length == 3 ? n[1] : n[0];
        int seconds = parts.Length == 3 ? n[2] : n[1];

        if (minutes > 59 || seconds > 59) return false;

        // Minutes and seconds alone stay in the hour the playhead is in, past midnight as well.
        if (parts.Length == 2)
        {
            long hour = new Timecode(current, rate).Hours;

            frame = Clamp(((hour * 60L + minutes) * 60L + seconds) * rate, dayFrames);
            return true;
        }

        if (n[0] > 23) return false;

        frame = Nearest(((n[0] * 60L + minutes) * 60L + seconds) * rate, rate, dayFrames, current);
        return true;
    }

    /// <summary>
    /// A timecode whose hours run past 23, on a ruler that goes that far. False when it is not that
    /// form, and true with <paramref name="error"/> set when it is but the ruler stops short.
    /// </summary>
    private static bool TryPastMidnight(string input, int rate, long dayFrames, out long frame, out string? error)
    {
        frame = 0;
        error = null;

        string[] parts = input.Split(new[] { ':', ';', '.' }, StringSplitOptions.None);

        if (parts.Length is < 3 or > 4) return false;

        var n = new int[4];
        for (int i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out n[i]) || n[i] < 0) return false;

        // Past 99 is a slip of the finger rather than a place on a ruler four days long.
        if (n[0] is < 24 or > 99 || n[1] > 59 || n[2] > 59 || n[3] >= rate) return false;

        long at = ((n[0] * 60L + n[1]) * 60L + n[2]) * rate + n[3];

        if (at >= dayFrames)
        {
            error = $"The timeline ends at {new Timecode(dayFrames - 1, rate)}.";
            return true;
        }

        frame = at;
        return true;
    }

    /// <summary>
    /// Which of the times a ruler longer than a day reads the same is closest to
    /// <paramref name="current"/>. A ruler of one day has only the one. Exactly twelve hours
    /// either way goes forward, where edits are still to come.
    /// </summary>
    private static long Nearest(long timeOfDay, int rate, long dayFrames, long current)
    {
        long oneDay = 86_400L * rate;
        long best = Clamp(timeOfDay, dayFrames);

        for (long at = timeOfDay; at < dayFrames; at += oneDay)
            if (Math.Abs(at - current) <= Math.Abs(best - current))
                best = at;

        return best;
    }

    private static long Clamp(long frame, long dayFrames) =>
        Math.Clamp(frame, 0, dayFrames - 1);
}
