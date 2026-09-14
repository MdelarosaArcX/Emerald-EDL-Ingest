using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Emerald.Core;
using Microsoft.Win32;

namespace Emerald.App;

/// <summary>One line as the grid shows it: everything already flattened, nothing computed per cell.</summary>
public sealed record MonitorRow(
    string Time,
    string Timecode,
    string Source,
    string Id,
    string File,
    string Message,
    Brush Accent,
    LogLine Line);

/// <summary>
/// Everything Emerald did, from the receiver to the transmitter, on one page.
///
/// It answers two questions that are not the same question. <b>What is happening</b> is the
/// strip across the top — the chain, live, five segments in the order the pictures travel.
/// <b>What happened</b> is the record below it, merged from every module, in one order, and
/// still there tomorrow.
///
/// Neither was answerable before. Each module kept a few hundred lines of its own until its
/// window closed, the capture deck kept a single overwriting label, and nothing was written
/// down anywhere.
/// </summary>
public partial class MonitorWindow : Window
{
    /// <summary>
    /// Rows held on screen. Larger than the log's own ring is pointless; smaller would throw
    /// away lines the operator can still see in the file.
    /// </summary>
    private const int MaxRows = ActivityLog.RingSize;

    private readonly AppSettings _settings;
    private readonly ObservableCollection<MonitorRow> _rows = new();

    /// <summary>
    /// Lines arrive from the capture thread, the playout thread and the scheduler. They queue
    /// here and are drained on a timer rather than each marshalling itself: a BeginInvoke per
    /// line from a real-time loop floods the dispatcher and makes the whole UI stutter.
    /// </summary>
    private readonly ConcurrentQueue<LogLine> _incoming = new();

