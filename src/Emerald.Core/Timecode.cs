using System.Globalization;

namespace Emerald.Core;

/// <summary>
/// Non-drop SMPTE timecode as a frame count plus the rate it was counted at.
/// The timecode server reports an integer nominal rate (25, 30, 50, 60...), so
/// HH:MM:SS:FF is a straight base-rate count; drop-frame renumbering is not applied.
/// </summary>
public readonly record struct Timecode(long TotalFrames, int Rate)
{
    public int Hours => (int)(TotalFrames / (3600L * Rate));
    public int Minutes => (int)(TotalFrames / (60L * Rate) % 60);
    public int Seconds => (int)(TotalFrames / Rate % 60);
    public int Frames => (int)(TotalFrames % Rate);

    public double TotalSeconds => Rate <= 0 ? 0 : (double)TotalFrames / Rate;

    public override string ToString() =>
        $"{Hours:D2}:{Minutes:D2}:{Seconds:D2}:{Frames:D2}";

    public Timecode AddFrames(long frames) => this with { TotalFrames = Math.Max(0, TotalFrames + frames) };

    /// <summary>Adds frames and wraps at 24:00:00:00, the way a timecode clock rolls over midnight.</summary>
    public Timecode AddWrapping(long frames)
    {
        // A default(Timecode) — what TryParse leaves behind on failure — has Rate 0, and
        // there is no day to wrap around. Return it untouched rather than dividing by zero.
        if (Rate <= 0) return this;

        long perDay = 24L * 3600L * Rate;
        long total = (TotalFrames + frames) % perDay;
        if (total < 0) total += perDay;
        return this with { TotalFrames = total };
    }

    public static Timecode Zero(int rate) => new(0, rate);

    public static bool TryParse(string? text, int rate, out Timecode value, out string? error)
    {
        value = default;
        error = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            error = "Timecode is empty.";
            return false;
        }

        if (rate <= 0)
        {
            error = "Frame rate is unknown.";
            return false;
        }

        string[] parts = text.Trim().Split(new[] { ':', ';', '.' }, StringSplitOptions.None);
        if (parts.Length != 4)
        {
            error = "Expected HH:MM:SS:FF.";
            return false;
        }

        var n = new int[4];
        for (int i = 0; i < 4; i++)
        {
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out n[i]))
            {
                error = "Expected HH:MM:SS:FF with digits only.";
                return false;
            }
        }

        if (n[0] > 23) { error = "Hours must be 00-23."; return false; }
        if (n[1] > 59) { error = "Minutes must be 00-59."; return false; }
        if (n[2] > 59) { error = "Seconds must be 00-59."; return false; }
        if (n[3] >= rate) { error = $"Frames must be 00-{rate - 1:D2} at {rate} fps."; return false; }

        long total = ((n[0] * 60L + n[1]) * 60L + n[2]) * rate + n[3];
        value = new Timecode(total, rate);
        return true;
    }

    /// <summary>Re-counts the same wall-clock position at a different frame rate.</summary>
    public Timecode Rebase(int newRate)
    {
        if (newRate <= 0 || newRate == Rate) return this;
        return new Timecode((long)Math.Round(TotalSeconds * newRate), newRate);
    }
}
