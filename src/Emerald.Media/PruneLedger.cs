using System.Globalization;
using System.IO;
using Emerald.Core;

namespace Emerald.Media;

/// <summary>
/// How much of a recording the storage limit has deleted from its front, kept in the
/// recording's own folder.
///
/// <b>Why it has to exist.</b> A segment's place on the clock is the folder's start plus the
/// lengths of every segment before it. Once the oldest are deleted those lengths are gone, and
/// the first segment left was being placed at the moment Record was pressed - every segment
/// after it slid back by however much had been deleted, an hour and more on a 100 GB store, so
/// the newest segments landed far behind air and the timeline looked as if recording had
/// stopped. The warden writes down what it takes as it takes it, so the first segment left
/// still starts where it really did.
///
/// One line, <c>number ticks</c>: the last segment number deleted, and how long the recording
/// had run by the end of it - which is exactly where the next segment begins.
/// </summary>
public static class PruneLedger
{
    /// <summary>The file in each recording folder. Not playable, so the warden never deletes it.</summary>
    public const string FileName = "pruned.ledger";

    public readonly record struct Entry(long Number, TimeSpan Through);

    /// <summary>What has been deleted from the recording in <paramref name="folder"/>, or null when nothing has.</summary>
    public static Entry? Read(string folder)
    {
        try
        {
            string path = Path.Combine(folder, FileName);
            if (!File.Exists(path)) return null;

            string[] parts = File.ReadAllText(path).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

            return parts.Length >= 2 &&
                   long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long number) &&
                   long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long ticks) &&
                   number >= 0 && ticks >= 0
                ? new Entry(number, TimeSpan.FromTicks(ticks))
                : null;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>
    /// Where segment <paramref name="number"/> starts, from the folder's start, given what has
    /// been deleted before it and how long a segment usually runs.
    ///
    /// Exact when the segment follows straight on from the last one deleted, which is the
    /// ordinary case. A gap the ledger does not cover - files deleted before there was a ledger,
    /// or by hand - is filled at <paramref name="typical"/> a segment, which is right to the frame
    /// for a recorder that cuts every segment the same length.
    /// </summary>
    public static TimeSpan StartOf(long number, Entry? ledger, TimeSpan typical)
    {
        long last = ledger?.Number ?? RecordingSequence.FirstNumber - 1;
        TimeSpan through = ledger?.Through ?? TimeSpan.Zero;

        TimeSpan start = through + typical * (number - last - 1);
        return start < TimeSpan.Zero ? TimeSpan.Zero : start;
    }

    /// <summary>
    /// Writes down that segment <paramref name="number"/>, <paramref name="length"/> long, has been
    /// deleted from the recording in <paramref name="folder"/>.
    /// </summary>
    public static void Record(string folder, long number, TimeSpan length)
    {
        Entry? was = Read(folder);

        // Already covered - the other half of a pair, or a sweep repeated over the same segment.
        if (was is { } w && number <= w.Number) return;

        Entry now = new(number, StartOf(number, was, length) + length);

        try
        {
            string path = Path.Combine(folder, FileName);
            string temp = path + ".tmp";

            File.WriteAllText(temp, string.Create(CultureInfo.InvariantCulture, $"{now.Number} {now.Through.Ticks}"));
            File.Move(temp, path, overwrite: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
