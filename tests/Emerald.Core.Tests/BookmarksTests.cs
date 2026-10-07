using System.IO;
using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// Edit and censor points on the timeline.
///
/// The ordering and the tolerance are the two things worth protecting. Order, because everything
/// that draws them walks left to right and an out-of-order list puts a label on the wrong line.
/// Tolerance, because at any useful zoom a pixel is many frames, so "did they click the
/// bookmark" is a question about nearness and not equality.
/// </summary>
public sealed class BookmarksTests
{
    [Fact]
    public void Marks_are_kept_in_frame_order_however_they_arrive()
    {
        var book = new BookmarkBook();

        book.Set(500, "third");
        book.Set(100, "first");
        book.Set(900, "fourth");
        book.Set(300, "second");

        Assert.Equal(new[] { 100L, 300, 500, 900 }, book.All.Select(m => m.Frame));
        Assert.Equal(new[] { "first", "second", "third", "fourth" }, book.All.Select(m => m.Note));
    }

    [Fact]
    public void Marking_the_same_frame_again_corrects_the_note_rather_than_adding_a_line()
    {
        var book = new BookmarkBook();

        Assert.True(book.Set(100, "swore"));
        Assert.False(book.Set(100, "swore twice"));

        Assert.Equal(1, book.Count);
        Assert.Equal("swore twice", book.At(100)!.Value.Note);
    }

    [Fact]
    public void A_mark_can_be_removed_and_removing_one_that_is_not_there_is_not_an_error()
    {
        var book = new BookmarkBook();

        book.Set(100, "a");
        book.Set(200, "b");

        Assert.True(book.Remove(100));
        Assert.False(book.Remove(100));
        Assert.False(book.Remove(999));

        Assert.Equal(200, Assert.Single(book.All).Frame);
    }

    [Fact]
    public void The_note_is_trimmed_and_a_missing_one_is_empty_rather_than_null()
    {
        var book = new BookmarkBook();

        book.Set(1, "   bleep here   ");
        book.Set(2, null);

        Assert.Equal("bleep here", book.At(1)!.Value.Note);
        Assert.Equal("", book.At(2)!.Value.Note);
    }

    [Fact]
    public void A_mark_with_no_note_still_has_something_to_show()
    {
        Assert.Equal("bookmark", new Bookmark(10, "").Label);
        Assert.Equal("cut here", new Bookmark(10, "cut here").Label);
    }

    [Fact]
    public void The_nearest_mark_within_the_tolerance_is_the_one_found()
    {
        var book = new BookmarkBook();

        book.Set(1000, "a");
        book.Set(1200, "b");

        Assert.Equal(1000, book.Nearest(1040, 90)!.Value.Frame);
        Assert.Equal(1200, book.Nearest(1160, 90)!.Value.Frame);

        // Exactly between two, inside tolerance of both: the first one wins, and it is at least
        // stable rather than depending on list order.
        Assert.Equal(1000, book.Nearest(1100, 200)!.Value.Frame);
    }

    [Fact]
    public void Nothing_is_found_outside_the_tolerance()
    {
        var book = new BookmarkBook();

        book.Set(1000, "a");

        Assert.Null(book.Nearest(1200, 90));
        Assert.Null(book.Nearest(1000, -1));
        Assert.Null(new BookmarkBook().Nearest(0, 1000));
    }

    [Fact]
    public void A_tolerance_of_zero_means_exactly_that_frame()
    {
        var book = new BookmarkBook();

        book.Set(1000, "a");

        Assert.Equal(1000, book.Nearest(1000, 0)!.Value.Frame);
        Assert.Null(book.Nearest(1001, 0));
    }

    [Fact]
    public void Only_the_marks_inside_the_window_are_handed_out_for_drawing()
    {
        var book = new BookmarkBook();

        foreach (long f in new[] { 10L, 100, 500, 900, 2000 }) book.Set(f, $"m{f}");

        Assert.Equal(new[] { 100L, 500, 900 }, book.Between(100, 900).Select(m => m.Frame));
        Assert.Empty(book.Between(1000, 1999));
    }

    [Fact]
    public void A_day_with_no_bookmarks_reads_back_empty_rather_than_failing()
    {
        string path = Path.Combine(Path.GetTempPath(), $"emerald-no-such-{Guid.NewGuid():N}.json");

        Assert.Equal(0, BookmarkBook.Load(path).Count);
    }

    [Fact]
    public void A_corrupt_notes_file_reads_back_empty_rather_than_stopping_the_window()
    {
        string path = Path.Combine(Path.GetTempPath(), $"emerald-bad-{Guid.NewGuid():N}.json");

        try
        {
            File.WriteAllText(path, "{ this is not bookmarks");

            Assert.Equal(0, BookmarkBook.Load(path).Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Bookmarks_survive_a_round_trip_through_a_file()
    {
        string path = Path.Combine(Path.GetTempPath(), $"emerald-bm-{Guid.NewGuid():N}.json");

        try
        {
            var book = new BookmarkBook();
            book.Set(1_234_567, "censor - name of a minor");
            book.Set(10, "");

            Assert.True(book.Save(path));

            BookmarkBook read = BookmarkBook.Load(path);

            Assert.Equal(2, read.Count);
            Assert.Equal(new[] { 10L, 1_234_567 }, read.All.Select(m => m.Frame));
            Assert.Equal("censor - name of a minor", read.At(1_234_567)!.Value.Note);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Clearing_the_last_bookmark_leaves_no_file_behind()
    {
        string path = Path.Combine(Path.GetTempPath(), $"emerald-bm-{Guid.NewGuid():N}.json");

        try
        {
            var book = new BookmarkBook();
            book.Set(10, "a");
            book.Save(path);

            Assert.True(File.Exists(path));

            book.Remove(10);

            Assert.True(book.Save(path));
            Assert.False(File.Exists(path));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Each_day_has_its_own_file_named_for_it()
    {
        string a = BookmarkBook.PathFor(new DateTime(2026, 10, 6), "C:\\notes");
        string b = BookmarkBook.PathFor(new DateTime(2026, 10, 7), "C:\\notes");

        Assert.EndsWith("2026-10-06.json", a);
        Assert.EndsWith("2026-10-07.json", b);
        Assert.NotEqual(a, b);
    }
}
