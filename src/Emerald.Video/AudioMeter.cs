namespace Emerald.Video;

/// <summary>
/// One channel's level, as a meter shows it.
///
/// Both numbers are dBFS — decibels below full scale — so 0 is as loud as 16-bit audio goes
/// and everything real is negative. <see cref="Silence"/> is the floor this meter bottoms
/// out at; anything quieter is reported as exactly that rather than as minus infinity, which
/// no bar can draw.
/// </summary>
public readonly record struct AudioLevel(double PeakDb, double RmsDb, bool Present)
{
    /// <summary>The bottom of the scale. -60 dBFS is quiet enough to read as nothing.</summary>
    public const double Silence = -60.0;

    public static readonly AudioLevel Absent = new(Silence, Silence, false);

    /// <summary>
    /// Where the bar should reach, 0 to 1, on the same -60..0 dBFS scale.
    ///
    /// Deliberately not linear in amplitude: a bar drawn from the raw sample value spends
    /// almost all of its travel in the top few dB and reads as either full or empty. Decibels
    /// are how loudness is judged and how every meter on a desk is marked.
    /// </summary>
    public double PeakFraction => Fraction(PeakDb);

    public double RmsFraction => Fraction(RmsDb);

    /// <summary>Past this, a broadcast chain is being pushed. Meters go amber here.</summary>
    public bool IsHot => PeakDb >= -10.0;

    /// <summary>At or above full scale: clipped, or near enough that it will be.</summary>
    public bool IsClipping => PeakDb >= -0.5;

    private static double Fraction(double db) =>
        double.IsNaN(db) ? 0 : Math.Clamp((db - Silence) / -Silence, 0, 1);
}

/// <summary>
/// Turns a stream of samples into a level a meter can draw.
///
/// A meter that showed the true peak of every frame would flicker unreadably, and one that
/// showed a plain average would miss the transients that matter. So this does what a hardware
/// meter does: the bar jumps to a rise immediately and falls back gradually, and the peak sits
/// on top for a moment before it starts to come down. The decay rates are per second and
/// applied against real elapsed time, so the meter looks the same at 25 and 50 fps.
///
/// Not thread-safe: one meter per channel, owned by whichever thread is feeding it.
/// </summary>
public sealed class AudioMeter
{
    /// <summary>How fast the bar falls, in dB per second. Roughly a standard PPM return.</summary>
    public const double DecayDbPerSecond = 26.0;

    /// <summary>How long the peak marker sits before it starts to fall.</summary>
    public static readonly TimeSpan PeakHold = TimeSpan.FromSeconds(1.2);

    /// <summary>How fast the held peak falls once the hold expires.</summary>
    public const double PeakDecayDbPerSecond = 12.0;

    private double _peakDb = AudioLevel.Silence;
    private double _rmsDb = AudioLevel.Silence;
    private double _heldPeakDb = AudioLevel.Silence;

    private long _lastTicks;
    private long _peakSetTicks;
    private bool _present;

    /// <summary>The peak marker, which lags the bar so a transient stays readable.</summary>
    public double HeldPeakDb => _heldPeakDb;

    public AudioLevel Level => new(_peakDb, _rmsDb, _present);

    /// <summary>
    /// Feeds one frame's samples for this channel.
    ///
    /// <paramref name="present"/> false means the channel is not on the wire at all, which is
    /// different from being silent: the meter is emptied and marked absent so the UI can grey
    /// the row rather than showing a live channel that happens to be quiet.
    /// </summary>
    public void Push(short[] samples, int count, bool present)
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double seconds = _lastTicks == 0
            ? 0
            : (now - _lastTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
        _lastTicks = now;

        _present = present;

        if (!present || count <= 0)
        {
            Decay(seconds);
            return;
        }

        int peak = 0;
        double sumSquares = 0;

        for (int i = 0; i < count; i++)
        {
            int v = samples[i];
            int magnitude = v < 0 ? -v : v;
            if (magnitude > peak) peak = magnitude;
            sumSquares += (double)v * v;
        }

        double peakDb = ToDb(peak / 32768.0);
        double rmsDb = ToDb(Math.Sqrt(sumSquares / count) / 32768.0);

        // Up instantly, down gradually. A meter that fell as fast as it rose would be a
        // flicker; one that rose as slowly as it falls would miss the transient entirely.
        _peakDb = peakDb > _peakDb ? peakDb : Math.Max(peakDb, _peakDb - DecayDbPerSecond * seconds);
        _rmsDb = rmsDb > _rmsDb ? rmsDb : Math.Max(rmsDb, _rmsDb - DecayDbPerSecond * seconds);

        if (peakDb >= _heldPeakDb)
        {
            _heldPeakDb = peakDb;
            _peakSetTicks = now;
        }
        else
        {
            double held = (now - _peakSetTicks) / (double)System.Diagnostics.Stopwatch.Frequency;
            if (held > PeakHold.TotalSeconds)
                _heldPeakDb = Math.Max(_peakDb, _heldPeakDb - PeakDecayDbPerSecond * seconds);
        }
    }

    /// <summary>
    /// Lets the meter fall with no new audio — the feed went away, or the UI is drawing
    /// faster than frames arrive. Without this a lost signal would leave the bar frozen at
    /// whatever it last read, which looks exactly like a signal that is still there.
    /// </summary>
    public void Idle(TimeSpan elapsed) => Decay(elapsed.TotalSeconds);

    private void Decay(double seconds)
    {
        if (seconds <= 0) return;

        _peakDb = Math.Max(AudioLevel.Silence, _peakDb - DecayDbPerSecond * seconds);
        _rmsDb = Math.Max(AudioLevel.Silence, _rmsDb - DecayDbPerSecond * seconds);
        _heldPeakDb = Math.Max(AudioLevel.Silence, _heldPeakDb - PeakDecayDbPerSecond * seconds);
    }

    public void Reset()
    {
        _peakDb = _rmsDb = _heldPeakDb = AudioLevel.Silence;
        _lastTicks = _peakSetTicks = 0;
        _present = false;
    }

    private static double ToDb(double amplitude) =>
        amplitude <= 0 ? AudioLevel.Silence
                       : Math.Max(AudioLevel.Silence, 20.0 * Math.Log10(amplitude));
}
