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
/// The two sound lanes: the ingest media's, item by item, and the recording's, drawn under its
/// segments - and what can be done to them.
///
/// It works like a clip in an editor. <b>C</b> cuts the recording's sound at the playhead; a click
/// on its lane picks up the sound of the segment under it, between any cuts in it (or the marked
/// range, when the click is inside the marks). A click on the ingest audio lane picks up that
/// item's sound. Either can be made louder or quieter a decibel at a time.
/// To take sound out, delete the scene: picture and sound go together, on air and on this lane.
///
/// What is set here is what goes to air. The levels are handed to the transmitter with the cues,
/// in frames of the recording, and applied to the recording's own sound as each frame goes out.
/// The no edit zone holds here as everywhere: what air has reached, or is about to, keeps its level.
/// </summary>
public partial class LiveEditWindow
{
    /// <summary>The levels set, in captured frames of this ruler's day, sorted and never overlapping.</summary>
    private IReadOnlyList<AudioEdit> _audioEdits = Array.Empty<AudioEdit>();

    /// <summary>Where the sound has been cut, in captured frames of this ruler's day.</summary>
    private readonly SortedSet<long> _audioCuts = new();

    /// <summary>The piece of sound picked up, in captured frames, or null.</summary>
    private (long From, long To)? _audioPiece;

    /// <summary>Files whose waveform is being read now, and files it could not be read from.</summary>
    private readonly HashSet<string> _waveReading = new();
    private readonly HashSet<string> _waveFailed = new();

    /// <summary>Waveforms read, by path - asked for every redraw, so kept here rather than checked against the disk.</summary>
    private readonly Dictionary<string, WavePeaks> _waves = new();

    /// <summary>Kept below this without a look; past it, only what the timeline still shows is kept.</summary>
    private const int WavesBeforeTrim = 1000;

    /// <summary>
    /// Forgets the waveforms of files not near the view: another day's, another recording's,
    /// segments the storage limit has deleted, and the parts of today an hour or more either side
    /// of what is on screen. They were kept for as long as the window was open, some twenty
    /// megabytes for every day it stayed up; keeping the whole of today still let a day of short
    /// segments run past the limit. A waveform scrolled back into view is read again.
    /// </summary>
    private void TrimWaves()
    {
        if (_waves.Count + _waveFailed.Count < WavesBeforeTrim) return;

        long margin = 3600L * _rate;
        long from = ViewStart - margin, to = ViewStart + ViewSpan + margin;

        var shown = new HashSet<string>(
            _placed.Where(p => p.EndFrame >= from && p.StartFrame <= to)
                   .Select(p => p.Clip.Path)
                   .Concat(_overlays.Select(o => o.Path)),
            StringComparer.OrdinalIgnoreCase);

        foreach (string path in _waves.Keys.Where(path => !shown.Contains(path)).ToList())
            _waves.Remove(path);

        _waveFailed.RemoveWhere(path => !shown.Contains(path));
    }

    /// <summary>Two ffmpegs at a time: enough to fill a screen quickly, not enough to starve the recorder.</summary>
    private static readonly SemaphoreSlim WaveSlots = new(2);

    private bool _waveRedrawQueued;

    /// <summary>The level the preview is playing the recording at, as a multiplier.</summary>
    private double _previewGain = 1;

    // ------------------------------------------------------------------ frames

    private long CapturedOf(long rulerFrame) => IngestTimeline.At(rulerFrame, IngestItems()).CapturedFrame;

    private long RulerOf(long capturedFrame) => IngestTimeline.RulerFrameOf(capturedFrame, IngestItems());

    /// <summary>The recording on this ruler, in captured frames: the first segment's start to the last one's end.</summary>
    private (long First, long End)? SoundExtent() =>
        _placed.Count == 0 ? null : (_placed.Min(p => p.StartFrame), _placed.Max(p => p.EndFrame));

    // ------------------------------------------------------------------ picking up a piece

