using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Emerald.Core;
using Emerald.Deltacast;
using Emerald.Edl;
using Emerald.Ingest;
using Emerald.LiveEdit;
using Emerald.Media;
using Emerald.Video;

namespace Emerald.App;

/// <summary>
/// The Emerald IP capture deck: the store on the left, the receiver on the right.
///
/// The stage plays back what has already been captured while the deck down the side shows
/// the receiver live and records it — which is why both halves live in one window rather
/// than two. EDL and Live Edit still open as windows in this process, so the shell preview
/// and the EDL recorder go on sharing one receiver through <see cref="RxLease"/>. Two
/// processes could not negotiate over hardware neither owns.
///
/// The recording configuration on the deck is the one the EDL records with: the settings
/// object is handed to <see cref="EdlWindow"/> and both assemble their recording through
/// <see cref="RecordingSetup"/>.
/// </summary>
public partial class ShellWindow : Window
{
    /// <summary>The design the layout is drawn at, before it is scaled to the monitor.</summary>
    private const double DesignWidth = 1060;
    private const double DesignHeight = 790;

    /// <summary>Rates the receiver is told to assume when it reports no standard of its own.</summary>
    private sealed record FpsOption(string Label, int Rate)
    {
        public override string ToString() => Label;
    }

    /// <summary>One tile in the clip strip. The thumbnail arrives later, hence the notification.</summary>
    private sealed class ClipItem : INotifyPropertyChanged
    {
        public required string Path { get; init; }
        public required string Display { get; init; }
        public required string Stamp { get; init; }
        public required string Details { get; init; }
        public required TimeSpan Duration { get; init; }

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

    private readonly AppSettings _settings = App.Settings;
    private readonly RxPreview _preview = new();
    private readonly SdiCapture _capture = new();

    /// <summary>The application's one clock. This window joins it; it never owns it.</summary>
    private readonly TimecodeService _timecode = TimecodeLink.Service;
    private readonly ObservableCollection<ClipItem> _clips = new();

    // The clock is redrawn a frame at a time; everything else needs looking at twice a
    // second at most, and doing both on one timer would either stutter the count or spend
    // the machine's afternoon re-reading the recorder's state.
    private readonly DispatcherTimer _clock = new(DispatcherPriority.Render)
        { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(500) };
    // Long enough for the video renderer to have presented a frame. Pausing sooner than that
    // leaves the stage black, because a paused MediaElement shows only what it has already
    // played - which is also why cueing plays at all.
    private readonly DispatcherTimer _cue = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private IReadOnlyList<BoardInfo> _boards = Array.Empty<BoardInfo>();

    /// <summary>Suppresses field handlers while the UI is being populated programmatically.</summary>
    private bool _loading = true;

    private EdlWindow? _edl;
    private LiveEditWindow? _liveEdit;
    private IngestControllerWindow? _ingest;

    // Player
    private bool _playing;
    private bool _cueing;
    private TimeSpan _clipLength;
    private TimeSpan? _markIn, _markOut;
    private bool _syncingInfo;
    private bool _expanded;

    // Recorder
    private bool _recording;
    private DateTime _recordStartedUtc;
    private TimeSpan? _recordLimit;

    // Bumped whenever the strip is rebuilt, so thumbnails from a previous pass are dropped
    // rather than landing on tiles that are no longer the ones they were rendered for.
    private int _stripGeneration;

    private string _previewNote = "";

    // The monitor while the recorder owns the receiver: its frames, copied off the capture
    // thread and written into a bitmap of this window's own.
    private byte[]? _tapBuffer;
    private WriteableBitmap? _tapBitmap;

    public ShellWindow()
    {
        InitializeComponent();

        // Before the window is shown, so it opens centred on the size it will actually be.
        FitToScreen();

        _preview.Status += OnPreviewStatus;
        _preview.FrameReady += OnPreviewFrame;
        _preview.FormatChanged += OnPreviewFormat;

        _capture.Message += OnCaptureMessage;
        _capture.PreviewFrame += OnCapturePreviewFrame;

        ClipStrip.ItemsSource = _clips;

        FpsCombo.ItemsSource = new[]
        {
            new FpsOption("24", 24),
            new FpsOption("25 DVB-T", 25),
            new FpsOption("30", 30),
            new FpsOption("50", 50),
            new FpsOption("60", 60),
        };

        VideoBitrateCombo.ItemsSource = RecordingProfile.ProxyBitrates;
        AudioBitrateCombo.ItemsSource = RecordingProfile.AudioBitrates;
        SampleRateCombo.ItemsSource = RecordingProfile.SampleRates;

        _clock.Tick += OnClockTick;
        _tick.Tick += OnTick;
        _cue.Tick += OnCueTick;

        Loaded += ShellWindow_Loaded;
        Closing += ShellWindow_Closing;
    }

