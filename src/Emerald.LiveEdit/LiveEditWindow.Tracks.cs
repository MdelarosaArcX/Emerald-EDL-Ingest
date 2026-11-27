using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Emerald.Core;
using Emerald.Media;
using Emerald.Video;

namespace Emerald.LiveEdit;

/// <summary>
/// The recording's sound tracks: a lane for each, and which of them the editor hears.
///
/// <b>For the editor's ears only.</b> Clicking a lane's name, or double-clicking in it, plays that
/// pair in the preview at once, and from then on until another is clicked. Nothing about it goes to
/// air: every pair goes out on its own channels, CH 1-2 first, and which is heard there is chosen
/// downstream, on the hardware.
/// </summary>
public partial class LiveEditWindow
{
    /// <summary>The pair the editor is listening to. Not saved and not sent: it is only what is heard here.</summary>
    private int _listenTrack;

    /// <summary>The clickable name at the left of each lane.</summary>
    private const double TrackLabelWidth = 108;

    // ------------------------------------------------------------------ the tracks there are

    private List<Placed>? _lanesOf;
    private int _lanes = 1;

    /// <summary>How many lanes: as many as the segment with the most tracks has.</summary>
    private int TrackLanes
    {
        get
        {
            if (!ReferenceEquals(_lanesOf, _placed))
            {
                _lanesOf = _placed;
                _lanes = _placed.Count == 0 ? 1 : Math.Max(1, _placed.Max(p => p.Clip.Info?.Audio.Count ?? 1));
            }

            // The first segment is still being written, so there is no file to count the tracks of.
            return _primaryRecording ? Math.Max(_lanes, PairsInDelay() ?? 1) : _lanes;
        }
    }

    /// <summary>
    /// A segment's tracks. The one still being recorded has the same as the rest of its recording:
    /// the number is settled when a recording rolls and does not change.
    /// </summary>
    private int TracksOf(Placed segment)
    {
        if (segment.Clip.Info?.Audio.Count is { } known and > 0) return known;
        if (!segment.Pending) return 1;

        Placed? before = _placed.LastOrDefault(p => !p.Pending && p.Folder == segment.Folder && p.Clip.Info?.Audio.Count > 0);
        return before?.Clip.Info?.Audio.Count ?? TrackLanes;
    }

    /// <summary>The pair heard of a segment: the one listened to, or its first when it has not got that one.</summary>
    private int PlayedTrack(Placed segment, long captured) => _listenTrack < TracksOf(segment) ? _listenTrack : 0;

    /// <summary>How many pairs the delay is carrying, once it is. Tracks past them are in the files only.</summary>
    private static int? PairsInDelay() =>
        LiveAir.Shared.Line is DelayLine { AudioChannels: > 0 } line ? line.AudioChannels / 2 : null;

    /// <summary>
    /// The name of a track that was added to the recording's files, or null for one the receiver
    /// carried - which the recorder names "Original", and which is all the delay holds.
    /// </summary>
    private string? AddedName(int track)
    {
        string? title = _placed.LastOrDefault(p => p.Clip.Info?.Audio.Count > track)?.Clip.Info?.Audio[track].Title?.Trim();

        return string.IsNullOrEmpty(title) || title == "SoundHandler" ||
               title.StartsWith(RecordingProfile.OriginalAudioLabel, StringComparison.Ordinal)
            ? null
            : title;
    }

    private string TrackName(int track) =>
        AddedName(track) ?? $"CH {track * 2 + 1}-{track * 2 + 2}";

    // ------------------------------------------------------------------ choosing one

    private int LaneAt(double y) =>
        Math.Clamp((int)(y / Math.Max(1, AudioCanvas.ActualHeight) * TrackLanes), 0, TrackLanes - 1);

    /// <summary>A click on the audio lanes that chooses a pair to hear: on a lane's name, or a double click in it.</summary>
    private bool TrackClicked(MouseButtonEventArgs e)
    {
        if (TrackLanes <= 1) return false;

        Point at = e.GetPosition(AudioCanvas);

        if (at.X >= TrackLabelWidth && e.ClickCount != 2) return false;

        e.Handled = true;
        Listen(LaneAt(at.Y));
        return true;
    }

