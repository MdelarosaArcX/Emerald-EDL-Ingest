using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Emerald.Core;
using Emerald.Deltacast;
using Emerald.Edl;
using Emerald.Video;

namespace Emerald.App;

/// <summary>
/// The playback deck: a transmitter, a confidence monitor on whatever is coming back, and
/// tidal lock.
///
/// It is laid out as the capture deck's opposite number — the same scaled design, the same
/// theme, a monitor on the left and the controls down the right — because it is the same
/// job in the other direction, and an operator should not have to re-learn the room. What
/// differs is the configuration: the capture deck has one receiver, this has a
/// <b>transmitter</b> to put pictures out of and, independently, a <b>receiver</b> to watch
/// them come back on. They are chosen separately and are usually on different boards.
///
/// Nothing here decodes or transmits anything itself. The picture comes from
/// <see cref="RxPreview"/>, exactly as the capture deck's does, and everything that reaches
/// the transmitter goes through <see cref="PlayoutService"/> — the same engine the EDL plays
/// out with, cued the same way against the same station clock.
/// </summary>
public partial class PlaybackWindow : Window
{
    private sealed record LogRow(string Time, string Message, Brush Brush);

    private readonly AppSettings _settings;
    private readonly TimecodeService _timecode = TimecodeLink.Service;
    private readonly RxPreview _preview = new();
    private readonly ObservableCollection<LogRow> _log = new();

    private readonly DispatcherTimer _clock = new(DispatcherPriority.Render)
        { Interval = TimeSpan.FromMilliseconds(25) };

    private PlayoutService? _playout;
    private IReadOnlyList<BoardInfo> _boards = Array.Empty<BoardInfo>();

    /// <summary>Suppresses field handlers while the deck is being populated programmatically.</summary>
    private bool _loading = true;

    /// <summary>The entry tidal lock is running, so a second countdown cannot queue another.</summary>
    private string? _tidalEntryId;

    private WriteableBitmap? _previewBitmap;
    private string _lastAirRender = "";

    public PlaybackWindow(AppSettings? settings = null)
    {
        _settings = settings ?? App.Settings;

        InitializeComponent();

        LogList.ItemsSource = _log;

        _preview.Status += OnPreviewStatus;
        _preview.FrameReady += OnPreviewFrame;

        _clock.Tick += OnClockTick;

        Loaded += Window_Loaded;
        Closing += Window_Closing;
    }

    // ------------------------------------------------------------------ lifecycle

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        DelayBox.ItemsSource = new[]
        {
            new DelayOption("30 seconds", TimeSpan.FromSeconds(30)),
            new DelayOption("1 minute", TimeSpan.FromMinutes(1)),
            new DelayOption("2 minutes", TimeSpan.FromMinutes(2)),
            new DelayOption("5 minutes", TimeSpan.FromMinutes(5)),
        };
        DelayBox.SelectedIndex = 1;

        Log("Playback deck started.");

        TimecodeLink.Connect(_settings);

        string? ffmpeg = Ffmpeg.Locate(_settings.FfmpegPath);
        _playout = new PlayoutService(_timecode, ffmpeg);
        _playout.Progress += OnPlayoutProgress;

        if (ffmpeg is null)
            Log("ffmpeg was not found - nothing can be decoded for the transmitter.", Level.Warn);

        TidalLock.Shared.Changed += OnTidalLockChanged;

        _clock.Start();
        _loading = false;

        await ScanBoardsAsync();
        RenderTidalLock();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        // Releasing the output drops whatever is on air, which is not something to do because
        // a window was closed by accident.
        if (_playout?.Current is not null)
        {
            MessageBoxResult answer = MessageBox.Show(this,
                "The transmitter is live.\n\nClosing the playback deck will release the output. Close anyway?",
                "Emerald Playback", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);

            if (answer != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
        }

        _clock.Stop();
        TidalLock.Shared.Changed -= OnTidalLockChanged;

        if (TidalLock.Shared.State != TidalLockState.Off)
            TidalLock.Shared.Disarm("Playback deck closed - tidal lock released.");

        _preview.Dispose();
        _playout?.Dispose();

        if (SelectedTxBoard is { } tx) _settings.PlaybackBoardIndex = tx.Index;
        if (TxPortBox.SelectedItem is ChannelPort port) _settings.PlaybackPort = port.Name;
        _settings.Save();
    }

