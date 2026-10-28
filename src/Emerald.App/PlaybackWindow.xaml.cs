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
    /// <summary>
    /// One line on this deck's log.
    ///
    /// <see cref="Source"/> is carried because the panel now shows the playout engine and tidal
    /// lock alongside the deck's own narration, and a line with no source on it is a line whose
    /// subject has to be guessed from its wording.
    /// </summary>
    private sealed record LogRow(string Time, string Source, string Message, Brush Brush);


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

        _preview.Status += OnPreviewStatus;
        _preview.FrameReady += OnPreviewFrame;

        // Full resolution on this deck.
        //
        // The capture deck's monitor is a 352-wide thumbnail and a quarter-size picture is more
        // than it can show. This one is the picture an operator judges the output by - "is that
        // the right material, is it clean" - and a 480x270 image scaled up to fill it was soft
        // enough to make that a guess. A 1080 frame converts in a couple of milliseconds, which
        // is affordable now that at most one frame is ever in flight to this thread.
        _preview.Decimation = 1;

        _clock.Tick += OnClockTick;

        Loaded += Window_Loaded;
        Closing += Window_Closing;
    }

    // ------------------------------------------------------------------ lifecycle

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        // "No delay" is the one that is not a delay: the transmitter is fed the frame the
        // receiver has just produced, so the feed reaches air as soon as there is a frame
        // rather than after a fill. It is what you want when re-arming part-way through a
        // recording that is already running and you do not want to wait out another minute
        // of black.
        //
        // It cannot make up a delay from a recording that already exists — the ring holds raw
        // frames and is allocated empty, and reaching back into the written files would be
        // playing a recording late, which is the design this feature deliberately is not.
        RenderDelayOptions();

        Log("Playback deck started.");

        TimecodeLink.Connect(_settings);

        string? ffmpeg = Ffmpeg.Locate(_settings.FfmpegPath);
        _playout = new PlayoutService(_timecode, ffmpeg);
        _playout.Progress += OnPlayoutProgress;

        if (ffmpeg is null)
            Log("ffmpeg was not found - nothing can be decoded for the transmitter.", Level.Warn);

        TxPortList.ItemsSource = _txPorts;

        // Whatever playback has already said this session, so opening the deck half an hour in
        // does not show a blank page.
        foreach (LogLine line in ActivityLog.Shared.Snapshot()) OnLogLine(line);

        ActivityLog.Shared.Line += OnLogLine;

        TidalLock.Shared.Changed += OnTidalLockChanged;
        PipelineState.Shared.Changed += OnPipelineChanged;
        SdiPorts.Shared.Changed += OnPortsChanged;

        _clock.Start();
        _loading = false;

        await ScanBoardsAsync();
        RenderTidalLock();
    }

    // ------------------------------------------------------------------ media


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
        PipelineState.Shared.Changed -= OnPipelineChanged;
        SdiPorts.Shared.Changed -= OnPortsChanged;

        // A static event holds this window for the life of the application otherwise, and every
        // line written after it closes still runs its handler.
        ActivityLog.Shared.Line -= OnLogLine;

        if (TidalLock.Shared.State != TidalLockState.Off)
            TidalLock.Shared.Disarm("Playback deck closed - tidal lock released.");

        // Not waited on. The card is let go of on a background thread, and a deck that hangs
        // for five seconds on the way out is the same freeze this moved off the UI thread.
        _ = StopDelayedFeed();

        _preview.Dispose();
        _playout?.Dispose();

        if (SelectedTxBoard is { } tx) _settings.PlaybackBoardIndex = tx.Index;
        // The whole chosen set, and the first of them in the old single-port field so a downgrade
        // or an older module still finds something sensible there.
        _settings.PlaybackPorts = string.Join('|', _txPorts.Where(r => r.On).Select(r => r.Name));
        _settings.PlaybackPort = _txPorts.FirstOrDefault(r => r.On)?.Name ?? _settings.PlaybackPort;
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

    /// <summary>
    /// Every TX port of the selected board, with the ones the operator has switched on.
    ///
    /// Remembered by port NAME rather than by index: a settings file outlives a board being moved
    /// to another slot, and an index then points at a different port.
    /// </summary>
    private readonly ObservableCollection<TxPortRow> _txPorts = new();

    /// <summary>The ports the running transmitter actually opened, for the ON AIR marks.</summary>
    private IReadOnlyCollection<int>? _portsOnAir;

    /// <summary>The ports switched on, in card order. What a transmitter is handed.</summary>
    private IReadOnlyList<int> ChosenTxPorts =>
        _txPorts.Where(r => r.On).OrderBy(r => r.Index).Select(r => r.Index).ToList();

    private void PopulateTxPorts()
    {
        bool wasLoading = _loading;
        _loading = true;

        BoardInfo? board = SelectedTxBoard;

        // Whatever was on before, so re-scanning or changing board keeps the choice wherever the
        // same ports still exist.
        var wanted = new HashSet<string>(
            _txPorts.Where(r => r.On).Select(r => r.Name), StringComparer.OrdinalIgnoreCase);

        if (wanted.Count == 0)
            foreach (string name in (_settings.PlaybackPorts ?? "")
                         .Split('|', StringSplitOptions.RemoveEmptyEntries))
                wanted.Add(name.Trim());

        // The single port older settings remember, so an upgrade does not arrive with nothing on.
        if (wanted.Count == 0 && _settings.PlaybackPort.Length > 0) wanted.Add(_settings.PlaybackPort);

        _txPorts.Clear();

        foreach (ChannelPort port in board?.TxPorts ?? Array.Empty<ChannelPort>())
            _txPorts.Add(new TxPortRow { Port = port, On = wanted.Contains(port.Name) });

        // Ports available but none chosen: the first, so an operator who never touches this still
        // has a transmitter.
        if (_txPorts.Count > 0 && _txPorts.All(r => !r.On)) _txPorts[0].On = true;

        TxPortsEmpty.Visibility = _txPorts.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        ShowTxPortSummary();
        ShowPortsOnAir();

        _loading = wasLoading;
    }

    private void ShowTxPortSummary()
    {
        BoardInfo? board = SelectedTxBoard;

        TxInfoText.Text = board is null
            ? "No board selected."
            : board.TxCount == 0
                ? "This board has no TX channels - it cannot transmit."
                : ChosenTxPorts.Count switch
                {
                    0 => "No port switched on - nothing will transmit.",
                    1 => $"{board.BoardTypeName} - one port on.",
                    var n => $"{board.BoardTypeName} - {n} ports on, all carrying the same picture.",
                };

        TxInfoText.Foreground = board is null or { TxCount: 0 } || ChosenTxPorts.Count == 0
            ? Brush("Warn")
            : Brush("IpDim");
    }

    private void ShowPortsOnAir()
    {
        foreach (TxPortRow row in _txPorts) row.ShowAiring(_portsOnAir);

        ShowPortsHeld();
    }

    /// <summary>
    /// How this deck names itself when it holds ports, so its own are never shown as taken.
    ///
    /// The same string <see cref="Emerald.Video.DelayTransmitter"/> uses, because it is this
    /// deck's transmitter doing the holding. If the two ever disagree the deck would grey out its
    /// own ports mid-transmission.
    /// </summary>
    private string PortOwner => $"tidal lock (board {_settings.PlaybackBoardIndex})";

    /// <summary>
    /// Another module took or gave back a port. Raised from whichever thread that module runs on,
    /// so it comes back to the dispatcher before touching the rows.
    /// </summary>
    private void OnPortsChanged() => Dispatcher.BeginInvoke(ShowPortsHeld);

    /// <summary>
    /// Marks the rows against what another module is using.
    ///
    /// A port the EDL is transmitting on cannot also carry this feed - a DELTACAST output takes
    /// one owner - so the row says who has it and the toggle goes dead. Without this the operator
    /// could switch it on, arm, and get a driver error some seconds later with the useful part
    /// (that the EDL already had it) buried in a return code.
    /// </summary>
    private void ShowPortsHeld()
    {
        uint board = (uint)Math.Max(0, _settings.PlaybackBoardIndex);

        foreach (TxPortRow row in _txPorts)
            row.ShowHeldBy(SdiPorts.Shared.HeldByOther(new SdiPort(board, row.Index), PortOwner));

        // A port switched on that has since been taken by something else would arm into a
        // failure, so it is switched off and said out loud rather than left looking ready.
        foreach (TxPortRow row in _txPorts.Where(r => r.On && r.HeldBy is not null).ToList())
        {
            bool wasLoading = _loading;
            _loading = true;

            row.On = false;

            _loading = wasLoading;

            Log($"TX{row.Index} switched off - {row.HeldBy} is using it.", Level.Warn);
        }
    }

    /// <summary>
    /// A port switched on or off.
    ///
    /// Said out loud, and said accurately: the ports are opened when the feed is armed, so one
    /// switched on while something is already on air is wanted and not yet carrying anything. A
    /// card does not grow another output under a running transmitter.
    /// </summary>
    private void TxPort_Toggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        ShowTxPortSummary();

        _settings.PlaybackPorts = string.Join('|', _txPorts.Where(r => r.On).Select(r => r.Name));
        _settings.Save();

        string chosen = ChosenTxPorts.Count == 0
            ? "none"
            : string.Join(", ", ChosenTxPorts.Select(c => $"TX{c}"));

        bool live = _delayTx is not null;

        Log($"TX ports set to {chosen}." +
            (live ? " What is on air keeps the ports it started with - re-arm to change them." : ""),
            live ? Level.Warn : Level.Info);
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

    private sealed record DelayOption(string Label, TimeSpan Delay, DelayStart Start = DelayStart.Fixed)
    {
        public override string ToString() => Label;
    }

    private static readonly DelayOption NoDelay = new("No delay", TimeSpan.Zero);

    private static readonly DelayOption[] RealDelays =
    {
        new("30 seconds", TimeSpan.FromSeconds(30)),
        new("1 minute", TimeSpan.FromMinutes(1)),
        new("2 minutes", TimeSpan.FromMinutes(2)),
        new("5 minutes", TimeSpan.FromMinutes(5)),
    };

    /// <summary>
    /// The two that start at the top of the recording rather than a set time behind it. The
    /// delay they hold is settled when the capture deck hands over the line - see
    /// <see cref="TidalLock.FramesFor"/> - so the time here is only what to show until then.
    /// </summary>
    private static readonly DelayOption[] FirstSegmentStarts =
    {
        new("From first segment", TimeSpan.Zero, DelayStart.FromFirstSegment),
        new("When first segment is ready", TimeSpan.Zero, DelayStart.WhenFirstSegmentReady),
    };

    /// <summary>
    /// Fills the delay list, with "no delay" on it only while the capture deck is recording.
    ///
    /// No delay means the transmitter takes the receiver's frames as they arrive. That is the
    /// right thing when re-arming part-way through a recording that is already running — the
    /// programme is already a delay's worth old, and waiting out another fill would put black
    /// to air for a minute over a feed that is there. With nothing recording it is not a
    /// choice at all: there is no delay to inherit, so it would simply be a way to arm with no
    /// safety on a feed that does not exist yet, which is not what anyone opens this for.
    ///
    /// It cannot make up a delay from a recording that already exists on disk either — the
    /// ring holds raw frames and is allocated empty, and reaching back into the written files
    /// would be playing a recording late, which is the design this feature deliberately is not.
    /// </summary>
    private void RenderDelayOptions()
    {
        bool recording = PipelineState.Shared.Recording;

        DelayOption? chosen = DelayBox.SelectedItem as DelayOption;

        var options = new List<DelayOption>(RealDelays.Length + 3);
        if (recording) options.Add(NoDelay);
        options.AddRange(RealDelays);
        options.AddRange(FirstSegmentStarts);

        DelayBox.ItemsSource = options;

        // A minute, which is what tidal lock is for. No delay is a choice, not a default — and
        // a choice that has just stopped being offered falls back to one rather than to
        // nothing selected at all.
        DelayBox.SelectedItem = chosen is not null && options.Contains(chosen)
            ? chosen
            : options.First(o => o.Delay == TidalLock.DefaultDelay);
    }

    /// <summary>
    /// The capture deck started or stopped recording, which decides whether "no delay" is on
    /// offer. Arrives on whichever thread changed it.
    /// </summary>
    private void OnPipelineChanged() => Dispatcher.BeginInvoke(() =>
    {
        // Not while it is armed: the list is disabled then, and rewriting the selection under
        // an operator who has already committed to a delay would change what they armed.
        if (TidalLock.Shared.State == TidalLockState.Off) RenderDelayOptions();
    });


    private DelayStart SelectedStart =>
        DelayBox.SelectedItem is DelayOption option ? option.Start : DelayStart.Fixed;

    private TimeSpan SelectedDelay =>
        DelayBox.SelectedItem is DelayOption option ? option.Delay : TidalLock.DefaultDelay;

    /// <summary>A delay in words, for a sentence rather than a field.</summary>
    private static string Describe(TimeSpan delay) => delay <= TimeSpan.Zero
        ? "immediately"
        : delay < TimeSpan.FromMinutes(1)
            ? $"{delay.TotalSeconds:0} seconds"
            : delay.TotalMinutes == 1
                ? "one minute"
                : $"{delay.TotalMinutes:0} minutes";

    /// <summary>
    /// The transmitter, if one is properly selected. Everything that reaches the card goes
    /// through this, so a missing board or port is reported once, here, in words.
    /// </summary>
    private bool TryTransmitTarget(out BoardInfo? board, out ChannelPort? port)
    {
        board = SelectedTxBoard;

        // The first port switched on, for anything that still wants one. The full set is
        // ChosenTxPorts, and that is what a transmitter is actually handed.
        port = _txPorts.FirstOrDefault(r => r.On)?.Port;

        if (board is null) { Log("No transmit board is selected.", Level.Warn); return false; }
        if (board.TxCount == 0) { Log($"{board.Model} has no TX channels.", Level.Warn); return false; }

        if (port is null)
        {
            Log("No TX port is switched on - switch at least one on to transmit.", Level.Warn);
            return false;
        }

        if (_playout?.FfmpegPath is null)
        {
            Log("ffmpeg was not found, so nothing can be decoded for the transmitter.", Level.Error);
            return false;
        }

        return true;
    }

    private async void Arm_Click(object sender, RoutedEventArgs e)
    {
        if (TidalLock.Shared.State != TidalLockState.Off)
        {
            // Said and drawn before the card is let go of, not after: the release can take as
            // long as the card takes, and a button that appears to have done nothing until it
            // finishes is a button an operator presses again.
            TidalLock.Shared.Disarm();
            Show("Tidal lock disarmed.", Level.Warn);

            ArmButton.IsEnabled = false;
            await StopDelayedFeed();
            ArmButton.IsEnabled = true;
            return;
        }

        if (!TryTransmitTarget(out BoardInfo? board, out ChannelPort? port)) return;

        TidalLock.Shared.Arm(SelectedDelay, SelectedStart);

        // Said plainly, because arming on its own transmits nothing and the next step is in
        // the other window - which is exactly the thing an operator would wait in vain for.
        Log($"Tidal lock armed on {port!.Name} of board {board!.Index}, " +
            $"{TidalLock.DescribeStart(SelectedStart, SelectedDelay)}.", Level.Ok);
        Log("Nothing goes to air yet. Press Record on the capture deck to start the countdown.",
            Level.Warn);
    }

    private void OnTidalLockChanged(TidalLock lockState) => Dispatcher.BeginInvoke(() =>
    {
        RenderTidalLock();

        switch (lockState.State)
        {
            // Shown, not logged: the lock writes its own transitions into the record, and
            // logging them here would say everything twice.
            case TidalLockState.CountingDown:
                Show(lockState.Detail, Level.Ok);
                StartDelayTransmitter(lockState);
                break;

            case TidalLockState.Failed:
                Show(lockState.Detail, Level.Error);
                break;

            default:
                Show(lockState.Detail);
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

        // Every port switched on, carrying the one delayed picture. They are fixed for the life
        // of this transmitter: a card does not grow another output under a running one, so a port
        // switched on from here is wanted at the next arm rather than now.
        IReadOnlyList<int> ports = ChosenTxPorts;

        try
        {
            _delayTx = new DelayTransmitter(board!.Index, ports, line, lockState.Delay,
                                            _settings.RecordingSegmentSeconds);
        }
        catch (DelayLineException ex)
        {
            TidalLock.Shared.Fail(ex.Message);
            return;
        }

        _portsOnAir = ports.ToArray();
        ShowPortsOnAir();

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
                StopDelayedFeed();
                break;

            case DelayPhase.Failed:
                TidalLock.Shared.Fail(status.Message.Length > 0 ? status.Message : "The delayed feed stopped.");
                StopDelayedFeed();
                break;
        }
    });

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

        // Coming back to off is the moment "no delay" can start or stop being on offer: the
        // capture deck may have rolled while this was armed.
        if (lockState.State == TidalLockState.Off) RenderDelayOptions();

        // Which of the three steps is live. The one still to be done by the operator is lit;
        // arming and then waiting for a window that is not this one is the mistake worth
        // designing against.
        // Says the delay actually chosen rather than a number baked into the markup. It read
        // "one minute later" whatever was selected, which was wrong for three of the four
        // options and is wrong for the default now.
        DelayStart start = lockState.State == TidalLockState.Off ? SelectedStart : lockState.Start;

        Step3Text.Text = start == DelayStart.Fixed || lockState.State is not (TidalLockState.Off or TidalLockState.Armed)
            ? $"3.  On air {Describe(lockState.State == TidalLockState.Off ? SelectedDelay : lockState.Delay)} later"
            : start == DelayStart.FromFirstSegment
                ? "3.  On air from the first segment"
                : "3.  On air from the first segment, once it is recorded";

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
    /// <summary>
    /// Lets go of the delayed feed without freezing the deck.
    ///
    /// Stopping means joining the transmitter's thread, and that thread spends nearly all of
    /// its life inside the card's blocking slot call — it only comes back when the card is
    /// ready for another frame, and a card that has stopped taking them does not come back at
    /// all. Doing that on the UI thread stopped the whole application dead for the five-second
    /// join, twice over once <see cref="DelayTransmitter.Dispose"/> joined it again, which is
    /// what "not responding" on DISARM was.
    ///
    /// The field is cleared first, so the deck is already disarmed as far as anything else is
    /// concerned while the card is still being let go of in the background.
    /// </summary>
    private Task StopDelayedFeed()
    {
        if (_delayTx is not { } transmitter) return Task.CompletedTask;

        _delayTx = null;
        transmitter.Status -= OnDelayStatus;

        return Task.Run(() =>
        {
            try
            {
                transmitter.Dispose();
            }
            catch (Exception ex)
            {
                // Nothing here is worth taking the application down for: the operator has
                // already been told the feed is off, and this is the card being let go of.
                ActivityLog.Shared.Warn(LogSource.Playback,
                    $"The delayed feed did not shut down cleanly: {ex.Message}",
                    @event: "delay.stopfailed");
            }
        });
    }

    // ------------------------------------------------------------------ output

    private async void StopOutput_Click(object sender, RoutedEventArgs e)
    {
        Log("Stop requested - releasing the transmitter.", Level.Warn);

        if (TidalLock.Shared.State != TidalLockState.Off)
            TidalLock.Shared.Disarm("Output stopped by the operator.");

        Task released = StopDelayedFeed();

        _playout?.StopAll();

        await released;
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

        ShowOnAirTimecode();
        ShowAirFrames();
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
                long target = lockState.TargetFrames;

                LockPanel.Visibility = Visibility.Visible;
                CountdownText.Foreground = Brush("IpMint");

                // Nothing to count down when there is no delay to fill: this state lasts the
                // frame or two before the first one arrives, and "ON AIR IN 00:00" for that
                // moment would read as a stalled countdown rather than as an imminent cut.
                if (target <= 0)
                {
                    LockHeadline.Text = "TIDAL LOCK - GOING TO AIR";
                    CountdownText.Text = "now";
                    CountdownDetail.Text =
                        "No delay - the transmitter takes the receiver's frames as they arrive.";
                    break;
                }

                LockHeadline.Text = "TIDAL LOCK - ON AIR IN";
                CountdownText.Text = $"{(int)left.TotalMinutes:00}:{left.Seconds:00}";

                CountdownDetail.Text =
                    $"{lockState.FramesBuffered:N0} of {target:N0} frames buffered" +
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

    /// <summary>
    /// Says something, on this panel and into the application record.
    ///
    /// The deck used to talk only to itself: four hundred lines that existed until the window
    /// closed, and nothing about arming, disarming, taking the transmitter or putting a clip
    /// to air ever reached the monitoring page. Everything an operator does here touches the
    /// output, so all of it belongs in the record.
    /// </summary>
    private void Log(string message, Level level = Level.Info)
    {
        ActivityLog.Shared.Write(LogSource.Playback, level switch
        {
            Level.Ok => LogLevel.Ok,
            Level.Warn => LogLevel.Warn,
            Level.Error => LogLevel.Error,
            _ => LogLevel.Info,
        }, message);

        Show(message, level);
    }

    /// <summary>
    /// Puts a line on this panel and nowhere else.
    ///
    /// For things already written down by whoever owns them — tidal lock narrates its own
    /// transitions from inside Core now — where going through <see cref="Log"/> would put the
    /// same sentence in the record twice.
    /// </summary>
    private void Show(string message, Level level = Level.Info) =>
        Add("DECK", message, level switch
        {
            Level.Ok => Brush("Ok"),
            Level.Warn => Brush("Warn"),
            Level.Error => Brush("IpRed"),
            _ => Brush("IpText"),
        });

    /// <summary>
    /// The sources this deck's log is about.
    ///
    /// Playback only, which is the point of it. The capture deck, the EDL and the ingest all
    /// write into the same record and none of them is this operator's business while they are
    /// watching a transmitter — the Monitor page is where everything together belongs. What does
    /// belong here is the playout engine and tidal lock, because those two <i>are</i> playback
    /// and the deck has only ever shown its own half of what they do.
    /// </summary>
    private static bool IsPlayback(LogSource source) =>
        source is LogSource.Playback or LogSource.Playout or LogSource.TidalLock;

    /// <summary>
    /// A line from the record, filtered to playback and marshalled onto the UI thread.
    ///
    /// Raised on whichever thread wrote it — the playout thread, very often — so it cannot touch
    /// the collection the panel is bound to without this.
    /// </summary>
    private void OnLogLine(LogLine line)
    {
        if (!IsPlayback(line.Source)) return;

        // The deck's own lines arrive here as well as through Show, because Log writes them to
        // the record. Taking them only from the record would be one path instead of two, but the
        // record is written from a background thread and the panel would then lag the button the
        // operator just pressed.
        if (line.Source == LogSource.Playback) return;

        Dispatcher.BeginInvoke(() => Add(line.SourceText, line.Message, line.Level switch
        {
            LogLevel.Ok => Brush("Ok"),
            LogLevel.Warn => Brush("Warn"),
            LogLevel.Error => Brush("IpRed"),
            _ => Brush("IpText"),
        }));
    }

    private void Add(string source, string message, Brush brush)
    {
        if (message.Length == 0) return;

        _log.Add(new LogRow(DateTime.Now.ToString("HH:mm:ss"), source, message, brush));
        while (_log.Count > 600) _log.RemoveAt(0);

        if (FollowLogButton.IsChecked == true) LogScroller.ScrollToEnd();
    }


    /// <summary>
    /// Frames out against frames recorded, which is the question asked of a delay: is it keeping
    /// up?
    ///
    /// The difference between the two should be exactly the delay and nothing else. Anything
    /// beyond it is a frame that was recorded and did not reach air, so it is named and it turns
    /// red rather than being left for someone to subtract in their head.
    /// </summary>
    private void ShowAirFrames()
    {
        FrameLedger frames = PipelineState.Shared.Frames;
        TidalLock tidal = TidalLock.Shared;

        if (frames.Aired == 0 && tidal.State is TidalLockState.Off or TidalLockState.Armed)
        {
            AirFramesText.Text = "-";
            AirFramesText.Foreground = Brush("IpDim");
            return;
        }

        long delay = tidal.TargetFrames;
        // From the frame of the recording actually on air, not from frames sent: the line can have
        // been filling since the recording began, long before this transmitter started counting.
        // A second of slack, because the recorded count is only published once a second.
        long slack = TidalLock.Shared.Line?.FrameRate ?? 25;
        long behind = PipelineState.Shared.AiredFrame is { } airedAt
            ? Math.Max(0, frames.Recorded - (airedAt + 1) - delay - slack)
            : 0;

        AirFramesText.Text =
            $"{frames.Aired:N0} out of {frames.Recorded:N0}  ·  delay {delay:N0}" +
            (behind > 0 ? $"  ·  {behind:N0} BEHIND" : "") +
            (frames.Skipped > 0 ? $"  ·  {frames.Skipped:N0} SKIPPED" : "") +
            (frames.Repeated > 0 ? $"  ·  {frames.Repeated:N0} repeated" : "") +
            (frames.Resyncs > 0 ? $"  ·  {frames.Resyncs} resync" : "") +
            (behind == 0 && frames.Skipped == 0 && frames.Repeated == 0 ? "  ·  no drops" : "");

        AirFramesText.Foreground = Brush(
            behind > 0 || frames.Skipped > 0 ? "IpRed"
            : frames.Repeated > 0 ? "Warn"
            : "IpTeal");
    }

    /// <summary>
    /// The timecode on air: the time of day the picture going out now was captured at, and how
    /// far into the recording it is. The same figure Live Edit draws its on-air mark from.
    ///
    /// From the frame the transmitter says it actually sent, not from the clock minus the delay,
    /// so every repeat and skip is already in it - this is what went out, not what should have.
    /// </summary>
    private void ShowOnAirTimecode()
    {
        if (PipelineState.Shared.AiredFrame is not { } frame)
        {
            AirTcPanel.Visibility = Visibility.Collapsed;
            return;
        }

        AirTcPanel.Visibility = Visibility.Visible;

        Timecode? start = PipelineState.Shared.RecordingStart;
        int rate = start?.Rate ?? TidalLock.Shared.Line?.FrameRate ?? 25;

        AirTcText.Text = PipelineState.TimecodeOf(start, frame)?.ToString() ?? "--:--:--:--";
        AirElapsedText.Text = $"+{new Timecode(frame, Math.Max(1, rate))}";
    }

    private void ClearLog_Click(object sender, RoutedEventArgs e) => _log.Clear();

    private static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.White;
}
