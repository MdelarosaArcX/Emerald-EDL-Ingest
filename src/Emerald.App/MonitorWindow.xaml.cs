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

/// <summary>One file under one message, flattened for the grid.</summary>
public sealed record TransmittedRow(
    string Group,
    string Kind,
    string Label,
    string Channels,
    string FileName,
    string Path,
    string State,
    string Times,
    string Last,
    string Queued,
    Brush KindBrush,
    Brush StateBrush,
    TransmittedFile File);

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
    /// Rows held on screen: the newest of a day read back from the database, then live lines.
    /// The list virtualizes, so this many costs memory and not drawing.
    /// </summary>
    private const int MaxRows = 20_000;

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
    private LogArea? _area;
    /// <summary>The day picked, or default for today live.</summary>
    private DateTime _day;
    private LogLevel _minimum = LogLevel.Info;

    /// <summary>
    /// Live lines at or below this sequence are already on screen from the database, or are
    /// still being read from it (long.MaxValue). Only lines after it are added as they arrive.
    /// </summary>
    private long _shownThrough = long.MaxValue;
    private int _rebuild;

    private IDisposable? _sampling;

    private readonly DispatcherTimer _resources =
        new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(1) };
    private string _search = "";
    private string _id = "";

    public MonitorWindow(AppSettings? settings = null)
    {
        _settings = settings ?? AppSettings.Load();

        InitializeComponent();

        LogList.ItemsSource = _rows;

        // Grouped by the message. A CollectionView does the grouping, so the rows stay one
        // flat virtualized list rather than a nest of ItemsControls.
        var grouped = new System.Windows.Data.CollectionViewSource { Source = _transmitted };
        grouped.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(TransmittedRow.Group)));
        TransmittedList.ItemsSource = grouped.View;

        AreaCombo.ItemsSource = new[] { new Choice<LogArea?>("All", null) }
            .Concat(new[] { LogArea.Errors }.Concat(LogDatabase.Areas)
                .Select(a => new Choice<LogArea?>(AreaName(a), a)))
            .ToList();
        AreaCombo.SelectedIndex = 0;

        // As far back as the database keeps.
        int keep = _settings.LogRetentionDays < 0 ? 60 : Math.Max(0, _settings.LogRetentionDays);
        DayCombo.ItemsSource = Enumerable.Range(0, keep + 1)
            .Select(d => DateTime.Today.AddDays(-d))
            .Select(d => new Choice<DateTime>(
                d == DateTime.Today ? "Today (live)" : d == DateTime.Today.AddDays(-1) ? "Yesterday" : d.ToString("ddd d MMM"),
                d == DateTime.Today ? default : d))
            .ToList();
        DayCombo.SelectedIndex = 0;

        LevelCombo.ItemsSource = Enum.GetValues<LogLevel>();
        LevelCombo.SelectedIndex = 0;

        _drain.Tick += (_, _) => DrainAndDraw();
        _strip.Tick += (_, _) => DrawStrip();
        _resources.Tick += (_, _) => DrawResources();

        Loaded += Window_Loaded;
        Closing += Window_Closing;
    }

    // ------------------------------------------------------------------ lifecycle

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        FolderText.Text = ActivityLog.Shared.Database is { } db
            ? $"database {db.Path}"
            : "not being written to disk";

        // The whole of today from the database, then live. Opening this half an hour into a
        // session must not show a blank page, and opening it at four must not claim nothing
        // aired this morning.
        LoadToday();

        foreach (LogLine line in ActivityLog.Shared.Snapshot()) _incoming.Enqueue(line);

        ActivityLog.Shared.Line += OnLine;
        PipelineState.Shared.Changed += OnPipelineChanged;
        TidalLock.Shared.Changed += OnTidalLockChanged;
        LiveAir.Shared.Changed += OnLiveAirChanged;

        Rebuild();
        DrainAndDraw();
        DrawStrip();

        _sampling = ResourceSampler.Shared.Watch();
        DrawResources();

        _drain.Start();
        _strip.Start();
        _resources.Start();

        // On if it was left on. The toggle raises the handler, which does the starting.
        if (_settings.MonitorApi) RemoteToggle.IsChecked = true;
        if (RemoteToggle.IsChecked == true) StartRemote();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        _drain.Stop();
        _strip.Stop();
        _resources.Stop();

        _sampling?.Dispose();
        _sampling = null;

        // All three are on objects that outlive this window. Without these it is held for the
        // life of the application, and every line written after it closes still runs its
        // handlers. Same trap TimecodeLink documents.
        ActivityLog.Shared.Line -= OnLine;
        PipelineState.Shared.Changed -= OnPipelineChanged;
        TidalLock.Shared.Changed -= OnTidalLockChanged;
        LiveAir.Shared.Changed -= OnLiveAirChanged;

        // The port goes with the window. The setting remembers it was on, so it comes back.
        _remote?.Dispose();
        _remote = null;
    }

    // ------------------------------------------------------------------ the record

    /// <summary>Called from whichever thread wrote the line. Does as little as possible.</summary>
    private void OnLine(LogLine line) => _incoming.Enqueue(line);

    private void Drain()
    {
        bool added = false;

        while (_incoming.TryDequeue(out LogLine? line))
        {
            // Folded first, and regardless of the filters: the filters are a view of the log,
            // and what went to air is not a matter of what is being looked at.
            _tally.Add(line);
            _tallyDirty = true;

            // An earlier day is what the database holds; nothing arriving now belongs in it.
            // And a line already read back from the database is not shown twice.
            if (!Live || line.At.Date != DateTime.Today || line.Seq <= _shownThrough) continue;
            if (!Matches(line)) continue;

            _rows.Add(ToRow(line));
            added = true;
        }

        if (!added) return;

        while (_rows.Count > MaxRows) _rows.RemoveAt(0);

        CountText.Text = $"ACTIVITY  ({_rows.Count} line{(_rows.Count == 1 ? "" : "s")} shown)";

        if (_following && _rows.Count > 0) LogList.ScrollIntoView(_rows[^1]);
    }

    private void DrainAndDraw()
    {
        Drain();
        DrawTransmitted();
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


    // ------------------------------------------------------------------ what went to air

    /// <summary>
    /// The tally, folded from the record as lines arrive. It is never rebuilt from scratch on
    /// a new line — the fold is cumulative and the same line twice changes nothing.
    /// </summary>
    private readonly TransmissionTally _tally = new();

    private readonly ObservableCollection<TransmittedRow> _transmitted = new();

    /// <summary>Set when the fold saw something the grid has not been told about yet.</summary>
    private bool _tallyDirty;

    private bool ShowingTransmitted => TransmittedTab.IsChecked == true;

    /// <summary>
    /// Loads the whole of today off the disk before the ring is replayed.
    ///
    /// The ring holds five thousand lines, which on a busy plant is an hour or two — opening
    /// this at four in the afternoon and being told nothing went to air before three would be
    /// a worse answer than none. The same lines then arrive again from the ring, and the fold
    /// counts each transmission once regardless.
    /// </summary>
    private void LoadToday()
    {
        if (ActivityLog.Shared.Database is null) return;

        try
        {
            foreach (LogLine line in ActivityLog.Shared.ReadDay(DateTime.Today)) _tally.Add(line);
        }
        catch (Exception ex)
        {
            // A day that will not load is worth saying, and is not worth refusing to open the
            // page over: everything from this session is still in the ring.
            ActivityLog.Shared.Warn(LogSource.App,
                $"Today's record could not be read back for the transmitted view: {ex.Message}",
                @event: "monitor.readfailed");
        }

        _tallyDirty = true;
    }

    /// <summary>
    /// Rewrites the grid from the tally.
    ///
    /// Only when the transmitted view is the one being looked at, and only when the fold has
    /// actually seen something — a page left open on the log while a message plays should not
    /// be rebuilding a grid nobody can see forty times a second.
    /// </summary>
    private void DrawTransmitted()
    {
        if (!_tallyDirty || !ShowingTransmitted) return;

        _tallyDirty = false;
        _transmitted.Clear();

        foreach ((TransmittedEdl edl, IReadOnlyList<TransmittedFile> files) in _tally.Rows())
        {
            string group = edl.Headline.Length > 0 ? edl.Headline : $"EDL {edl.EdlId}";

            foreach (TransmittedFile file in files) _transmitted.Add(ToRow(group, file));
        }

        int aired = _transmitted.Count(r => r.File.Aired);

        TransmittedCountText.Text =
            $"TRANSMITTED  ({aired} of {_transmitted.Count} file(s) to air, " +
            $"{_tally.EdlCount} message{(_tally.EdlCount == 1 ? "" : "s")})";
    }

    private TransmittedRow ToRow(string group, TransmittedFile file) => new(
        Group: group,
        Kind: file.Kind,
        Label: file.IsVideo ? "" : file.Label,
        Channels: file.Channels,
        FileName: file.FileName,
        Path: file.Path,
        State: file.State,
        Times: file.TimesText,
        Last: file.LastAiredText,
        Queued: file.Queued is { } at ? at.ToString("HH:mm:ss") : "-",
        KindBrush: Brush(file.IsVideo ? "Text" : "Tc"),
        StateBrush: Brush(file.Aired ? "Ok" : "Muted"),
        File: file);

    private void View_Changed(object sender, RoutedEventArgs e)
    {
        bool transmitted = ReferenceEquals(sender, TransmittedTab);

        ActivityTab.IsChecked = !transmitted;
        TransmittedTab.IsChecked = transmitted;

        ActivityPanel.Visibility = transmitted ? Visibility.Collapsed : Visibility.Visible;
        TransmittedPanel.Visibility = transmitted ? Visibility.Visible : Visibility.Collapsed;

        // Switching to it is the moment to catch up on everything folded while it was hidden.
        if (transmitted) { _tallyDirty = true; DrawTransmitted(); }
    }

    /// <summary>Opens the folder the file is in, which is what an operator wants from a file name.</summary>
    private void TransmittedList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TransmittedList.SelectedItem is not TransmittedRow row) return;

        try
        {
            if (System.IO.File.Exists(row.Path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.Path}\"")
                { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ActivityLog.Shared.Warn(LogSource.App, $"Could not open {row.FileName}: {ex.Message}");
        }
    }

    // ------------------------------------------------------------------ filtering

    private bool Matches(LogLine line)
    {
        if (line.Level < _minimum) return false;

        if (_area is { } area &&
            (area == LogArea.Errors ? line.Level < LogLevel.Warn : LogDatabase.AreaOf(line) != area))
            return false;

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

        _area = (AreaCombo.SelectedItem as Choice<LogArea?>)?.Value;
        _day = (DayCombo.SelectedItem as Choice<DateTime>)?.Value ?? default;
        _minimum = LevelCombo.SelectedItem is LogLevel level ? level : LogLevel.Info;
        _search = SearchBox.Text.Trim();
        _id = IdBox.Text.Trim();

        Rebuild();
    }

    /// <summary>"Today (live)" is whatever today is, so a page left open past midnight carries on live.</summary>
    private bool Live => _day == default;

    private DateTime ShownDay => Live ? DateTime.Today : _day;

    // The day last read from the database, kept so that typing in the search box filters it in
    // memory rather than reading the whole day again on every key.
    private (DateTime Day, LogArea? Area)? _cacheKey;
    private LogLine[] _cache = Array.Empty<LogLine>();
    private long _cacheThrough;

    /// <summary>
    /// Re-applies the filter to the whole day, read from the database off the UI thread.
    ///
    /// From the record rather than from what is on screen: filtering the rows already shown
    /// would mean a filter could only ever narrow. The day is read once per day and area; the
    /// search, id and level filter what was read. For today, the lines written since the read
    /// come from memory, and live lines carry on from there - in it or after it, never both.
    /// </summary>
    private async void Rebuild()
    {
        int version = ++_rebuild;
        DateTime day = ShownDay;
        LogArea? area = _area;
        bool live = Live;

        _shownThrough = long.MaxValue;

        // Today's read goes stale once more has been written since than memory still holds.
        bool stale = _cacheKey != (day, area) ||
                     (live && ActivityLog.Shared.LastSeq - _cacheThrough > ActivityLog.RingSize / 2);

        if (stale)
        {
            CountText.Text = "ACTIVITY  (reading ...)";

            long through = ActivityLog.Shared.LastSeq;
            LogLine[] lines;

            try { lines = await Task.Run(() => ActivityLog.Shared.ReadDay(day, area)); }
            catch (Exception ex)
            {
                lines = Array.Empty<LogLine>();
                ActivityLog.Shared.Warn(LogSource.App, $"The record for {day:d} could not be read: {ex.Message}",
                                        @event: "monitor.readfailed");
            }

            // A newer filter was asked for while this one read.
            if (version != _rebuild) return;

            // Lines this session wrote after the read began are in the database too; they come
            // from memory instead, so they are not here.
            _cache = lines.Where(l => !(l.At >= SessionStarted && l.Seq > through)).ToArray();
            _cacheKey = (day, area);
            _cacheThrough = through;
        }

        IEnumerable<LogLine> all = _cache;
        long newest = _cacheThrough;

        // Today: what this session has written since the read, and the progress lines that are
        // never stored, from memory; then live from here on.
        if (live)
        {
            var extra = new List<LogLine>();

            foreach (LogLine line in ActivityLog.Shared.Snapshot())
            {
                newest = Math.Max(newest, line.Seq);
                if (line.At.Date != day) continue;
                if (line.Transient || line.Seq > _cacheThrough) extra.Add(line);
            }

            all = _cache.Concat(extra).OrderBy(l => l.At).ThenBy(l => l.Seq);
        }

        var shown = all.Where(Matches).TakeLast(MaxRows).Select(ToRow).ToList();

        _rows.Clear();
        foreach (MonitorRow row in shown) _rows.Add(row);

        if (live) _shownThrough = newest;

        CountText.Text = $"ACTIVITY  ({_rows.Count} line{(_rows.Count == 1 ? "" : "s")} shown" +
                         (live ? ")" : $", {day:ddd d MMM})");

        if (_following && _rows.Count > 0) LogList.ScrollIntoView(_rows[^1]);
    }

    private void ClearFilters_Click(object sender, RoutedEventArgs e)
    {
        AreaCombo.SelectedIndex = 0;
        DayCombo.SelectedIndex = 0;
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


    // ------------------------------------------------------------------ remote monitoring

    /// <summary>
    /// The server that serves this page to other machines, while it is on.
    ///
    /// Owned by the window because the toggle is here, and stopped when the window closes: a
    /// port left open by a page nobody is looking at is a port nobody knows is open.
    /// </summary>
    private MonitorServer? _remote;

    private void Remote_Changed(object sender, RoutedEventArgs e)
    {
        if (RemoteToggle.IsChecked == true) StartRemote(); else StopRemote();
    }

    private void StartRemote()
    {
        _remote ??= new MonitorServer(ActivityLog.Shared, PipelineState.Shared, TidalLock.Shared,
                                      () => _timecode.TryGetCurrent(out Timecode now) ? now : null,
                                      Environment.MachineName, LiveAir.Shared);

        int port = _settings.MonitorApiPort > 0 ? _settings.MonitorApiPort : MonitorServer.DefaultPort;

        if (!_remote.TryStart(port, out string? problem))
        {
            RemoteToggle.IsChecked = false;

            RemoteText.Text = problem;
            RemoteText.Foreground = Brush("Bad");
            RemoteText.Visibility = Visibility.Visible;

            ActivityLog.Shared.Error(LogSource.App, $"Remote monitoring could not start: {problem}",
                                     @event: "monitor.api.failed");
            return;
        }

        _settings.MonitorApi = true;
        _settings.MonitorApiPort = port;
        _settings.Save();

        ShowRemote();
    }

    private void StopRemote()
    {
        _remote?.Stop();

        _settings.MonitorApi = false;
        _settings.Save();

        RemoteText.Visibility = Visibility.Collapsed;
    }

    private void ShowRemote()
    {
        if (_remote is not { Running: true } server)
        {
            RemoteText.Visibility = Visibility.Collapsed;
            return;
        }

        // Every address the machine has, because the operator on the other PC has to be told
        // which one to type and only they know which network they are on.
        RemoteText.Text =
            "Remote monitoring is on - open this in a browser on another machine:   " +
            string.Join("    ", server.Addresses) +
            "\nIf it cannot be reached, Windows is blocking the port. Once, in an administrator " +
            $"command prompt:   {MonitorFeed.FirewallCommand(server.Port)}";

        RemoteText.Foreground = Brush("Tc");
        RemoteText.Visibility = Visibility.Visible;
    }

    // ------------------------------------------------------------------ the strip

    private void OnPipelineChanged() => Dispatcher.BeginInvoke(DrawStrip);
    private void OnTidalLockChanged(TidalLock _) => Dispatcher.BeginInvoke(DrawStrip);
    private void OnLiveAirChanged(LiveAir _) => Dispatcher.BeginInvoke(DrawStrip);

    /// <summary>
    /// Every card from <see cref="PlantStatus"/>, the same reading the remote page draws: the
    /// playback deck's tidal lock or Live Edit's transmission, whichever is in use, the EDL
    /// window's queue or Live Edit's EDL, and the ingest queue.
    /// </summary>
    private void DrawStrip()
    {
        PipelineState state = PipelineState.Shared;
        PlantStatus plant = PlantStatus.Read(state, TidalLock.Shared, LiveAir.Shared);

        TcDisplay.Text = _timecode.TryGetCurrent(out Timecode now) ? now.ToString() : "--:--:--:--";

        Show(CaptureHeadline, CaptureDetail, plant.Capture);
        Show(EdlHeadline, EdlDetail, plant.Edl);
        Show(AirHeadline, AirDetail, plant.OnAir);
        Show(IngestHeadline, IngestDetail, plant.Ingest);
        Show(DelayHeadline, DelayDetail, plant.Delay);
        DrawFrames(plant);

        RecordText.Text = ActivityLog.Shared.Dropped > 0
            ? $"{ActivityLog.Shared.Dropped} line(s) dropped"
            : "";
    }


    /// <summary>
    /// The two frame counts, one under each side, so a drop reads as a number rather than
    /// being suspected from a picture.
    ///
    /// Under CAPTURE: what the recorder took, and what it could not hand on. Under TIDAL LOCK:
    /// what went out against what was recorded - the difference should be the delay and
    /// nothing else - and every repeat and skip on the way. Red the moment anything is lost.
    /// Whichever transmitter holds the delay, the playback deck's or Live Edit's.
    /// </summary>
    private void DrawFrames(PlantStatus plant)
    {
        FrameLedger frames = PipelineState.Shared.Frames;

        if (frames.Recorded == 0 && !PipelineState.Shared.Recording)
        {
            CaptureFrames.Text = "";
        }
        else
        {
            CaptureFrames.Text =
                $"{frames.Recorded:N0} frames" +
                (frames.NotEncoded > 0 ? $"  ·  {frames.NotEncoded:N0} NOT ENCODED" : "  ·  0 dropped") +
                (frames.NotDelayed > 0 ? $"  ·  {frames.NotDelayed:N0} NOT DELAYED" : "");

            CaptureFrames.Foreground = Brush(frames.LostToDisk > 0 ? "Bad" : "Tc");
        }

        if (!plant.Transmitting || frames.Aired == 0)
        {
            DelayFrames.Text = "";
            return;
        }

        // The delay itself accounts for exactly this many frames not yet being out. Anything
        // beyond it is a frame that was recorded and is not going to air.
        long delay = plant.TargetFrames;
        // From the frame of the recording actually on air, not from frames sent: the line can have
        // been filling since the recording began, long before this transmitter started counting.
        // A second of slack, because the recorded count is only published once a second.
        long slack = plant.FrameRate;
        long behind = PipelineState.Shared.AiredFrame is { } airedAt
            ? Math.Max(0, frames.Recorded - (airedAt + 1) - delay - slack)
            : 0;

        DelayFrames.Text =
            $"{frames.Aired:N0} out of {frames.Recorded:N0}  ·  delay {delay:N0}" +
            (behind > 0 ? $"  ·  {behind:N0} BEHIND" : "") +
            (frames.Skipped > 0 ? $"  ·  {frames.Skipped:N0} SKIPPED" : "") +
            (frames.Repeated > 0 ? $"  ·  {frames.Repeated:N0} repeated" : "") +
            (frames.Resyncs > 0 ? $"  ·  {frames.Resyncs} resync" : "") +
            (frames.Skipped == 0 && frames.Repeated == 0 && behind == 0 ? "  ·  no drops" : "");

        DelayFrames.Foreground = Brush(frames.Skipped > 0 || behind > 0 ? "Bad"
                                     : frames.Repeated > 0 ? "Warn"
                                     : "Tc");
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

    // ------------------------------------------------------------------ the machine

    /// <summary>When this process started: lines stamped since are this session's.</summary>
    private static readonly DateTimeOffset SessionStarted = Process.GetCurrentProcess().StartTime;

    private sealed record Choice<T>(string Text, T Value)
    {
        public override string ToString() => Text;
    }

    private static string AreaName(LogArea area) => area switch
    {
        LogArea.LiveEdit => "Live Edit",
        LogArea.Edl => "EDL",
        LogArea.OnAir => "On air",
        _ => area.ToString(),
    };

    /// <summary>One tile: a name, the figure now, a line under it, and the last ten minutes.</summary>
    private sealed class Tile
    {
        public required TextBlock Value { get; init; }
        public required TextBlock Detail { get; init; }
        public required System.Windows.Shapes.Polyline Line { get; init; }
        public required Border Border { get; init; }
    }

    private readonly Dictionary<string, Tile> _tiles = new();

    private Tile TileFor(string name)
    {
        if (_tiles.TryGetValue(name, out Tile? tile)) return tile;

        var label = new TextBlock { Text = name.ToUpperInvariant(), Style = (Style)FindResource("StageLabel") };
        var value = new TextBlock { Style = (Style)FindResource("StageHeadline") };
        var detail = new TextBlock { Style = (Style)FindResource("StageDetail"), TextTrimming = TextTrimming.CharacterEllipsis };
        var line = new System.Windows.Shapes.Polyline { StrokeThickness = 1.2, Stroke = Brush("Tc") };
        var graph = new Canvas { Height = 26, Margin = new Thickness(0, 4, 0, 0), ClipToBounds = true };
        graph.Children.Add(line);

        var panel = new StackPanel();
        panel.Children.Add(label);
        panel.Children.Add(value);
        panel.Children.Add(detail);
        panel.Children.Add(graph);

        var border = new Border { Style = (Style)FindResource("StageCard"), Child = panel };
        ResourcePanel.Children.Add(border);

        return _tiles[name] = new Tile { Value = value, Detail = detail, Line = line, Border = border };
    }

    /// <summary>Redraws the tiles from the sampler's last ten minutes. Once a second.</summary>
    private void DrawResources()
    {
        ResourceSample[] history = ResourceSampler.Shared.History();
        if (history.Length == 0) return;

        ResourceSample now = history[^1];

        Show(TileFor("CPU"), $"{now.CpuPercent:0}%", $"{Environment.ProcessorCount} logical processors",
             history.Select(s => s.CpuPercent), 100);

        double ramPercent = now.RamTotalBytes > 0 ? 100.0 * now.RamUsedBytes / now.RamTotalBytes : 0;
        Show(TileFor("RAM"), $"{ramPercent:0}%", $"{Gb(now.RamUsedBytes)} of {Gb(now.RamTotalBytes)} GB in use",
             history.Select(s => s.RamTotalBytes > 0 ? 100.0 * s.RamUsedBytes / s.RamTotalBytes : 0), 100);

        Show(TileFor("Emerald"), $"{Gb(now.EmeraldPrivateBytes + now.FfmpegPrivateBytes)} GB",
             $"app {Gb(now.EmeraldPrivateBytes)} GB · ffmpeg {Gb(now.FfmpegPrivateBytes)} GB",
             history.Select(s => (double)(s.EmeraldPrivateBytes + s.FfmpegPrivateBytes)),
             Math.Max(1, now.RamTotalBytes), warnAt: double.MaxValue);

        if (now.GpuPercent is { } gpu)
            Show(TileFor("GPU"), $"{gpu:0}%",
                 $"encode {now.GpuEncodePercent ?? 0:0}%" + (now.GpuMemoryBytes is { } m ? $" · {Gb(m)} GB memory" : ""),
                 history.Select(s => s.GpuPercent ?? 0), 100);

        foreach (DiskSample disk in now.Disks)
        {
            Show(TileFor(disk.Name), $"{disk.ActivePercent:0}% active",
                 $"R {Mb(disk.ReadBytesPerSecond)} · W {Mb(disk.WriteBytesPerSecond)} MB/s" +
                 (disk.TotalBytes > 0 ? $" · {Gb(disk.FreeBytes, 0)} GB free" : ""),
                 history.Select(s => s.Disks.FirstOrDefault(d => d.Name == disk.Name)?.ActivePercent ?? 0), 100);
        }

        ResourcePanel.Columns = _tiles.Count;
    }

    private void Show(Tile tile, string value, string detail, IEnumerable<double> values, double max,
                      double warnAt = 70)
    {
        double[] points = values.ToArray();
        double latest = points.Length == 0 ? 0 : points[^1];
        double level = max > 0 ? 100 * latest / max : 0;

        tile.Value.Text = value;
        tile.Detail.Text = detail;
        tile.Value.Foreground = Brush(level >= 90 && warnAt < double.MaxValue ? "Bad" : level >= warnAt ? "Warn" : "Text");

        double width = Math.Max(40, tile.Border.ActualWidth - 24);
        const double height = 26;
        var line = new PointCollection(points.Length);

        for (int i = 0; i < points.Length; i++)
        {
            double x = width * (i + ResourceSampler.HistoryLength - points.Length) / (ResourceSampler.HistoryLength - 1);
            double y = height - height * Math.Clamp(points[i] / max, 0, 1);
            line.Add(new Point(x, y));
        }

        tile.Line.Points = line;
    }

    private static string Gb(long bytes, int decimals = 1) =>
        (bytes / 1024.0 / 1024 / 1024).ToString(decimals == 0 ? "N0" : "N1");

    private static string Mb(double bytesPerSecond) => (bytesPerSecond / 1024 / 1024).ToString("0.0");

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = ActivityLog.Shared.Database is { } db
            ? System.IO.Path.GetDirectoryName(db.Path)!
            : _settings.DataFolderOrDefault;

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