    // ------------------------------------------------------------------ startup

    private async void ShellWindow_Loaded(object sender, RoutedEventArgs e)
    {
        OutputPathBox.Text = MediaLibrary.FolderFor(_settings);
        RecordTitleBox.Text = _settings.RecordingTitle.Length == 0 ? "capture" : _settings.RecordingTitle;
        RecordDescriptionBox.Text = _settings.RecordingDescription;
        SetDurationBox.Text = _settings.RecordingDuration;

        LoadProfile();
        UpdateBreadcrumbs();
        UpdateRecordUi();

        TimecodeLink.Connect(_settings);
        TimecodeLink.UrlChanged += OnTimecodeUrlChanged;
        ApiUrlBox.Text = TimecodeLink.Url;

        _clock.Start();
        _tick.Start();

        await ScanBoards();
        RefreshClips();
    }

    /// <summary>
    /// Opens at the largest whole-design fit the monitor allows, leaving the taskbar and the
    /// title bar their room. From there the window resizes freely: the Viewbox scales the
    /// design to fill it, so this only chooses where that starts.
    /// </summary>
    private void FitToScreen()
    {
        double chromeWidth = 2 * SystemParameters.ResizeFrameVerticalBorderWidth;
        double chromeHeight = SystemParameters.WindowCaptionHeight
                            + 2 * SystemParameters.ResizeFrameHorizontalBorderHeight;

        double availableWidth = SystemParameters.WorkArea.Width - chromeWidth - 40;
        double availableHeight = SystemParameters.WorkArea.Height - chromeHeight - 24;

        double scale = Math.Clamp(
            Math.Min(availableWidth / DesignWidth, availableHeight / DesignHeight), 1.0, 1.6);

        Width = Math.Round(DesignWidth * scale + chromeWidth);
        Height = Math.Round(DesignHeight * scale + chromeHeight);
    }

    private async Task ScanBoards()
    {
        bool wasLoading = _loading;
        _loading = true;

        RefreshButton.IsEnabled = false;
        ApiVersionText.Text = "scanning boards...";

        BoardScanResult scan = await BoardService.ScanAsync();
        _boards = scan.Boards;

        BoardBox.ItemsSource = _boards;

        ApiVersionText.Text = scan.Error is not null
            ? scan.Error
            : $"VideoMaster {scan.ApiVersionString} - {_boards.Count} board{(_boards.Count == 1 ? "" : "s")}";

        // Come up on whatever the operator last recorded from, and only on a board that can
        // actually receive: the preview and the recorder share one receiver, so showing a
        // different one by default would be misleading.
        BoardInfo? board = _boards.FirstOrDefault(b => b.Index == _settings.CaptureBoardIndex && b.RxCount > 0)
                        ?? _boards.FirstOrDefault(b => b.RxCount > 0)
                        ?? _boards.FirstOrDefault();

        BoardBox.SelectedItem = board;
        _loading = wasLoading;

        PopulatePorts(_settings.CapturePort);
        RefreshButton.IsEnabled = true;
        UpdateBoardDetail();
        StartPreview();
    }

    private void PopulatePorts(string? preferred)
    {
        bool wasLoading = _loading;
        _loading = true;

        var board = BoardBox.SelectedItem as BoardInfo;
        PortBox.ItemsSource = board?.RxPorts;

        PortBox.SelectedItem = board is null
            ? null
            : board.RxPorts.FirstOrDefault(p => p.Name == preferred) ?? board.RxPorts.FirstOrDefault();

        _loading = wasLoading;
    }

    private void UpdateBoardDetail()
    {
        BoardDetailText.Text = BoardBox.SelectedItem is BoardInfo board
            ? $"{board.DisplayName} - {board.BoardTypeName}, {board.RxCount} RX / {board.TxCount} TX"
            : "no DELTACAST board selected";
    }

    // ------------------------------------------------------------------ preview

    private void StartPreview()
    {
        if (BoardBox.SelectedItem is not BoardInfo board || PortBox.SelectedItem is not ChannelPort port)
        {
            _preview.Stop();
            SetStatus("no receiver selected", true);
            UpdateSourceDetails();
            return;
        }

        // Recording owns the receiver while it runs; re-opening the preview underneath it
        // would only be refused by the lease.
        if (_recording || _capture.IsRunning) return;

        SetStatus($"opening RX{port.Index} on board {board.Index}...", false);
        PreviewImage.Source = null;
        PreviewPlaceholder.Visibility = Visibility.Visible;
        UpdateSourceDetails();

        _preview.Start(board.Index, port.Index);
    }

    private void OnPreviewStatus(string text, bool problem)
    {
        SetStatus(text, problem);

        // A message about not having the input means there is nothing to show; drop the last
        // frame so a stale picture cannot be mistaken for live signal.
        if (problem)
        {
            PreviewImage.Source = null;
            PreviewPlaceholder.Visibility = Visibility.Visible;
        }
    }

