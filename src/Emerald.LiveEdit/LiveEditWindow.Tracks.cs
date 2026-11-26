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
/// The recording's sound tracks: a lane for each, and which of them plays.
///
/// A recording carries a track for every pair the receiver had when it rolled. Each is drawn on a
/// lane of its own, and one of them plays - bright, with the others dimmed. Clicking a lane's name,
/// or double-clicking in it, chooses that track and asks when: instantly, or from a timecode. It
/// plays until the next change on the timeline. It can also be set to start on the mark in.
///
/// <b>What plays here is what goes to air.</b> The changes are handed to the transmitter with the
/// cues, in frames of the recording, and it takes the chosen pair out of the delay - which carries
/// them all - so a change needs nothing opened and is in time right up to air. That is why
/// "instantly" may be inside the no edit zone, which is there for media that has to be opened.
/// </summary>
public partial class LiveEditWindow
{
    /// <summary>Which track plays and from where, in captured frames of this ruler's day, sorted.</summary>
    private IReadOnlyList<AudioSwitch> _audioSwitches = Array.Empty<AudioSwitch>();

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

    /// <summary>The track of a segment that plays at a captured frame. One it has not got plays its first, as on air.</summary>
    private int PlayedTrack(Placed segment, long captured) =>
        AudioSwitches.TrackAt(captured, _audioSwitches) is var track && track < TracksOf(segment) ? track : 0;

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

    /// <summary>A click on the audio lanes that chooses a track: on a lane's name, or a double click in it.</summary>
    private bool TrackClicked(MouseButtonEventArgs e)
    {
        if (TrackLanes <= 1) return false;

        Point at = e.GetPosition(AudioCanvas);
        bool onName = at.X < TrackLabelWidth;

        if (!onName && e.ClickCount != 2) return false;

        e.Handled = true;

        ChooseTrack(LaneAt(at.Y), onName
            ? _at
            : TimelineZoom.FrameAt(at.X, ViewStart, ViewSpan, AudioCanvas.ActualWidth, _dayFrames - 1));

        return true;
    }

    /// <summary>A track has been chosen at a ruler frame: asks when it plays, and sets it.</summary>
    private void ChooseTrack(int track, long rulerFrame)
    {
        if (AddedName(track) is not null || (PairsInDelay() is { } pairs && track >= pairs))
        {
            Refuse($"{TrackName(track)} is only in the recording's files. Air plays what the receiver carried, so it cannot be chosen.");
            return;
        }

        long? first = InstantFromAir();

        // The mark as it is now: the one the dialog shows, whatever happens while it is open.
        long? markIn = _markIn;
        bool markAired = markIn is { } mark && first is { } open && CapturedOf(mark) < open;

        var ask = new AudioTrackDialog(TrackName(track), first is not null, markIn is { } at ? Stamp(at) : null,
                                       markAired, Stamp(rulerFrame), _rate) { Owner = this };
        if (ask.ShowDialog() != true) return;

        // Asked again whether air has reached it: that may have happened while it was decided.
        if (ask.Instantly) PlayTrackInstantly(track);
        else if (ask.OnMarkIn && markIn is { } from) PlayTrackFrom(track, from, "mark in ");
        else PlayTrackFrom(track, RulerOf(ask.At, rulerFrame));

        Focus();
    }

    /// <summary>A typed timecode on this ruler: past midnight when that is nearer to where it was asked from.</summary>
    private long RulerOf(Timecode typed, long near)
    {
        long same = typed.TotalFrames, next = same + _oneDay;
        return next < _dayFrames && Math.Abs(next - near) < Math.Abs(same - near) ? next : same;
    }

    /// <summary>Half a second of air: how far past what it is committed to a change of track is placed, to be there in time.</summary>
    private long AudioLead => Math.Max(2, _liveRate / 2);

    /// <summary>The first captured frame a change made now can start at on air, or null when nothing has gone out.</summary>
    private long? InstantFromAir() =>
        LiveAir.Shared.Active && KnowsLive() && LiveAir.Shared.CommittedFrame is { } committed
            ? RecordingToDay(committed + AudioLead)
            : null;

    private void PlayTrackInstantly(int track)
    {
        long from = InstantFromAir() ?? CapturedOf(_at);
        SetSwitches(s => AudioSwitches.Set(s, from, track), from, $"{TrackName(track)} instantly, from {Stamp(RulerOf(from))}");
    }

    private void PlayTrackFrom(int track, long rulerFrame, string where = "")
    {
        long from = CapturedOf(rulerFrame);

        if (InstantFromAir() is { } first && from < first)
        {
            Refuse($"{where}{Stamp(rulerFrame)} has gone to air or is about to - the earliest is {Stamp(RulerOf(first))}. Choose Play instantly, or a later timecode.");
            return;
        }

        SetSwitches(s => AudioSwitches.Set(s, from, track), from, $"{TrackName(track)} from {where}{Stamp(rulerFrame)}");
    }

    /// <summary>A right click on a change takes it off, if air has not reached it.</summary>
    private void Audio_RightClick(object sender, MouseButtonEventArgs e)
    {
        double x = e.GetPosition(AudioCanvas).X;

        double X(long captured) => (RulerOf(captured) - ViewStart) / (double)ViewSpan * AudioCanvas.ActualWidth;

        AudioSwitch? near = _audioSwitches.Where(s => Math.Abs(X(s.StartFrame) - x) < 8)
                                          .MinBy(s => Math.Abs(X(s.StartFrame) - x));
        if (near is null) return;

        e.Handled = true;

        if (InstantFromAir() is { } first && near.StartFrame < first)
        {
            Refuse("That audio change has gone to air or is about to.");
            return;
        }

        SetSwitches(s => AudioSwitches.Remove(s, near.StartFrame), near.StartFrame,
                    $"the change at {Stamp(RulerOf(near.StartFrame))} taken off");
    }