    private void Audio_Click(object sender, MouseButtonEventArgs e)
    {
        LeaveRecordingView();

        long frame = TimelineZoom.FrameAt(e.GetPosition(AudioCanvas).X, ViewStart, ViewSpan,
                                          AudioCanvas.ActualWidth, _dayFrames - 1);

        _selected = null;
        _selectedSound = false;
        _audioPiece = AudioPieceAt(frame);

        // The playhead goes there too, as on every other part of the timeline - and it is where
        // C cuts.
        Pause();
        Seek(frame);

        DrawAudio();
        ShowLayer();
        Focus();
    }

    /// <summary>
    /// The marked range when the frame is inside it; otherwise the sound of the segment under it,
    /// divided further by any cuts in it. One segment, not the whole lane: a click on a file's
    /// sound means that file.
    /// </summary>
    private (long From, long To)? AudioPieceAt(long rulerFrame)
    {
        if (_markIn is { } mIn && _markOut is { } mOut && mOut > mIn && rulerFrame >= mIn && rulerFrame < mOut)
            return (CapturedOf(mIn), CapturedOf(mOut));

        long captured = CapturedOf(rulerFrame);

        if (ClipAt(captured) is not { } segment) return null;

        return AudioEdits.PieceAt(captured, _audioCuts, segment.StartFrame, segment.EndFrame);
    }

    // ------------------------------------------------------------------ cutting, levels, deleting

    private void CutAudio_Click(object sender, RoutedEventArgs e) => CutAudio();
    private void AudioDown_Click(object sender, RoutedEventArgs e) => NudgeSound(-1);
    private void AudioUp_Click(object sender, RoutedEventArgs e) => NudgeSound(+1);

    /// <summary>The sound picked up a decibel up or down: an ingest item's, or a piece of the recording's.</summary>
    private void NudgeSound(double by)
    {
        if (_selectedSound) NudgeSelectedGain(by);
        else NudgeAudio(by);
    }

    /// <summary>Divides the sound at the playhead, so either side can be picked up on its own.</summary>
    private void CutAudio()
    {
        long at = CapturedOf(_at);

        if (SoundExtent() is not { } extent || at <= extent.First || at >= extent.End)
        {
            Refuse("There is no recorded sound at the playhead to cut.");
            return;
        }

        if (_audioCuts.Contains(at)) return;

        Remember($"cutting the sound at {Stamp(_at)}");
        _audioCuts.Add(at);

        // The piece picked up is the one the playhead is in now, which the cut has just made.
        _audioPiece = AudioPieceAt(_at);
        _selected = null;
        _selectedSound = false;

        ActivityLog.Shared.Info(LogSource.LiveEdit,
            $"AUDIO  cut at {Stamp(_at)} - the sound either side can now be set on its own.",
            @event: "liveedit.audiocut");

        DrawAudio();
        ShowLayer();
    }

    /// <summary>A decibel up or down on the piece picked up. Up from silence starts at the bottom of the range.</summary>
    private void NudgeAudio(double by) =>
        SetAudio(db => AudioEdits.Clamp((double.IsNegativeInfinity(db) ? AudioEdits.MinDb : db) + by),
                 "level", key: "audiolevel");

    private void SetAudio(Func<double, double> change, string what, string? key)
    {
        if (_audioPiece is not { } piece)
        {
            Refuse("Click the audio lane to pick up a piece of sound first - C cuts it at the playhead.");
            return;
        }

        // Only what air has not reached: a piece that is partly gone out is changed from the end
        // of the no edit zone on, and one wholly behind it not at all.
        long from = piece.From;

        if (EditableFrom is { } editable && CapturedOf(editable) is var open && open > from) from = open;

        if (from >= piece.To)
        {
            Refuse("NO EDIT ZONE - that sound has gone to air or is about to, so its level cannot be changed.");
            return;
        }

        double before = AudioEdits.GainDbAt(from, _audioEdits);
        double after = change(before);

        if (after.Equals(before)) return;

        Remember(what == "delete" ? (double.IsNegativeInfinity(after) ? "deleting sound" : "restoring sound")
                                  : "the sound's level",
                 key is null ? null : $"{key}:{from}:{piece.To}");

        _audioEdits = AudioEdits.Set(_audioEdits, from, piece.To, after);

        ActivityLog.Shared.Info(LogSource.LiveEdit,
            $"AUDIO  {Stamp(RulerOf(from))} - {Stamp(RulerOf(piece.To))} " +
            (double.IsNegativeInfinity(after) ? "deleted - silent on air, the picture carries on."
             : double.IsNegativeInfinity(before) ? $"restored at {Db(after)}."
             : $"set to {Db(after)}."),
            @event: "liveedit.audiolevel");

        PushCues("audio level changed");
        FollowAudioLevel(force: true);
        DrawAudio();
        ShowLayer();
    }

