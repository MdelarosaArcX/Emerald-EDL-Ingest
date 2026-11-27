using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Emerald.Core;
using Emerald.Media;

namespace Emerald.LiveEdit;

/// <summary>
/// Playing from one segment into the next without a break.
///
/// Waiting for the player to say its file had ended, and only then starting the next, was a break at
/// every join: the message comes after the last picture, the next player takes a moment to move, and
/// sometimes Windows lost the play it was given and the picture stood still until the segment was
/// over (seen 2026-11-25). So the next segment is set going just before this one ends, out of sight,
/// and put on screen once it is seen to be moving.
/// </summary>
public partial class LiveEditWindow
{
    /// <summary>How long before the end the join is watched at every screen refresh rather than every tick.</summary>
    private const double JoinWatchSeconds = 0.4;

    /// <summary>How long before the end the next segment is set going: what a player takes to move.</summary>
    private const double RollSeconds = 0.045;

    /// <summary>
    /// How long before the end it is put on screen: a screen refresh, so it is not missed. Much more
    /// loses the last picture, and at the end the player has nothing to show.
    /// </summary>
    private const double CutSeconds = 0.016;

    /// <summary>A player told to play and not moving after this long has lost it.</summary>
    private const double NotMovingSeconds = 0.15;

    private bool _watchingJoin;

    /// <summary>How far the segment loaded has played, and which one that is. Started again by every seek.</summary>
    private double _played = -1;
    private Placed? _playedOf;

    /// <summary>
    /// How far into its file the recording has played, in seconds. A player that has ended reads a
    /// whole second - 29.000 of 29.760 - until it says so: read as it stands, that put the playhead
    /// and the sound most of a second back for a moment at a join.
    /// </summary>
    private double PlayedSeconds()
    {
        double at = Player.Position.TotalSeconds;

        if (_playing && _loaded is { } playing && ReferenceEquals(playing, _playedOf) && _seekedCaptured is null &&
            at < _played && _played > playing.Frames / (double)_rate - 0.4)
            return _played;

        _playedOf = _loaded;
        return _played = at;
    }

    /// <summary>The standby has been set going for a join, when, and where it started.</summary>
    private bool _rolling;
    private DateTime _rolledAt;
    private TimeSpan _rolledFrom;

    /// <summary>
    /// The segment that plays straight on from the end of the one loaded, with nothing placed or cut
    /// between. The one still being recorded only when <paramref name="recording"/> is true.
    /// </summary>
    private Placed? StraightOn(bool recording = false)
    {
        if (_onIngest is not null || _loaded is not { } ending) return null;
        if (NextSegmentFrom(ending.EndFrame) is not { } next || ReferenceEquals(next, ending)) return null;
        if (next.Pending && !recording) return null;

        List<IngestItem> items = PlayedItems();
        if (items.Count == 0) return next;

        // On the ruler too: not with an item at the join, or a deleted scene running up to it or over it.
        long join = Math.Max(ending.EndFrame, next.StartFrame);
        long last = IngestTimeline.RulerFrameOf(ending.EndFrame - 1, items);
        long first = IngestTimeline.RulerFrameOf(join, items);
        (_, long to) = IngestTimeline.RunAround(_at, items);

        return first < to && first - last == join - ending.EndFrame + 1 ? next : null;
    }

    /// <summary>The join about to be played over, if the standby is ready for it, and how long until it.</summary>
    private (Placed Next, double Left)? Join()
    {
        if (!_playing || _held || !_open || _seekedCaptured is not null || _onIngest is not null ||
            _loaded is not { } ending) return null;

        double left = ending.Frames / (double)_rate - PlayedSeconds();

        return left <= JoinWatchSeconds && (ReadyNext() ?? HeldNext()) is { } next ? (next, left) : null;
    }