    /// <summary>
    /// Changes which track plays, here and on air. When playback will not take it, nothing is changed
    /// here either, so the timeline never shows a sound air is not going to play.
    /// </summary>
    private void SetSwitches(Func<IReadOnlyList<AudioSwitch>, IReadOnlyList<AudioSwitch>> change, long at, string what)
    {
        IReadOnlyList<AudioSwitch> before = _audioSwitches, after = change(before);

        if (AudioSwitches.FirstDifference(before, after) is null)
        {
            LayerText.Text = $"AUDIO  {what}: it already plays there.";
            LayerText.Foreground = Brush("Muted");
            return;
        }

        EditState was = Capture($"audio {what}");
        bool undoable = Editable(RulerOf(at));

        _audioSwitches = after;

        if (!PushCues($"audio: {what}"))
        {
            _audioSwitches = before;
            return;
        }

        if (undoable)
        {
            Push(_undo, was);
            _redo.Clear();
            _rememberedKey = null;
        }
        else
        {
            // On air before it could be undone, so it is part of every state there is to go back
            // to. Undoing an edit made before it then leaves the sound on air alone.
            Rewrite(_undo);
            Rewrite(_redo);

            void Rewrite(List<EditState> states)
            {
                for (int i = 0; i < states.Count; i++)
                    states[i] = states[i] with { Switches = change(states[i].Switches) };
            }
        }

        ShowUndo();

        ActivityLog.Shared.Info(LogSource.LiveEdit, $"AUDIO  {what}.", @event: "liveedit.audiotrack");

        LayerText.Text = $"AUDIO  {what}";
        LayerText.Foreground = Brush("Ok");

        DrawAudio();
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
        PreviewNext? next = segment.EndFrame - CapturedOf(_at) < PreloadSeconds * (long)_rate &&
                            StraightOn() is { } following && TracksOf(following) > 1
            ? new PreviewNext(following.Clip.Path, TracksOf(following), following.StartFrame)
            : null;

        _preview.Play(new PreviewMoment(
            segment.Clip.Path, tracks, segment.StartFrame, segment.Frames, _rate,
            PlayedSeconds(), System.Diagnostics.Stopwatch.GetTimestamp(),
            _audioSwitches, _audioEdits, silent ? 0 : LevelSlider.Value, next));

        if (_preview.Problem is { } problem && !_soundProblemSaid)
        {
            _soundProblemSaid = true;
            StoreText.Text = $"The preview has no sound: {problem}.";
            ActivityLog.Shared.Warn(LogSource.LiveEdit, $"The preview has no sound: {problem}.", @event: "liveedit.nosound");
        }
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

    private int _drawnTrack = -1, _drawnAirTrack = -1;

    /// <summary>Every tick: the lanes drawn again when what plays, here or on air, has changed.</summary>
    private void FollowTrack()
    {
        if (TrackLanes > 1 &&
            (AudioSwitches.TrackAt(CapturedOf(_at), _audioSwitches) != _drawnTrack || (AirTrack() ?? -1) != _drawnAirTrack))
            DrawAudio();
    }

    /// <summary>The track going to air now, or null when nothing is.</summary>
    private int? AirTrack() =>
        LiveAir.Shared.Active && LiveAir.Shared.Position is { RecordingFrame: >= 0 and var frame }
            ? AudioSwitches.TrackAt(frame, LiveAir.Shared.AudioSwitches)
            : null;

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

    /// <summary>The changes of track, and each lane's name, over the lanes.</summary>
    private void DrawTracks(double width, double height, long start, long span)
    {
        _drawnTrack = AudioSwitches.TrackAt(CapturedOf(_at), _audioSwitches);
        _drawnAirTrack = AirTrack() ?? -1;

        int lanes = TrackLanes;
        if (lanes <= 1) return;

        double each = height / lanes;

        foreach (AudioSwitch at in _audioSwitches)
        {
            double x = (RulerOf(at.StartFrame) - start) / (double)span * width;
            if (x < 0 || x > width) continue;

            AudioCanvas.Children.Add(new Rectangle
            {
                Width = 2,
                Height = height,
                Fill = Brush("Ok"),
                Margin = new Thickness(x, 0, 0, 0),
                IsHitTestVisible = false,
            });

            var tag = new TextBlock
            {
                Text = $"▶ {TrackName(at.Track)}",
                Foreground = Brush("Ok"),
                FontFamily = Mono,
                FontSize = 9.5,
                FontWeight = FontWeights.Bold,
                IsHitTestVisible = false,
            };

            Canvas.SetLeft(tag, x + 4);
            Canvas.SetTop(tag, Math.Min(at.Track, lanes - 1) * each + 2);
            AudioCanvas.Children.Add(tag);
        }

        for (int lane = 0; lane < lanes; lane++)
        {
            bool playing = lane == _drawnTrack, air = lane == _drawnAirTrack;

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
                BorderBrush = Brush(air ? "Bad" : playing ? "Ok" : "Line"),
                BorderThickness = new Thickness(air || playing ? 1.5 : 1),
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    Text = (air ? "● " : playing ? "▶ " : "  ") + TrackName(lane),
                    Foreground = Brush(air ? "Bad" : playing ? "Ok" : "Muted"),
                    FontFamily = Mono,
                    FontSize = 10,
                    FontWeight = playing || air ? FontWeights.Bold : FontWeights.Normal,
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