    private static string Db(double db) =>
        double.IsNegativeInfinity(db) ? "silent" : $"{db:+0.0;-0.0;0.0} dB";

    /// <summary>What the piece picked up is and what it is set to, where the selection is described.</summary>
    private void ShowAudioPiece((long From, long To) piece)
    {
        double db = AudioEdits.GainDbAt(piece.From, _audioEdits);
        long frames = piece.To - piece.From;

        LayerText.Text = $"AUDIO  {Stamp(RulerOf(piece.From))} - {Stamp(RulerOf(piece.To))}  " +
                         $"({new Timecode(frames, _rate)})  {Db(db)}" +
                         "   [ ] level   C cut at playhead";

        LayerText.Foreground = Brush(double.IsNegativeInfinity(db) ? "Bad" : "Warn");
    }

    /// <summary>
    /// The preview plays the recording at the level set under the playhead, so what is heard here
    /// is what will go out. Only as loud as the window's own level allows: a boost is heard as far
    /// as full volume and no further.
    /// </summary>
    private void FollowAudioLevel(bool force = false)
    {
        // On an ingest item, its own level - or nothing, for one going out as video only. An
        // audio-only one has its picture covered, as it is on air.
        Overlay? ingest = _onIngest is null ? null : IngestAt(_at);

        // A deleted scene goes to air as black and silence, so it is previewed as that: the
        // recording plays on underneath, covered and muted.
        bool blank = _onIngest is null && _overlays.Any(o => o.IsBlank && HoldsOnRuler(o, _at));

        Visibility cover = blank || ingest is { HasVideo: false } ? Visibility.Visible : Visibility.Collapsed;
        if (AudioOnlyCover.Visibility != cover) AudioOnlyCover.Visibility = cover;

        string coverText = blank ? "DELETED - BLACK AND SILENCE ON AIR" : "AUDIO ONLY";
        if (AudioOnlyCoverText.Text != coverText) AudioOnlyCoverText.Text = coverText;

        // Under audio only laid over the recording, the recording's picture plays and its sound
        // does not: the item's sound has taken its place, from the sound player.
        double gain = blank ? 0
                    : _onIngest is null
                        ? SoundOverRecordingAt(_at) is not null ? 0
                        : AudioEdits.Linear(AudioEdits.GainDbAt(CapturedOf(_at), _audioEdits))
                    : ingest is { HasAudio: false } ? 0
                    : AudioEdits.Linear(ingest?.GainDb ?? 0);

        if (!force && gain.Equals(_previewGain)) return;

        _previewGain = gain;
        ApplyLevel();
    }

    // ------------------------------------------------------------------ audio only over the recording

    /// <summary>
    /// Plays the sound of an audio-only item laid over the recording, beside the big screen.
    ///
    /// The big screen goes on showing the recording, as air does, so the item's sound cannot come
    /// from the player on screen: it has a second player of its own, with no picture. It follows
    /// the playhead rather than keeping its own time, and is put back on it whenever the two are
    /// more than a quarter of a second apart.
    /// </summary>
    private readonly MediaPlayer _sound = new();

    private Overlay? _soundItem;
    private bool _soundPlaying;

    private Overlay? SoundOverRecordingAt(long rulerFrame) =>
        _overlays.FirstOrDefault(o => SoundOverRecording(o) && HoldsOnRuler(o, rulerFrame));

