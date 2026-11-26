namespace Emerald.Video;

/// <summary>
/// What a transmitter does while a frame it wants is still being decoded (<see cref="DelayReadResult.NotReady"/>).
///
/// The card has a third of a second queued, so a short wait is nothing. After that the picture on air is
/// held, one a frame's time, so the card never runs dry; and after a second the frame is given up, so one
/// that never comes cannot freeze air.
/// </summary>
internal sealed class OverflowWait
{
    public const int QuietMs = 120;
    public const int GiveUpMs = 1000;
    private const int HoldEveryMs = 36;

    private readonly Func<long> _now;
    private readonly Action<int> _sleep;
    private long _since = -1;

    public OverflowWait() : this(() => Environment.TickCount64, Thread.Sleep) { }

    internal OverflowWait(Func<long> now, Action<int> sleep)
    {
        _now = now;
        _sleep = sleep;
    }

    public void Reset() => _since = -1;

    /// <summary>True to give the frame up. Otherwise it has waited a moment, and <paramref name="push"/> says to hold a picture.</summary>
    public bool GiveUp(out bool push)
    {
        long now = _now();
        if (_since < 0) _since = now;

        long waited = now - _since;
        push = waited >= QuietMs;

        if (waited >= GiveUpMs) return true;

        _sleep(push ? HoldEveryMs : 3);
        return false;
    }
}