    /// <summary>The segment still being recorded, straight on from this one, when the delay holds its first frame.</summary>
    private Placed? HeldNext()
    {
        if (_loaded is not { } ending || StraightOn(recording: true) is not { Pending: true } next) return null;

        long join = Math.Max(ending.EndFrame, next.StartFrame);

        if (_stopAt is { } stop && stop <= IngestTimeline.RulerFrameOf(join, PlayedItems())) return null;

        return HeldHolds(join) ? next : null;
    }

    /// <summary>Whether the delay holds a captured frame of the day.</summary>
    private bool HeldHolds(long capturedFrame) =>
        _heldPicture.Use(HeldLine) && _heldPicture.Line is { } line &&
        RecordingFrameOfDay(capturedFrame, line.FrameRate) is { } frame && frame >= 0 && line.SequenceOf(frame) >= 0;

    /// <summary>The segment that plays straight on, when the standby holds it on its first frame and it is not to stop before.</summary>
    private Placed? ReadyNext()
    {
        if (!_standbyReady || _loaded is not { } ending || StraightOn() is not { } next) return null;

        long join = Math.Max(ending.EndFrame, next.StartFrame);

        if (_stopAt is { } stop && stop <= IngestTimeline.RulerFrameOf(join, PlayedItems())) return null;

        return _standbyUri == new Uri(next.Clip.Path) && _standbyOffset == next.OffsetOf(join) ? next : null;
    }

    /// <summary>Every tick.</summary>
    private void FollowJoin()
    {
        // The standby is out of the delay's way, being made ready for the file closing under it.
        if (_closing is not null) return;

        if (Join() is null && RunJoin() is null) { LeaveJoin(); return; }
        if (_watchingJoin) return;

        _watchingJoin = true;
        CompositionTarget.Rendering += JoinFrame;
    }

    private void JoinFrame(object? sender, EventArgs e)
    {
        if (Join() is not { } join)
        {
            if (RunJoin() is { } run) RunFrame(run.Cut, run.Left);
            else LeaveJoin();

            return;
        }

        // Into the segment still being recorded: nothing to set going, the delay is already there.
        if (join.Next.Pending)
        {
            if (join.Left <= CutSeconds) IntoHeld(join.Next);
            return;
        }

        if (!_rolling)
        {
            if (join.Left <= RollSeconds) Roll();
        }
        else if (_standby.Position > _rolledFrom)
        {
            if (join.Left <= CutSeconds) CutTo(join.Next);
        }
        else if ((DateTime.UtcNow - _rolledAt).TotalSeconds > NotMovingSeconds)
        {
            _rolledAt = DateTime.UtcNow;
            Nudge(_standby);
        }
    }

    private void Roll()
    {
        _rolling = true;
        _rolledAt = DateTime.UtcNow;
        _rolledFrom = _standby.Position;
        _standby.Play();
    }

    /// <summary>Away from the join; if it was not gone over after all, the standby is put back on its frame.</summary>
    private void LeaveJoin()
    {
        if (_watchingJoin)
        {
            _watchingJoin = false;
            CompositionTarget.Rendering -= JoinFrame;
        }

        _closing = null;

        if (!_rolling) return;

        _rolling = false;
        _standby.SpeedRatio = 1;
        if (_standbyReady) Park(_standbyOffset);
    }

    // ------------------------------------------------------------------ into and out of ingest items