    /// <summary>Every tick: the item's sound on, positioned and at its level, or off.</summary>
    private void FollowIngestSound()
    {
        if ((_playing ? SoundOverRecordingAt(_at) : null) is not { Plan: { } plan } item)
        {
            StopIngestSound();
            return;
        }

        // Opened when the playhead reaches the item, not looked up every tick: finding the
        // playable copy beside an export reads the disk. A sound file plays as it is.
        if (!ReferenceEquals(item, _soundItem))
        {
            StopIngestSound();

            _sound.Open(new Uri(RangeExport.PlayablePathFor(item.Path) ?? item.Path));
            _soundItem = item;
        }

        long at = IngestTimeline.RulerStart(new IngestItem(item.Path, plan), IngestItems());
        TimeSpan want = TimeSpan.FromSeconds((_at - at) * plan.Speed / _rate);

        _sound.SpeedRatio = plan.Speed;
        _sound.IsMuted = MuteButton.IsChecked == true;
        _sound.Volume = Math.Clamp(LevelSlider.Value * AudioEdits.Linear(item.GainDb), 0, 1);

        if (Math.Abs((_sound.Position - want).TotalMilliseconds) > 250) _sound.Position = want;

        if (!_soundPlaying)
        {
            _sound.Play();
            _soundPlaying = true;
        }
    }

    private void StopIngestSound()
    {
        if (_soundItem is null) return;

        _sound.Stop();
        _sound.Close();
        _soundItem = null;
        _soundPlaying = false;
    }

    private static readonly Brush CoveredFill = Frozen(new SolidColorBrush(Color.FromArgb(0xB4, 0x1C, 0x1F, 0x24)));

    /// <summary>
    /// Greys out the stretch of a segment lane an ingest item replaces - its picture on the
    /// segments, its sound on the segment audio - because that part of the recording does not go
    /// to air. Only where the two overlap: an insert pushes the recording along rather than
    /// covering any of it, so it greys out nothing.
    /// </summary>
    private void GreyCovered(Canvas canvas, double width, long start, long span, double height, bool sound)
    {
        List<IngestItem> items = IngestItems();

        foreach (Overlay overlay in _overlays)
        {
            if (overlay.Plan is not { } plan || plan.Mode is IngestMode.Insert or IngestMode.Cut) continue;
            if (sound ? !plan.HasAudio : !plan.HasVideo) continue;

            long at = IngestTimeline.RulerStart(new IngestItem(overlay.Path, plan), items);
            if (at + plan.Frames < start || at > start + span) continue;

            double from = Math.Max(0, (at - start) / (double)span * width);
            double to = Math.Min(width, (at + plan.Frames - start) / (double)span * width);
            if (to <= from) continue;

            canvas.Children.Add(new Rectangle
            {
                Width = Math.Max(1, to - from),
                Height = height,
                Fill = CoveredFill,
                Margin = new Thickness(from, 0, 0, 0),
                IsHitTestVisible = false,
            });
        }
    }

    private static readonly Brush BlankFill = Frozen(new SolidColorBrush(Color.FromArgb(0xF2, 0x00, 0x00, 0x00)));

