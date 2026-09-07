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
using Emerald.Media;
using Emerald.Video;

namespace Emerald.App;

/// <summary>
/// The playback deck: the media store on the left, the transmitter on the right.
///
/// It is laid out as the capture deck's opposite number — the same scaled design, the same
/// theme, media down the left and the deck down the right — because it is the same job in
/// the other direction, and an operator should not have to re-learn the room. The capture
/// deck auditions what has been recorded and records more of it; this auditions the same
/// store and puts it to air.
///
/// The configuration is what differs. The capture deck has one receiver; this has a
/// <b>transmitter</b> to put pictures out of and, independently, a <b>receiver</b> to watch
/// them come back on. They are chosen separately and are usually on different boards.
///
/// Nothing here decodes or transmits anything itself. The picture comes from
/// <see cref="RxPreview"/>, exactly as the capture deck's does; the audition is a WPF
/// <c>MediaElement</c>, as the capture deck's stage is; and everything that reaches the
/// transmitter goes through <see cref="PlayoutService"/> — the same engine the EDL plays out
/// with, cued the same way against the same station clock.
/// </summary>
public partial class PlaybackWindow : Window
{
    private sealed record LogRow(string Time, string Message, Brush Brush);

    /// <summary>One tile in the clip strip. The thumbnail arrives later, hence the notification.</summary>
    private sealed class ClipItem : INotifyPropertyChanged
    {
        public required string Path { get; init; }
        public required string Display { get; init; }
        public required string Stamp { get; init; }

        private ImageSource? _thumbnail;

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly ObservableCollection<ClipItem> _clips = new();

    /// <summary>
    /// Bumped whenever the strip is rebuilt, so thumbnails from a previous pass are dropped
    /// rather than landing on tiles that are no longer the ones they were rendered for.
    /// </summary>
    private int _stripGeneration;

    private bool _auditionPlaying;

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

    /// <summary>The delayed feed, while one is running.</summary>
    private DelayTransmitter? _delayTx;

    private WriteableBitmap? _previewBitmap;
    private string _lastAirRender = "";

    public PlaybackWindow(AppSettings? settings = null)
    {
        _settings = settings ?? App.Settings;

        InitializeComponent();

        LogList.ItemsSource = _log;
        ClipStrip.ItemsSource = _clips;

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
        RefreshClips();
    }

    // ------------------------------------------------------------------ media

    /// <summary>
    /// Reads the same store the capture deck records into and Live Edit lists, so what is on
    /// this strip is exactly what has been captured — nothing is copied or imported.
    /// </summary>
    private void RefreshClips()
    {
        int generation = ++_stripGeneration;
        string? ffmpeg = _playout?.FfmpegPath;

        _clips.Clear();
        StripEmpty.Text = "reading the capture store...";
        StripEmpty.Visibility = Visibility.Visible;

        // Listing probes every clip through ffprobe, which is far too slow for the UI thread
        // once the store has a few hours in it.
        Task.Run(() => MediaLibrary.List(_settings))
            .ContinueWith(t =>
            {
                if (generation != _stripGeneration) return;

                IReadOnlyList<CapturedClip> clips =
                    t.IsFaulted ? Array.Empty<CapturedClip>() : t.Result;

                foreach (CapturedClip clip in clips)
                {
                    _clips.Add(new ClipItem
                    {
                        Path = clip.Path,
                        Display = clip.Name,
                        Stamp = $"{clip.DurationText}  |  {clip.SizeText}",
                    });
                }

                StripEmpty.Text = "the capture store is empty";
                StripEmpty.Visibility = clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                if (clips.Count > 0) Log($"{clips.Count} clip(s) in the store.");

                if (ffmpeg is not null) LoadThumbnails(generation, ffmpeg);
            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void LoadThumbnails(int generation, string ffmpeg)
    {
        ClipItem[] tiles = _clips.ToArray();

        Task.Run(() =>
        {
            foreach (ClipItem tile in tiles)
            {
                if (generation != _stripGeneration) return;

                BitmapImage? thumb = ClipThumbnails.Get(ffmpeg, tile.Path);
                if (thumb is null) continue;

                Dispatcher.BeginInvoke(() =>
                {
                    if (generation == _stripGeneration) tile.Thumbnail = thumb;
                });
            }
        });
    }

    private void RefreshClips_Click(object sender, RoutedEventArgs e) => RefreshClips();

    private ClipItem? SelectedClip => ClipStrip.SelectedItem as ClipItem;

    private void ClipStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedClip is not { } clip)
        {
            Player.Source = null;
            StagePlaceholder.Visibility = Visibility.Visible;
            ClipNameText.Text = "-";
            return;
        }

        ClipNameText.Text = clip.Display;
        StagePlaceholder.Visibility = Visibility.Collapsed;

        Player.Source = new Uri(clip.Path);
        Player.Play();          // a paused MediaElement shows nothing until it has presented
        _auditionPlaying = true;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (Player.Source is null) return;

        if (_auditionPlaying) Player.Pause();
        else Player.Play();

        _auditionPlaying = !_auditionPlaying;
    }

    private void StopAudition_Click(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        _auditionPlaying = false;
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e) =>
        StagePlaceholder.Visibility = Visibility.Collapsed;

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        _auditionPlaying = false;
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        StagePlaceholder.Text = "this clip cannot be previewed here";
        StagePlaceholder.Visibility = Visibility.Visible;
        Log($"Audition failed: {e.ErrorException?.Message}", Level.Warn);
    }

