namespace Emerald.Video;

/// <summary>
/// Keeps a transmitter's "holding the last frame" warnings to one every half minute.
///
/// A transmitter goes to Holding when the ring has had nothing new for a second, and back on
/// air the moment a frame arrives. A disk that keeps falling behind does that over and over:
/// 38 warnings and 38 "on air" lines a minute were measured on 2026-11-18, which buried every
/// other line in the record. The first hold is said; holds within <see cref="QuietMs"/> of it
/// are counted instead, and the next one said carries the count. A quiet hold's return to air
/// is not said either. The screen still gets every change - only the log line is held back.
/// </summary>
public sealed class HoldNotices
{
    public const int QuietMs = 30_000;

    private readonly Func<long> _now;
    private long _saidAt = long.MinValue / 2;
    private int _unsaid;
    private bool _holdingQuietly;

    public HoldNotices(Func<long>? now = null) => _now = now ?? (() => Environment.TickCount64);

    /// <summary>
    /// The message to log for a change from <paramref name="was"/> to <paramref name="now"/>, or
    /// "" for one that is only shown, not logged.
    /// </summary>
    public string Filter(DelayPhase was, DelayPhase now, string message)
    {
        if (now == DelayPhase.Holding)
        {
            long t = _now();

            if (t - _saidAt < QuietMs)
            {
                _unsaid++;
                _holdingQuietly = true;
                return "";
            }

            _saidAt = t;
            _holdingQuietly = false;

            if (_unsaid > 0)
            {
                message += $" ({_unsaid} more short hold(s) since the last one logged.)";
                _unsaid = 0;
            }

            return message;
        }

        if (was == DelayPhase.Holding && _holdingQuietly)
        {
            _holdingQuietly = false;

            // Back on air after a hold nobody was told about: nothing to say. Anything else -
            // failing, draining, finishing - is still said.
            if (now == DelayPhase.OnAir) return "";
        }

        return message;
    }
}