    private void SetStatus(string text, bool problem)
    {
        _previewNote = text;
        PreviewStatus.Text = text;
        PreviewStatus.Foreground = (Brush)FindResource(problem ? "Warn" : "IpMuted");
        SourceDescriptionText.Text = text;
    }

    private void OnPreviewFrame(WriteableBitmap bitmap)
    {
        PreviewImage.Source = bitmap;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
    }

    private void OnPreviewFormat(CaptureFormat? format) =>
        SourceDurationText.Text = format is null ? "no lock" : $"live  |  {format.Name}";

    private void UpdateSourceDetails()
    {
        var board = BoardBox.SelectedItem as BoardInfo;
        var port = PortBox.SelectedItem as ChannelPort;

        SourceUrlText.Text = board is null || port is null
            ? "no receiver selected"
            : $"board {board.Index}  |  {port.Name}";

        SourceTitleText.Text = board?.Model ?? "-";
        SourceDescriptionText.Text = _previewNote.Length == 0 ? "-" : _previewNote;
    }

    private void BoardBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        PopulatePorts(null);
        UpdateBoardDetail();
        StartPreview();
    }

    private void PortBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        StartPreview();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _preview.Stop();
        await ScanBoards();
    }

    // ------------------------------------------------------------------ the profile

    /// <summary>Puts the stored recording settings into the fields that drive them.</summary>
    private void LoadProfile()
    {
        bool wasLoading = _loading;
        _loading = true;

        RecordingProfile profile = RecordingProfile.From(_settings);

        FpsCombo.SelectedItem = FpsCombo.Items.OfType<FpsOption>()
                                    .FirstOrDefault(o => o.Rate == _settings.CaptureFrameRate)
                             ?? FpsCombo.Items.OfType<FpsOption>().First(o => o.Rate == 25);

        Select(VideoBitrateCombo, RecordingProfile.ProxyBitrates, profile.ProxyBitrateKbps);
        Select(AudioBitrateCombo, RecordingProfile.AudioBitrates, profile.AudioBitrateKbps);
        Select(SampleRateCombo, RecordingProfile.SampleRates, profile.AudioSampleRate);

        _loading = wasLoading;
        ShowProfile(profile);

        static void Select<T>(ComboBox box, IReadOnlyList<RecordingOption<T>> options, T value) =>
            box.SelectedItem = options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value))
                            ?? options[0];
    }

    /// <summary>The profile as the fields currently stand.</summary>
    private RecordingProfile CurrentProfile() => new(
        ProxyBitrateKbps: Value(VideoBitrateCombo, RecordingProfile.Default.ProxyBitrateKbps),
        AudioBitrateKbps: Value(AudioBitrateCombo, RecordingProfile.Default.AudioBitrateKbps),
        AudioSampleRate: Value(SampleRateCombo, RecordingProfile.Default.AudioSampleRate),
        SegmentSeconds: _settings.RecordingSegmentSeconds);

    private static T Value<T>(ComboBox box, T fallback) =>
        box.SelectedItem is RecordingOption<T> option ? option.Value : fallback;

    /// <summary>
    /// A change to any encoder field is written straight through to the settings, because
    /// they are the settings the EDL records with too — it holds this same object.
    /// </summary>
    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        RecordingProfile profile = CurrentProfile();
        profile.ApplyTo(_settings);
        _settings.CaptureFrameRate = SelectedRate();

        ShowProfile(profile);

        // The stage quotes clip lengths against the configured rate.
        if (ClipStrip.SelectedItem is ClipItem clip)
            ClipDurationText.Text = $"{Format(_clipLength > TimeSpan.Zero ? _clipLength : clip.Duration)}  |  {SelectedRate()} fps";
    }

    private void ShowProfile(RecordingProfile profile)
    {
        // The receiver's own format is what the master keeps; the proxy is half of it.
        SourceFormatText.Text = RecordingProfile.Summary(RecordingProfile.HighRes);

        string proxy = profile.ProxyBitrateKbps > 0 ? $"{profile.ProxyBitrateKbps}k" : "auto";

        ProfileNote.Text =
            $"{profile.SegmentSeconds / 60} min segments, written twice  ·  " +
            $"low\\ half-size {RecordingProfile.LowRes.VideoLabel} {proxy}  ·  " +
            $"high\\ {RecordingProfile.HighRes.VideoLabel}  ·  " +
            $"{RecordingProfile.AudioLabel} {profile.AudioBitrateKbps}k " +
            $"{RecordingProfile.Label(RecordingProfile.SampleRates, profile.AudioSampleRate)}";

        ProfileNote.Foreground = (Brush)FindResource("IpDim");
    }

    // ------------------------------------------------------------------ the store

    private string StoreFolder
    {
        get
        {
            string typed = OutputPathBox.Text.Trim();
            return typed.Length == 0 ? MediaLibrary.DefaultFolder : typed;
        }
    }

    private void OutputPath_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_loading) return;

        OutputPathBox.Text = StoreFolder;
        _settings.CaptureFolder = OutputPathBox.Text.Trim();
        RefreshClips();
    }

    private void RefreshClips()
    {
        string folder = StoreFolder;
        int rate = SelectedRate();
        string? ffmpeg = Ffmpeg.Locate(_settings.FfmpegPath);
        string? selected = (ClipStrip.SelectedItem as ClipItem)?.Path;
        int generation = ++_stripGeneration;

        UpdateBreadcrumbs();

        // Listing probes every file with ffprobe, which is far too slow to do on the way
        // through a UI event.
        Task.Run(() =>
        {
            IReadOnlyList<CapturedClip> clips = MediaLibrary.List(folder, MediaProbe.LocateFfprobe(ffmpeg), rate);

            var items = clips.Select(c => new ClipItem
            {
                Path = c.Path,
                Display = c.Name,
                Stamp = $"{c.Recorded:HH:mm:ss}  |  {c.DurationText}",
                Details = $"{c.FormatText}, {c.SizeText}" +
                          (c.HasMaster ? $"  ·  {RecordingProfile.HighRes.VideoLabel} master on disk" : ""),
                Duration = c.Info?.Duration ?? TimeSpan.Zero,
            }).ToList();

            Dispatcher.BeginInvoke(() =>
            {
                if (generation != _stripGeneration) return;

                _clips.Clear();
                foreach (ClipItem item in items) _clips.Add(item);

                StripEmpty.Visibility = _clips.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                ClipStrip.SelectedItem = _clips.FirstOrDefault(c => c.Path == selected);
            });

            if (ffmpeg is null) return;

            foreach (ClipItem item in items)
            {
                if (generation != _stripGeneration) return;

                ImageSource? thumb = ClipThumbnails.Get(ffmpeg, item.Path);
                if (thumb is null) continue;

                Dispatcher.BeginInvoke(() =>
                {
                    if (generation == _stripGeneration) item.Thumbnail = thumb;
                });
            }
        });
    }

    private void UpdateBreadcrumbs()
    {
        string folder = StoreFolder;

        try
        {
            var dir = new DirectoryInfo(folder);
            StoreCrumb.Content = dir.Name;
            ParentCrumb.Content = dir.Parent?.Name ?? "...";
        }
        catch (ArgumentException)
        {
            StoreCrumb.Content = "media";
            ParentCrumb.Content = "...";
        }

        StoreCrumb.ToolTip = folder;
        ParentCrumb.ToolTip = folder;
    }

    private void RefreshClips_Click(object sender, RoutedEventArgs e) => RefreshClips();

    private void OpenStore_Click(object sender, RoutedEventArgs e)
    {
        string folder = StoreFolder;

        try
        {
            Directory.CreateDirectory(folder);
            Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"cannot open {folder}: {ex.Message}", true);
        }
    }

    // ------------------------------------------------------------------ the stage

    private void ClipStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ClipStrip.SelectedItem is not ClipItem clip)
        {
            Player.Stop();
            Player.Source = null;
            SetPlaying(false);
            _clipLength = TimeSpan.Zero;
            StagePlaceholder.Text = "no clip selected";
            StagePlaceholder.Visibility = Visibility.Visible;
            ClipTitleText.Text = "-";
            ClipDescriptionText.Text = "-";
            ClipDurationText.Text = "-";
            UpdateScrub();
            return;
        }

        ClearMarks();

        ClipTitleText.Text = clip.Display;
        ClipDescriptionText.Text = clip.Details;
        ClipDurationText.Text = $"{Format(clip.Duration)}  |  {SelectedRate()} fps";

        _clipLength = clip.Duration;
        StagePlaceholder.Text = "opening clip...";
        StagePlaceholder.Visibility = Visibility.Visible;

        // Selecting a clip cues it, it does not start it: the operator presses play. Cueing
        // still means letting it run for a moment, because a paused MediaElement presents no
        // frame it has not already played — so it is muted across the cue and stopped by
        // OnCueTick as soon as there is a picture.
        Player.Source = new Uri(clip.Path);
        BeginCue();
    }

    /// <summary>Runs the player just far enough to put its first frame on the stage.</summary>
    private void BeginCue()
    {
        _cueing = true;
        Player.IsMuted = true;
        Player.Play();
        SetPlaying(false);
    }

    private void OnCueTick(object? sender, EventArgs e)
    {
        _cue.Stop();
        EndCue();
    }

    private void EndCue()
    {
        if (!_cueing) return;

        _cueing = false;
        Player.Pause();
        Player.IsMuted = MuteButton.Tag == FindResource("IconMute");
        SetPlaying(false);
        UpdateScrub();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        StagePlaceholder.Visibility = Visibility.Collapsed;

        if (Player.NaturalDuration.HasTimeSpan)
        {
            _clipLength = Player.NaturalDuration.TimeSpan;
            ClipDurationText.Text = $"{Format(_clipLength)}  |  {SelectedRate()} fps";
        }

        // Only now is there anything to present, so the cue is timed from here rather than
        // from the click that opened the file.
        if (_cueing) { _cue.Stop(); _cue.Start(); }

        UpdateScrub();
    }

    private void Player_MediaEnded(object sender, RoutedEventArgs e)
    {
        // Held on the last frame rather than stopped. Stop() clears the picture to black,
        // and so does cueing back to the head, so the end of the clip is what stays on
        // screen; play from here starts again at the head.
        Player.Pause();
        SetPlaying(false);
        UpdateScrub();
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _cueing = false;
        SetPlaying(false);
        StagePlaceholder.Text = "this clip cannot be played here";
        StagePlaceholder.Visibility = Visibility.Visible;
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        PlayButton.Tag = FindResource(playing ? "IconPause" : "IconPlay");
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (Player.Source is null) return;

        // A click during the cue is the operator asking for the clip now.
        if (_cueing)
        {
            _cueing = false;
            _cue.Stop();
            Player.IsMuted = MuteButton.Tag == FindResource("IconMute");
            SetPlaying(true);
            return;
        }

        if (_playing) { Player.Pause(); SetPlaying(false); return; }

        // Play at the end of a clip means play it again, not sit there.
        if (_clipLength > TimeSpan.Zero && Player.Position >= _clipLength - TimeSpan.FromMilliseconds(200))
            Player.Position = _markIn ?? TimeSpan.Zero;

        Player.Play();
        SetPlaying(true);
    }

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        if (Player.Source is null) return;

        EndCue();
        Player.Pause();
        Player.Position = _markIn ?? TimeSpan.Zero;
        SetPlaying(false);
        UpdateScrub();
    }

    private void Rewind_Click(object sender, RoutedEventArgs e) => Nudge(TimeSpan.FromSeconds(-10));

    private void Forward_Click(object sender, RoutedEventArgs e) => Nudge(TimeSpan.FromSeconds(10));

    private void Nudge(TimeSpan by)
    {
        if (Player.Source is null) return;

        TimeSpan target = Player.Position + by;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (_clipLength > TimeSpan.Zero && target > _clipLength) target = _clipLength;

        Player.Position = target;
        UpdateScrub();
    }

    private void PrevClip_Click(object sender, RoutedEventArgs e) => StepClip(-1);

    private void NextClip_Click(object sender, RoutedEventArgs e) => StepClip(1);

    private void StepClip(int by)
    {
        if (_clips.Count == 0) return;

        int index = ClipStrip.SelectedIndex + by;
        if (index < 0 || index >= _clips.Count) return;

        ClipStrip.SelectedIndex = index;
        ClipStrip.ScrollIntoView(ClipStrip.SelectedItem);
    }

    private void MarkIn_Click(object sender, RoutedEventArgs e)
    {
        if (Player.Source is null) return;

        _markIn = Player.Position;
        if (_markOut is not null && _markOut <= _markIn) _markOut = null;
        UpdateScrub();
    }

    private void MarkOut_Click(object sender, RoutedEventArgs e)
    {
        if (Player.Source is null) return;

        _markOut = Player.Position;
        if (_markIn is not null && _markIn >= _markOut) _markIn = null;
        UpdateScrub();
    }

    private void ClearMarks_Click(object sender, RoutedEventArgs e)
    {
        ClearMarks();
        UpdateScrub();
    }

    private void ClearMarks()
    {
        _markIn = null;
        _markOut = null;
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        bool muted = MuteButton.Tag != FindResource("IconMute");

        MuteButton.Tag = FindResource(muted ? "IconMute" : "IconVolume");
        MuteButton.Foreground = (Brush)FindResource(muted ? "IpMuted" : "IpBlue");

        // The cue runs silent whatever the operator chose; it restores this on the way out.
        if (!_cueing) Player.IsMuted = muted;
    }

    private void Scrub_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Player.Source is null || _clipLength <= TimeSpan.Zero || ScrubTrack.ActualWidth <= 0) return;

        double ratio = Math.Clamp(e.GetPosition(ScrubTrack).X / ScrubTrack.ActualWidth, 0, 1);
        Player.Position = TimeSpan.FromSeconds(_clipLength.TotalSeconds * ratio);
        UpdateScrub();
    }

    /// <summary>Redraws the playhead and the two marks against the track's own width.</summary>
    private void UpdateScrub()
    {
        double width = ScrubTrack.ActualWidth;

        if (width <= 0 || _clipLength <= TimeSpan.Zero)
        {
            ScrubFill.Width = 0;
            MarkInTick.Visibility = Visibility.Collapsed;
            MarkOutTick.Visibility = Visibility.Collapsed;
            return;
        }

        double At(TimeSpan t) => Math.Clamp(t.TotalSeconds / _clipLength.TotalSeconds, 0, 1) * width;

        ScrubFill.Width = At(Player.Position);

        Place(MarkInTick, _markIn);
        Place(MarkOutTick, _markOut);

        void Place(Border tick, TimeSpan? at)
        {
            if (at is null) { tick.Visibility = Visibility.Collapsed; return; }

            tick.Visibility = Visibility.Visible;
            tick.Margin = new Thickness(Math.Max(0, At(at.Value) - 1), 0, 0, 0);
        }
    }

    private void ToggleInfo_Click(object sender, RoutedEventArgs e) =>
        SetInfoVisible(InfoOverlay.Visibility != Visibility.Visible);

    private void HideInfo_Click(object sender, RoutedEventArgs e) => SetInfoVisible(false);

    private void Notes_Changed(object sender, RoutedEventArgs e)
    {
        // A toggle that starts out checked raises this while the window is still being
        // parsed, when the fields it reaches for have not been assigned yet. The XAML
        // already declares the state those first events would set.
        if (!IsInitialized || _syncingInfo) return;

        SetInfoVisible(NotesToggle.IsChecked == true);
    }

    private void SetInfoVisible(bool visible)
    {
        InfoOverlay.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

        _syncingInfo = true;
        NotesToggle.IsChecked = visible;
        _syncingInfo = false;
    }

    private void StripToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        StripHost.Visibility = StripToggle.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Hands the picture the rest of the window, and hands it back.</summary>
    private void Expand_Click(object sender, RoutedEventArgs e)
    {
        _expanded = !_expanded;

        StripToggle.IsChecked = !_expanded;
        CaptureDeckTab.IsChecked = !_expanded;
    }

    // ------------------------------------------------------------------ recorder

    private int SelectedRate() => (FpsCombo.SelectedItem as FpsOption)?.Rate ?? 25;

    private void Record_Click(object sender, RoutedEventArgs e)
    {
        if (_recording) { StopRecording(); return; }
        StartRecording();
    }

    private void StopRecord_Click(object sender, RoutedEventArgs e) => StopRecording();

    private void StartRecording()
    {
        if (BoardBox.SelectedItem is not BoardInfo board || PortBox.SelectedItem is not ChannelPort port)
        {
            SetStatus("no receiver selected - pick a board and RX port.", true);
            return;
        }

        string folder = StoreFolder;
        OutputPathBox.Text = folder;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            SetStatus($"cannot write to {folder}: {ex.Message}", true);
            return;
        }

        // Written through before the request is assembled, because the request is built from
        // the settings - the same way the EDL builds its own.
        CurrentProfile().ApplyTo(_settings);
        _settings.CaptureFrameRate = SelectedRate();
        _settings.CaptureFolder = folder;

        if (!RecordingSetup.TryBuild(_settings, board.Index, port.Index, folder, SelectedRate(),
                                     RecordTitleBox.Text.Trim(), out CaptureRequest? request, out string? problem))
        {
            SetStatus(problem!, true);
            return;
        }

        _recordLimit = ParseSetDuration(SelectedRate());
        _recordStartedUtc = DateTime.UtcNow;

        _capture.Start(request!);

        _recording = true;
        UpdateRecordUi();
    }

    private async void StopRecording()
    {
        if (!_recording && !_capture.IsRunning) return;

        _recording = false;
        _recordLimit = null;
        RecordText.Text = "Stopping...";

        // Stop joins the capture thread so the encoder can finalise the file, which takes
        // long enough to be worth keeping off the UI thread.
        await Task.Run(() => _capture.Stop());

        UpdateRecordUi();
        RefreshClips();

        // The recorder held the receiver; the preview can have it back.
        StartPreview();
    }

    /// <summary>The Set Duration field, or null when it is empty or zero.</summary>
    private TimeSpan? ParseSetDuration(int rate)
    {
        if (!Timecode.TryParse(SetDurationBox.Text, rate, out Timecode value, out _)) return null;
        return value.TotalFrames <= 0 ? null : TimeSpan.FromSeconds(value.TotalSeconds);
    }

    private void UpdateRecordUi()
    {
        bool live = _recording || _capture.IsRunning;

        RecordText.Text = live ? "Recording" : "Record";
        RecordText.Foreground = (Brush)FindResource(live ? "IpMint" : "IpText");
        StopRecordButton.Foreground = (Brush)FindResource(live ? "IpText" : "IpMuted");

        // The receiver cannot be moved out from under a running recording.
        BoardBox.IsEnabled = PortBox.IsEnabled = !live;
    }

    private void OnCaptureMessage(string text, bool problem) =>
        Dispatcher.BeginInvoke(() => SetStatus(text, problem));

    /// <summary>
    /// The picture while recording. The card allows one open handle per input, so the shell's
    /// own preview steps aside when the recorder claims the receiver — and the recorder hands
    /// back the frames it is already reading, which is what keeps the monitor live across the
    /// handover instead of going dark for the length of the recording.
    /// </summary>
    private void OnCapturePreviewFrame(byte[] bgra, int width, int height)
    {
        // Copied here, on the capture thread, because the recorder reuses its buffers and the
        // write into the bitmap happens a hop later.
        byte[]? buffer = _tapBuffer;

        if (buffer is null || buffer.Length != bgra.Length)
            _tapBuffer = buffer = new byte[bgra.Length];

        Buffer.BlockCopy(bgra, 0, buffer, 0, bgra.Length);

        Dispatcher.BeginInvoke(() =>
        {
            if (_tapBitmap is null || _tapBitmap.PixelWidth != width || _tapBitmap.PixelHeight != height)
                _tapBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);

            _tapBitmap.WritePixels(new Int32Rect(0, 0, width, height), buffer, width * 4, 0);

            PreviewImage.Source = _tapBitmap;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
        });
    }

    private void Snapshot_Click(object sender, RoutedEventArgs e)
    {
        if (PreviewImage.Source is not BitmapSource source)
        {
            SetStatus("no frame to save yet.", true);
            return;
        }

        try
        {
            string folder = StoreFolder;
            Directory.CreateDirectory(folder);

            string path = Path.Combine(folder, $"snapshot_{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.png");

            // The preview bitmap is written to in place, so the encoder gets a copy of its
            // own rather than whatever the next frame turns it into.
            BitmapSource frame = source.Clone();
            frame.Freeze();

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(frame));

            using (FileStream file = File.Create(path)) encoder.Save(file);

            SetStatus($"saved {Path.GetFileName(path)}", false);
        }
        catch (Exception ex)
        {
            SetStatus($"snapshot failed: {ex.Message}", true);
        }
    }

    // ------------------------------------------------------------------ clock

    /// <summary>
    /// The master clock, redrawn every 25 ms so the frame field counts rather than steps.
    /// <see cref="TimecodeService"/> free-wheels between server polls and slews the
    /// disagreement out, so this only has to ask it what the time is.
    /// </summary>
    private void OnClockTick(object? sender, EventArgs e)
    {
        ClockText.Text = _timecode.TryGetCurrent(out Timecode now) ? now.ToString() : "--:--:--:--";

        if (_playing)
        {
            UpdateScrub();

            // Playback stops at the out point when one is set, so a marked range can be
            // reviewed without watching the rest of the segment.
            if (_markOut is not null && Player.Position >= _markOut)
            {
                Player.Pause();
                SetPlaying(false);
            }
        }
    }

    private void OnTick(object? sender, EventArgs e)
    {
        LinkButton.Foreground = (Brush)FindResource(_timecode.State switch
        {
            TimecodeLinkState.Online => "IpMint",
            TimecodeLinkState.Connecting => "Warn",
            _ => "IpDim",
        });

        ClockStatusText.Text = $"timecode: {_timecode.State.ToString().ToLowerInvariant()}" +
                               (_timecode.FrameRate > 0 ? $" at {_timecode.FrameRate} fps" : "") +
                               (_timecode.LastError is { Length: > 0 } error ? $" - {error}" : "");

        // The recorder can also stop on its own - a failed start, or a signal that never
        // arrived - so the button follows the thread rather than only the last click.
        if (_recording && !_capture.IsRunning)
        {
            _recording = false;
            _recordLimit = null;
            UpdateRecordUi();
            RefreshClips();
            StartPreview();
            return;
        }

        if (_recording && _recordLimit is not null && DateTime.UtcNow - _recordStartedUtc >= _recordLimit)
        {
            SetStatus($"recorded the set duration of {SetDurationBox.Text}.", false);
            StopRecording();
        }
    }

    // ------------------------------------------------------------------ decks

    private void SourceDeck_Click(object sender, RoutedEventArgs e) =>
        OpenSourceDeck(SourceDrawer.Visibility != Visibility.Visible);

    private void SourceDeck_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;
        OpenSourceDeck(SourceDeckTab.IsChecked == true);
    }

    private void CloseSourceDeck_Click(object sender, RoutedEventArgs e) => OpenSourceDeck(false);

    private void OpenSourceDeck(bool open)
    {
        SourceDrawer.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        SourceDeckTab.IsChecked = open;
    }

    private void CaptureDeck_Toggled(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized) return;

        bool open = CaptureDeckTab.IsChecked == true;
        CaptureDeck.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        // Coming back by the tab alone should also bring the strip back.
        if (open && _expanded)
        {
            _expanded = false;
            StripToggle.IsChecked = true;
        }
    }

    private void Link_Click(object sender, RoutedEventArgs e)
    {
        // Re-dial the shared timecode feed, and show what it says about itself.
        TimecodeLink.Redial();
        OpenSourceDeck(true);
    }

    /// <summary>
    /// Points every Emerald module at a different timecode generator. The address is one
    /// setting shared by the deck, the EDL and the Ingest Controller, so this is the same
    /// field they each show.
    /// </summary>
    private void ApplyApi_Click(object sender, RoutedEventArgs e)
    {
        if (!TimecodeLink.TrySetUrl(ApiUrlBox.Text, out string? problem))
        {
            ClockStatusText.Text = problem!;
            ClockStatusText.Foreground = (Brush)FindResource("IpRed");
            return;
        }

        ApiUrlBox.Text = TimecodeLink.Url;
        ClockStatusText.Foreground = (Brush)FindResource("IpDim");
    }

    /// <summary>Another module changed the generator's address; show the new one.</summary>
    private void OnTimecodeUrlChanged(string url) =>
        Dispatcher.BeginInvoke(() => ApiUrlBox.Text = url);

    private void Playback_Click(object sender, RoutedEventArgs e) =>
        ShowModule(ref _liveEdit, () => new LiveEditWindow());

    private void Logging_Click(object sender, RoutedEventArgs e) =>
        ShowModule(ref _edl, () => new EdlWindow(_settings));

    private void Ingest_Click(object sender, RoutedEventArgs e) =>
        ShowModule(ref _ingest, () => new IngestControllerWindow(_settings));

    /// <summary>
    /// Opens one of the deck's modules by name, for a second launch of Emerald that was
    /// handed to this instance rather than starting a process of its own. The window is the
    /// same one the nav buttons open, on this deck's settings.
    /// </summary>
    public void OpenModule(string? mode)
    {
        switch (mode)
        {
            case "--edl":
                ShowModule(ref _edl, () => new EdlWindow(_settings));
                break;

            case "--ingest":
                ShowModule(ref _ingest, () => new IngestControllerWindow(_settings));
                break;

            default:
                if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
                Activate();
                break;
        }
    }

    /// <summary>Opens a module window, or brings the existing one forward.</summary>
    private void ShowModule<T>(ref T? window, Func<T> create) where T : Window
    {
        if (window is null)
        {
            window = create();
            window.Owner = this;

            T created = window;
            created.Closed += (_, _) =>
            {
                if (ReferenceEquals(_edl, created)) _edl = null;
                if (ReferenceEquals(_liveEdit, created)) _liveEdit = null;
                if (ReferenceEquals(_ingest, created)) _ingest = null;

                // The EDL edits the settings this deck is showing.
                if (!_loading) LoadProfile();
            };

            window.Show();
            return;
        }

        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    // ------------------------------------------------------------------ shutdown

    private void ShellWindow_Closing(object? sender, CancelEventArgs e)
    {
        _clock.Stop();
        _tick.Stop();
        _cue.Stop();
        Player.Close();

        _capture.Stop();
        _preview.Dispose();

        // The clock is the application's, not this window's. Only the subscription is ours.
        TimecodeLink.UrlChanged -= OnTimecodeUrlChanged;

        SaveDeckSettings();

        // Module windows are owned, so they would close with the shell anyway; closing them
        // explicitly gives the EDL its chance to stop playout and save settings first.
        _edl?.Close();
        _liveEdit?.Close();
    }

    private void SaveDeckSettings()
    {
        CurrentProfile().ApplyTo(_settings);

        _settings.CaptureFolder = OutputPathBox.Text.Trim();
        _settings.CaptureFrameRate = SelectedRate();
        _settings.RecordingTitle = RecordTitleBox.Text.Trim();
        _settings.RecordingDescription = RecordDescriptionBox.Text.Trim();
        _settings.RecordingDuration = SetDurationBox.Text.Trim();

        if (BoardBox.SelectedItem is BoardInfo board) _settings.CaptureBoardIndex = board.Index;
        if (PortBox.SelectedItem is ChannelPort port) _settings.CapturePort = port.Name;

        _settings.Save();
    }

    /// <summary>HH:MM:SS:FF against the configured rate, which is how the deck quotes lengths.</summary>
    private string Format(TimeSpan span)
    {
        int rate = SelectedRate();
        return new Timecode((long)Math.Round(span.TotalSeconds * rate), rate).ToString();
    }
}