    /// <summary>
    /// Puts the selected clip on the transmitter now. The audition on the left is a WPF
    /// player and reaches no hardware; this is the same engine the EDL plays out with, so
    /// what goes to air is cued, paced and encoded the way everything else in Emerald is.
    /// </summary>
    private void PlayToTx_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedClip is not { } clip)
        {
            Log("Select a clip first.", Level.Warn);
            return;
        }

        if (!TryTransmitTarget(out BoardInfo? board, out ChannelPort? port)) return;

        if (TidalLock.Shared.State is TidalLockState.CountingDown or TidalLockState.OnAir)
        {
            Log("Tidal lock has the transmitter. Disarm it before playing a clip out.", Level.Warn);
            return;
        }

        int rate = _timecode.FrameRate > 0 ? _timecode.FrameRate : _settings.CaptureFrameRate;
        Timecode start = _timecode.TryGetCurrent(out Timecode now) ? now : Timecode.Zero(rate);

        _playout!.Enqueue(new PlayoutEntry
        {
            Id = Guid.NewGuid().ToString("N")[..8],
            MediaLabel = clip.Display,
            Request = new PlayoutRequest(
                BoardIndex: board!.Index,
                BoardModel: board.Model,
                TxChannel: port!.Index,
                VideoFiles: new[] { clip.Path },
                Start: start,                  // now: a start already gone by cues immediately
                DurationFrames: null,
                FrameRate: rate,
                Som: Timecode.Zero(rate),
                SeekOffset: TimeSpan.Zero,
                PostPlay: PostPlay.BlackScreen,
                AudioTracks: new[] { new AudioTrack(clip.Display, new[] { clip.Path }) }),
        });

        Log($"Playing {clip.Display} out of {port.Name} on board {board.Index}.", Level.Ok);
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

        StopDelayedFeed();
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

    /// <summary>
    /// The transmitter, if one is properly selected. Everything that reaches the card goes
    /// through this, so a missing board or port is reported once, here, in words.
    /// </summary>
    private bool TryTransmitTarget(out BoardInfo? board, out ChannelPort? port)
    {
        board = SelectedTxBoard;
        port = TxPortBox.SelectedItem as ChannelPort;

        if (board is null) { Log("No transmit board is selected.", Level.Warn); return false; }
        if (board.TxCount == 0) { Log($"{board.Model} has no TX channels.", Level.Warn); return false; }
        if (port is null) { Log("No TX port is selected.", Level.Warn); return false; }

        if (_playout?.FfmpegPath is null)
        {
            Log("ffmpeg was not found, so nothing can be decoded for the transmitter.", Level.Error);
            return false;
        }

        return true;
    }

