using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Emerald.Core;
using Emerald.Deltacast;
using Emerald.Media;
using Microsoft.Win32;

namespace Emerald.Ingest;

/// <summary>One line in the activity log.</summary>
public sealed record IngestLogRow(string Time, string Message, Brush Brush);

/// <summary>
/// The Ingest Controller screen.
///
/// It is a view and nothing more. It reads fields, hands them to
/// <see cref="IIngestControllerService"/> as a request, and draws back what the service
/// says — the queue, the log, the schedule, the recent clips. It never opens a receiver,
/// never starts an encoder and never decides when a recording rolls; the one calculation it
/// performs is the one it has to show you as you type, and even that is delegated to
/// <see cref="ITimecodeCalculationService"/> so the number on screen is the number the
/// scheduler will use.
///
/// Code-behind rather than a view model, because that is how every other Emerald window is
/// written and a module that felt like a different application would be a worse module.
/// </summary>
public partial class IngestControllerWindow : Window
{
    private const int FallbackFrameRate = 25;

    private readonly AppSettings _settings;

    /// <summary>The application's one clock, shared with the deck and the EDL Generator.</summary>
    private readonly TimecodeService _timecode = TimecodeLink.Service;
    private readonly ClipNameService _clipNames = new();
    private readonly ObservableCollection<IngestLogRow> _log = new();
    private readonly ObservableCollection<IngestQueueRow> _queueRows = new();
    private readonly ObservableCollection<IngestRecordingRow> _recentRows = new();

    private readonly DispatcherTimer _uiTimer;

    private IIngestControllerService? _controller;
    private IngestHistoryWindow? _history;

    private IReadOnlyList<BoardInfo> _boards = Array.Empty<BoardInfo>();
    private int _frameRate = FallbackFrameRate;
    private string _lastLinkRender = "";

    /// <summary>Suppresses field handlers while the form is being filled in programmatically.</summary>
    private bool _initialising = true;

    /// <summary>Suppresses the derived field's own change handler while it is being written.</summary>
    private bool _syncingTiming;

    /// <summary>
    /// Which of EOM and Duration is currently the calculated one.
    ///
    /// There is no mode to choose: whichever of the pair the operator last typed into is the
    /// one they are driving, and the other follows. Editing the reference timecode then
    /// moves whichever is still derived, leaving the one they set alone.
    /// </summary>
    private IngestTimingMode _timingMode = IngestTimingMode.DurationControlsEom;

    /// <summary>The last validation, so the buttons and the preview agree with the labels.</summary>
    private IngestValidation? _validation;

    /// <summary>
    /// The shell hands its own settings in, so the ingest records with the profile chosen on
    /// the capture deck. Opened on its own, it loads its own.
    /// </summary>
    public IngestControllerWindow(AppSettings? settings = null)
    {
        _settings = settings ?? AppSettings.Load();

        InitializeComponent();

        LogList.ItemsSource = _log;
        QueueList.ItemsSource = _queueRows;
        RecentList.ItemsSource = _recentRows;

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
        _uiTimer.Tick += UiTimer_Tick;

        Loaded += Window_Loaded;
        Closing += Window_Closing;
    }

    // ------------------------------------------------------------------ lifecycle

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        SomBox.Text = _settings.IngestSom;
        DurationBox.Text = _settings.IngestDuration;
        MetadataBox.Text = _settings.IngestMetadata;
        SimulateToggle.IsChecked = _settings.IngestMockMode;

        _timingMode = _settings.IngestTimingMode == "eom"
            ? IngestTimingMode.EomControlsDuration
            : IngestTimingMode.DurationControlsEom;

        // A blank setting means the operator has never chosen. Point at the Emerald media
        // store, so an ingested clip lands where Live Edit and the deck can already see it.
        DirectoryBox.Text = string.IsNullOrWhiteSpace(_settings.IngestDirectory)
                          ? MediaLibrary.DefaultFolder
                          : _settings.IngestDirectory;

        ClipNameBox.Text = _clipNames.Generate();

        TimecodeLink.Connect(_settings);
        TimecodeLink.UrlChanged += OnTimecodeUrlChanged;
        ApiUrlBox.Text = TimecodeLink.Url;

        _uiTimer.Start();

        BuildController();
        _initialising = false;

        await ScanBoardsAsync();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // A live ingest is not something to lose to a closed window. It is stopped
        // deliberately, with the operator's agreement, or the window stays open.
        IReadOnlyList<IngestJob> live = _controller?.Queue()
            .Where(j => j.Status == IngestStatus.Recording)
            .ToList() ?? new List<IngestJob>();