    /// <summary>
    /// The editor hears <paramref name="track"/> from now on, in the preview only, and it plays: a
    /// click on a pair is to hear it.
    /// </summary>
    private void Listen(int track)
    {
        bool changed = track != _listenTrack;
        _listenTrack = track;

        LayerText.Text = $"LISTENING  {TrackName(track)} - in Live Edit only. Air sends every pair, CH 1-2 first.";
        LayerText.Foreground = Brush("Ok");

        if (changed)
            ActivityLog.Shared.Info(LogSource.LiveEdit, $"LISTEN  {TrackName(track)} in Live Edit's preview (not on air).",
                                    @event: "liveedit.listen");

        DrawAudio();

        if (!_playing) PlayPause();
        Focus();
    }

    // ------------------------------------------------------------------ hearing it

    /// <summary>The sound of recordings with several tracks, once one has been played. See <see cref="PreviewSound"/>.</summary>
    private PreviewSound? _preview;

    private bool _ownSound;
    private string? _previewFfmpeg;
    private bool _soundProblemSaid;

    /// <summary>
    /// True when the recording in the player has several tracks, so its sound is played beside it.
    ///
    /// The player cannot be told which track to play, and plays the wrong one: Windows lists a file's
    /// sound tracks last first and takes the one it lists first, so of four pairs it is CH 7-8 that is
    /// heard (read from Media Foundation, 2026-11-25).
    /// </summary>
    private bool PlaysOwnSound =>
        _onIngest is null && _loaded is { } segment && TracksOf(segment) > 1 &&
        (_previewFfmpeg ??= Ffmpeg.Locate(_settings.FfmpegPath)) is not null;

    /// <summary>
    /// Every tick: tells the sound where the picture is, and what plays there. The picture's file plays
    /// straight through a change of track - only the sound changes, between one frame and the next.
    /// </summary>
    private void FollowSound()
    {
        if (_held) { FollowHeldSound(); return; }

        bool own = PlaysOwnSound;

        if (own != _ownSound)
        {
            _ownSound = own;
            ApplyLevel();
        }

        if (!own || _loaded is not { } segment)
        {
            _preview?.Pause();
            return;
        }

        _preview ??= new PreviewSound(_previewFfmpeg!);

        int tracks = TracksOf(segment);

        if (!_playing || _held || !_open || _seekedCaptured is not null)
        {
            _preview.Pause();

            // Ready for when it is played from here.
            _preview.Prepare(segment.Clip.Path, tracks, segment.OffsetOf(CapturedOf(_at)) / (double)_rate);
            return;
        }

        // Black and silent on air, or another sound in its place.
        bool silent = MuteButton.IsChecked == true || SoundOverRecordingAt(_at) is not null ||
                      _overlays.Any(o => o.IsBlank && HoldsOnRuler(o, _at));

        // The segment that plays straight on from this one: its sound runs on from this one's end.
        // The one still being recorded is heard from the delay.
        PreviewNext? next = null;

        if (segment.EndFrame - CapturedOf(_at) < PreloadSeconds * (long)_rate && StraightOn(recording: true) is { } following)
        {
            if (!following.Pending)
            {
                if (TracksOf(following) > 1) next = new PreviewNext(following.Clip.Path, TracksOf(following), following.StartFrame);
            }
            else if (HeldOf(following) is { } held)
            {
                next = new PreviewNext(DelaySound.KeyOf(following.Clip.Path), TracksOf(following), following.StartFrame, held);
            }
        }

        _preview.Play(new PreviewMoment(
            segment.Clip.Path, tracks, segment.StartFrame, segment.Frames, _rate,
            PlayedSeconds(), System.Diagnostics.Stopwatch.GetTimestamp(),
            _listenTrack, _audioEdits, silent ? 0 : LevelSlider.Value, next));

        if (_preview.Problem is { } problem && !_soundProblemSaid)
        {
            _soundProblemSaid = true;
            StoreText.Text = $"The preview has no sound: {problem}.";
            ActivityLog.Shared.Warn(LogSource.LiveEdit, $"The preview has no sound: {problem}.", @event: "liveedit.nosound");
        }
    }

    /// <summary>The waveform of the segment still being recorded, read from the delay: whose, its peaks, and how far read.</summary>
    private (string Path, float[][] Peaks, long Read, WavePeaks[] Ready)? _heldWave;

    private bool _heldWaveReading;
    private DateTime _heldWaveAt = DateTime.MinValue;

