using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Emerald.Core;
using Emerald.Video;

namespace Emerald.LiveEdit;

/// <summary>
/// The segment still being recorded - the yellow one - on the big screen.
///
/// No file of it can be opened until the recorder closes it, so the playhead on it used to show
/// a black screen and a sentence, and so did the on-air mark the moment it reached it. But the
/// last few minutes of the recording are already held in the capture deck's delay line, raw and
/// numbered by their frame of the recording - it is what goes to air - so that part of the
/// segment is shown from there instead, and plays along at the real rate. Further back than the
/// line reaches is still the sentence.
///
/// Picture only: the line's sound is the recorder's raw input, and there is no player here to
/// send it through.
/// </summary>
public partial class LiveEditWindow
{
    private readonly HeldPicture _heldPicture = new();
    private WriteableBitmap? _heldBitmap;

    /// <summary>True while the big screen is showing, and playing, from the delay line.</summary>
    private bool _held;

    /// <summary>Where a held run started on the ruler, and the clock it runs on.</summary>
    private long _heldFrom;
    private readonly Stopwatch _heldClock = new();

    /// <summary>Captured frame less ruler frame at the start of a held run: a change means a deleted scene was crossed.</summary>
    private long _heldShift;

    /// <summary>When the held picture was left for a file, so it can cover the file's opening.</summary>
    private DateTime? _heldLeftAt;

    /// <summary>The line to show from: the one kept ready, or else whichever is on air.</summary>
    private static IDelayLine? HeldLine =>
        RecordingPicture.Shared.Line ?? LiveAir.Shared.Line ?? TidalLock.Shared.Line;

    /// <summary>The frame of the recording a frame of this ruler is, through time - as the on-air mark is placed.</summary>
    private long? RecordingFrameOfDay(long dayFrame, int lineRate)
    {
        if (PipelineState.Shared.RecordingStartedAt is not { } started) return null;

        DateTime at = _day.Date + TimeSpan.FromSeconds(dayFrame / (double)_rate);
        return (long)Math.Round((at - started).TotalSeconds * lineRate);
    }

    /// <summary>
    /// Puts frame <paramref name="capturedFrame"/> of the day on the big screen from the delay
    /// line. False, with the screen left as it was, when the line does not hold it.
    /// </summary>
    private bool ShowHeld(long capturedFrame)
    {
        if (!_heldPicture.Use(HeldLine) || _heldPicture.Line is not { } line) return false;

        if (RecordingFrameOfDay(capturedFrame, line.FrameRate) is not { } frame || frame < 0) return false;

        if (!_heldPicture.TryRead(frame, out byte[] bgra, out int width, out int height)) return false;

        if (_heldBitmap is null || _heldBitmap.PixelWidth != width || _heldBitmap.PixelHeight != height)
        {
            _heldBitmap = new WriteableBitmap(width, height, 96, 96, PixelFormats.Bgra32, null);
            HeldImage.Source = _heldBitmap;
        }

        _heldBitmap.WritePixels(new Int32Rect(0, 0, width, height), bgra, width * 4, 0);

        HeldView.Visibility = Visibility.Visible;
        _heldLeftAt = null;
        return true;
    }

    /// <summary>
    /// From Seek, for a frame of the segment still being recorded: shows it from the delay line
    /// and makes the transport run on that. False when the line does not hold it.
    /// </summary>
    private bool EnterHeld(long capturedFrame)
    {
        if (!ShowHeld(capturedFrame)) { LeaveHeld(); return false; }

        if (!_held)
            ActivityLog.Shared.Info(LogSource.LiveEdit,
                $"HELD  Live Edit is showing the segment still being recorded from the delay buffer, at {Stamp(_at)}.",
                @event: "liveedit.held");

        _held = true;
        _heldFrom = _at;
        _heldShift = capturedFrame - _at;
        _heldClock.Restart();
        return true;
    }

    /// <summary>
    /// Back to the players. The picture stays up until the file replacing it has opened, so the
    /// hand-over is not a black frame; with nothing replacing it, it goes at once.
    /// </summary>
    private void LeaveHeld(bool now = true)
    {
        _held = false;
        _heldClock.Reset();

        if (HeldView.Visibility != Visibility.Visible) return;

        if (now) HideHeld();
        else _heldLeftAt ??= DateTime.UtcNow;
    }

    private void HideHeld()
    {
        HeldView.Visibility = Visibility.Collapsed;
        _heldLeftAt = null;
    }

    /// <summary>Every tick: lets the held picture go once the file after it is showing.</summary>
    private void SettleHeld()
    {
        if (_held || _heldLeftAt is not { } left) return;

        if (_open || DateTime.UtcNow - left > TimeSpan.FromSeconds(3)) HideHeld();
    }

    /// <summary>
    /// Every tick while playing held: the playhead moves on the clock, and the picture with it.
    /// False when it handed over to something else - the end of the segment, an ingest item.
    /// </summary>
    private bool AdvanceHeld()
    {
        _at = Math.Min(_dayFrames - 1,
                       _heldFrom + (long)Math.Round(_heldClock.Elapsed.TotalSeconds * _rate));

        if (StoppedAtMarkOut()) return false;

        TimelineAt on = IngestTimeline.At(_at, IngestItems());

        // Run into an ingest item, a deleted scene, or a closed file: the ordinary path takes it.
        if (on.OnIngest || ClipAt(on.CapturedFrame) is not { Pending: true } ||
            on.CapturedFrame - _at != _heldShift)
        {
            Seek(_at);

            if (_loaded is not null || _onIngest is not null) Player.Play();
            else if (!_held) Pause();

            return false;
        }

        // Not held: caught up with the recorder, or the line has gone. The last picture stays.
        if (!ShowHeld(on.CapturedFrame) && _heldPicture.Line is null)
        {
            Pause();
            Seek(_at);
            return false;
        }

        if (!Following && (_at < _scroll || _at >= _scroll + ViewSpan))
        {
            EnsureVisible(_at);
            ShowScroll();
            Draw();
        }

        return true;
    }
}