    /// <summary>
    /// The change of source the run playing ends in - into an ingest item placed on the timeline, or
    /// out of one back to the recording - when the standby holds what comes after it on the right
    /// frame: the ruler frame of the change, and how long until it. The same going over as between
    /// two segments: what comes next is set going just before, and put on screen once it moves.
    /// </summary>
    private (long Cut, double Left)? RunJoin()
    {
        if (!_playing || _held || !_open || _seekedCaptured is not null || _seekedIngest is not null) return null;

        List<IngestItem> items = PlayedItems();
        if (items.Count == 0) return null;

        long cut;
        double left;

        if (_onIngest is not null)
        {
            TimelineAt on = IngestTimeline.At(_at, items);
            if (!on.OnIngest || on.Path != _onIngest) return null;

            int found = items.FindIndex(i =>
                i.Path == on.Path && IngestTimeline.RulerStart(i, items) is var s && _at >= s && _at < s + i.Plan.Frames);
            if (found < 0) return null;

            IngestItem item = items[found];

            long start = IngestTimeline.RulerStart(item, items);
            double speed = Math.Max(0.0001, on.Speed);

            // Where the player is, on the ruler: the item's start, on by however far into the file it is.
            double fileAtStart = on.Offset - (_at - start) * speed;
            double ruler = start + (Player.Position.TotalSeconds * _rate - fileAtStart) / speed;

            cut = start + item.Plan.Frames;
            left = (cut - ruler) / _rate;
        }
        else if (_loaded is { } loaded)
        {
            (_, long to) = IngestTimeline.RunAround(_at, items);
            if (to == long.MaxValue) return null;

            TimelineAt here = IngestTimeline.At(_at, items);
            if (here.OnIngest) return null;

            // The segment ends first: that is a join between files.
            long cutCaptured = here.CapturedFrame + (to - _at);
            if (cutCaptured > loaded.EndFrame) return null;

            cut = to;
            left = (cutCaptured - loaded.StartFrame) / (double)_rate - PlayedSeconds();
        }
        else return null;

        if (left > JoinWatchSeconds) return null;
        if (_stopAt is { } stop && stop <= cut) return null;

        return _standbyReady && SourceAt(cut, items) is { } next && next.Uri == _standbyUri && next.Offset == _standbyOffset
            ? (cut, left)
            : null;
    }

    private void RunFrame(long cut, double left)
    {
        if (!_rolling)
        {
            // Windows drops a position set while a file opens: put back on its frame before it is wanted.
            if (!Parked())
            {
                Park(_standbyOffset);
                return;
            }

            if (left > RollSeconds) return;

            // At the speed it will play at: a fit-to-fill item runs faster or slower than the clock.
            TimelineAt after = IngestTimeline.At(cut, PlayedItems());
            _standby.SpeedRatio = after.OnIngest ? after.Speed : 1;
            Roll();
        }
        else if (_standby.Position > _rolledFrom)
        {
            if (left <= CutSeconds) CutRun(cut);
        }
        else if ((DateTime.UtcNow - _rolledAt).TotalSeconds > NotMovingSeconds)
        {
            _rolledAt = DateTime.UtcNow;
            Nudge(_standby);
        }
    }

    /// <summary>
    /// Over the change, onto the standby that is already playing. Through the ordinary seek, which
    /// finds the standby holding what it wants and only puts it on screen; the timeline is redrawn
    /// after, out of the picture's way.
    /// </summary>
    private void CutRun(long cut)
    {
        _rolling = false;
        _quickSeek = true;

        try { Seek(cut); }
        finally { _quickSeek = false; }
    }

    /// <summary>
    /// From the frame timer, at the end of a run: the change as the join makes it, when the standby
    /// has been set going for it. True when it was.
    /// </summary>
    private bool RunCutNow()
    {
        if (!_rolling || RunJoin() is not { } run) return false;

        CutRun(run.Cut);
        return true;
    }

    /// <summary>
    /// The item playing has ended with what follows it held ready at <paramref name="cut"/>: that
    /// set going and put on screen. False when the standby does not hold it.
    /// </summary>
    private bool IntoReady(long cut)
    {
        if (!_standbyReady || SourceAt(cut, PlayedItems()) is not { } next || next.Uri != _standbyUri || next.Offset != _standbyOffset)
            return false;

        if (!_rolling)
        {
            if (!Parked()) return false;

            TimelineAt after = IngestTimeline.At(cut, PlayedItems());
            _standby.SpeedRatio = after.OnIngest ? after.Speed : 1;
            Roll();
        }

        CutRun(cut);
        return true;
    }

    /// <summary>The standby sits on the frame it was sent to, near enough.</summary>
    private bool Parked() => Math.Abs(_standby.Position.TotalSeconds - _standbyOffset / (double)_rate) <= 0.25;

