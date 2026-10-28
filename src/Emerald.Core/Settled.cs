namespace Emerald.Core;

/// <summary>
/// Reports a changing value only once it has stopped changing, and says how much it flapped.
///
/// <b>Why a log needs this.</b> Some of what Emerald watches is a reading off a wire rather than
/// a decision somebody made: how many audio pairs the receiver is carrying, whether a signal is
/// locked. A reading like that can sit on a boundary and oscillate, and a record written on every
/// edge then fills with the same two lines alternating — which buries everything else and tells
/// the reader nothing they could not have got from one line.
///
/// Observed on this plant: a pair count alternating between one and none about twice a second,
/// filling the capture log with "1 of 8 carrying (was 0)" and "0 of 8 carrying (was 1)" and
/// nothing else.
///
/// So a change is reported once it has held for a settling time, and the flapping that preceded
/// it is reported as a count rather than as one line each. An unstable input is itself worth
/// knowing about — it is usually the actual fault — but it is one fact, not two hundred.
///
/// Time is passed in rather than read, so the behaviour can be tested without waiting.
/// </summary>
/// <typeparam name="T">The value being watched. Compared with its default equality.</typeparam>
public sealed class Settled<T> where T : notnull
{
    private readonly TimeSpan _settlingTime;

    private T? _reported;
    private bool _everReported;

    private T? _candidate;
    private DateTime _candidateSince;

    /// <summary>How many times the value moved away from the reported one and came back.</summary>
    public int Flaps { get; private set; }

    public Settled(TimeSpan settlingTime) => _settlingTime = settlingTime;

    /// <summary>The value last reported as settled.</summary>
    public T? Current => _reported;

    /// <summary>
    /// Offers the latest reading.
    ///
    /// Returns true when it is time to report <paramref name="value"/> as a settled change — it
    /// differs from the last reported value and has held for the settling time. Returns false
    /// while it is still moving, while it matches what was already reported, and for the very
    /// first reading, which is the panel filling in rather than anything changing.
    /// </summary>
    public bool Offer(T value, DateTime now)
    {
        // The first reading is the starting point, not a change.
        if (!_everReported)
        {
            _everReported = true;
            _reported = value;
            _candidate = value;
            _candidateSince = now;
            return false;
        }

        // Back to what was last reported: whatever excursion was under way is over, and it
        // counts as a flap rather than as a change.
        if (EqualityComparer<T>.Default.Equals(value, _reported))
        {
            if (!EqualityComparer<T>.Default.Equals(_candidate, value)) Flaps++;

            _candidate = value;
            _candidateSince = now;
            return false;
        }

        // A different value: start it settling, or let it carry on settling. The elapsed check
        // follows either way rather than returning early on the first sight of it, so a caller
        // that asked for no settling time gets the change reported at once instead of on its
        // second reading.
        if (!EqualityComparer<T>.Default.Equals(_candidate, value))
        {
            _candidate = value;
            _candidateSince = now;
        }

        if (now - _candidateSince < _settlingTime) return false;

        _reported = value;
        return true;
    }

    /// <summary>
    /// Takes the flap count and resets it, for a caller about to write it down.
    ///
    /// Read-and-clear in one, so two callers cannot both report the same flapping and a count
    /// cannot be lost between reading it and clearing it.
    /// </summary>
    public int TakeFlaps()
    {
        int flaps = Flaps;
        Flaps = 0;
        return flaps;
    }
}
