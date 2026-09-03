using Emerald.Core;
using Emerald.Deltacast;
using Emerald.Video;
using Emerald.Media;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;

namespace Emerald.Edl;

public sealed record LogEntry(string Time, string Message, Brush Brush);

/// <summary>One row in the queue panel: everything already flattened for display.</summary>
public sealed record QueueRow(string Headline, string Timing, string Media, Brush Accent);

public partial class EdlWindow : Window
{
    private const int FallbackFrameRate = 25;

    private readonly AppSettings _settings;

    /// <summary>The application's one clock, shared with the deck and the Ingest Controller.</summary>
    private readonly TimecodeService _timecode = TimecodeLink.Service;
    private PlayoutService? _playout;
    private readonly SdiCapture _capture = new();
    private bool _captureArmed;
    private readonly ObservableCollection<LogEntry> _log = new();
    private readonly ObservableCollection<QueueRow> _queueRows = new();

    /// <summary>Last announced playing/next pair, so the status is logged only when it changes.</summary>
    private string _lastQueueAnnouncement = "";

    /// <summary>The composed command as JSON, for the Copy JSON button.</summary>
    private string _recordJson = "";

    private readonly DispatcherTimer _uiTimer;

    private IReadOnlyList<BoardInfo> _boards = Array.Empty<BoardInfo>();
    private MediaSelection? _media;

    /// <summary>ffprobe result for the first file - informational only.</summary>
    private MediaInfo? _mediaInfo;
    private string? _ffprobePath;
    private string _pendingCommandId = Guid.NewGuid().ToString("N");

    /// <summary>Suppresses field handlers while the UI is being populated programmatically.</summary>
    private bool _initialising = true;

    private int _frameRate = FallbackFrameRate;
    private string _lastLinkRender = "";

    /// <summary>
    /// The shell hands its own settings in, so a recording profile chosen on the capture
    /// deck is the one the EDL records with. Opened on its own, it loads its own.
    /// </summary>
    public EdlWindow(AppSettings? settings = null)
    {
        _settings = settings ?? AppSettings.Load();

        InitializeComponent();
        LogList.ItemsSource = _log;
        QueueList.ItemsSource = _queueRows;

        PostPlayCombo.ItemsSource = new[] { "Black Screen", "Freeze on last frame" };

        _uiTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(25) };
        _uiTimer.Tick += UiTimer_Tick;

