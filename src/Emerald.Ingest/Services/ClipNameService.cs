using System.IO;
using Emerald.Core;

namespace Emerald.Ingest;

/// <summary>
/// Names the clip an ingest produces.
///
/// It is a service of its own for one reason: naming conventions change. A station that
/// today wants CLIP_20260902_14501120 will one day want the programme code in front of it,
/// and that should be one class to replace rather than a format string copied into a
/// window, a scheduler and a test.
/// </summary>
public interface IClipNameService
{
    /// <summary>
    /// A name for a clip being created now. When the realtime timecode is available its
    /// frame field is used, so two clips created inside the same second are still distinct
    /// and the name lines up with what the operator can see on the clock.
    /// </summary>
    string Generate(Timecode? timecode = null, DateTime? now = null);

    /// <summary>Strips anything a filename cannot hold; returns "" when nothing is left.</summary>
    string Sanitise(string? name);

    /// <summary>True when the name is usable as it stands.</summary>
    bool IsValid(string? name, out string? error);
}

public sealed class ClipNameService : IClipNameService
{
    /// <summary>Long enough to be descriptive, short enough to survive a path.</summary>
    public const int MaxLength = 60;

    public string Generate(Timecode? timecode = null, DateTime? now = null)
    {
        DateTime stamp = now ?? DateTime.Now;

        // The frame field when there is a clock to read it off, hundredths of a second when
        // there is not — either way two digits, so the name is a fixed width.
        int tail = timecode is { Rate: > 0 } tc ? tc.Frames : stamp.Millisecond / 10;

        return $"CLIP_{stamp:yyyyMMdd}_{stamp:HHmmss}{tail:D2}";
    }

    public string Sanitise(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";

        char[] invalid = Path.GetInvalidFileNameChars();

        var clean = new string(name.Trim()
            .Select(c => invalid.Contains(c) || c == '%' ? '_' : c)
            .ToArray());

        return clean.Length <= MaxLength ? clean : clean[..MaxLength];
    }

    public bool IsValid(string? name, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(name))
        {
            error = "Clip name is empty.";
            return false;
        }

        string trimmed = name.Trim();

        if (trimmed.Length > MaxLength)
        {
            error = $"Clip name is longer than {MaxLength} characters.";
            return false;
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || trimmed.Contains('%'))
        {
            error = "Clip name contains characters a filename cannot hold.";
            return false;
        }

        // Trailing dots and spaces are legal to type and illegal on disk, which is a
        // difference the operator should be told about rather than have corrected for them.
        if (trimmed != trimmed.TrimEnd('.', ' '))
        {
            error = "Clip name cannot end in a dot or a space.";
            return false;
        }

        return true;
    }
}