    private void Arm_Click(object sender, RoutedEventArgs e)
    {
        if (TidalLock.Shared.State != TidalLockState.Off)
        {
            TidalLock.Shared.Disarm();
            StopDelayedFeed();
            Log("Tidal lock disarmed.", Level.Warn);
            return;
        }

        if (!TryTransmitTarget(out BoardInfo? board, out ChannelPort? port)) return;

        TidalLock.Shared.Arm(SelectedDelay);

        // Said plainly, because arming on its own transmits nothing and the next step is in
        // the other window - which is exactly the thing an operator would wait in vain for.
        Log($"Tidal lock armed on {port!.Name} of board {board!.Index}, " +
            $"{TidalLock.Describe(SelectedDelay)} behind.", Level.Ok);
        Log("Nothing goes to air yet. Press Record on the capture deck to start the countdown.",
            Level.Warn);
    }

    private void OnTidalLockChanged(TidalLock lockState) => Dispatcher.BeginInvoke(() =>
    {
        RenderTidalLock();

        switch (lockState.State)
        {
            case TidalLockState.CountingDown:
                Log(lockState.Detail, Level.Ok);
                StartDelayTransmitter(lockState);
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
    /// Starts draining the line to the transmitter. The transmitter does the waiting: it
    /// opens the output now and holds black on it until a whole delay has accumulated, so
    /// what it cuts to is already that far behind the receiver.
    ///
    /// Nothing here goes through <see cref="PlayoutService"/>. That engine decodes files and
    /// cues them against the station clock, which is the right thing for the EDL and the
    /// wrong thing for a delay: there is no file to decode and no cue to wait for, only a
    /// ring of frames and a fixed distance to keep from its head.
    /// </summary>
    private void StartDelayTransmitter(TidalLock lockState)
    {
        if (_delayTx is not null) return;

        if (lockState.Line is not DelayLine line)
        {
            TidalLock.Shared.Fail("The capture deck did not open a delay line.");
            return;
        }

        if (!TryTransmitTarget(out BoardInfo? board, out ChannelPort? port)) return;

        // A TX channel cannot be opened twice, and PlayoutService holds its output open
        // indefinitely once the queue drains. It has to let go first.
        _playout?.StopAll();

        try
        {
            _delayTx = new DelayTransmitter(board!.Index, port!.Index, line, lockState.Delay);
        }
        catch (DelayLineException ex)
        {
            TidalLock.Shared.Fail(ex.Message);
            return;
        }

        _delayTx.Status += OnDelayStatus;
        _delayTx.Start();

        Log($"Delaying {line.Width}x{line.Height}{line.FrameRate} to {port!.Name} on board " +
            $"{board!.Index}, {TidalLock.Describe(lockState.Delay)} behind. " +
            $"Ring: {line.SlotCount} frames at {line.Path}.", Level.Ok);
    }

    /// <summary>The transmitter reports from its own thread; everything here marshals.</summary>
    private void OnDelayStatus(DelayStatus status) => Dispatcher.BeginInvoke(() =>
    {
        (string label, string brush) = status.Phase switch
        {
            DelayPhase.Opening => ("Opening the transmitter", "IpMuted"),
            DelayPhase.Filling => ("Filling the delay", "Warn"),
            DelayPhase.OnAir => ("ON AIR", "Ok"),
            DelayPhase.Holding => ("Holding - the receiver has paused", "Warn"),
            DelayPhase.Draining => ("Draining the last of the recording", "IpTeal"),
            DelayPhase.Finished => ("Finished", "IpTeal"),
            _ => ("Failed", "IpRed"),
        };

        OutputStateText.Text = label;
        OutputStateText.Foreground = Brush(brush);

        OutputDetailText.Text = status.Phase == DelayPhase.Filling
            ? $"{status.Buffered} of {status.Target} frames buffered"
            : $"{status.FramesOut} frames out";

        AirDot.Fill = Brush(brush);
        AirText.Text = label;
        AirText.Foreground = Brush(brush);

        StopOutputButton.IsEnabled = status.Phase is not (DelayPhase.Finished or DelayPhase.Failed);

        if (status.Message.Length > 0)
        {
            Log(status.Message, status.Phase switch
            {
                DelayPhase.Failed => Level.Error,
                DelayPhase.Holding => Level.Warn,
                DelayPhase.OnAir => Level.Ok,
                _ => Level.Info,
            });
        }

        switch (status.Phase)
        {
            case DelayPhase.OnAir:
                TidalLock.Shared.WentToAir();
                break;

            case DelayPhase.Finished:
                TidalLock.Shared.DrainComplete();
                ClearDelayTransmitter();
                break;

            case DelayPhase.Failed:
                TidalLock.Shared.Fail(status.Message.Length > 0 ? status.Message : "The delayed feed stopped.");
                ClearDelayTransmitter();
                break;
        }
    });

    private void ClearDelayTransmitter()
    {
        if (_delayTx is null) return;

        _delayTx.Status -= OnDelayStatus;
        _delayTx.Dispose();
        _delayTx = null;
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

        // Which of the three steps is live. The one still to be done by the operator is lit;
        // arming and then waiting for a window that is not this one is the mistake worth
        // designing against.
        Step1Text.Foreground = Brush(lockState.State == TidalLockState.Off ? "IpMint" : "IpDim");
        Step2Text.Foreground = Brush(lockState.State == TidalLockState.Armed ? "IpMint" : "IpDim");
        Step3Text.Foreground = Brush(
            lockState.State is TidalLockState.CountingDown or TidalLockState.OnAir
                             or TidalLockState.Draining ? "IpMint" : "IpDim");
    }

    /// <summary>
    /// Ends the delayed feed. Note this is a deliberate act: stopping the <i>recording</i>
    /// does not come through here, because a whole delay's worth of programme is still in the
    /// line and has not been transmitted yet.
    /// </summary>
    private void StopDelayedFeed()
    {
        if (_delayTx is null) return;

        _delayTx.Stop();
        ClearDelayTransmitter();
    }

    // ------------------------------------------------------------------ output

    private void StopOutput_Click(object sender, RoutedEventArgs e)
    {
        Log("Stop requested - releasing the transmitter.", Level.Warn);

        StopDelayedFeed();
        _playout?.StopAll();

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

        switch (lockState.State)
        {
            case TidalLockState.Armed:
                // Armed and waiting. Said on the stage, not just in a corner: arming
                // transmits nothing, and the next move is in the other window.
                LockPanel.Visibility = Visibility.Visible;
                LockHeadline.Text = "TIDAL LOCK ARMED";
                CountdownText.Text = TidalLock.Describe(lockState.Delay);
                CountdownText.Foreground = Brush("Warn");
                CountdownDetail.Text = "Waiting for the capture deck. Press Record there to start the countdown.";
                break;

            case TidalLockState.CountingDown:
                // Counted in frames actually in the line, not against the station clock. The
                // receiver's own cadence is the truth about how much delay exists, so this
                // stays honest through a timecode outage and cannot reach zero before the
                // frames to fill the delay have really arrived.
                TimeSpan left = lockState.TimeToAir;

                LockPanel.Visibility = Visibility.Visible;
                LockHeadline.Text = "TIDAL LOCK - ON AIR IN";
                CountdownText.Text = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";
                CountdownText.Foreground = Brush("IpMint");

                CountdownDetail.Text =
                    $"{lockState.FramesBuffered:N0} of {lockState.Line?.TargetFrames ?? 0:N0} frames buffered" +
                    (lockState.CueAt is { } cue ? $"  |  on air at {cue}" : "") +
                    "  |  holding black on the transmitter until then";
                break;

            case TidalLockState.Draining:
                LockPanel.Visibility = Visibility.Visible;
                LockHeadline.Text = "TIDAL LOCK - DRAINING";
                CountdownText.Text = TidalLock.Describe(lockState.Delay);
                CountdownText.Foreground = Brush("IpTeal");
                CountdownDetail.Text =
                    "The recording has stopped. What is left in the delay is still going to air.";
                break;

            default:
                LockPanel.Visibility = Visibility.Collapsed;
                break;
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