    /// <summary>
    /// The deleted scenes, over a segment lane: black across the whole of each, on the picture
    /// and on the sound alike, because that is exactly what goes to air there. Drawn over the
    /// segments rather than in place of them - the recording is still there, and the timeline
    /// keeps its length - and labelled with how long it runs and where it stands with playback.
    /// </summary>
    private void DrawBlanks(Canvas canvas, double width, long start, long span, double height, bool sound)
    {
        List<IngestItem> items = IngestItems();

        foreach (Overlay overlay in _overlays.Where(o => o.IsBlank))
        {
            (long at, long end) = RulerSpan(overlay, items);
            if (end < start || at > start + span) continue;

            double from = Math.Max(0, (at - start) / (double)span * width);
            double to = Math.Min(width, (end - start) / (double)span * width);
            if (to <= from) continue;

            bool chosen = ReferenceEquals(overlay, _selected);
            EdlEditRecord? record = _edl.Edit(overlay.Id);

            canvas.Children.Add(new Rectangle
            {
                Width = Math.Max(1, to - from),
                Height = height,
                Fill = BlankFill,
                Stroke = Brush(chosen ? "Tc" : "Bad"),
                StrokeThickness = chosen ? 2 : 1,
                StrokeDashArray = record?.Conform == EdlConformStatus.Conformed ? null : Dash42,
                Margin = new Thickness(from, 0, 0, 0),
                ToolTip = $"Deleted scene: {IngestPlacement.Describe(overlay.Plan!.Value, _rate)}\n" +
                          $"{Stamp(at)} to {Stamp(end)}\n" +
                          (record?.Conform switch
                          {
                              EdlConformStatus.Conformed => $"Conformed in EDL v{record.ConformedVersion}",
                              EdlConformStatus.Conforming => $"Conforming in EDL v{record.ConformVersion}",
                              EdlConformStatus.Rejected => "Playback refused its conform",
                              _ => "Not yet conformed - it is conformed when its first frame reaches the no edit zone",
                          }),
            });

            if (to - from < 30) continue;

            string state = record?.Conform switch
            {
                EdlConformStatus.Conformed => $"  v{record.ConformedVersion} ✓",
                EdlConformStatus.Conforming => $"  v{record.ConformVersion}…",
                EdlConformStatus.Rejected => "  REFUSED",
                _ => "",
            };

            var tag = new TextBlock
            {
                Text = sound ? "SILENCE" : $"■ DELETED {new Timecode(end - at, _rate)}{state}",
                Foreground = Brush(chosen ? "Tc" : "Bad"),
                FontSize = 9.5,
                FontFamily = Mono,
                FontWeight = FontWeights.Bold,
                IsHitTestVisible = false,
                MaxWidth = Math.Max(0, to - from - 6),
                TextTrimming = TextTrimming.CharacterEllipsis,
            };

            Canvas.SetLeft(tag, from + 4);
            Canvas.SetTop(tag, Math.Max(0, height / 2 - 7));
            canvas.Children.Add(tag);
        }
    }

    // ------------------------------------------------------------------ drawing

