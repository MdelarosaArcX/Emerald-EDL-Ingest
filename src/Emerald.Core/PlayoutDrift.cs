namespace Emerald.Core;

/// <summary>
/// Whether playout is keeping up with the clock, and by how much it is not.
///
/// <b>What "dropped" means here.</b> The card paces the output: a push blocks until a slot is
/// free, so frames are never thrown away on the way to the transmitter. What can happen is that
/// the engine fails to produce one in time — a decoder stalling on a slow disk, a file that
/// takes too long to open, a machine busy with something else. The card then has nothing new for
/// its next slot and repeats or blacks the one before.
///
/// Nothing in the loop notices that, because the loop only counts what it managed to push. So it
/// is measured against the wall clock instead: in a given stretch of time a transmitter at 25 fps
/// must have taken 25 frames a second, and the difference between that and what was actually
/// pushed is what did not reach air. That is the number an operator means by dropped frames, and
/// it needs no cooperation from the decoder to compute.
/// </summary>
public static class PlayoutDrift
{
    /// <summary>How many frames a transmitter at this rate must have taken in this time.</summary>
    public static long Expected(TimeSpan elapsed, int rate) =>
        rate <= 0 || elapsed <= TimeSpan.Zero ? 0 : (long)(elapsed.TotalSeconds * rate);

    /// <summary>
    /// Frames that should have been on air by now and were not.
    ///
    /// Never negative. Running slightly ahead is normal and meaningless — the card's queue holds
    /// a few frames, so the engine is always a little in front of what is actually radiating —
    /// and reporting that as a negative drop would be noise.
    /// </summary>
    public static long Dropped(TimeSpan elapsed, int rate, long framesOut) =>
        Math.Max(0, Expected(elapsed, rate) - framesOut);

    /// <summary>
    /// Whether a new figure is worth another line in the log.
    ///
    /// A transmitter that is a frame or two behind is within the queue's own slack and saying so
    /// every second would bury everything else. A fifth of a second of loss is the point at which
    /// it is a fault rather than jitter, and each further fifth is worth repeating.
    /// </summary>
    public static bool WorthSaying(long dropped, long alreadySaid, int rate) =>
        dropped - alreadySaid >= Math.Max(1, rate / 5);
}