    // ------------------------------------------------------------------ boards

    private BoardInfo? SelectedTxBoard => TxBoardBox.SelectedItem as BoardInfo;
    private BoardInfo? SelectedPreviewBoard => PreviewBoardBox.SelectedItem as BoardInfo;

    private async Task ScanBoardsAsync()
    {
        TxInfoText.Text = "Scanning for DELTACAST boards...";

        BoardScanResult result = await BoardService.ScanAsync();
        _boards = result.Boards;

        bool wasLoading = _loading;
        _loading = true;

        TxBoardBox.ItemsSource = _boards;
        PreviewBoardBox.ItemsSource = _boards;

        if (_boards.Count > 0)
        {
            // Each side falls back to the first board that can actually do that job: a board
            // with eight receivers and no transmitter is never a playback board.
            TxBoardBox.SelectedItem = Pick(_settings.PlaybackBoardIndex, b => b.TxCount > 0);
            PreviewBoardBox.SelectedItem = Pick(_settings.CaptureBoardIndex, b => b.RxCount > 0);
        }

        _loading = wasLoading;

        PopulateTxPorts();
        PopulatePreviewPorts();

        if (result.Error is { } error) Log(error, Level.Warn);
        else foreach (BoardInfo b in _boards)
            Log($"Board {b.Index}: {b.Model} - {b.RxCount} RX / {b.TxCount} TX");
    }

    private BoardInfo Pick(uint preferred, Func<BoardInfo, bool> usable) =>
        _boards.FirstOrDefault(b => b.Index == preferred && usable(b))
        ?? _boards.FirstOrDefault(usable)
        ?? _boards[0];

    private void PopulateTxPorts()
    {
        bool wasLoading = _loading;
        _loading = true;

        BoardInfo? board = SelectedTxBoard;
        TxPortBox.ItemsSource = board?.TxPorts;

        if (board is not null)
        {
            TxPortBox.SelectedItem =
                board.TxPorts.FirstOrDefault(p => p.Name == _settings.PlaybackPort) ?? board.TxPorts.FirstOrDefault();

            TxInfoText.Text = board.TxCount == 0
                ? "This board has no TX channels - it cannot transmit."
                : $"{board.BoardTypeName} - available: {string.Join(", ", board.TxPorts.Select(p => p.Name))}";

            TxInfoText.Foreground = board.TxCount == 0 ? Brush("Warn") : Brush("IpDim");
        }
        else
        {
            TxInfoText.Text = "No board selected.";
        }

        TxPortBox.IsEnabled = board is { TxCount: > 0 };
        _loading = wasLoading;
    }

    private void PopulatePreviewPorts()
    {
        bool wasLoading = _loading;
        _loading = true;

        BoardInfo? board = SelectedPreviewBoard;
        PreviewPortBox.ItemsSource = board?.RxPorts;

        if (board is not null)
        {
            PreviewPortBox.SelectedItem = board.RxPorts.FirstOrDefault();

            PreviewInfoText.Text = board.RxCount == 0
                ? "This board has no RX channels - nothing to monitor on."
                : $"{board.BoardTypeName} - available: {string.Join(", ", board.RxPorts.Select(p => p.Name))}";

            PreviewInfoText.Foreground = board.RxCount == 0 ? Brush("Warn") : Brush("IpDim");
        }
        else
        {
            PreviewInfoText.Text = "No board selected.";
        }

        PreviewPortBox.IsEnabled = board is { RxCount: > 0 };
        _loading = wasLoading;
    }