        if (live.Count > 0)
        {
            MessageBoxResult answer = MessageBox.Show(this,
                $"{live.Count} ingest(s) are recording right now.\n\n" +
                "Closing the Ingest Controller will stop them. Close anyway?",
                "Ingest Controller", MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _uiTimer.Stop();

        _settings.IngestSom = SomBox.Text.Trim();
        _settings.IngestDuration = DurationBox.Text.Trim();
        _settings.IngestDirectory = DirectoryBox.Text.Trim();
        _settings.IngestMetadata = MetadataBox.Text;
        _settings.IngestMockMode = SimulateToggle.IsChecked == true;
        _settings.IngestTimingMode = _timingMode == IngestTimingMode.EomControlsDuration ? "eom" : "duration";
        if (SelectedBoard is { } board) _settings.IngestBoardIndex = board.Index;
        if (PortCombo.SelectedItem is ChannelPort port) _settings.IngestPort = port.Name;
        _settings.Save();

        _controller?.Dispose();
        _controller = null;

        // The clock belongs to the application; this window joined it rather than owning it.
        // The unsubscribe does matter: a static event would otherwise hold a closed window.
        TimecodeLink.UrlChanged -= OnTimecodeUrlChanged;

        _history?.Close();
    }

    // ------------------------------------------------------------------ the service

    /// <summary>
    /// Stands the controller service up, wired to either the real hardware and the station
    /// clock or to their simulated counterparts. Switching between the two rebuilds it,
    /// which is why it is a method rather than a line in the constructor.
    /// </summary>
    private void BuildController()
    {
        _controller?.Dispose();

        bool mock = SimulateToggle.IsChecked == true;

        IIngestHardware hardware = mock ? new MockIngestHardware() : new DeltacastIngestHardware();
        IIngestClock clock = mock
            ? new SystemIngestClock(_timecode.FrameRate > 0 ? _timecode.FrameRate : FallbackFrameRate)
            : new TimecodeServiceClock(_timecode);

        var log = new IngestLog();
        log.Entry += entry => Dispatcher.BeginInvoke(() => Append(entry));

        _controller = new IngestControllerService(_settings, clock, hardware, log: log);
        _controller.Scheduler.QueueChanged += () => Dispatcher.BeginInvoke(RenderQueue);
        _controller.Scheduler.JobChanged += _ => Dispatcher.BeginInvoke(RenderQueue);

        _controller.Initialise();

        MockBanner.Visibility = mock ? Visibility.Visible : Visibility.Collapsed;

        RenderQueue();
        RenderRecent();
    }

    private void Simulate_Changed(object sender, RoutedEventArgs e)
    {
        if (_initialising) return;

        // Never mid-flight. Swapping the recorder underneath a running ingest would leave a
        // half-written clip nothing is accounting for.
        bool busy = _controller?.Queue().Any(j => !IngestStatusRules.IsTerminal(j.Status)) == true;

        if (busy)
        {
            Log("Cannot change simulate mode while ingests are queued or recording.", IngestLogLevel.Warn);
            _initialising = true;
            SimulateToggle.IsChecked = _controller!.Hardware.IsMock;
            _initialising = false;
            return;
        }

        BuildController();
        _ = ScanBoardsAsync();
    }

    // ------------------------------------------------------------------ boards

    private BoardInfo? SelectedBoard => BoardCombo.SelectedItem as BoardInfo;

    private async Task ScanBoardsAsync()
    {
        if (_controller is null) return;

        RescanButton.IsEnabled = false;
        BoardInfoText.Text = "Scanning for DELTACAST boards...";

        IngestHardwareScan scan = await _controller.Hardware.ScanAsync();

        _boards = scan.CaptureBoards;
        ApiInfoText.Text = scan.Summary;

        bool wasInitialising = _initialising;
        _initialising = true;

        BoardCombo.ItemsSource = _boards;
        if (_boards.Count > 0)
        {
            BoardCombo.SelectedItem =
                _boards.FirstOrDefault(b => b.Index == _settings.IngestBoardIndex) ?? _boards[0];
        }

        _initialising = wasInitialising;

        PopulatePorts();

        if (scan.Error is { } error)
        {
            BoardInfoText.Text = error;
            Log(error, IngestLogLevel.Warn);
        }
        else
        {
            foreach (BoardInfo b in scan.Boards)
                Log($"Board {b.Index}: {b.Model} ({b.BoardTypeName}) - {b.RxCount} RX / {b.TxCount} TX");
        }

        if (_boards.Count == 0 && !_controller.Hardware.IsMock)
        {
            Log("No board with an RX channel was found, so nothing can be ingested. " +
                "Turn on SIMULATE to work without a card.", IngestLogLevel.Error);
        }

        RescanButton.IsEnabled = true;
        Recompute();
    }

    private void PopulatePorts()
    {
        bool wasInitialising = _initialising;
        _initialising = true;

        BoardInfo? board = SelectedBoard;
        PortCombo.ItemsSource = board?.RxPorts;

        if (board is not null)
        {
            BoardInfoText.Text = $"{board.BoardTypeName} - {board.RxCount} RX / {board.TxCount} TX";

            PortCombo.SelectedItem =
                board.RxPorts.FirstOrDefault(p => p.Name == _settings.IngestPort) ?? board.RxPorts.FirstOrDefault();

            PortInfoText.Text = board.RxCount == 0
                ? "No RX channels on this board."
                : $"Available: {string.Join(", ", board.RxPorts.Select(p => p.Name))}";

            PortInfoText.Foreground = board.RxCount == 0 ? Brush("Warn") : Brush("Muted", "#6B7382");
        }
        else
        {
            BoardInfoText.Text = "No board selected.";
            PortInfoText.Text = "-";
        }

        PortCombo.IsEnabled = board is { RxCount: > 0 };
        _initialising = wasInitialising;
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanBoardsAsync();

    private void BoardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising) return;
        PopulatePorts();
        Recompute();
    }