        Loaded += EdlWindow_Loaded;
        Closing += EdlWindow_Closing;
    }

    // ------------------------------------------------------------------ lifecycle

    private async void EdlWindow_Loaded(object sender, RoutedEventArgs e)
    {
        ApiUrlBox.Text = _settings.TimecodeApiUrl;
        StartTcBox.Text = _settings.StartTimecode;
        SomBox.Text = _settings.Som;
        EomBox.Text = _settings.Eom;
        PostPlayCombo.SelectedIndex = _settings.PostPlay == "freezeLastFrame" ? 1 : 0;
        // A blank setting means the operator has never chosen: point at the Emerald media
        // store so recordings land somewhere Live Edit can find them, rather than nowhere.
        CaptureFolderBox.Text = string.IsNullOrWhiteSpace(_settings.CaptureFolder)
                              ? MediaLibrary.DefaultFolder
                              : _settings.CaptureFolder;
        AudioTrackList.ItemsSource = _audioTracks;

        Log("EDL Generator started.", LogLevel.Info);

        // ffprobe is located before media is loaded so the probe can report length and
        // stream layout in the media summary.
        string? ffmpeg = Ffmpeg.Locate(_settings.FfmpegPath);
        _ffprobePath = MediaProbe.LocateFfprobe(ffmpeg);

        SetMedia(MediaScanner.Resolve(_settings.MediaSource), announce: false);

        _playout = new PlayoutService(_timecode, ffmpeg);
        _capture.Message += (text, problem) =>
            Dispatcher.BeginInvoke(() => Log(text, problem ? LogLevel.Error : LogLevel.Info));

        _playout.Progress += OnPlayoutProgress;
        _playout.QueueChanged += OnQueueChanged;

        foreach (AudioTrackSetting saved in _settings.AudioTracks)
            RestoreAudioTrack(saved);

        RenumberAudioTracks();

        if (ffmpeg is null)
            Log("ffmpeg was not found - SDI playout is unavailable. Install it, or set \"ffmpegPath\" in settings.json.",
                LogLevel.Warn);
        else
            Log($"Decoder: {ffmpeg}", LogLevel.Info);

        TimecodeLink.Connect(_settings);
        TimecodeLink.UrlChanged += OnTimecodeUrlChanged;
        _uiTimer.Start();

        _initialising = false;
        await ScanBoardsAsync();
    }

    private void EdlWindow_Closing(object? sender, CancelEventArgs e)
    {
        _uiTimer.Stop();
        _capture.Dispose();
        _playout?.Dispose();

        // The clock belongs to the application, not to this window: it is joined, not owned,
        // and the deck or an ingest may still be reading it. Unsubscribing matters though —
        // the event is static and would otherwise hold a closed window alive.
        TimecodeLink.UrlChanged -= OnTimecodeUrlChanged;

        _settings.StartTimecode = StartTcBox.Text.Trim();
        _settings.Som = SomBox.Text.Trim();
        _settings.Eom = EomBox.Text.Trim();
        _settings.PostPlay = SelectedPostPlay == PostPlay.FreezeLastFrame ? "freezeLastFrame" : "blackScreen";
        _settings.MediaSource = _media?.Path ?? "";
        _settings.CaptureFolder = CaptureFolderBox.Text.Trim();
        _settings.AudioTracks = _audioTracks
            .Select(t => new AudioTrackSetting
            {
                Label = t.Label,
                Source = t.Selection.Path,
                OffsetMs = t.OffsetMs,
                IsDefault = t.IsDefault,
            })
            .ToList();
        if (CaptureBoard is { } cb) _settings.CaptureBoardIndex = cb.Index;
        if (PlaybackBoard is { } pb) _settings.PlaybackBoardIndex = pb.Index;
        if (CaptureCombo.SelectedItem is ChannelPort rx) _settings.CapturePort = rx.Name;
        if (PlaybackCombo.SelectedItem is ChannelPort tx) _settings.PlaybackPort = tx.Name;
        _settings.Save();
    }

    // ------------------------------------------------------------------ boards

    private BoardInfo? CaptureBoard => CaptureBoardCombo.SelectedItem as BoardInfo;
    private BoardInfo? PlaybackBoard => PlaybackBoardCombo.SelectedItem as BoardInfo;

    private async Task ScanBoardsAsync()
    {
        RefreshBoardsButton.IsEnabled = false;
        CaptureBoardInfo.Text = PlaybackBoardInfo.Text = "Scanning for DELTACAST boards...";

        BoardScanResult result = await BoardService.ScanAsync();

        _boards = result.Boards;
        ApiInfoText.Text = $"VideoMaster SDK {result.ApiVersionString} - {_boards.Count} board(s) detected";

        bool wasInitialising = _initialising;
        _initialising = true;

        CaptureBoardCombo.ItemsSource = _boards;
        PlaybackBoardCombo.ItemsSource = _boards;

        if (_boards.Count > 0)
        {
            // Fall back to the first board that can actually do the job in that direction:
            // a 12G-elp-h with 8 RX and 0 TX is a valid capture board but never a playback one.
            CaptureBoardCombo.SelectedItem = PickBoard(_settings.CaptureBoardIndex, b => b.RxCount > 0);
            PlaybackBoardCombo.SelectedItem = PickBoard(_settings.PlaybackBoardIndex, b => b.TxCount > 0);
        }

        _initialising = wasInitialising;

        PopulateCapturePorts();
        PopulatePlaybackPorts();

        if (result.Error is { } err)
        {
            CaptureBoardInfo.Text = PlaybackBoardInfo.Text = err;
            Log(err, LogLevel.Warn);
        }
        else
        {
            foreach (BoardInfo b in _boards)
                Log($"Board {b.Index}: {b.Model} ({b.BoardTypeName}) - {b.RxCount} RX / {b.TxCount} TX", LogLevel.Info);
        }

        RefreshBoardsButton.IsEnabled = true;
        UpdatePreviewAndValidation();
    }

    private BoardInfo PickBoard(uint preferredIndex, Func<BoardInfo, bool> usable) =>
        _boards.FirstOrDefault(b => b.Index == preferredIndex && usable(b))
        ?? _boards.FirstOrDefault(usable)
        ?? _boards[0];

    private void PopulateCapturePorts()
    {
        bool wasInitialising = _initialising;
        _initialising = true;

        BoardInfo? board = CaptureBoard;
        CaptureCombo.ItemsSource = board?.RxPorts;

        if (board is not null)
        {
            CaptureBoardInfo.Text = $"{board.BoardTypeName} - {board.RxCount} RX / {board.TxCount} TX";
            CaptureCombo.SelectedItem =
                board.RxPorts.FirstOrDefault(p => p.Name == _settings.CapturePort) ?? board.RxPorts.FirstOrDefault();
            CaptureWarning.Visibility = board.RxCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            CaptureBoardInfo.Text = "No board selected.";
            CaptureWarning.Visibility = Visibility.Collapsed;
        }

        CaptureCombo.IsEnabled = board is { RxCount: > 0 };
        _initialising = wasInitialising;
    }

    private void PopulatePlaybackPorts()
    {
        bool wasInitialising = _initialising;
        _initialising = true;

        BoardInfo? board = PlaybackBoard;
        PlaybackCombo.ItemsSource = board?.TxPorts;

        if (board is not null)
        {
            PlaybackBoardInfo.Text = $"{board.BoardTypeName} - {board.RxCount} RX / {board.TxCount} TX";
            PlaybackCombo.SelectedItem =
                board.TxPorts.FirstOrDefault(p => p.Name == _settings.PlaybackPort) ?? board.TxPorts.FirstOrDefault();
            PlaybackWarning.Visibility = board.TxCount == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        else
        {
            PlaybackBoardInfo.Text = "No board selected.";
            PlaybackWarning.Visibility = Visibility.Collapsed;
        }

        PlaybackCombo.IsEnabled = board is { TxCount: > 0 };
        _initialising = wasInitialising;
    }

    private async void RefreshBoards_Click(object sender, RoutedEventArgs e) => await ScanBoardsAsync();

    private void CaptureBoardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising) return;
        PopulateCapturePorts();
        UpdatePreviewAndValidation();
    }

    private void PlaybackBoardCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising) return;
        PopulatePlaybackPorts();
        UpdatePreviewAndValidation();
    }

    private void PortCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initialising) return;
        UpdatePreviewAndValidation();
    }

    // ------------------------------------------------------------------ timecode link

    private void ApplyApi_Click(object sender, RoutedEventArgs e)
    {
        if (!TimecodeLink.TrySetUrl(ApiUrlBox.Text, out string? problem))
        {
            Log(problem!, LogLevel.Error);
            return;
        }

        _lastLinkRender = "";
        Log($"Timecode generator set to {TimecodeLink.Url} - every Emerald module now reads it.", LogLevel.Ok);
    }

    /// <summary>Another module changed the generator's address; show the new one.</summary>
    private void OnTimecodeUrlChanged(string url) => Dispatcher.BeginInvoke(() =>
    {
        if (ApiUrlBox.Text.Trim() == url) return;

        ApiUrlBox.Text = url;
        _lastLinkRender = "";
        Log($"Timecode generator changed to {url} by another module.", LogLevel.Info);
    });

    private void UiTimer_Tick(object? sender, EventArgs e)
    {
        TcDisplay.Text = _timecode.TryGetCurrent(out Timecode now) ? now.ToString() : "--:--:--:--";

        int rate = _timecode.FrameRate;
        if (rate > 0 && rate != _frameRate)
        {
            _frameRate = rate;
            UpdatePreviewAndValidation();
        }

        // Status text only changes on state transitions; re-rendering it every 25 ms
        // would fight the user for the UI thread for no visible gain.
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
                TcMetaText.Text = $"{r.Mode} / {r.ServerRole} - source: {r.TimeSource} - {r.ConnectedReaders} reader(s)";
                break;

            case TimecodeLinkState.Offline:
                TcDot.Fill = Brush("Bad");
                TcStatusText.Foreground = Brush("Bad");
                TcStatusText.Text = "offline";
                TcMetaText.Text = _timecode.LastError ?? "Timecode API unreachable.";
                break;

            default:
                TcDot.Fill = Brush("Muted");
                TcStatusText.Foreground = Brush("Muted");
                TcStatusText.Text = "connecting...";
                TcMetaText.Text = $"Polling {_timecode.Url}";
                break;
        }
    }

    /// <summary>How far ahead of the clock the Now button places the start timecode.</summary>
    private const int NowLeadMinutes = 2;

    /// <summary>
    /// Stamps the realtime timecode plus a couple of minutes' lead.
    ///
    /// Stamping the clock exactly would put the cue in the past by the time the operator has
    /// finished the rest of the form, and a start that has already gone by plays immediately
    /// rather than waiting. The lead leaves room to finish setting up and still hit the mark.
    /// </summary>
    private void Now_Click(object sender, RoutedEventArgs e)
    {
        if (!_timecode.TryGetCurrent(out Timecode now))
        {
            Log("No timecode available yet from the API.", LogLevel.Warn);
            return;
        }

        int rate = _frameRate > 0 ? _frameRate : FallbackFrameRate;
        Timecode cue = now.AddWrapping(NowLeadMinutes * 60L * rate);

        StartTcBox.Text = cue.ToString();
        Log($"Start timecode set to {cue} - {NowLeadMinutes} minutes ahead of {now}.", LogLevel.Info);
    }

    // ------------------------------------------------------------------ media source

    private void MediaDropZone_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok) MediaDropZone.BorderBrush = Brush("Accent");
        e.Handled = true;
    }

    private void MediaDropZone_DragLeave(object sender, DragEventArgs e) =>
        MediaDropZone.BorderBrush = Brush("Line");

    private void MediaDropZone_Drop(object sender, DragEventArgs e)
    {
        MediaDropZone.BorderBrush = Brush("Line");

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;

        if (paths.Length > 1)
            Log($"{paths.Length} items dropped; using the first one.", LogLevel.Warn);

        MediaSelection? selection = MediaScanner.Resolve(paths[0]);
        if (selection is null)
        {
            Log($"Dropped path does not exist: {paths[0]}", LogLevel.Error);
            return;
        }

        SetMedia(selection);
    }

    private void MediaDropZone_Click(object sender, MouseButtonEventArgs e) => BrowseFolder_Click(sender, e);

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select media folder" };
        if (Directory.Exists(_media?.Path)) dialog.InitialDirectory = _media!.Path;

        if (dialog.ShowDialog(this) == true)
            SetMedia(MediaScanner.Resolve(dialog.FolderName));
    }

    private void BrowseFile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select media file",
            Filter = "Media files|*.mxf;*.mov;*.mp4;*.avi;*.mkv;*.ts;*.m2t;*.m2ts;*.mpg;*.mpeg;*.dv;*.gxf;*.lxf;*.webm;*.yuv;*.raw;*.wav|All files|*.*",
        };

        if (dialog.ShowDialog(this) == true)
            SetMedia(MediaScanner.Resolve(dialog.FileName));
    }

    private void ClearMedia_Click(object sender, RoutedEventArgs e) => SetMedia(null);

    private void SetMedia(MediaSelection? selection, bool announce = true)
    {
        _media = selection;
        _mediaInfo = null;

        // SOM and EOM are source timecodes, so the media's own start timecode has to be
        // known before they mean anything. Broadcast media conventionally starts at
        // 01:00:00:00, not zero.
        if (selection is { IsEmpty: false })
        {
            string first = selection.Kind == "file"
                ? selection.Path
                : Path.Combine(selection.Path, selection.Files[0]);

            _mediaInfo = MediaProbe.Probe(_ffprobePath, first, _frameRate > 0 ? _frameRate : FallbackFrameRate);

            if (announce && _mediaInfo is { } info)
                Log($"Media: {Path.GetFileName(first)} - {info.VideoCodec} {info.Width}x{info.Height}, " +
                    info.Summary(_frameRate > 0 ? _frameRate : FallbackFrameRate), LogLevel.Info);

            // Choosing a file seeds the marks from it. SOM is a position on the media's own
            // timecode, so starting it anywhere else would be an in-point the operator did
            // not choose - and on media that starts at 01:00:00:00, an invalid one.
            if (announce) SeedMarksFromMedia();
        }

        if (selection is null)
        {
            MediaPathText.Text = "Optional - drop a folder or file here, or click to browse";
            MediaPathText.Foreground = Brush("Muted", "#5C6474");
            MediaSummaryText.Visibility = Visibility.Collapsed;
        }
        else
        {
            MediaPathText.Text = selection.Path;
            MediaPathText.Foreground = Brush("Text");
            int rate = _frameRate > 0 ? _frameRate : FallbackFrameRate;
            MediaSummaryText.Text = _mediaInfo is { } mi
                ? $"{selection.Kind} - {selection.Summary}   |   {mi.Summary(rate)}"
                : $"{selection.Kind} - {selection.Summary}";
            MediaSummaryText.Foreground = selection.IsEmpty ? Brush("Warn") : Brush("Muted");
            MediaSummaryText.Visibility = Visibility.Visible;

            if (announce) Log($"Media source: {selection.Path} ({selection.Summary})", LogLevel.Info);
        }

        UpdatePreviewAndValidation();
    }

    // ------------------------------------------------------------------ validation + payload

    private void EdlField_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initialising) return;
        UpdatePreviewAndValidation();
    }

    /// <summary>
    /// Which of EOM and Duration is currently the calculated one. There is no mode to pick:
    /// whichever the operator last typed into is the one they are driving.
    /// </summary>
    private enum Derived { Eom, Duration }

    private Derived _derived = Derived.Duration;

    /// <summary>Guards the write to the calculated field, so the pair cannot chase each other.</summary>
    private bool _syncingTiming;

    private void Som_Changed(object sender, TextChangedEventArgs e)
    {
        // Moving a mark leaves whichever field the operator set alone and moves the other:
        // drag the in-point while driving Duration and the out-point slides with it; drive
        // EOM instead and the duration shortens, which is what trimming an in-point means.
        if (_initialising) return;
        UpdatePreviewAndValidation();
    }

    private void Eom_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initialising || _syncingTiming) return;

        _derived = Derived.Duration;
        UpdatePreviewAndValidation();
    }

    private void Duration_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initialising || _syncingTiming) return;

        _derived = Derived.Eom;
        UpdatePreviewAndValidation();
    }

    /// <summary>
    /// Writes whichever of EOM and Duration is the calculated one, from the other and SOM.
    /// Called before validation, so what is parsed is what is on screen.
    /// </summary>
    private void SyncTiming(int rate)
    {
        if (!Timecode.TryParse(SomBox.Text, rate, out Timecode som, out _)) return;

        _syncingTiming = true;

        try
        {
            if (_derived == Derived.Eom)
            {
                if (Timecode.TryParse(DurationBox.Text, rate, out Timecode duration, out _))
                    EomBox.Text = som.AddWrapping(duration.TotalFrames).ToString();
            }
            else if (Timecode.TryParse(EomBox.Text, rate, out Timecode eom, out _)
                     && eom.TotalFrames >= som.TotalFrames)
            {
                // Marks on a piece of media, so this is plain subtraction and not the
                // wrapping kind: an out-point behind the in-point is an error to report,
                // not a duration of nearly twenty-four hours.
                DurationBox.Text = new Timecode(eom.TotalFrames - som.TotalFrames, rate).ToString();
            }
        }
        finally
        {
            _syncingTiming = false;
        }

        // The calculated one is tinted, so which way the arithmetic is running is visible
        // without a control to read.
        DurationBox.Foreground = _derived == Derived.Duration ? Brush("Tc") : Brush("Text");
        EomBox.Foreground = _derived == Derived.Eom ? Brush("Tc") : Brush("Text");
    }

    private void UpdatePreviewAndValidation()
    {
        SyncTiming(_frameRate > 0 ? _frameRate : FallbackFrameRate);

        EdlCommand? command = BuildCommand(out List<string> problems);

        // The record JSON is no longer shown in a panel — the queue is — but Copy JSON
        // still hands the operator the composed command.
        _recordJson = command?.ToPrettyJson() ?? "";

        SendButton.IsEnabled = command is not null;
        SendStatusText.Text = command is not null ? "" : problems.FirstOrDefault() ?? "";
        SendStatusText.Foreground = Brush("Muted");
    }

    /// <summary>
    /// Validates every field and, when they all hold, returns the command ready to send.
    /// Field-level errors are written straight onto the inline error labels.
    /// </summary>
    private EdlCommand? BuildCommand(out List<string> problems)
    {
        problems = new List<string>();
        int rate = _frameRate > 0 ? _frameRate : FallbackFrameRate;

        BoardInfo? captureBoard = CaptureBoard;
        if (captureBoard is null) problems.Add("No capture board selected.");

        BoardInfo? playbackBoard = PlaybackBoard;
        if (playbackBoard is null) problems.Add("No playback board selected.");
        else if (playbackBoard.TxCount == 0) problems.Add($"{playbackBoard.Model} has no TX channels.");

        var capture = CaptureCombo.SelectedItem as ChannelPort;
        if (capture is null) problems.Add("No capture port available.");

        var playback = PlaybackCombo.SelectedItem as ChannelPort;
        if (playback is null) problems.Add("No playback port available.");

        // Start timecode
        bool startOk = Timecode.TryParse(StartTcBox.Text, rate, out Timecode start, out string? startError);
        ShowFieldError(StartTcError, startOk ? null : startError);
        if (!startOk) problems.Add($"Start timecode: {startError}");

        // SOM and EOM are marks **on the media's own timecode**. SOM is the in-point: the
        // frame the message starts from, so a three-minute clip with SOM 00:01:00:00 goes on
        // air one minute in, with that first minute skipped. EOM is the out-point, and the
        // duration between them is EOM - SOM.
        //
        // The marks are quoted against the media's own start timecode, which broadcast media
        // conventionally puts at 01:00:00:00 rather than zero — so SOM is seeded from the
        // file when one is chosen, and can never be earlier than it.
        //
        // The fields are fixed-width masks and can never be empty, so an open-ended message
        // is expressed as EOM equal to SOM - a zero duration, which is the same 00:00:00:00
        // pair the boxes start out holding.
        Timecode mediaStart = MediaStart(rate);

        Timecode som = Timecode.Zero(rate);
        string? somEomError = null;

        if (!Timecode.TryParse(SomBox.Text, rate, out som, out string? somErr))
            somEomError = $"SOM: {somErr}";
        else if (som.TotalFrames < mediaStart.TotalFrames)
            somEomError = $"SOM cannot be earlier than the media's own start timecode ({mediaStart}).";
        else if (MediaEnd(rate) is { } mediaEnd && som.TotalFrames >= mediaEnd.TotalFrames)
            somEomError = $"SOM is at or past the end of the media ({mediaEnd}); there would be nothing to play.";

        Timecode? duration = null;

        if (somEomError is null)
        {
            if (!Timecode.TryParse(EomBox.Text, rate, out Timecode eom, out string? eomErr))
                somEomError = $"EOM: {eomErr}";
            else if (eom.TotalFrames < som.TotalFrames)
                somEomError = "EOM must not be earlier than SOM.";
            else if (eom.TotalFrames > som.TotalFrames)
                duration = new Timecode(eom.TotalFrames - som.TotalFrames, rate);
        }

        ShowFieldError(SomEomError, somEomError);
        SomEomError.Foreground = Brush("Bad");
        if (somEomError is not null) problems.Add(somEomError);

        // The message goes on air at the start timecode itself. SOM no longer delays that —
        // it moves the in-point within the media instead, so the frame on air at the start
        // timecode is the frame at SOM.
        Timecode onAir = startOk ? start : Timecode.Zero(rate);

        // Duration is a field the operator types into now, so it is never written back here;
        // SyncTiming has already put it in step with EOM. Only the stop time is derived.
        StopTimeBox.Text = duration is { } d && startOk
            ? onAir.AddWrapping(d.TotalFrames).ToString()
            : "open-ended";

        // Media. Either side alone is a valid message: video only plays silent, audio only
        // plays over black. Only having neither is a problem.
        if (_media is { IsEmpty: true }) problems.Add("The selected video folder contains no playable media.");

        if (_media is null && _audioTracks.Count == 0)
            problems.Add("Select a video source, an audio track, or both.");


        if (problems.Count > 0) return null;

        Timecode? stop = duration is { } dur ? onAir.AddWrapping(dur.TotalFrames) : null;

        return new EdlCommand
        {
            Id = _pendingCommandId,
            IssuedAt = DateTimeOffset.Now,
            Capture = new EdlCommand.PortRef
            {
                Board = Describe(captureBoard!),
                Port = capture!.Name,
                Index = capture.Index,
            },
            Playback = new EdlCommand.PortRef
            {
                Board = Describe(playbackBoard!),
                Port = playback!.Name,
                Index = playback.Index,
            },
            Timing = new EdlCommand.TimingSpec
            {
                FrameRate = rate,
                StartTimecode = start.ToString(),
                StartFrame = start.TotalFrames,
                OnAirTimecode = onAir.ToString(),
                OnAirFrame = onAir.TotalFrames,
                MediaStartTimecode = (_mediaInfo?.StartTimecode.Rebase(rate) ?? Timecode.Zero(rate)).ToString(),
                Som = som.ToString(),
                SomFrame = som.TotalFrames,
                Eom = duration is null ? null : som.AddFrames(duration.Value.TotalFrames).ToString(),
                EomFrame = duration is null ? null : som.TotalFrames + duration.Value.TotalFrames,
                Duration = duration?.ToString(),
                DurationFrames = duration?.TotalFrames,
                StopTime = stop?.ToString(),
                StopFrame = stop?.TotalFrames,
                PostPlay = SelectedPostPlay == PostPlay.FreezeLastFrame ? "freezeLastFrame" : "blackScreen",
                Loop = true,
            },
            Media = _media is null
                ? null
                : new EdlCommand.MediaSpec
                {
                    Kind = _media.Kind,
                    Source = _media.Path,
                    FileCount = _media.Files.Count,
                    Files = _media.Files.ToList(),
                },
            Audio = _audioTracks.Count == 0
                ? null
                : _audioTracks.Select(t => new EdlCommand.AudioTrackSpec
                {
                    Label = t.Label,
                    Kind = t.Selection.Kind,
                    Source = t.Selection.Path,
                    FileCount = t.Selection.Files.Count,
                    Files = t.Selection.Files.ToList(),
                    OffsetMs = t.OffsetMs,
                    Channels = $"{t.Index * 2 + 1}-{t.Index * 2 + 2}",
                }).ToList(),
        };
    }

    /// <summary>
    /// Puts SOM at the head of the newly chosen media, and moves EOM with it so the duration
    /// the operator had set is preserved rather than being silently re-interpreted against a
    /// different file.
    /// </summary>
    private void SeedMarksFromMedia()
    {
        int rate = _frameRate > 0 ? _frameRate : FallbackFrameRate;
        Timecode mediaStart = MediaStart(rate);

        long keepDuration =
            Timecode.TryParse(SomBox.Text, rate, out Timecode oldSom, out _) &&
            Timecode.TryParse(EomBox.Text, rate, out Timecode oldEom, out _) &&
            oldEom.TotalFrames >= oldSom.TotalFrames
                ? oldEom.TotalFrames - oldSom.TotalFrames
                : 0;

        // Never longer than the media itself: a duration carried over from a longer file
        // would otherwise loop this one to fill the difference.
        if (MediaEnd(rate) is { } mediaEnd)
            keepDuration = Math.Min(keepDuration, mediaEnd.TotalFrames - mediaStart.TotalFrames);

        _syncingTiming = true;
        try
        {
            SomBox.Text = mediaStart.ToString();
            EomBox.Text = mediaStart.AddWrapping(keepDuration).ToString();
        }
        finally
        {
            _syncingTiming = false;
        }

        Log(_mediaInfo is { HasEmbeddedTimecode: true }
                ? $"SOM set to the media's start timecode {mediaStart}."
                : $"This media carries no timecode; SOM set to {mediaStart}.",
            LogLevel.Info);

        UpdatePreviewAndValidation();
    }

    /// <summary>
    /// The media's own start timecode — where its first frame sits. Broadcast media
    /// conventionally starts at 01:00:00:00; a file with no embedded timecode, or no media at
    /// all, starts at zero, which is what makes SOM read as a plain offset in that case.
    /// </summary>
    private Timecode MediaStart(int rate) =>
        _mediaInfo is { HasEmbeddedTimecode: true } info ? info.StartTimecode.Rebase(rate) : Timecode.Zero(rate);

    /// <summary>Where the media runs out, or null when its length is unknown.</summary>
    private Timecode? MediaEnd(int rate) =>
        _mediaInfo is { } info && info.Duration > TimeSpan.Zero
            ? MediaStart(rate).AddWrapping((long)Math.Round(info.Duration.TotalSeconds * rate))
            : null;

    /// <summary>
    /// How far into the file the decoder must seek to reach SOM. This is the whole of what
    /// SOM does now: ffmpeg is given it as an in-point, so the first frame on air is the
    /// frame at SOM rather than the head of the file.
    /// </summary>
    private TimeSpan SeekFor(Timecode som, int rate)
    {
        long frames = som.TotalFrames - MediaStart(rate).TotalFrames;
        return frames <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(frames / (double)rate);
    }

    private static EdlCommand.BoardRef Describe(BoardInfo board) =>
        new() { Index = board.Index, Model = board.Model, Type = board.BoardType };

    private static void ShowFieldError(TextBlock label, string? message)
    {
        label.Text = message ?? "";
        label.Visibility = message is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ issuing the EDL

    private void SendEdl_Click(object sender, RoutedEventArgs e)
    {
        EdlCommand? command = BuildCommand(out List<string> problems);
        if (command is null)
        {
            foreach (string p in problems) Log(p, LogLevel.Error);
            return;
        }

        SendStatusText.Foreground = Brush("Muted");
        SendStatusText.Text = "";

        // Record the command, then put it on air. Playout is entirely local: the board and
        // the timecode clock are all it needs.
        LogStartBlock(command);
        StartPlayout(command);

        // A fresh id for the next command, so each one is independently traceable.
        _pendingCommandId = Guid.NewGuid().ToString("N");

        UpdatePreviewAndValidation();
    }

    /// <summary>
    /// Announces the accepted command in the log: when it starts, how long it runs and
    /// when it ends, plus how far away the start is on the realtime clock.
    /// </summary>
    private void LogStartBlock(EdlCommand cmd)
    {
        EdlCommand.TimingSpec t = cmd.Timing;

        Log($"EDL {cmd.Id[..8]} is about to start", LogLevel.Info);
        Log($"    start     {t.StartTimecode}", LogLevel.Info);
        Log($"    on air    {t.OnAirTimecode}   (start + SOM {t.Som})" +
            CountdownTo(t.OnAirFrame, t.FrameRate), LogLevel.Info);

        Log(t.Duration is { } dur
            ? $"    duration  {dur}   ({t.DurationFrames} frames @ {t.FrameRate} fps)"
            : "    duration  open-ended   (media loops until stopped)", LogLevel.Info);

        Log(t.Eom is { } eom
            ? $"    som/eom   {t.Som}  ->  {eom}"
            : $"    som/eom   {t.Som}  ->  end of media", LogLevel.Info);

        Log(t.StopTime is { } stop
            ? $"    stop time {stop}"
            : "    stop time open-ended", LogLevel.Info);

        Log($"    post play {t.PostPlay}", LogLevel.Info);

        Log($"    path      {cmd.Capture.Port} @ {cmd.Capture.Board.Index}. {cmd.Capture.Board.Model}" +
            $"  ->  {cmd.Playback.Port} @ {cmd.Playback.Board.Index}. {cmd.Playback.Board.Model}", LogLevel.Info);

        Log(cmd.Media is { } m
            ? $"    media     {m.FileCount} file(s) from {m.Source}"
            : "    media     none - black screen", LogLevel.Info);
    }

    /// <summary>How far ahead the start timecode is on the realtime clock, as " (in HH:MM:SS:FF)".</summary>
    private string CountdownTo(long startFrame, int rate)
    {
        if (rate <= 0 || !_timecode.TryGetCurrent(out Timecode now)) return "";

        long perDay = 24L * 3600L * rate;
        long delta = ((startFrame - now.TotalFrames) % perDay + perDay) % perDay;

        return delta == 0 ? "   (now)" : $"   (in {new Timecode(delta, rate)})";
    }

    // ------------------------------------------------------------------ playout

    // ------------------------------------------------------------------ audio tracks

    private readonly ObservableCollection<AudioTrackRow> _audioTracks = new();

    private void AddAudioTrack_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Select an audio track",
            Filter = "Audio files|*.wav;*.mp3;*.aac;*.m4a;*.flac;*.ogg;*.opus;*.ac3;*.eac3;*.mp2;*.aif;*.aiff" +
                     "|Media files|*.mxf;*.mov;*.mp4;*.mkv;*.ts;*.avi|All files|*.*",
            Multiselect = true,
        };

        if (dialog.ShowDialog(this) != true) return;

        foreach (string path in dialog.FileNames) AddAudioTrack(path);
    }

    private void AudioDropZone_DragOver(object sender, DragEventArgs e)
    {
        bool ok = e.Data.GetDataPresent(DataFormats.FileDrop);
        e.Effects = ok ? DragDropEffects.Copy : DragDropEffects.None;
        if (ok) AudioDropZone.BorderBrush = Brush("Accent");
        e.Handled = true;
    }

    private void AudioDropZone_DragLeave(object sender, DragEventArgs e) =>
        AudioDropZone.BorderBrush = Brush("Line");

    private void AudioDropZone_Drop(object sender, DragEventArgs e)
    {
        AudioDropZone.BorderBrush = Brush("Line");

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;

        foreach (string path in paths) AddAudioTrack(path);
    }

    private void AddAudioTrack(string path)
    {
        if (_audioTracks.Count >= PlayoutService.MaxAudioTracks)
        {
            Log($"Audio track limit reached ({PlayoutService.MaxAudioTracks}); \"{Path.GetFileName(path)}\" ignored.",
                LogLevel.Warn);
            return;
        }

        MediaSelection? selection = MediaScanner.ResolveAudio(path);
        if (selection is null) { Log($"Audio path does not exist: {path}", LogLevel.Error); return; }

        if (selection.IsEmpty)
        {
            Log($"No playable audio found in {selection.Path}", LogLevel.Warn);
            return;
        }

        var row = new AudioTrackRow
        {
            Selection = selection,
            Label = Path.GetFileNameWithoutExtension(
                selection.Kind == "file" ? selection.Path : selection.Files[0]),
        };

        _audioTracks.Add(row);

        // The probe's HasAudio finally earns its keep: a "track" with no audio stream would
        // play silent, which is worth saying out loud rather than leaving to be discovered.
        string first = row.FullPaths[0];
        MediaInfo? info = MediaProbe.Probe(_ffprobePath, first, _frameRate > 0 ? _frameRate : FallbackFrameRate);
        if (info is { HasAudio: false })
            Log($"\"{row.Label}\" carries no audio stream - it will play silent.", LogLevel.Warn);

        Log($"Audio track added: {row.Label} ({row.Summary})", LogLevel.Info);
        RenumberAudioTracks();
    }

    private void RemoveAudioTrack_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;

        _audioTracks.Remove(row);
        Log($"Audio track removed: {row.Label}", LogLevel.Info);
        RenumberAudioTracks();
    }

    private void AudioTrackMinus_Click(object sender, RoutedEventArgs e) => NudgeTrack(sender, -AudioTrackRow.StepMs);
    private void AudioTrackPlus_Click(object sender, RoutedEventArgs e) => NudgeTrack(sender, +AudioTrackRow.StepMs);

    private void AudioTrackReset_Click(object sender, RoutedEventArgs e)
    {
        if (RowOf(sender) is not { } row) return;
        row.OffsetMs = 0;
        PushTrackOffset(row);
    }

    private void NudgeTrack(object sender, int deltaMs)
    {
        if (RowOf(sender) is not { } row) return;
        row.OffsetMs += deltaMs;
        PushTrackOffset(row);
    }

    /// <summary>Straight at the engine, so a nudge lands on the next frame whether on air or not.</summary>
    private void PushTrackOffset(AudioTrackRow row)
    {
        _playout?.SetTrackOffset(row.Index, row.OffsetMs);
        UpdatePreviewAndValidation();
    }

    /// <summary>
    /// List order is the engine's track index, which is also the SDI channel pair the track
    /// is embedded on, so it is restamped on every change.
    /// </summary>
    private void RenumberAudioTracks()
    {
        for (int i = 0; i < _audioTracks.Count; i++)
        {
            _audioTracks[i].Index = i;
            _playout?.SetTrackOffset(i, _audioTracks[i].OffsetMs);
        }

        AudioEmptyText.Visibility = _audioTracks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        AddAudioButton.IsEnabled = _audioTracks.Count < PlayoutService.MaxAudioTracks;

        UpdatePreviewAndValidation();
    }

    private AudioTrackRow? RowOf(object sender) => (sender as FrameworkElement)?.DataContext as AudioTrackRow;

    /// <summary>Rebuilds a track from settings, silently skipping anything no longer on disk.</summary>
    private void RestoreAudioTrack(AudioTrackSetting saved)
    {
        MediaSelection? selection = MediaScanner.ResolveAudio(saved.Source);
        if (selection is null || selection.IsEmpty) return;

        _audioTracks.Add(new AudioTrackRow
        {
            Selection = selection,
            Label = saved.Label,
            OffsetMs = saved.OffsetMs,
            IsDefault = saved.IsDefault,
        });
    }

    private PostPlay SelectedPostPlay =>
        PostPlayCombo.SelectedIndex == 1 ? PostPlay.FreezeLastFrame : PostPlay.BlackScreen;

    /// <summary>Adds the command to the playout queue.</summary>
    private void StartPlayout(EdlCommand command)
    {
        if (_playout is null) return;

        if (_playout.FfmpegPath is null)
        {
            Log("Not queued: ffmpeg is not available to decode the media.", LogLevel.Warn);
            return;
        }

        if (_media is { IsEmpty: true }) return;
        if (_media is null && _audioTracks.Count == 0) return;

        // MediaSelection holds bare file names against a folder; playout needs full paths.
        IReadOnlyList<string>? files = _media is null
            ? null
            : _media.Kind == "file"
                ? new[] { _media.Path }
                : _media.Files.Select(name => Path.Combine(_media.Path, name)).ToList();

        List<AudioTrack> tracks = _audioTracks
            .Select(t => new AudioTrack(t.Label, t.FullPaths))
            .ToList();

        // Each language's trim is pushed before the entry runs, so a bed starts at whatever
        // it was last set to rather than at zero.
        foreach (AudioTrackRow row in _audioTracks) _playout.SetTrackOffset(row.Index, row.OffsetMs);

        var request = new PlayoutRequest(
            BoardIndex: command.Playback.Board.Index,
            BoardModel: command.Playback.Board.Model,
            TxChannel: command.Playback.Index,
            VideoFiles: files,
            // The engine cues on the start timecode itself, holding the post-play fill until
            // then. SOM does not delay that: it is where the media is entered, handed over as
            // the seek below, so the first frame on air is the frame at SOM.
            Start: new Timecode(command.Timing.StartFrame, command.Timing.FrameRate),
            DurationFrames: command.Timing.DurationFrames,
            FrameRate: command.Timing.FrameRate,
            Som: new Timecode(command.Timing.SomFrame, command.Timing.FrameRate),
            SeekOffset: SeekFor(new Timecode(command.Timing.SomFrame, command.Timing.FrameRate),
                                command.Timing.FrameRate),
            PostPlay: SelectedPostPlay,
            AudioTracks: tracks.Count > 0 ? tracks : null);

        var entry = new PlayoutEntry
        {
            Id = command.Id[..8],
            Request = request,
            MediaLabel = DescribeSources(files, tracks),
        };

        int position = _playout.PendingCount + 1;
        _playout.Enqueue(entry);

        Log($"EDL {entry.Id} queued (position {position}) - starts {request.Start}, " +
            $"duration {entry.DurationLabel}, stops {entry.StopLabel}, " +
            $"post play {PlayoutService.Describe(request.PostPlay)}.", LogLevel.Info);

        Log(tracks.Count switch
        {
            0 => "    audio     none - video plays out silent",
            1 => $"    audio     \"{tracks[0].Label}\"",
            _ => $"    audio     {tracks.Count} tracks, all on air - " +
                 $"{string.Join(", ", tracks.Select((t, i) => $"ch {i * 2 + 1}-{i * 2 + 2} \"{t.Label}\""))}",
        }, LogLevel.Info);
    }

    private string DescribeSources(IReadOnlyList<string>? files, List<AudioTrack> tracks)
    {
        string video = files is null
            ? "black screen"
            : _media!.Kind == "file" ? Path.GetFileName(_media.Path) : $"{files.Count} file(s) - {_media.Path}";

        return tracks.Count == 0 ? $"{video}, silent" : $"{video}  +  {tracks.Count} audio track(s)";
    }

    private void StopPlayout_Click(object sender, RoutedEventArgs e)
    {
        Log("Stop requested - clearing the queue and releasing the output.", LogLevel.Info);
        _playout?.StopAll();
    }

    private void ClearQueue_Click(object sender, RoutedEventArgs e) => _playout?.ClearFinished();

    // ------------------------------------------------------------------ capture folder

    private void BrowseCaptureFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFolderDialog { Title = "Select the folder for RX recordings" };
        if (Directory.Exists(CaptureFolderBox.Text.Trim())) dialog.InitialDirectory = CaptureFolderBox.Text.Trim();

        if (dialog.ShowDialog(this) == true)
            CaptureFolderBox.Text = dialog.FolderName;
    }

    private void CaptureFolder_Changed(object sender, TextChangedEventArgs e)
    {
        if (_initialising) return;

        string folder = CaptureFolderBox.Text.Trim();

        if (folder.Length == 0)
        {
            CaptureFolderHint.Text = "Leave empty for no recording. RX is recorded while a message is on air, in 2-minute files.";
            CaptureFolderHint.Foreground = Brush("Muted", "#6B7382");
        }
        else if (Directory.Exists(folder))
        {
            CaptureFolderHint.Text = $"Recording here while a message is on air, in 2-minute files. {FreeSpace(folder)}";
            CaptureFolderHint.Foreground = Brush("Muted", "#6B7382");
        }
        else
        {
            CaptureFolderHint.Text = "That folder does not exist.";
            CaptureFolderHint.Foreground = Brush("Bad");
        }
    }

    private static string FreeSpace(string folder)
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(folder)!);
            return $"{drive.AvailableFreeSpace / 1024d / 1024 / 1024:F0} GB free on {drive.Name}";
        }
        catch
        {
            return "";
        }
    }


    // ------------------------------------------------------------------ capture

    /// <summary>
    /// Recording follows playout: it starts when a message goes on air and stops when the
    /// message ends, so what lands on disk lines up with what was transmitted.
    /// </summary>
    private void UpdateCapture(PlayoutState state)
    {
        bool shouldRun = state is PlayoutState.Playing;

        if (shouldRun && !_captureArmed)
        {
            _captureArmed = true;
            StartCapture();
        }
        else if (!shouldRun && _captureArmed &&
                 state is PlayoutState.Finished or PlayoutState.Stopped or PlayoutState.Failed)
        {
            _captureArmed = false;
            _capture.Stop();
        }
    }

    private void StartCapture()
    {
        string folder = CaptureFolderBox.Text.Trim();

        if (folder.Length == 0) return;                      // recording simply not wanted

        if (CaptureBoard is not { } board || CaptureCombo.SelectedItem is not ChannelPort rx)
        {
            Log("Capture skipped: no capture board or RX port selected.", LogLevel.Warn);
            return;
        }

        // The same assembly the capture deck records through, so a message recorded here is
        // encoded exactly the way the deck is set to encode.
        if (!RecordingSetup.TryBuild(_settings, board.Index, rx.Index, folder,
                                     _frameRate > 0 ? _frameRate : FallbackFrameRate,
                                     "capture", out CaptureRequest? request, out string? problem))
        {
            Log($"Capture skipped: {problem}", LogLevel.Warn);
            return;
        }

        _capture.Start(request!);
    }

    // ------------------------------------------------------------------ queue display

    private void OnQueueChanged() => Dispatcher.BeginInvoke(RenderQueue);

    private void RenderQueue()
    {
        if (_playout is null) return;

        IReadOnlyList<PlayoutEntry> entries = _playout.Snapshot();

        _queueRows.Clear();
        foreach (PlayoutEntry e in entries)
            _queueRows.Add(ToRow(e));

        QueueEmptyText.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int pending = entries.Count(e => e.State == EntryState.Queued);
        QueueHeader.Text = pending > 0 ? $"QUEUE  ({pending} waiting)" : "QUEUE";

        AnnounceQueue(entries);
    }

    private QueueRow ToRow(PlayoutEntry e)
    {
        (string label, string brush) = e.State switch
        {
            EntryState.Playing => ("PLAYING", "Ok"),
            EntryState.Cued => ("CUED", "Warn"),
            EntryState.Queued => ("QUEUED", "Info"),
            EntryState.Completed => ("DONE", "Muted"),
            EntryState.Stopped => ("STOPPED", "Muted"),
            _ => ("FAILED", "Bad"),
        };

        string progress = e.State == EntryState.Playing
            ? $"  {new Timecode(e.FramesOut, e.Request.FrameRate)}"
            : e.Detail.Length > 0 ? $"  ({e.Detail})" : "";

        return new QueueRow(
            Headline: $"{label}  {e.Id}{progress}",
            Timing: $"start {e.Request.Start}   dur {e.DurationLabel}   stop {e.StopLabel}   " +
                    $"TX{e.Request.TxChannel}   {PlayoutService.Describe(e.Request.PostPlay)}",
            Media: e.MediaLabel,
            Accent: Brush(brush));
    }

    /// <summary>
    /// Logs which message is on air and which one follows, but only when that pair
    /// changes — this is called on every frame-rate tick.
    /// </summary>
    private void AnnounceQueue(IReadOnlyList<PlayoutEntry> entries)
    {
        PlayoutEntry? current = entries.FirstOrDefault(e => e.State is EntryState.Playing or EntryState.Cued);
        PlayoutEntry? next = entries.FirstOrDefault(e => e.State == EntryState.Queued);

        string signature = $"{current?.Id}:{current?.State}|{next?.Id}";
        if (signature == _lastQueueAnnouncement) return;
        _lastQueueAnnouncement = signature;

        if (current is not null)
        {
            string verb = current.State == EntryState.Playing ? "NOW PLAYING" : "CUED";
            Log($"{verb}: EDL {current.Id} - stops {current.StopLabel} ({current.DurationLabel})", LogLevel.Info);
        }

        if (next is not null)
            Log($"NEXT UP: EDL {next.Id} - loaded, starts {next.Request.Start}", LogLevel.Info);
        else if (current is not null)
            Log("NEXT UP: nothing queued - " +
                $"{PlayoutService.Describe(current.Request.PostPlay)} will hold after this message.", LogLevel.Info);
    }

    /// <summary>Progress arrives on the playout thread, so everything here is marshalled.</summary>
    private void OnPlayoutProgress(PlayoutStatus status) =>
        Dispatcher.BeginInvoke(() => RenderPlayout(status));

    private void RenderPlayout(PlayoutStatus status)
    {
        UpdateCapture(status.State);

        PlayoutBar.Visibility = Visibility.Visible;

        (string label, string brush) = status.State switch
        {
            PlayoutState.Opening => ("Opening output", "Muted"),
            PlayoutState.WaitingForCue => ("Armed - waiting for cue", "Warn"),
            PlayoutState.Playing => ("On air", "Ok"),
            PlayoutState.Finished => ("Finished", "Info"),
            PlayoutState.Stopped => ("Stopped", "Muted"),
            PlayoutState.Failed => ("Playout failed", "Bad"),
            _ => ("Idle", "Muted"),
        };

        PlayoutDot.Fill = Brush(brush);
        PlayoutStateText.Foreground = Brush(brush);
        PlayoutStateText.Text = label;

        PlayoutDetailText.Text = BuildPlayoutDetail(status);

        // PostPlay counts as running: the queue may be empty but the output is still open and
        // holding black or a freeze on the TX, and STOP is the only way to release it.
        bool running = status.State is PlayoutState.Opening or PlayoutState.WaitingForCue
                                    or PlayoutState.Playing or PlayoutState.PostPlay;
        StopPlayoutButton.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

        // Only the narrative transitions belong in the log; the per-second ticks would bury it.
        if (status.Message.Length > 0)
        {
            LogLevel level = status.State switch
            {
                PlayoutState.Failed => LogLevel.Error,
                PlayoutState.WaitingForCue => LogLevel.Warn,
                PlayoutState.Finished => LogLevel.Ok,
                _ => LogLevel.Info,
            };

            Log(status.Message, level);
        }
    }

    private string BuildPlayoutDetail(PlayoutStatus status)
    {
        if (status.State is not PlayoutState.Playing) return status.Message;

        int rate = _frameRate > 0 ? _frameRate : FallbackFrameRate;
        string elapsed = new Timecode(status.FramesOut, rate).ToString();

        string progress = status.FramesTotal is { } total
            ? $"{elapsed} of {new Timecode(total, rate)}"
            : $"{elapsed} elapsed - looping";

        return status.CurrentFile is { } file ? $"{progress}   |   {file}" : progress;
    }

    // ------------------------------------------------------------------ log

    private enum LogLevel { Info, Ok, Warn, Error }

    private void Log(string message, LogLevel level)
    {
        Brush brush = level switch
        {
            LogLevel.Info => Brush("Info"),   // blue
            LogLevel.Ok => Brush("Ok"),       // green
            LogLevel.Warn => Brush("Warn"),   // amber
            LogLevel.Error => Brush("Bad"),   // red
            _ => Brush("Text"),
        };

        _log.Add(new LogEntry(DateTime.Now.ToString("HH:mm:ss"), message, brush));
        while (_log.Count > 500) _log.RemoveAt(0);

        LogScroller.ScrollToEnd();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private void CopyPayload_Click(object sender, RoutedEventArgs e)
    {
        if (_recordJson.Length == 0)
        {
            Log("Nothing to copy - the command is not valid yet.", LogLevel.Warn);
            return;
        }

        try
        {
            Clipboard.SetText(_recordJson);
            Log("EDL record copied to clipboard.", LogLevel.Info);
        }
        catch (Exception ex)
        {
            // The clipboard is a shared OS resource and can be locked by another process.
            Log($"Could not copy to clipboard: {ex.Message}", LogLevel.Warn);
        }
    }

    private static Brush Brush(string key, string? fallbackHex = null)
    {
        if (Application.Current.TryFindResource(key) is Brush brush && fallbackHex is null) return brush;
        if (fallbackHex is not null) return (Brush)new BrushConverter().ConvertFromString(fallbackHex)!;
        return Brushes.White;
    }
}