    /// <summary>Set while the join makes its seek: the timeline redrawn after, not in the way of the picture.</summary>
    private bool _quickSeek;

    private DispatcherTimer? _redrawLater;

    /// <summary>The end of every seek: the view and the timeline brought up to date.</summary>
    private void AfterSeek()
    {
        if (_quickSeek)
        {
            DrawMarkers();

            // A quarter of a second on: a redraw straight after held up the first frames of what came next.
            if (_redrawLater is null)
            {
                _redrawLater = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
                _redrawLater.Tick += (_, _) =>
                {
                    _redrawLater!.Stop();
                    if (_closed) return;

                    EnsureVisible(_at);
                    ShowScroll();
                    Draw();
                    ShowPosition();
                };
            }

            _redrawLater.Stop();
            _redrawLater.Start();
            return;
        }

        EnsureVisible(_at);
        ShowScroll();

        Draw();
        DrawOnAir();
        ShowPosition();
    }

    /// <summary>From the file onto the delay's picture of the segment still being recorded, playing on.</summary>
    private void IntoHeld(Placed next)
    {
        long join = Math.Max(_loaded!.EndFrame, next.StartFrame);

        LeaveJoin();
        Seek(IngestTimeline.RulerFrameOf(join, PlayedItems()));
    }

    /// <summary>
    /// The segment being played from the delay has closed, and the file is being made ready to go on
    /// from: the segment, and the captured frame the file takes over at.
    /// </summary>
    private (Placed Segment, long At)? _closing;

    /// <summary>
    /// Every tick while the delay plays a segment that has since closed. The delay plays on while the
    /// file opens, out of sight, on a frame a second ahead; at that frame the file is set going, and
    /// it takes over once it moves. Opening the file where the playhead was held the picture still
    /// while it opened. True once the file has the screen; false while the delay plays on; null to go
    /// over the ordinary way.
    /// </summary>
    private bool? HeldIntoFile(Placed closed, long captured)
    {
        var uri = new Uri(closed.Clip.Path);

        if (_closing is not { } closing || !string.Equals(closing.Segment.Clip.Path, closed.Clip.Path, StringComparison.OrdinalIgnoreCase))
        {
            // Too near its end for the file to be worth it: the next one is a moment away.
            if (closed.EndFrame - captured < 2L * _rate) return null;

            long at = captured + _rate;
            _closing = (closed, at);
            _rolling = false;
            Preload((uri, closed.OffsetOf(at)));

            // Its sound read too, for the moment it takes over from the delay's.
            _preview?.Prepare(closed.Clip.Path, TracksOf(closed), closed.OffsetOf(at) / (double)_rate);
            return false;
        }

        // Never got ready, or the standby was wanted for something else.
        if (_standbyUri != uri || captured > closing.At + _rate)
        {
            _closing = null;
            return null;
        }

        if (!_standbyReady) return false;

        if (!_rolling)
        {
            // Parked where it was sent. Windows drops a position set as a file opens, which for a
            // join is the start anyway; here it would go back to the start of the segment.
            if (!Parked())
            {
                Park(_standbyOffset);
                return false;
            }

            // A tick ahead, for the moment a player takes to move.
            if (captured >= closing.At - 1) Roll();
            return false;
        }

        if (_standby.Position <= _rolledFrom)
        {
            if ((DateTime.UtcNow - _rolledAt).TotalSeconds > NotMovingSeconds)
            {
                _rolledAt = DateTime.UtcNow;
                Nudge(_standby);
            }

            return false;
        }

        _closing = null;
        _rolling = false;
        _held = false;
        _heldClock.Reset();

        _loaded = closed;
        _wantOffset = closed.OffsetOf(closing.At);
        _seekedCaptured = null;
        _playFloor = closing.At;
        _playedOf = null;
        _at = closing.At - _heldShift;

        Show(uri, later: true);
        HideHeld();

        ActivityLog.Shared.Info(LogSource.App,
            $"LOAD  {closed.Clip.Name} - in Live Edit at {Stamp(closed.StartFrame)}, closed while it played from the delay, " +
            $"{closed.Clip.SizeText}, from {System.IO.Path.GetDirectoryName(closed.Clip.Path)}.",
            @event: "liveedit.loaded", file: closed.Clip.Path);

        DrawMarkers();
        Dispatcher.BeginInvoke(() => { if (!_closed) Draw(); }, DispatcherPriority.Background);
        return true;
    }