    private async void Rescan_Click(object sender, RoutedEventArgs e) => await ScanBoardsAsync();

    private void TxBoard_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        PopulateTxPorts();
    }

    private void PreviewBoard_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        PopulatePreviewPorts();
    }

    private void PreviewPort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (_preview.IsRunning) StartPreview();      // follow the operator onto the new input
    }

    private void Field_Changed(object sender, SelectionChangedEventArgs e) { }

    // ------------------------------------------------------------------ preview

    private void StartPreview_Click(object sender, RoutedEventArgs e) => StartPreview();

    private void StopPreview_Click(object sender, RoutedEventArgs e)
    {
        _preview.Stop();
        PreviewStatus.Text = "preview stopped";
        PreviewPlaceholder.Visibility = Visibility.Visible;
    }

    private void StartPreview()
    {
        if (SelectedPreviewBoard is not { } board || PreviewPortBox.SelectedItem is not ChannelPort port)
        {
            Log("No preview receiver selected.", Level.Warn);
            return;
        }

        // A yielding claim, the same as the capture deck's: a recorder that wants this input
        // takes it, and the preview steps aside rather than failing the recording.
        _preview.Start(board.Index, port.Index);
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void OnPreviewStatus(string text, bool problem) => Dispatcher.BeginInvoke(() =>
    {
        PreviewStatus.Text = text;
        PreviewStatus.Foreground = problem ? Brush("IpRed") : Brush("IpMuted");

        if (problem)
        {
            Log(text, Level.Warn);
            PreviewPlaceholder.Visibility = Visibility.Visible;
        }
    });

    private void OnPreviewFrame(WriteableBitmap bitmap) => Dispatcher.BeginInvoke(() =>
    {
        if (!ReferenceEquals(_previewBitmap, bitmap))
        {
            _previewBitmap = bitmap;
            PreviewImage.Source = bitmap;
        }

        PreviewPlaceholder.Visibility = Visibility.Collapsed;
    });

    // ------------------------------------------------------------------ tidal lock

    private sealed record DelayOption(string Label, TimeSpan Delay)
    {
        public override string ToString() => Label;
    }

    private TimeSpan SelectedDelay =>
        DelayBox.SelectedItem is DelayOption option ? option.Delay : TidalLock.DefaultDelay;

    private void Arm_Click(object sender, RoutedEventArgs e)
    {
        if (TidalLock.Shared.State != TidalLockState.Off)
        {
            TidalLock.Shared.Disarm();
            _tidalEntryId = null;
            _playout?.StopAll();
            Log("Tidal lock disarmed.", Level.Warn);
            return;
        }

        if (SelectedTxBoard is not { } board || TxPortBox.SelectedItem is not ChannelPort port)
        {
            Log("Select a transmit board and TX port before arming.", Level.Warn);
            return;
        }

        if (_playout?.FfmpegPath is null)
        {
            Log("ffmpeg is not available, so nothing can be put to air.", Level.Error);
            return;
        }

        TidalLock.Shared.Arm(SelectedDelay);
        Log($"Tidal lock armed on {port.Name} of board {board.Index}, " +
            $"{TidalLock.Describe(SelectedDelay)} behind. Start recording on the capture deck.", Level.Ok);
    }

    private void OnTidalLockChanged(TidalLock lockState) => Dispatcher.BeginInvoke(() =>
    {
        RenderTidalLock();

        switch (lockState.State)
        {
            case TidalLockState.CountingDown:
                Log(lockState.Detail, Level.Ok);
                CueDelayedPlayout(lockState);
                break;

            case TidalLockState.Failed:
                Log(lockState.Detail, Level.Error);
                break;

            default:
                Log(lockState.Detail);
                break;
        }
    });

    /// <summary>
    /// Queues the delayed feed. The engine does the waiting: it opens the transmitter now,
    /// holds black on it through the countdown, and cuts to the recording at the cue — which
    /// is the recorder's roll point plus the delay, so the file it opens is already that far
    /// behind the write head.
    /// </summary>
    private void CueDelayedPlayout(TidalLock lockState)
    {
        if (_playout is null || _tidalEntryId is not null) return;

        if (SelectedTxBoard is not { } board || TxPortBox.SelectedItem is not ChannelPort port)
        {
            TidalLock.Shared.Fail("No transmit board and TX port are selected.");
            return;
        }

        if (lockState.DelayFile is not { } file || lockState.CueAt is not { } cueAt)
        {
            TidalLock.Shared.Fail("The capture deck did not say what it was recording to.");
            return;
        }

        int rate = _timecode.FrameRate > 0 ? _timecode.FrameRate : _settings.CaptureFrameRate;

        var request = new PlayoutRequest(
            BoardIndex: board.Index,
            BoardModel: board.Model,
            TxChannel: port.Index,
            VideoFiles: new[] { file },
            Start: cueAt,
            DurationFrames: null,               // runs until the recording stops or STOP is pressed
            FrameRate: rate,
            Som: Timecode.Zero(rate),
            SeekOffset: TimeSpan.Zero,          // the delay is the head start, not a seek
            PostPlay: PostPlay.BlackScreen,
            // The recording's own audio, read back from the same delay line.
            AudioTracks: new[] { new AudioTrack("delay line", new[] { file }) },
            Follow: true);

        _tidalEntryId = Guid.NewGuid().ToString("N")[..8];

        _playout.Enqueue(new PlayoutEntry
        {
            Id = _tidalEntryId,
            Request = request,
            MediaLabel = $"tidal lock - {Path.GetFileName(file)}",
        });

        Log($"Cued the delayed feed on {port.Name} for {cueAt} " +
            $"({TidalLock.Describe(lockState.Delay)} behind {lockState.RecordingStarted}).", Level.Ok);
    }

    private void RenderTidalLock()
    {
        TidalLock lockState = TidalLock.Shared;

        (string label, string brush) = lockState.State switch
        {
            TidalLockState.Armed => ("ARMED", "Warn"),
            TidalLockState.CountingDown => ("COUNTING DOWN", "IpTeal"),
            TidalLockState.OnAir => ("ON AIR", "Ok"),
            TidalLockState.Failed => ("FAILED", "IpRed"),
            _ => ("OFF", "IpMuted"),
        };

        LockDot.Fill = Brush(brush);
        LockStateText.Text = label;
        LockStateText.Foreground = Brush(brush);
        LockDetailText.Text = lockState.Detail;

        ArmButton.Content = lockState.State == TidalLockState.Off ? "ARM TIDAL LOCK" : "DISARM TIDAL LOCK";
        DelayBox.IsEnabled = lockState.State == TidalLockState.Off;

        if (lockState.State is TidalLockState.Off or TidalLockState.Failed) _tidalEntryId = null;
    }

    // ------------------------------------------------------------------ output

    private void StopOutput_Click(object sender, RoutedEventArgs e)
    {
        Log("Stop requested - releasing the transmitter.", Level.Warn);
        _playout?.StopAll();
        _tidalEntryId = null;

        if (TidalLock.Shared.State != TidalLockState.Off)
            TidalLock.Shared.Disarm("Output stopped by the operator.");
    }

    private void OnPlayoutProgress(PlayoutStatus status) => Dispatcher.BeginInvoke(() =>
    {
        (string label, string brush) = status.State switch
        {
            PlayoutState.Opening => ("Opening the transmitter", "IpMuted"),
            PlayoutState.WaitingForCue => ("Cued - holding black", "Warn"),
            PlayoutState.Playing => ("ON AIR", "Ok"),
            PlayoutState.PostPlay => ("Holding after playout", "IpMuted"),
            PlayoutState.Finished => ("Finished", "IpTeal"),
            PlayoutState.Stopped => ("Stopped", "IpMuted"),
            PlayoutState.Failed => ("Failed", "IpRed"),
            _ => ("Idle", "IpMuted"),
        };

        OutputStateText.Text = label;
        OutputStateText.Foreground = Brush(brush);

        OutputDetailText.Text = status.State == PlayoutState.Playing
            ? $"{new Timecode(status.FramesOut, _timecode.FrameRate > 0 ? _timecode.FrameRate : 25)} out" +
              (status.CurrentFile is { } f ? $"   |   {f}" : "")
            : status.Message;

        bool live = status.State is PlayoutState.Opening or PlayoutState.WaitingForCue
                                 or PlayoutState.Playing or PlayoutState.PostPlay;
        StopOutputButton.IsEnabled = live;

        AirDot.Fill = Brush(brush);
        AirText.Text = status.State == PlayoutState.Playing ? "ON AIR" : label;
        AirText.Foreground = Brush(brush);

        if (status.State == PlayoutState.Playing) TidalLock.Shared.WentToAir();

        if (status.State == PlayoutState.Failed && TidalLock.Shared.State != TidalLockState.Off)
            TidalLock.Shared.Fail(status.Message);

        // Only the narrative transitions belong in the log; the per-second ticks would bury it.
        if (status.Message.Length > 0 && status.State != PlayoutState.Playing)
        {
            Log(status.Message, status.State switch
            {
                PlayoutState.Failed => Level.Error,
                PlayoutState.WaitingForCue => Level.Warn,
                _ => Level.Info,
            });
        }
        else if (status.Message.Length > 0 && $"{status.State}|{status.Message}" != _lastAirRender)
        {
            _lastAirRender = $"{status.State}|{status.Message}";
            Log(status.Message);
        }
    });

    // ------------------------------------------------------------------ clock + countdown

    private void OnClockTick(object? sender, EventArgs e)
    {
        bool have = _timecode.TryGetCurrent(out Timecode now);

        ClockText.Text = have ? now.ToString() : "--:--:--:--";
        ClockDot.Fill = Brush(_timecode.State switch
        {
            TimecodeLinkState.Online => "IpMint",
            TimecodeLinkState.Connecting => "Warn",
            _ => "IpRed",
        });

        // The countdown is the whole point of the panel, so it is redrawn on the clock rather
        // than on a slower tick: an operator watching the last seconds should see every one.
        TidalLock lockState = TidalLock.Shared;

        if (lockState.State == TidalLockState.CountingDown && have && lockState.CueAt is { } cueAt)
        {
            int rate = now.Rate > 0 ? now.Rate : 25;
            long perDay = 24L * 3600L * rate;
            long frames = ((cueAt.TotalFrames - now.TotalFrames) % perDay + perDay) % perDay;

            // More than half a day away means the cue has just gone by, not that it is
            // twenty-three hours out.
            if (frames > perDay / 2) frames = 0;

            var left = TimeSpan.FromSeconds(frames / (double)rate);

            CountdownPanel.Visibility = Visibility.Visible;
            CountdownText.Text = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
            CountdownDetail.Text = $"on air at {cueAt}";
        }
        else
        {
            CountdownPanel.Visibility = Visibility.Collapsed;
        }
    }

    // ------------------------------------------------------------------ log

    private enum Level { Info, Ok, Warn, Error }

    private void Log(string message, Level level = Level.Info)
    {
        Brush brush = level switch
        {
            Level.Ok => Brush("Ok"),
            Level.Warn => Brush("Warn"),
            Level.Error => Brush("IpRed"),
            _ => Brush("IpText"),
        };

        _log.Add(new LogRow(DateTime.Now.ToString("HH:mm:ss"), message, brush));
        while (_log.Count > 400) _log.RemoveAt(0);

        LogScroller.ScrollToEnd();
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.White;
}
