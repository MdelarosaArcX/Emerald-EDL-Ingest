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

}

/// <summary>
/// The record of what a message lost, stall by stall.
///
/// <b>Why a high-water mark and not a difference.</b> The shortfall against the clock can shrink:
/// the engine catches up a little, the figure comes down. Those frames still did not go out, so
/// forgiving them would make the closing line understate what happened. The total only ever
/// rises, and what rises is what gets reported.
///
/// Separate from the engine so the accounting can be tested: the invariants worth having — a
/// stall is counted once, the losses sum to the total, recovery costs nothing and earns nothing —
/// are exactly the ones that are hard to see are wrong by reading a play loop.
/// </summary>
public sealed class DropLedger
{
    /// <summary>Frames lost in this message, in total.</summary>
    public long Total { get; private set; }

    /// <summary>How many separate stalls those frames came from.</summary>
    public long Stalls { get; private set; }

    /// <summary>
    /// Takes the shortfall as it stands and returns how many frames have been lost since last
    /// asked — zero when nothing has got worse, which is the usual answer.
    /// </summary>
    public long Record(long droppedSoFar)
    {
        if (droppedSoFar <= Total) return 0;

        long lost = droppedSoFar - Total;

        Total = droppedSoFar;
        Stalls++;

        return lost;
    }

    /// <summary>Starts again for the next message, so one is never judged on another's stalls.</summary>
    public void Reset()
    {
        Total = 0;
        Stalls = 0;
    }

    /// <summary>
    /// What to say on a closing line.
    ///
    /// Said on every message, including the clean ones. "no frames dropped" is the sentence an
    /// operator wants after a transmission, and a line that is simply absent when all is well
    /// cannot be told apart from the feature not working.
    /// </summary>
    public string Summary(int rate) =>
        Total <= 0
            ? "no frames dropped"
            : $"{Total:N0} frame(s) dropped in {Stalls:N0} stall(s) " +
              $"({Total / (double)Math.Max(1, rate):F2} s lost)";
}
