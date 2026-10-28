using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Emerald.Core;

namespace Emerald.LiveEdit;

/// <summary>One cue on the timeline, as the log window lists it.</summary>
public sealed record EdlRow(string At, string In, string What, string Length, string Status, Brush StatusBrush);

/// <summary>
/// Live Edit's own record: the EDL the timeline has written - every ingest item and deleted
/// scene, when the recording reaches it and what became of it - above the lines Live Edit and
/// its transmitter have written to the application's record.
///
/// Not a second log. The lines are the activity log's, filtered to the ones that are Live
/// Edit's business, so what is read here is exactly what is on disk afterwards.
/// </summary>
public partial class LiveLogWindow : Window
{
    public sealed record LogRow(string Time, string Message, Brush Brush);

    private readonly Func<IReadOnlyList<EdlRow>> _edl;
    private readonly Func<string> _live;
    private readonly ObservableCollection<LogRow> _lines = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(500) };

    private const int MaxLines = 600;

    public LiveLogWindow(Func<IReadOnlyList<EdlRow>> edl, Func<string> live)
    {
        InitializeComponent();

        _edl = edl;
        _live = live;

        LogList.ItemsSource = _lines;

        foreach (LogLine line in ActivityLog.Shared.Snapshot().Where(Ours).TakeLast(MaxLines))
            _lines.Add(Row(line));

        ActivityLog.Shared.Line += OnLine;

        _timer.Tick += (_, _) => ShowEdl();
        _timer.Start();

        ShowEdl();

        Loaded += (_, _) => LogScroller.ScrollToEnd();

        Closed += (_, _) =>
        {
            _timer.Stop();
            ActivityLog.Shared.Line -= OnLine;
        };
    }

    /// <summary>Live Edit's lines, and its transmitter's.</summary>
    public static bool Ours(LogLine line) =>
        line.Source == LogSource.LiveEdit ||
        line.Event?.StartsWith("liveedit.", StringComparison.Ordinal) == true;

    private void OnLine(LogLine line)
    {
        if (!Ours(line) || line.Transient) return;

        Dispatcher.BeginInvoke(() =>
        {
            _lines.Add(Row(line));
            while (_lines.Count > MaxLines) _lines.RemoveAt(0);

            LogScroller.ScrollToEnd();
        });
    }

    private LogRow Row(LogLine line) => new(
        line.Time,
        line.Message,
        (Brush)FindResource(line.Level switch
        {
            LogLevel.Error => "Bad",
            LogLevel.Warn => "Warn",
            LogLevel.Ok => "Ok",
            _ => "Text",
        }));

    private void ShowEdl()
    {
        IReadOnlyList<EdlRow> rows = _edl();

        EdlList.ItemsSource = rows;
        EdlEmpty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        LiveText.Text = _live();
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => _lines.Clear();
}
