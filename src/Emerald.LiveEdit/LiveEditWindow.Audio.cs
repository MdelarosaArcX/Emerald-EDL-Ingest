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
/// The audio lane: the recording's sound drawn under its segments, and what can be done to it.
///
/// It works like a clip in an editor. <b>C</b> cuts the sound at the playhead; a click on the lane
/// picks up the piece between the cuts either side (or the marked range, when the click is inside
/// the marks). That piece can be made louder or quieter a decibel at a time.
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
        _audioPiece = AudioPieceAt(frame);

        // The playhead goes there too, as on every other part of the timeline - and it is where
        // C cuts.
        Pause();
        Seek(frame);

        DrawAudio();
        ShowLayer();
        Focus();
    }

    /// <summary>The marked range when the frame is inside it, otherwise the piece between the cuts either side.</summary>
    private (long From, long To)? AudioPieceAt(long rulerFrame)
    {
        if (SoundExtent() is not { } extent) return null;

        if (_markIn is { } mIn && _markOut is { } mOut && mOut > mIn && rulerFrame >= mIn && rulerFrame < mOut)
            return (CapturedOf(mIn), CapturedOf(mOut));

        long captured = CapturedOf(rulerFrame);

        if (captured < extent.First || captured >= extent.End) return null;

        return AudioEdits.PieceAt(captured, _audioCuts, extent.First, extent.End);
    }

    // ------------------------------------------------------------------ cutting, levels, deleting

    private void CutAudio_Click(object sender, RoutedEventArgs e) => CutAudio();
    private void AudioDown_Click(object sender, RoutedEventArgs e) => NudgeAudio(-1);
    private void AudioUp_Click(object sender, RoutedEventArgs e) => NudgeAudio(+1);

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
        _audioPiece = AudioEdits.PieceAt(at, _audioCuts, extent.First, extent.End);
        _selected = null;

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

        PushCues();
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
        double gain = _onIngest is null ? AudioEdits.Linear(AudioEdits.GainDbAt(CapturedOf(_at), _audioEdits)) : 1;

        if (!force && gain.Equals(_previewGain)) return;

        _previewGain = gain;
        ApplyLevel();
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

    private void DrawAudio()
    {
        double width = AudioCanvas.ActualWidth;
        AudioCanvas.Children.Clear();

        if (width <= 1) return;

        DrawAudio(width, ViewStart, ViewSpan);
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
            foreach (Placed placed in _placed)
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
                    FontFamily = new FontFamily("Consolas"),
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
            FontFamily = new FontFamily("Consolas"),
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
