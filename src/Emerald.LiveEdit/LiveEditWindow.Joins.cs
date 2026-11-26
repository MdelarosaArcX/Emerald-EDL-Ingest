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

    /// <summary>The segment that plays straight on from the end of the one loaded, with nothing placed or cut between.</summary>
    private Placed? StraightOn()
    {
        if (_onIngest is not null || _loaded is not { } ending) return null;
        if (NextSegmentFrom(ending.EndFrame) is not { Pending: false } next || ReferenceEquals(next, ending)) return null;

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

        return left <= JoinWatchSeconds && ReadyNext() is { } next ? (next, left) : null;
    }

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
        if (Join() is null) { LeaveJoin(); return; }
        if (_watchingJoin) return;

        _watchingJoin = true;
        CompositionTarget.Rendering += JoinFrame;
    }

    private void JoinFrame(object? sender, EventArgs e)
    {
        if (Join() is not { } join) { LeaveJoin(); return; }

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

        if (!_rolling) return;

        _rolling = false;
        if (_standbyReady) Park(_standbyOffset);
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
