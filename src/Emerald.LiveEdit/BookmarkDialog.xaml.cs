using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Emerald.Core;

namespace Emerald.LiveEdit;

/// <summary>
/// Every bookmark of the day in one place, and the way to add, amend and remove them.
///
/// This replaced a dialog that edited one bookmark — the one nearest the playhead — which meant
/// the only way to see what had been marked was to walk the playhead along the timeline and open
/// them one at a time, and the only way to reach one was to find its line on screen first. A
/// censor list is a list; it should be readable as one.
///
/// <b>Frame accurate.</b> The timecode field is parsed by <see cref="FramePick"/>, so it takes
/// HH:MM:SS:FF and lands on that exact frame, and a bare frame number works too. Nothing here
/// rounds to a second or snaps to anything near it.
/// </summary>
public partial class BookmarkDialog : Window
{
    /// <summary>One row of the list.</summary>
    private sealed record Row(long Frame, string Timecode, string Name);

    private readonly BookmarkBook _book;
    private readonly int _rate;
    private readonly long _dayFrames;

    /// <summary>True when anything was added, amended or removed, so the caller knows to save.</summary>
    public bool Changed { get; private set; }

    /// <summary>Set when the operator asked to be taken to a bookmark.</summary>
    public long? GoTo { get; private set; }

    public BookmarkDialog(BookmarkBook book, int rate, long dayFrames, long playhead, DateTime day)
    {
        InitializeComponent();

        _book = book;
        _rate = rate;
        _dayFrames = dayFrames;

        DayText.Text = $"{day:ddd d MMM yyyy}  -  times are frames of the day, at {rate} fps";

        // The playhead, because "mark where I am looking" is the common case and typing it out
        // again would be the operator doing arithmetic the window already knows.
        TimecodeBox.Text = new Timecode(playhead, rate).ToString();

        Refresh();

        Loaded += (_, _) => NameBox.Focus();
    }

    private void Refresh()
    {
        var rows = _book.All
            .Select(m => new Row(m.Frame, new Timecode(m.Frame, _rate).ToString(), m.Note))
            .ToList();

        MarkList.ItemsSource = rows;

        EmptyText.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // Nothing selected after a rebuild, so the buttons that act on a selection go dead with
        // it rather than acting on a row that is no longer there.
        UpdateButton.IsEnabled = DeleteButton.IsEnabled = GoToButton.IsEnabled = false;
    }

    private void Mark_Selected(object sender, SelectionChangedEventArgs e)
    {
        bool got = MarkList.SelectedItem is Row;

        UpdateButton.IsEnabled = DeleteButton.IsEnabled = GoToButton.IsEnabled = got;

        if (MarkList.SelectedItem is not Row row) return;

        // Loaded into the fields, so Update amends this one and the operator can see what they
        // are changing from.
        NameBox.Text = row.Name;
        TimecodeBox.Text = row.Timecode;

        Complain(null);
    }

    private void Mark_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (MarkList.SelectedItem is Row) GoTo_Click(sender, e);
    }

    /// <summary>The frame the timecode field names, or null with the reason shown.</summary>
    private long? Frame()
    {
        if (FramePick.TryParse(TimecodeBox.Text, _rate, _dayFrames, 0,
                               out long frame, out string? problem))
            return frame;

        Complain(problem ?? "That is not a timecode.");
        return null;
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (Frame() is not { } frame) return;

        // Said rather than silently overwritten: two bookmarks cannot share a frame, and an
        // operator adding one where one already is has probably mistyped the timecode.
        if (_book.At(frame) is { } existing)
        {
            Complain($"There is already a bookmark at {new Timecode(frame, _rate)}" +
                     (existing.Note.Length > 0 ? $" - \"{existing.Note}\"" : "") +
                     ". Select it to change it.");
            return;
        }

        _book.Set(frame, NameBox.Text);
        Changed = true;

        Complain(null);
        NameBox.Clear();
        Refresh();
    }

    private void Update_Click(object sender, RoutedEventArgs e)
    {
        if (MarkList.SelectedItem is not Row row) return;
        if (Frame() is not { } frame) return;

        // A moved bookmark is a remove and an add, because the frame is its identity.
        if (frame != row.Frame && _book.At(frame) is not null)
        {
            Complain($"There is already a bookmark at {new Timecode(frame, _rate)}.");
            return;
        }

        if (frame != row.Frame) _book.Remove(row.Frame);

        _book.Set(frame, NameBox.Text);
        Changed = true;

        Complain(null);
        Refresh();
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (MarkList.SelectedItem is not Row row) return;

        _book.Remove(row.Frame);
        Changed = true;

        NameBox.Clear();
        Complain(null);
        Refresh();
    }

    private void GoTo_Click(object sender, RoutedEventArgs e)
    {
        if (MarkList.SelectedItem is not Row row) return;

        GoTo = row.Frame;
        DialogResult = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => DialogResult = true;

    private void Complain(string? problem)
    {
        ProblemText.Text = problem ?? "";
        ProblemText.Visibility = problem is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