    /// <summary>
    /// The waveform of the segment still being recorded, as far as the delay has it; null until some
    /// has been read. Read on in the background twice a second at most, and only what is new.
    /// </summary>
    private WavePeaks[]? HeldWaves(Placed segment, int tracks)
    {
        if (_heldWave is { } had && had.Path != segment.Clip.Path) _heldWave = null;

        if (!_heldWaveReading && (DateTime.UtcNow - _heldWaveAt).TotalMilliseconds >= 500 &&
            HeldOf(segment) is { Line: DelayLine line } held && line.TryAddRef())
        {
            _heldWaveReading = true;
            _heldWaveAt = DateTime.UtcNow;

            string path = segment.Clip.Path;
            int length = (int)((segment.Frames + 2L * _rate) * Waveform.PeaksPerSecond / _rate) + 1;
            float[][] peaks = _heldWave is { Peaks: var old } && old.Length == tracks && old[0].Length == length
                ? old
                : Enumerable.Range(0, tracks).Select(_ => new float[length]).ToArray();
            long from = _heldWave is { } going && ReferenceEquals(going.Peaks, peaks) ? going.Read + 1 : 0;
            long to = segment.Frames + 2L * _rate - 1;

            _ = Task.Run(() =>
            {
                long read;
                try { read = DelaySound.Peaks(line, held.RecordingStart, from, to, peaks, Waveform.PeaksPerSecond); }
                finally { line.Release(); }

                WavePeaks[] ready = peaks.Select(p => new WavePeaks((float[])p.Clone())).ToArray();

                _ = Dispatcher.BeginInvoke(() =>
                {
                    _heldWaveReading = false;
                    if (_closed) return;

                    _heldWave = (path, peaks, read, ready);

                    if (read >= from && !_waveRedrawQueued)
                    {
                        _waveRedrawQueued = true;
                        _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                        {
                            _waveRedrawQueued = false;
                            DrawAudio();
                        });
                    }
                });
            });
        }