    /// <summary>The segment has ended with the next one ready. False when there is none.</summary>
    private bool EndedIntoNext()
    {
        if (!_playing || _held || ReadyNext() is not { } next) return false;

        if (!_rolling) Roll();
        CutTo(next);
        return true;
    }

    /// <summary>Puts the next segment, already playing, on screen. No seek, and nothing else in the way of it.</summary>
    private void CutTo(Placed next)
    {
        long join = Math.Max(_loaded!.EndFrame, next.StartFrame);

        // The sound and the playhead read on from the new file without waiting for anything.
        _rolling = false;
        _loaded = next;
        _wantOffset = next.OffsetOf(join);
        _seekedCaptured = null;
        _playFloor = join;
        _playedOf = null;
        _at = IngestTimeline.RulerFrameOf(join, PlayedItems());

        Show(new Uri(next.Clip.Path), later: true);
        LeaveJoin();

        ActivityLog.Shared.Info(LogSource.App,
            $"LOAD  {next.Clip.Name} - in Live Edit at {Stamp(next.StartFrame)}, " +
            $"{next.Clip.SizeText}, from {System.IO.Path.GetDirectoryName(next.Clip.Path)}.",
            @event: "liveedit.loaded", file: next.Clip.Path);

        DrawMarkers();

        // The timeline again for the segment that is now lit, once the picture has changed over.
        Dispatcher.BeginInvoke(() =>
        {
            if (_closed) return;

            if (!Following) EnsureVisible(_at);
            ShowScroll();
            Draw();
        }, DispatcherPriority.Background);
    }

    /// <summary>Lets go of a player that has been cut away from, out of the way of the cut.</summary>
    private void ReleaseLater(MediaElement player)
    {
        player.IsMuted = true;

        var timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };

        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // Unless it has been given something since.
            if (!_closed && ReferenceEquals(player, _standby) && _standbyUri is null) Release(player);
        };

        timer.Start();
    }

    private static void Release(MediaElement player)
    {
        player.Stop();
        player.Source = null;
        player.IsMuted = true;
        player.SpeedRatio = 1;
    }

    private static void Nudge(MediaElement player)
    {
        player.Pause();
        player.Play();
    }

    private TimeSpan _lastPosition = TimeSpan.MinValue;
    private DateTime _lastMoved = DateTime.MinValue;
    private int _nudges;

    /// <summary>
    /// Every tick: a player that should be playing and stands still is told again. Windows loses a
    /// play now and then, and the picture stood until the operator did something.
    /// </summary>
    private void KeepMoving()
    {
        DateTime now = DateTime.UtcNow;

        bool should = _playing && !_held && _open && _seekedCaptured is null && Player.Source is not null &&
                      Player.NaturalDuration is { HasTimeSpan: true } all &&
                      Player.Position < all.TimeSpan - TimeSpan.FromSeconds(0.2);

        if (!should || Player.Position != _lastPosition)
        {
            _lastPosition = should ? Player.Position : TimeSpan.MinValue;
            _lastMoved = now;
            _nudges = 0;
            return;
        }

        if ((now - _lastMoved).TotalSeconds < 0.4 || _nudges >= 10) return;

        _lastMoved = now;
        _nudges++;

        // Told again first, and put back where it is as well if that was not enough.
        if (_nudges % 2 == 0) Player.Position = _lastPosition;
        Nudge(Player);

        ActivityLog.Shared.Warn(LogSource.LiveEdit,
            $"The preview stood still at {Stamp(_at)} and was told to play again ({_nudges}).",
            @event: "liveedit.stoodstill");
    }
}