    private static readonly Brush WaveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)));
    private static readonly Brush WaveAiredBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xB0, 0xF5, 0x9E, 0x0B)));
    private static readonly Brush DeletedFill = Frozen(new SolidColorBrush(Color.FromArgb(0x40, 0xEF, 0x44, 0x44)));
    private static readonly Brush LevelFill = Frozen(new SolidColorBrush(Color.FromArgb(0x26, 0xF5, 0x9E, 0x0B)));
    private static readonly Brush PickedFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0x7D, 0xD3, 0xFC)));

    private static readonly Brush AudioBlockFill = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x2A, 0x1E)));
    private static readonly Brush AudioBlockStroke = Frozen(new SolidColorBrush(Color.FromRgb(0x2F, 0x6B, 0x47)));
    private static readonly Brush PendingBlockFill = Frozen(new SolidColorBrush(Color.FromArgb(0x30, 0xF5, 0x9E, 0x0B)));

    /// <summary>The quietest level drawn above the centre line. Below this is drawn as a hairline.</summary>
    private const double FloorDb = -48;

    /// <summary>
    /// How tall a peak is drawn, 0 to 1, on a decibel scale - the way a level meter reads.
    ///
    /// Drawn straight from the sample value, ordinary speech peaks at a third of full scale and
    /// its body far lower, so a normal recording looked like a thin line and read as too quiet
    /// when it was not. On a decibel scale -48 dB is the centre line and 0 dB the full height,
    /// and a set level moves the drawing by exactly what it does to the sound.
    /// </summary>
    private static double Loudness(float peak, double gainDb)
    {
        if (peak <= 0 || double.IsNegativeInfinity(gainDb)) return 0;

        double db = 20 * Math.Log10(peak) + gainDb;

        return Math.Clamp((db - FloorDb) / -FloorDb, 0, 1);
    }

    /// <summary>Both sound lanes - the ingest media's and the recording's.</summary>
    private void DrawAudio()
    {
        double width = AudioCanvas.ActualWidth;
        AudioCanvas.Children.Clear();

        if (width <= 1) return;

        DrawIngestAudio(width, ViewStart, ViewSpan);
        DrawAudio(width, ViewStart, ViewSpan);
    }

    private static readonly Brush IngestWaveBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x7D, 0xD3, 0xFC)));
    private static readonly Brush IngestBlockFill = Frozen(new SolidColorBrush(Color.FromRgb(0x14, 0x22, 0x36)));

    /// <summary>
    /// The ingest audio lane: each item's sound, as its own block with its own waveform, drawn at
    /// the level it goes to air at. A video-only item has no sound here - what is heard under it is
    /// the recording's, on the lane below.
    /// </summary>
    private void DrawIngestAudio(double width, long start, long span)
    {
        IngestAudioCanvas.Children.Clear();

        double height = Math.Max(10, IngestAudioCanvas.ActualHeight);
        if (height <= 10) return;

        double mid = height / 2;
        List<IngestItem> items = IngestItems();
        long? airedTo = OnAirFrame();

        double X(long rulerFrame) => (rulerFrame - start) / (double)span * width;

        foreach (Overlay overlay in _overlays.Where(o => o.HasAudio))
        {
            IngestPlan plan = overlay.Plan!.Value;
            long at = IngestTimeline.RulerStart(new IngestItem(overlay.Path, plan), items);
            long end = at + overlay.Frames;

            if (end < start || at > start + span) continue;

            double from = Math.Max(0, X(at));
            double to = Math.Min(width, X(end));
            if (to - from < 0.5) continue;

            bool chosen = ReferenceEquals(overlay, _selected) && _selectedSound;

            IngestAudioCanvas.Children.Add(new Rectangle
            {
                Width = Math.Max(1, to - from - 1),
                Height = height - 2,
                RadiusX = 3,
                RadiusY = 3,
                Fill = chosen ? PickedFill : IngestBlockFill,
                Stroke = Brush(chosen ? "Tc" : "Accent"),
                StrokeThickness = chosen ? 2 : 1,
                Margin = new Thickness(from, 1, 0, 0),
                ToolTip = $"{overlay.Name}  {Db(overlay.GainDb)}",
            });

            if (_waves.TryGetValue(overlay.Path, out WavePeaks? peaks))
            {
                var wave = new StreamGeometry();
                var aired = new StreamGeometry();

                using (StreamGeometryContext live = wave.Open())
                using (StreamGeometryContext past = aired.Open())
                {
                    for (double x = Math.Floor(from); x < to; x++)
                    {
                        long f0 = Math.Max(at, start + (long)Math.Floor(x / width * span));
                        long f1 = Math.Min(end, Math.Max(f0 + 1, start + (long)Math.Floor((x + 1) / width * span)));
                        if (f1 <= f0) continue;

                        // Into the file, in seconds - faster than the ruler for a stretched item.
                        double s0 = (f0 - at) * plan.Speed / _rate;
                        double s1 = (f1 - at) * plan.Speed / _rate;

                        double h = Math.Max(0.5, Loudness(peaks.Max(s0, s1), overlay.GainDb) * (mid - 3));

                        StreamGeometryContext into = airedTo is { } air && f1 <= air ? past : live;
                        into.BeginFigure(new Point(x + 0.5, mid - h), false, false);
                        into.LineTo(new Point(x + 0.5, mid + h), true, false);
                    }
                }

                wave.Freeze();
                aired.Freeze();

                IngestAudioCanvas.Children.Add(new Path { Data = aired, Stroke = WaveAiredBrush, StrokeThickness = 1, IsHitTestVisible = false });
                IngestAudioCanvas.Children.Add(new Path { Data = wave, Stroke = IngestWaveBrush, StrokeThickness = 1, IsHitTestVisible = false });
            }
            else if (to - from >= 3)
            {
                ReadWaveform(overlay.Path);
            }

            if (to - from > 60)
            {
                var name = new TextBlock
                {
                    Text = $"{overlay.Name}   {Db(overlay.GainDb)}" +
                           (plan.Tracks == IngestTracks.AudioOnly ? "   audio only" : ""),
                    Foreground = Brush(overlay.GainDb == 0 ? "Text" : "Warn"),
                    FontSize = 9.5,
                    FontFamily = Mono,
                    MaxWidth = Math.Max(20, to - from - 8),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    IsHitTestVisible = false,
                };

                Canvas.SetLeft(name, from + 5);
                Canvas.SetTop(name, 3);
                IngestAudioCanvas.Children.Add(name);
            }
        }

        var label = new TextBlock
        {
            Text = "INGEST AUDIO",
            Foreground = Brush("Accent"),
            FontFamily = Mono,
            FontSize = 9.5,
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(label, 4);
        Canvas.SetTop(label, height - 14);
        IngestAudioCanvas.Children.Add(label);
    }

    private void DrawAudio(double width, long start, long span)
    {
        AudioCanvas.Children.Clear();

        double height = Math.Max(10, AudioCanvas.ActualHeight);
        double mid = height / 2;
        List<IngestItem> items = IngestItems();
        long? airedTo = OnAirFrame();

        double X(long rulerFrame) => (rulerFrame - start) / (double)span * width;

        // The waveform, piece by piece of each segment as the ruler lays it out, so a segment cut
        // by an insert has its sound on both sides of it.
        var wave = new StreamGeometry();
        var aired = new StreamGeometry();

        using (StreamGeometryContext live = wave.Open())
        using (StreamGeometryContext past = aired.Open())
        {
            // Only the run of segments that can be on screen, found by halving.
            foreach (Placed placed in Visible(_placed, items, start, span, _longestPlaced))
            {
                foreach (IngestTimeline.Piece piece in IngestTimeline.Split(placed.StartFrame, placed.Frames, items))
                {
                    if (piece.RulerEnd < start || piece.RulerStart > start + span) continue;

                    double from = Math.Max(0, X(piece.RulerStart));
                    double to = Math.Min(width, X(piece.RulerEnd));

                    if (to - from < 0.5) continue;

                    // A block per segment, as in the picture row above, so the sound reads as the
                    // same files: where one ends and the next begins.
                    if (to - from >= 3)
                        AudioCanvas.Children.Add(new Rectangle
                        {
                            Width = Math.Max(1, to - from - 1),
                            Height = height - 2,
                            RadiusX = 3,
                            RadiusY = 3,
                            Fill = placed.Pending ? PendingBlockFill : AudioBlockFill,
                            Stroke = placed.Pending ? Brush("Warn") : AudioBlockStroke,
                            StrokeThickness = 1,
                            Margin = new Thickness(from, 1, 0, 0),
                            IsHitTestVisible = false,
                        });

                    // Still being written: no file to read its sound from yet.
                    if (placed.Pending) continue;

                    if (!_waves.TryGetValue(placed.Clip.Path, out WavePeaks? peaks))
                    {
                        // Only once there is room to draw it: a whole day of segments a pixel
                        // wide each would be hundreds of reads for nothing visible.
                        if (to - from >= 3) ReadWaveform(placed.Clip.Path);
                        continue;
                    }

                    for (double x = Math.Floor(from); x < to; x++)
                    {
                        long f0 = start + (long)Math.Floor(x / width * span);
                        long f1 = Math.Max(f0 + 1, start + (long)Math.Floor((x + 1) / width * span));

                        f0 = Math.Max(f0, piece.RulerStart);
                        f1 = Math.Min(f1, piece.RulerEnd);
                        if (f1 <= f0) continue;

                        // Into the file, in seconds: the piece's captured start plus how far along it.
                        long c0 = piece.CapturedStart + (f0 - piece.RulerStart) - placed.StartFrame;
                        long c1 = piece.CapturedStart + (f1 - piece.RulerStart) - placed.StartFrame;

                        double gainDb = AudioEdits.GainDbAt(placed.StartFrame + c0, _audioEdits);
                        double a = Loudness(peaks.Max(c0 / (double)_rate, c1 / (double)_rate), gainDb);
                        double h = Math.Max(0.5, a * (mid - 3));

                        StreamGeometryContext into = airedTo is { } air && f1 <= air ? past : live;
                        into.BeginFigure(new Point(x + 0.5, mid - h), false, false);
                        into.LineTo(new Point(x + 0.5, mid + h), true, false);
                    }
                }
            }
        }

        wave.Freeze();
        aired.Freeze();

        // What has been set, under the waveform: deleted sound red, a changed level amber.
        foreach (AudioEdit edit in _audioEdits)
        {
            double from = Math.Max(0, X(RulerOf(edit.StartFrame)));
            double to = Math.Min(width, X(RulerOf(edit.EndFrame)));
            if (to <= from) continue;

            AudioCanvas.Children.Add(new Rectangle
            {
                Width = Math.Max(1, to - from),
                Height = height,
                Fill = edit.Muted ? DeletedFill : LevelFill,
                Margin = new Thickness(from, 0, 0, 0),
                IsHitTestVisible = false,
            });

            if (to - from > 44)
            {
                var tag = new TextBlock
                {
                    Text = edit.Muted ? "DELETED" : Db(edit.GainDb),
                    Foreground = Brush(edit.Muted ? "Bad" : "Warn"),
                    FontFamily = Mono,
                    FontSize = 9,
                    FontWeight = FontWeights.Bold,
                    IsHitTestVisible = false,
                };

                Canvas.SetLeft(tag, from + 3);
                Canvas.SetTop(tag, height - 13);
                AudioCanvas.Children.Add(tag);
            }
        }

        if (_audioPiece is { } picked)
        {
            double from = Math.Max(0, X(RulerOf(picked.From)));
            double to = Math.Min(width, X(RulerOf(picked.To)));

            if (to > from)
                AudioCanvas.Children.Add(new Rectangle
                {
                    Width = Math.Max(1, to - from),
                    Height = height,
                    Fill = PickedFill,
                    Stroke = Brush("Tc"),
                    StrokeThickness = 1,
                    Margin = new Thickness(from, 0, 0, 0),
                    IsHitTestVisible = false,
                });
        }

        AudioCanvas.Children.Add(new Path { Data = aired, Stroke = WaveAiredBrush, StrokeThickness = 1, IsHitTestVisible = false });
        AudioCanvas.Children.Add(new Path { Data = wave, Stroke = WaveBrush, StrokeThickness = 1, IsHitTestVisible = false });

        GreyCovered(AudioCanvas, width, start, span, height, sound: true);
        DrawBlanks(AudioCanvas, width, start, span, height, sound: true);

        // The cuts: a thin line through the lane, like a razor cut in an editor.
        foreach (long cut in _audioCuts)
        {
            double x = X(RulerOf(cut));
            if (x < 0 || x > width) continue;

            AudioCanvas.Children.Add(new Rectangle
            {
                Width = 1,
                Height = height,
                Fill = Brush("Text"),
                Opacity = 0.8,
                Margin = new Thickness(x, 0, 0, 0),
                IsHitTestVisible = false,
            });
        }

        var label = new TextBlock
        {
            Text = "AUDIO",
            Foreground = Brush("Ok"),
            FontFamily = Mono,
            FontSize = 9.5,
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(label, 4);
        Canvas.SetTop(label, 2);
        AudioCanvas.Children.Add(label);
    }

    /// <summary>
    /// Reads a segment's waveform in the background, and draws the lane again once it is in.
    /// Asked for again on every redraw until it is in, so only what is on screen is ever read.
    /// </summary>
    private void ReadWaveform(string path)
    {
        if (_waveReading.Contains(path) || _waveFailed.Contains(path)) return;
        if (Ffmpeg.Locate(_settings.FfmpegPath) is not { } ffmpeg) return;

        _waveReading.Add(path);

        _ = Task.Run(async () =>
        {
            await WaveSlots.WaitAsync();

            WavePeaks? peaks;
            try { peaks = Waveform.Peaks(ffmpeg, path); }
            finally { WaveSlots.Release(); }

            _ = Dispatcher.BeginInvoke(() =>
            {
                // Read after the window closed: nothing to keep it for, or draw it on.
                if (_closed) return;

                _waveReading.Remove(path);
                if (peaks is null) _waveFailed.Add(path);
                else _waves[path] = peaks;

                // One redraw for however many arrive together.
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