    private readonly DispatcherTimer _drain =
        new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(25) };

    private readonly DispatcherTimer _strip =
        new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(200) };

    private readonly TimecodeService _timecode = TimecodeLink.Service;

    private ScrollViewer? _scroller;
    private bool _following = true;

    // Filters, read on every row rather than re-queried from the controls.
    private LogSource? _source;
    private LogLevel _minimum = LogLevel.Info;
    private string _search = "";
    private string _id = "";

    public MonitorWindow(AppSettings? settings = null)
    {
        _settings = settings ?? AppSettings.Load();

        InitializeComponent();

        LogList.ItemsSource = _rows;

        SourceCombo.ItemsSource = new object[] { "All" }
            .Concat(Enum.GetValues<LogSource>().Cast<object>()).ToList();
        SourceCombo.SelectedIndex = 0;

        LevelCombo.ItemsSource = Enum.GetValues<LogLevel>();
        LevelCombo.SelectedIndex = 0;

        _drain.Tick += (_, _) => Drain();
        _strip.Tick += (_, _) => DrawStrip();

        Loaded += Window_Loaded;
        Closing += Window_Closing;
    }

    // ------------------------------------------------------------------ lifecycle

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FolderText.Text = ActivityLog.Shared.Folder is { } folder
            ? $"writing to {folder}"
            : "not being written to disk";

        // Everything that already happened, before the first new line arrives. Opening this
        // half an hour into a session must not show a blank page.
        foreach (LogLine line in ActivityLog.Shared.Snapshot()) _incoming.Enqueue(line);

        ActivityLog.Shared.Line += OnLine;
        PipelineState.Shared.Changed += OnPipelineChanged;
        TidalLock.Shared.Changed += OnTidalLockChanged;

        Drain();
        DrawStrip();

        _drain.Start();
        _strip.Start();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _drain.Stop();
        _strip.Stop();

        // All three are on objects that outlive this window. Without these it is held for the
        // life of the application, and every line written after it closes still runs its
        // handlers. Same trap TimecodeLink documents.
        ActivityLog.Shared.Line -= OnLine;
        PipelineState.Shared.Changed -= OnPipelineChanged;
        TidalLock.Shared.Changed -= OnTidalLockChanged;
    }

    // ------------------------------------------------------------------ the record

    /// <summary>Called from whichever thread wrote the line. Does as little as possible.</summary>
    private void OnLine(LogLine line) => _incoming.Enqueue(line);

    private void Drain()
    {
        bool added = false;

        while (_incoming.TryDequeue(out LogLine? line))
        {
            if (!Matches(line)) continue;

            _rows.Add(ToRow(line));
            added = true;
        }

        if (!added) return;

        while (_rows.Count > MaxRows) _rows.RemoveAt(0);

        CountText.Text = $"ACTIVITY  ({_rows.Count} line{(_rows.Count == 1 ? "" : "s")} shown)";

        if (_following && _rows.Count > 0) LogList.ScrollIntoView(_rows[^1]);
    }

    private MonitorRow ToRow(LogLine line) => new(
        Time: line.Time,
        Timecode: line.TimecodeText,
        Source: line.SourceText,
        Id: line.Correlation ?? "",
        File: line.FileText,
        Message: line.Message,
        Accent: Brush(line.Level switch
        {
            LogLevel.Ok => "Ok",
            LogLevel.Warn => "Warn",
            LogLevel.Error => "Bad",
            // Deliberately not the blue the module panels use for Info. Most lines are Info,
            // and a page where most of the text is blue reads as decoration rather than as
            // levels - the ones that matter are the three above.
            _ => "Text",
        }),
        Line: line);

    // ------------------------------------------------------------------ filtering

    private bool Matches(LogLine line)
    {
        if (line.Level < _minimum) return false;
        if (_source is { } source && line.Source != source) return false;

        if (_id.Length > 0 &&
            (line.Correlation is null ||
             !line.Correlation.Contains(_id, StringComparison.OrdinalIgnoreCase)))
            return false;

        if (_search.Length == 0) return true;

        // Message, file and id together: an operator searching "promo" means the clip, and
        // searching an id means the command, and neither should have to say which.
        return line.Message.Contains(_search, StringComparison.OrdinalIgnoreCase)
            || (line.File?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (line.Correlation?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false)
            || (line.Event?.Contains(_search, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private void Filter_Changed(object sender, EventArgs e)
    {
        if (!IsLoaded) return;

        _source = SourceCombo.SelectedItem as LogSource?;
        _minimum = LevelCombo.SelectedItem is LogLevel level ? level : LogLevel.Info;
        _search = SearchBox.Text.Trim();
        _id = IdBox.Text.Trim();

        Rebuild();
    }

    /// <summary>
    /// Re-applies the filter to everything in the log's ring.
    ///
    /// From the log rather than from what is on screen: filtering the rows already shown would
    /// mean a filter could only ever narrow, and widening one would silently show less than it
    /// should.
    /// </summary>
    private void Rebuild()
    {
        _rows.Clear();

        foreach (LogLine line in ActivityLog.Shared.Snapshot())
            if (Matches(line)) _rows.Add(ToRow(line));

        while (_rows.Count > MaxRows) _rows.RemoveAt(0);

        CountText.Text = $"ACTIVITY  ({_rows.Count} line{(_rows.Count == 1 ? "" : "s")} shown)";

        if (_following && _rows.Count > 0) LogList.ScrollIntoView(_rows[^1]);
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        SourceCombo.SelectedIndex = 0;
        LevelCombo.SelectedIndex = 0;
        SearchBox.Text = "";
        IdBox.Text = "";
    }

    /// <summary>
    /// Double-clicking a line follows its id.
    ///
    /// One click to see a whole EDL from the moment it was queued to the moment its last file
    /// left the transmitter — which is the question this page exists to answer and would
    /// otherwise mean copying an eight-character id by eye.
    /// </summary>
    private void LogList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LogList.SelectedItem is not MonitorRow row || row.Id.Length == 0) return;

        IdBox.Text = row.Id;
    }

    // ------------------------------------------------------------------ the strip

    private void OnPipelineChanged() => Dispatcher.BeginInvoke(DrawStrip);
    private void OnTidalLockChanged(TidalLock _) => Dispatcher.BeginInvoke(DrawStrip);

    private void DrawStrip()
    {
        PipelineState state = PipelineState.Shared;

        TcDisplay.Text = _timecode.TryGetCurrent(out Timecode now) ? now.ToString() : "--:--:--:--";

        Show(CaptureHeadline, CaptureDetail, state.Capture);
        Show(EdlHeadline, EdlDetail, state.Edl);
        Show(AirHeadline, AirDetail, state.OnAir);
        Show(IngestHeadline, IngestDetail, state.Ingest);
        Show(DelayHeadline, DelayDetail, Delay());

        RecordText.Text = ActivityLog.Shared.Dropped > 0
            ? $"{ActivityLog.Shared.Dropped} line(s) dropped"
            : "";
    }

    /// <summary>
    /// Tidal lock reports itself, so it is read where it lives rather than mirrored into
    /// <see cref="PipelineState"/> — one fewer thing that can disagree with the truth.
    /// </summary>
    private static StageState Delay()
    {
        TidalLock lockState = TidalLock.Shared;

        return lockState.State switch
        {
            TidalLockState.Off => StageState.Idle,
            TidalLockState.Armed => new StageState("ARMED", lockState.Detail, LogLevel.Warn),
            TidalLockState.CountingDown =>
                new StageState("FILLING", $"{lockState.FillFraction * 100:0}% - {lockState.Detail}", LogLevel.Warn),
            TidalLockState.OnAir => new StageState("ON AIR", lockState.Detail, LogLevel.Ok),
            TidalLockState.Draining => new StageState("DRAINING", lockState.Detail, LogLevel.Warn),
            _ => new StageState("FAILED", lockState.Detail, LogLevel.Error),
        };
    }

    private void Show(TextBlock headline, TextBlock detail, StageState stage)
    {
        headline.Text = stage.Headline;
        detail.Text = stage.Detail;

        headline.Foreground = Brush(stage == StageState.Idle
            ? "Muted"
            : stage.Level switch
            {
                LogLevel.Ok => "Ok",
                LogLevel.Warn => "Warn",
                LogLevel.Error => "Bad",
                _ => "Text",
            });
    }

    // ------------------------------------------------------------------ following

    /// <summary>
    /// Turns following off the moment the operator scrolls back, and on again at the bottom.
    ///
    /// A page that yanked itself to the newest line while somebody was reading would be
    /// unusable, and one that had to be told to follow again would be forgotten.
    /// </summary>
    private void OnScroll(object sender, ScrollChangedEventArgs e)
    {
        if (_scroller is null || e.ExtentHeightChange != 0) return;

        bool atEnd = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 2;

        if (_following != atEnd)
        {
            _following = atEnd;
            FollowToggle.IsChecked = atEnd;
        }
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);

        _scroller = FindScroller(LogList);
        if (_scroller is not null) _scroller.ScrollChanged += OnScroll;

        FollowToggle.Checked += (_, _) => { _following = true; Drain(); };
        FollowToggle.Unchecked += (_, _) => _following = false;
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        if (root is ScrollViewer found) return found;

        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            if (FindScroller(VisualTreeHelper.GetChild(root, i)) is { } scroller) return scroller;
        }

        return null;
    }

    // ------------------------------------------------------------------ export

    /// <summary>
    /// Saves what is on screen — the filtered view, not everything.
    ///
    /// Somebody exporting after narrowing to one EDL wants that EDL, and handing them the
    /// whole day instead would be the wrong answer to an unambiguous question. The whole day
    /// is one button along, in the folder.
    /// </summary>
    private void Export_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save the activity shown",
            Filter = "Text|*.txt|All files|*.*",
            FileName = $"emerald-activity-{DateTime.Now:yyyyMMdd-HHmmss}.txt",
        };

        if (dialog.ShowDialog(this) != true) return;

        var text = new StringBuilder();

        foreach (MonitorRow row in _rows)
        {
            text.Append(row.Time).Append("  ")
                .Append(row.Timecode).Append("  ")
                .Append(row.Source.PadRight(10))
                .Append(row.Id.PadRight(10))
                .AppendLine(row.Message);

            if (row.Line.Detail is { Length: > 0 } detail)
                foreach (string line in detail.Split('\n')) text.Append("        ").AppendLine(line.TrimEnd());
        }

        try
        {
            File.WriteAllText(dialog.FileName, text.ToString());
            ActivityLog.Shared.Ok(LogSource.App, $"Activity exported to {dialog.FileName}.");
        }
        catch (Exception ex)
        {
            ActivityLog.Shared.Error(LogSource.App, $"Could not export the activity: {ex.Message}");
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = _settings.LogFolderOrDefault;

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ActivityLog.Shared.Warn(LogSource.App, $"Could not open {folder}: {ex.Message}");
        }
    }

    private static Brush Brush(string key) =>
        Application.Current.TryFindResource(key) as Brush ?? Brushes.White;
}
