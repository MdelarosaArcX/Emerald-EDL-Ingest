namespace Emerald.Video;

/// <summary>What the transmitter should do with the next slot it is about to send.</summary>
public enum PaceAction
{
    /// <summary>Not enough delay has accumulated yet. Send black; this is the countdown.</summary>
    Fill,

    /// <summary>Send the frame at the read position and move on. The normal case.</summary>
    Advance,

    /// <summary>Send the last frame again, muted, and do not move on.</summary>
    Repeat,

    /// <summary>Send the frame at the read position and then skip one, to shed drift.</summary>
    Skip,

    /// <summary>The writer has lapped the reader. Re-establish the delay and say so.</summary>
    Resync,

    /// <summary>The recording has stopped and everything written has been sent.</summary>
    Finish,

    /// <summary>The receiver stopped delivering long enough that black is more honest.</summary>
    Fail,
}

/// <summary>
/// The pacing policy of the delay line, as a pure function.
///
/// The receiver and the transmitter are clocked by different oscillators. Even genlocked
/// they are not the same clock, and free-running they differ by tens of parts per million —
/// which at 25 fps is a frame every few minutes. A delay line therefore cannot simply read
/// one frame for every frame it writes forever; it needs a stated policy for what happens
/// when the two ends drift apart, when the reader catches the writer, and when the writer
/// laps the reader.
///
/// That policy is the part of the feature most likely to be wrong and least likely to be
/// exercised on a bench, so it is separated from every file handle and every card handle and
/// made a static function over integers. Everything in the table below is unit-tested; the
/// transmitter around it is three calls in a row.
/// </summary>
public static class DelayPacer
{
    /// <summary>
    /// How far the delay may wander before it is corrected: half a second either way. A
    /// fixed delay does not need to be fixed to the frame, and correcting every frame would
    /// mean visibly stuttering the output to chase an oscillator.
    /// </summary>
    public static long Deadband(int frameRate) => Math.Max(1, frameRate / 2);

    /// <summary>How long a silent receiver is held on the last frame before it becomes black.</summary>
    public const int GiveUpAfterMs = 10_000;

    /// <summary>How long a silent receiver is tolerated before the operator is told.</summary>
    public const int HoldingAfterMs = 1_000;

    /// <param name="written">Frames the writer has committed.</param>
    /// <param name="readSeq">The frame the transmitter is about to send.</param>
    /// <param name="target">The delay, in frames.</param>
    /// <param name="slotCount">Ring capacity, in frames.</param>
    /// <param name="isSealed">The recording has stopped; no more frames are coming.</param>
    /// <param name="sealedAt">Frames written when it was sealed. Only meaningful if sealed.</param>
    /// <param name="stalledMs">How long since the writer last committed a frame.</param>
    public static PaceAction Decide(long written, long readSeq, long target, long slotCount,
                                    bool isSealed, long sealedAt, long stalledMs, int frameRate)
    {
        // Everything written has gone out and nothing more is coming. This is the end of a
        // drain, and it is the only clean ending the feature has.
        if (isSealed && readSeq >= sealedAt) return PaceAction.Finish;

        // The writer has lapped the reader: the frame wanted has already been overwritten.
        // Checked before the underrun case because both can be true at once after a stall,
        // and this is the one that would put a torn frame on air.
        if (readSeq < written - slotCount) return PaceAction.Resync;

        // Nothing to send yet. Before anything has gone to air this is the countdown; after,
        // it is an underrun and the last frame is held.
        if (readSeq >= written)
        {
            if (!isSealed && stalledMs >= GiveUpAfterMs) return PaceAction.Fail;
            return written < target ? PaceAction.Fill : PaceAction.Repeat;
        }

        // Still filling: hold black until a whole delay has accumulated, so the first frame
        // on air is already the full distance behind the receiver.
        if (written < target) return PaceAction.Fill;

        // Drift. Correct at most one frame at a time, and only outside the deadband.
        long lag = written - readSeq;
        long deadband = Deadband(frameRate);

        if (lag > target + deadband) return PaceAction.Skip;     // delay growing; shed a frame
        if (lag < target - deadband) return PaceAction.Repeat;   // delay shrinking; hold one

        return PaceAction.Advance;
    }
}