        return _heldWave is { } wave && wave.Path == segment.Clip.Path && wave.Ready.Length == tracks ? wave.Ready : null;
    }

    /// <summary>Where the delay holds a segment still being recorded, if it does and at this frame rate.</summary>
    private PreviewHeld? HeldOf(Placed segment) =>
        _heldPicture.Use(HeldLine) && _heldPicture.Line is { } line && line.FrameRate == _rate &&
        RecordingFrameOfDay(segment.StartFrame, line.FrameRate) is { } start
            ? new PreviewHeld(line, start)
            : null;

    /// <summary>
    /// Every tick while the segment still being recorded plays from the delay: its sound, out of the
    /// delay, at the delay's clock. Whatever the number of tracks - there is no file, so no player to
    /// play it.
    /// </summary>
    private void FollowHeldSound()
    {
        if (!_playing || ClipAt(CapturedOf(_at)) is not { Pending: true } segment || HeldOf(segment) is not { } held ||
            (_previewFfmpeg ??= Ffmpeg.Locate(_settings.FfmpegPath)) is null)
        {
            _preview?.Pause();
            return;
        }

        _preview ??= new PreviewSound(_previewFfmpeg!);

        bool silent = MuteButton.IsChecked == true || SoundOverRecordingAt(_at) is not null ||
                      _overlays.Any(o => o.IsBlank && HoldsOnRuler(o, _at));

        double position = (_heldFrom + _heldShift - segment.StartFrame) / (double)_rate + _heldClock.Elapsed.TotalSeconds;

        _preview.Play(new PreviewMoment(
            DelaySound.KeyOf(segment.Clip.Path), TracksOf(segment), segment.StartFrame, segment.Frames, _rate,
            position, System.Diagnostics.Stopwatch.GetTimestamp(),
            _listenTrack, _audioEdits, silent ? 0 : LevelSlider.Value, null, held));
    }

    /// <summary>
    /// What the small monitor plays of a recorded file: a copy with CH 1-2 in it when the file has
    /// several tracks, for the reason given at <see cref="PlaysOwnSound"/>. Null while that is being made; <paramref name="ready"/>
    /// is called when it is there, or cannot be.
    /// </summary>
    private string? MonitorPathOf(string path, Action ready)
    {
        if (_placed.FirstOrDefault(p => string.Equals(p.Clip.Path, path, StringComparison.OrdinalIgnoreCase)) is not { Pending: false } segment ||
            TracksOf(segment) <= 1 || TrackCopy.HasFailed(path, 0))
            return path;

        if (TrackCopy.Known(path, 0) is { } copy) return copy;
        if (Ffmpeg.Locate(_settings.FfmpegPath) is not { } ffmpeg) return path;

        TrackCopy.Request(ffmpeg, path, 0, () => Dispatcher.BeginInvoke(() =>
        {
            if (!_closed) ready();
        }));

        return null;
    }

    private int _drawnTrack = -1;

    /// <summary>Every tick: the lanes drawn again when the pair heard has changed.</summary>
    private void FollowTrack()
    {
        if (TrackLanes > 1 && _listenTrack != _drawnTrack) DrawAudio();
    }

    // ------------------------------------------------------------------ drawing

    private static readonly Brush WaveOffBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x58, 0x8B, 0x98, 0xA8)));
    private static readonly Brush TrackLabelFill = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x16, 0x1B, 0x22)));

    /// <summary>The audio row grows with its lanes, so four are still tall enough to read and to click.</summary>
    private void ShowTrackLanes()
    {
        int lanes = Math.Min(TrackLanes, 8);

        var row = new GridLength(Math.Max(1, lanes * 0.5), GridUnitType.Star);
        var timeline = new GridLength(286 + (lanes - 1) * 22);

        if (AudioRow.Height != row) AudioRow.Height = row;
        if (TimelineRow.Height != timeline) TimelineRow.Height = timeline;

        var margin = new Thickness(lanes > 1 ? TrackLabelWidth + 8 : 48, 3, 0, 0);
        if (AudioButtons.Margin != margin) AudioButtons.Margin = margin;
    }

    /// <summary>Each lane's name, over the lanes, the one heard marked.</summary>
    private void DrawTracks(double width, double height, long start, long span)
    {
        _drawnTrack = _listenTrack;

        int lanes = TrackLanes;
        if (lanes <= 1) return;

        double each = height / lanes;

        for (int lane = 0; lane < lanes; lane++)
        {
            bool playing = lane == _drawnTrack;

            if (lane > 0)
                AudioCanvas.Children.Add(new Rectangle
                {
                    Width = width,
                    Height = 1,
                    Fill = Brush("Line"),
                    Margin = new Thickness(0, lane * each, 0, 0),
                    IsHitTestVisible = false,
                });

            var name = new Border
            {
                Width = TrackLabelWidth - 4,
                Height = Math.Max(12, Math.Min(20, each - 4)),
                CornerRadius = new CornerRadius(3),
                Background = TrackLabelFill,
                BorderBrush = Brush(playing ? "Ok" : "Line"),
                BorderThickness = new Thickness(playing ? 1.5 : 1),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = (playing ? "▶ " : "  ") + TrackName(lane),
                    Foreground = Brush(playing ? "Ok" : "Muted"),
                    FontFamily = Mono,
                    FontSize = 10,
                    FontWeight = playing ? FontWeights.Bold : FontWeights.Normal,
                    Margin = new Thickness(5, 0, 2, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            };

            Canvas.SetLeft(name, 2);
            Canvas.SetTop(name, lane * each + 2);
            AudioCanvas.Children.Add(name);
        }
    }

    // ------------------------------------------------------------------ waveforms

    /// <summary>Where a track's waveform is kept: the path itself for the first, as it always was.</summary>
    private static string WaveKey(string path, int track) => track == 0 ? path : $"{path}|{track}";

    /// <summary>The file a kept waveform is of. No path holds a bar.</summary>
    private static string WavePath(string key) => key.IndexOf('|') is >= 0 and var bar ? key[..bar] : key;

    /// <summary>
    /// Reads every track's waveform of a segment in the background, and draws the lanes again once
    /// they are in. Asked for again on every redraw until then, so only what is on screen is read.
    /// </summary>
    private void ReadWaveforms(string path, int tracks)
    {
        if (tracks <= 1)
        {
            ReadWaveform(path);
            return;
        }

        if (_waveReading.Contains(path) || _waveFailed.Contains(path)) return;
        if (Ffmpeg.Locate(_settings.FfmpegPath) is not { } ffmpeg) return;

        _waveReading.Add(path);

        _ = Task.Run(async () =>
        {
            await WaveSlots.WaitAsync();

            IReadOnlyList<WavePeaks>? peaks;
            try { peaks = Waveform.Tracks(ffmpeg, path, tracks); }
            finally { WaveSlots.Release(); }

            _ = Dispatcher.BeginInvoke(() =>
            {
                if (_closed) return;

                _waveReading.Remove(path);

                if (peaks is null) _waveFailed.Add(path);
                else
                    for (int i = 0; i < peaks.Count; i++)
                        _waves[WaveKey(path, i)] = peaks[i];

                if (_waveRedrawQueued) return;
                _waveRedrawQueued = true;

                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
                {
                    _waveRedrawQueued = false;
                    DrawAudio();
                });
            });
        });
    }
}
