using System.IO;
using System.Text;
using System.Text.Json;

namespace Emerald.Core;

/// <summary>
/// A point on the recording worth coming back to, and what it is.
///
/// Not an in/out pair. An edit or censor point is a single instant — "bleep here", "cut from
/// here", "the guest swore" — and forcing it into a range would mean inventing an end that
/// nobody decided. So it is one frame and a note, and the note is the whole value of it: a line
/// with nothing written on it is a thing an operator has to remember the meaning of.
/// </summary>
public readonly record struct Bookmark(long Frame, string Note)
{
    /// <summary>What to show when there is no note. An unnamed mark is still a mark.</summary>
    public string Label => Note.Length > 0 ? Note : "bookmark";
}

/// <summary>
/// The bookmarks of one day, kept in frame order.
///
/// Ordered because everything that reads them walks the timeline left to right, and a list that
/// has to be sorted at each draw would be sorted twenty-five times a second. Ordering is
/// maintained on insert instead, which happens when somebody types.
///
/// One frame holds at most one bookmark. Marking the same frame twice is an operator correcting
/// the note, not asking for two lines in the same pixel.
/// </summary>
public sealed class BookmarkBook
{
    private readonly List<Bookmark> _marks = new();

    public IReadOnlyList<Bookmark> All => _marks;

    public int Count => _marks.Count;

    /// <summary>
    /// Puts a bookmark at a frame, replacing the note if one is already there.
    ///
    /// Returns true when this added a new mark, false when it amended one — the caller says
    /// different things in the log for the two, and "bookmark added" for an edit would be a lie.
    /// </summary>
    public bool Set(long frame, string? note)
    {
        var mark = new Bookmark(frame, (note ?? "").Trim());

        int at = IndexOf(frame);

        if (at >= 0)
        {
            _marks[at] = mark;
            return false;
        }

        // Kept in order by inserting where it belongs. BinarySearch on the frame would need a
        // comparer over the record; the list is small enough - a day's censor points are tens,
        // not thousands - that finding the place plainly is clearer and costs nothing.
        int i = _marks.FindIndex(m => m.Frame > frame);

        if (i < 0) _marks.Add(mark);
        else _marks.Insert(i, mark);

        return true;
    }

    public bool Remove(long frame)
    {
        int at = IndexOf(frame);

        if (at < 0) return false;

        _marks.RemoveAt(at);
        return true;
    }

    public Bookmark? At(long frame)
    {
        int at = IndexOf(frame);
        return at < 0 ? null : _marks[at];
    }

    /// <summary>
    /// The bookmark nearest a frame, within a tolerance, or none.
    ///
    /// The tolerance is what makes this usable from a mouse or from a playhead that is never
    /// exactly where a mark was set: at an hour-wide zoom a pixel is ninety frames, so "on the
    /// bookmark" has to mean "within so many frames of it" or nothing would ever be hit. The
    /// caller works the tolerance out from the zoom, because only it knows what a pixel is worth.
    /// </summary>
    public Bookmark? Nearest(long frame, long within)
    {
        if (_marks.Count == 0 || within < 0) return null;

        Bookmark? best = null;
        long closest = long.MaxValue;

        foreach (Bookmark mark in _marks)
        {
            long gap = Math.Abs(mark.Frame - frame);

            if (gap > within || gap >= closest) continue;

            closest = gap;
            best = mark;
        }

        return best;
    }

    /// <summary>The bookmarks inside a window, for drawing. In order, like everything here.</summary>
    public IEnumerable<Bookmark> Between(long from, long to) =>
        _marks.Where(m => m.Frame >= from && m.Frame <= to);

    private int IndexOf(long frame) => _marks.FindIndex(m => m.Frame == frame);

    // ------------------------------------------------------------------ on disk

    /// <summary>
    /// Where a day's bookmarks live.
    ///
    /// Beside the settings rather than in the store, because a bookmark is a note about the
    /// media and not part of it: a store swept for space, or a recording folder deleted, must
    /// not take an operator's censor list with it.
    /// </summary>
    public static string PathFor(DateTime day, string? folder = null) =>
        Path.Combine(
            folder ?? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Emerald", "bookmarks"),
            $"{day:yyyy-MM-dd}.json");

    /// <summary>
    /// Reads a day's bookmarks. A day with none, or a file that cannot be read, comes back empty
    /// rather than throwing: a corrupt notes file must not stop the window opening.
    /// </summary>
    public static BookmarkBook Load(string path)
    {
        var book = new BookmarkBook();

        try
        {
            if (!File.Exists(path)) return book;

            Bookmark[]? read = JsonSerializer.Deserialize<Bookmark[]>(File.ReadAllText(path));

            foreach (Bookmark mark in read ?? Array.Empty<Bookmark>())
                book.Set(mark.Frame, mark.Note);
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }

        return book;
    }

    /// <summary>
    /// Writes a day's bookmarks, and says whether it got there.
    ///
    /// An empty book deletes the file rather than leaving "[]" behind, so clearing the last
    /// bookmark of a day leaves no trace to puzzle over later.
    /// </summary>
    public bool Save(string path)
    {
        try
        {
            if (_marks.Count == 0)
            {
                if (File.Exists(path)) File.Delete(path);
                return true;
            }

            string? folder = Path.GetDirectoryName(path);

            if (folder is not null) Directory.CreateDirectory(folder);

            File.WriteAllText(
                path,
                JsonSerializer.Serialize(_marks, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));

            return true;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }
}