    private void Field_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising) return;
        Recompute();
    }

    // ------------------------------------------------------------------ clock

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        TcDisplay.Text = _timecode.TryGetCurrent(out Timecode now) ? now.ToString() : "--:--:--:--";

        int rate = _timecode.FrameRate;
        if (rate > 0 && rate != _frameRate)
        {
            _frameRate = rate;
            Recompute();
        }

        // The status line only changes on transitions; re-rendering it forty times a second
        // would fight the operator for the UI thread and show them nothing new.
        string render = $"{_timecode.State}|{_timecode.LastError}|{_timecode.LastResponse?.SourceStatus}|{rate}";
        if (render == _lastLinkRender) return;
        _lastLinkRender = render;

        switch (_timecode.State)
        {
            case TimecodeLinkState.Online:
                TimecodeApiResponse r = _timecode.LastResponse!;
                bool locked = string.Equals(r.SourceStatus, "LOCKED", StringComparison.OrdinalIgnoreCase);
                TcDot.Fill = Brush(locked ? "Ok" : "Warn");
                TcStatusText.Foreground = Brush(locked ? "Ok" : "Warn");
                TcStatusText.Text = $"{r.TimecodeType} - {r.SourceStatus} - {rate} fps";
                break;

            case TimecodeLinkState.Offline:
                TcDot.Fill = Brush("Bad");
                TcStatusText.Foreground = Brush("Bad");
                TcStatusText.Text = _controller?.Clock.IsMock == true
                    ? _controller.Clock.StatusText
                    : "offline";
                break;

            default:
                TcDot.Fill = Brush("Muted");
                TcStatusText.Foreground = Brush("Muted");
                TcStatusText.Text = "connecting...";
                break;
        }
    }

    private void ApplyApi_Click(object sender, RoutedEventArgs e)
    {
        if (!TimecodeLink.TrySetUrl(ApiUrlBox.Text, out string? problem))
        {
            Log(problem!, IngestLogLevel.Error);
            return;
        }

        _lastLinkRender = "";
        Log($"Timecode generator set to {TimecodeLink.Url} - every Emerald module now reads it.",
            IngestLogLevel.Ok);
    }

    /// <summary>Another module changed the generator's address; show the new one.</summary>
    private void OnTimecodeUrlChanged(string url) => Dispatcher.BeginInvoke(() =>
    {
        if (ApiUrlBox.Text.Trim() == url) return;

        ApiUrlBox.Text = url;
        _lastLinkRender = "";
        Log($"Timecode generator changed to {url} by another module.");
    });

    private void Now_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;

        if (!_controller.Clock.TryGetCurrent(out Timecode now))
        {
            Log("No realtime timecode is available yet.", IngestLogLevel.Warn);
            return;
        }

        ReferenceBox.Text = now.ToString();
    }

    private void RegenerateName_Click(object sender, RoutedEventArgs e)
    {
        Timecode? now = _controller is not null && _controller.Clock.TryGetCurrent(out Timecode tc) ? tc : null;
        ClipNameBox.Text = _clipNames.Generate(now);
    }

    // ------------------------------------------------------------------ timing fields

    private void Field_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initialising) return;
        Recompute();
    }

    private void Reference_Changed(object sender, TextChangedEventArgs e) => Field_Changed(sender, e);
    private void Som_Changed(object sender, TextChangedEventArgs e) => Field_Changed(sender, e);

    /// <summary>
    /// Typing in Duration makes EOM the calculated one. Only a real keystroke counts — the
    /// write that lands here when EOM is being derived is guarded, so the pair cannot chase
    /// each other round.
    /// </summary>
    private void Duration_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initialising || _syncingTiming) return;

        _timingMode = IngestTimingMode.DurationControlsEom;
        Recompute();
    }

    private void Eom_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initialising || _syncingTiming) return;

        _timingMode = IngestTimingMode.EomControlsDuration;
        Recompute();
    }

    private void Metadata_Changed(object sender, TextChangedEventArgs e)
    {
        MetadataCount.Text = MetadataBox.Text.Length.ToString();
        if (_initialising) return;
        Recompute();
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the directory for ingested clips" };
        if (Directory.Exists(DirectoryBox.Text.Trim())) dialog.InitialDirectory = DirectoryBox.Text.Trim();

        if (dialog.ShowDialog(this) == true) DirectoryBox.Text = dialog.FolderName;
    }

    // ------------------------------------------------------------------ derive + validate

    private IngestRequest CurrentRequest()
    {
        var port = PortCombo.SelectedItem as ChannelPort;
        BoardInfo? board = SelectedBoard;

        return new IngestRequest
        {
            BoardIndex = board?.Index ?? 0,
            BoardName = board?.Model ?? "",
            Port = port?.Name ?? "",
            PortIndex = port?.Index ?? -1,
            FrameRate = _controller?.Clock.FrameRate is > 0 and { } rate ? rate : _frameRate,
            ReferenceTimecode = ReferenceBox.Text.Trim(),
            Som = SomBox.Text.Trim(),
            Eom = EomBox.Text.Trim(),
            Duration = DurationBox.Text.Trim(),
            TimingMode = _timingMode,
            ClipName = ClipNameBox.Text.Trim(),
            Metadata = MetadataBox.Text,
            Directory = DirectoryBox.Text.Trim(),
            Mock = _controller?.Hardware.IsMock ?? false,
        };
    }

    /// <summary>
    /// Fills in whichever of EOM and Duration is derived, redraws the schedule preview, and
    /// re-runs validation. Called on every keystroke, so it does no work the operator cannot
    /// see the result of.
    /// </summary>
    private void Recompute()
    {
        if (_controller is null) return;

        int rate = _controller.Clock.FrameRate > 0 ? _controller.Clock.FrameRate : _frameRate;
        ITimecodeCalculationService calc = _controller.Timecodes;

        bool haveReference = Timecode.TryParse(ReferenceBox.Text, rate, out Timecode reference, out _);

        Timecode som = Timecode.Zero(rate);
        string somText = SomBox.Text.Trim();
        bool haveSom = somText.Length == 0 || Timecode.TryParse(somText, rate, out som, out _);

        // The derived half of the EOM/Duration pair, written back into its field. Guarded, so
        // the write does not come back round as another edit.
        _syncingTiming = true;
        try
        {
            if (haveReference && _timingMode == IngestTimingMode.DurationControlsEom)
            {
                if (Timecode.TryParse(DurationBox.Text, rate, out Timecode duration, out _))
                    EomBox.Text = calc.CalculateEomFromDuration(reference, duration).ToString();
            }
            else if (haveReference)
            {
                if (Timecode.TryParse(EomBox.Text, rate, out Timecode eom, out _))
                    DurationBox.Text = calc.CalculateDurationFromEom(reference, eom).ToString();
            }
        }
        finally
        {
            _syncingTiming = false;
        }

        // Both stay editable - either is a valid thing to type into. The calculated one is
        // tinted, so it is clear at a glance which way the arithmetic is currently running.
        DurationBox.Foreground = _timingMode == IngestTimingMode.EomControlsDuration ? Brush("Tc") : Brush("Text");
        EomBox.Foreground = _timingMode == IngestTimingMode.DurationControlsEom ? Brush("Tc") : Brush("Text");

        // Where the clip's own timecode ends up: it starts at SOM and runs for the duration.
        if (haveSom && Timecode.TryParse(DurationBox.Text, rate, out Timecode length, out _))
            MediaEndBox.Text = som.AddWrapping(length.TotalFrames).ToString();

        IngestValidation validation = _controller.Validate(CurrentRequest());
        _validation = validation;

        ShowError(BoardError, validation.For(IngestFields.Board) ?? validation.For(IngestFields.Port));
        ShowError(ReferenceError, validation.For(IngestFields.Reference));
        ShowError(SomEomError, validation.For(IngestFields.Som) ?? validation.For(IngestFields.Eom));
        ShowError(DurationError, validation.For(IngestFields.Duration));
        ShowError(ClipNameError, validation.For(IngestFields.ClipName));

        RenderDirectoryHint(validation);
        RenderPreview(validation, reference, som, rate, haveReference && haveSom);
        RenderStatusPanel(validation);

        StartButton.IsEnabled = validation.IsValid;
        StartStatusText.Foreground = Brush("Muted");
        StartStatusText.Text = validation.IsValid
            ? validation.Warnings.FirstOrDefault() ?? ""
            : validation.Messages.FirstOrDefault() ?? "";
    }

    private void RenderPreview(IngestValidation validation, Timecode reference, Timecode som,
                               int rate, bool usable)
    {
        if (_controller is null) return;

        if (!usable)
        {
            PreviewReference.Text = PreviewActualStart.Text = PreviewEom.Text =
                PreviewDuration.Text = PreviewMediaStart.Text = "--:--:--:--";
            return;
        }

        ITimecodeCalculationService calc = _controller.Timecodes;

        Timecode duration = Timecode.TryParse(DurationBox.Text, rate, out Timecode d, out _)
            ? d : Timecode.Zero(rate);

        // The recorder rolls on the start timecode itself; SOM is what the file will read.
        PreviewReference.Text = reference.ToString();
        PreviewActualStart.Text = reference.ToString();
        PreviewEom.Text = calc.CalculateEomFromDuration(reference, duration).ToString();
        PreviewDuration.Text = duration.ToString();
        PreviewMediaStart.Text = som.ToString();

        PreviewNote.Text = validation.EstimatedBytes is { } bytes and > 0
            ? $"Rolls at {reference} and records {duration} - about {DiskSpace.Describe(bytes)} " +
              $"across the master and its proxy. The file will read {som} at its first frame."
            : "Recording rolls on the start timecode and stops at EOM. SOM is written into the file.";
    }

    private void RenderStatusPanel(IngestValidation validation)
    {
        BoardInfo? board = SelectedBoard;
        var port = PortCombo.SelectedItem as ChannelPort;

        StatusBoardText.Text = board is null ? "-" : board.DisplayName;
        StatusPortText.Text = port?.Name ?? "-";

        // What the receiver in front of the operator is doing beats what the form thinks.
        IngestJob? live = board is not null && port is not null
            ? _controller?.Scheduler.RecordingOn(board.Index, port.Index)
            : null;

        if (live is not null)
        {
            StatusStateText.Text = "RECORDING";
            StatusStateText.Foreground = Brush("Ok");
            ReadyDot.Fill = Brush("Ok");
            ReadyText.Foreground = Brush("Ok");
            ReadyText.Text = $"Recording {live.ClipName} - {live.ProgressText}";
            return;
        }

        IReadOnlyList<IngestJob> queue = _controller?.Queue() ?? Array.Empty<IngestJob>();
        int waiting = queue.Count(j => IngestStatusRules.IsPending(j.Status));

        StatusStateText.Text = waiting > 0 ? "WAITING" : "IDLE";
        StatusStateText.Foreground = Brush(waiting > 0 ? "Warn" : "Muted");

        if (validation.IsValid)
        {
            ReadyDot.Fill = Brush("Ok");
            ReadyText.Foreground = Brush("Ok");
            ReadyText.Text = "Ready to ingest";
        }
        else
        {
            ReadyDot.Fill = Brush("Warn");
            ReadyText.Foreground = Brush("Warn");
            ReadyText.Text = validation.Messages.FirstOrDefault() ?? "Not ready";
        }
    }

    private void RenderDirectoryHint(IngestValidation validation)
    {
        string folder = DirectoryBox.Text.Trim();

        if (validation.For(IngestFields.Directory) is { } problem)
        {
            DirectoryHint.Text = problem;
            DirectoryHint.Foreground = Brush("Bad");
            return;
        }

        long? free = DiskSpace.AvailableBytes(folder);

        DirectoryHint.Text = free is { } bytes
            ? $"{DiskSpace.Describe(bytes)} free. Masters are written to \\high, proxies to \\low."
            : "Masters are written to \\high, proxies to \\low.";

        DirectoryHint.Foreground = Brush("Muted", "#6B7382");
    }

    private static void ShowError(TextBlock label, string? message)
    {
        label.Text = message ?? "";
        label.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ starting an ingest

    private void StartIngest_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;

        IngestValidation result = _controller.StartIngest(CurrentRequest());

        if (!result.IsValid)
        {
            StartStatusText.Foreground = Brush("Bad");
            StartStatusText.Text = result.Messages.FirstOrDefault() ?? "This ingest cannot be scheduled.";
            Recompute();
            return;
        }

        // A fresh name for the next one, so two ingests booked back to back never collide.
        Timecode? now = _controller.Clock.TryGetCurrent(out Timecode tc) ? tc : null;
        ClipNameBox.Text = _clipNames.Generate(now);

        RenderQueue();
        Recompute();
    }

    private void CancelJob_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || _controller is null) return;
        _controller.Cancel(row.Id);
        RenderQueue();
    }

    private void RemoveJob_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row || _controller is null) return;
        _controller.Remove(row.Id);
        RenderQueue();
    }

    private void ClearFinished_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;

        foreach (IngestJob job in _controller.Queue().Where(j => IngestStatusRules.IsTerminal(j.Status)).ToList())
            _controller.Remove(job.Id);

        RenderQueue();
    }

    private IngestQueueRow? RowOf(object sender) =>
        (sender as FrameworkElement)?.DataContext as IngestQueueRow;

    // ------------------------------------------------------------------ queue + recents

    private void RenderQueue()
    {
        if (_controller is null) return;

        IReadOnlyList<IngestJob> jobs = _controller.Queue();

        _queueRows.Clear();
        for (int i = 0; i < jobs.Count; i++) _queueRows.Add(IngestQueueRow.From(jobs[i], i + 1));

        QueueEmptyText.Visibility = jobs.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int pending = jobs.Count(j => IngestStatusRules.IsPending(j.Status));
        int recording = jobs.Count(j => j.Status == IngestStatus.Recording);

        QueueHeader.Text = recording > 0
            ? $"INGEST QUEUE  ({recording} recording, {pending} waiting)"
            : pending > 0 ? $"INGEST QUEUE  ({pending} waiting)" : "INGEST QUEUE";

        RenderRecent();
        RenderStatusPanel(_validation ?? new IngestValidation());
    }

    private void RenderRecent()
    {
        if (_controller is null) return;

        IReadOnlyList<IngestRecording> recent = _controller.RecentRecordings(6);

        _recentRows.Clear();
        foreach (IngestRecording r in recent) _recentRows.Add(IngestRecordingRow.From(r));

        RecentEmptyText.Visibility = recent.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ViewAll_Click(object sender, RoutedEventArgs e)
    {
        if (_controller is null) return;

        if (_history is null)
        {
            _history = new IngestHistoryWindow(_controller) { Owner = this };
            _history.Closed += (_, _) => _history = null;
            _history.Show();
            return;
        }

        if (_history.WindowState == WindowState.Minimized) _history.WindowState = WindowState.Normal;
        _history.Activate();
    }

    // ------------------------------------------------------------------ log

    private void Append(IngestLogEntry entry)
    {
        Brush brush = entry.Level switch
        {
            IngestLogLevel.Ok => Brush("Ok"),
            IngestLogLevel.Warn => Brush("Warn"),
            IngestLogLevel.Error => Brush("Bad"),
            _ => Brush("Info"),
        };

        _log.Add(new IngestLogRow(entry.Time, entry.Message, brush));
        while (_log.Count > 500) _log.RemoveAt(0);

        LogScroller.ScrollToEnd();
    }

    private void Log(string message, IngestLogLevel level = IngestLogLevel.Info) =>
        Append(new IngestLogEntry(DateTime.Now, message, level));

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private static Brush Brush(string key, string? fallbackHex = null)
    {
        if (fallbackHex is null && Application.Current?.TryFindResource(key) is Brush brush) return brush;
        if (fallbackHex is not null) return (Brush)new BrushConverter().ConvertFromString(fallbackHex)!;
        return Brushes.White;
    }
}
