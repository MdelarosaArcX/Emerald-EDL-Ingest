namespace Emerald.Core;

/// <summary>
/// Keeping a recording's audio in step with its picture when frames are being dropped.
///
/// <b>Why dropping a frame can break lip sync.</b> The recorder hands ffmpeg two raw pipes — a
/// rawvideo stream and a PCM stream — and neither carries a timestamp. ffmpeg therefore assumes
/// a constant rate on each: one video frame is 1/25th of a second, 1920 samples of audio is
/// 1/25th of a second. The alignment between them is nothing more than how many of each were
/// written.
///
/// So the two must be written in lockstep. A slot whose video is dropped because the encoder is
/// behind, but whose audio is written anyway, adds a frame of audio against no picture — and the
/// file comes out with audio 40 ms longer than video for every frame lost. A recording that
/// dropped a few hundred frames over an hour has audio seconds ahead of the picture, drifting
/// further the whole way through, which is what an operator sees as sync going little by little.
///
/// The rule is simply: write as much audio as there is video, and no more. Stated here rather
/// than in the slot loop because it is the lip-sync guarantee, and a one-line arithmetic slip in
/// a real-time loop is invisible until somebody watches a recording back.
/// </summary>
public static class CaptureSync
{
    /// <summary>
    /// How many chunks of audio to write for the slot just handled.
    ///
    /// Normally one: a slot's video went in, so its audio follows. Zero when that slot's video
    /// was dropped — the audio goes with it, because a frame of sound with no picture is what
    /// moves them apart. More than one only when a previous slot's audio could not be queued and
    /// the two have fallen out of step, in which case this catches up.
    /// </summary>
    /// <param name="videoFramesIn">Video frames handed to the encoder so far.</param>
    /// <param name="audioChunksIn">Frames' worth of audio handed to it so far.</param>
    /// <param name="catchUpLimit">
    /// Most chunks to write in one slot while catching up. Without a limit, an audio pipe that
    /// was blocked for a second would try to write a second of audio in one slot and stall the
    /// loop that is reading the receiver — trading a sync error for lost frames.
    /// </param>
    public static int AudioChunksDue(long videoFramesIn, long audioChunksIn, int catchUpLimit = 3)
    {
        long behind = videoFramesIn - audioChunksIn;

        if (behind <= 0) return 0;

        return (int)Math.Min(behind, Math.Max(1, catchUpLimit));
    }

    /// <summary>
    /// How far out of step the two streams are, in frames, for the record.
    ///
    /// Positive means audio is behind the picture — frames of sound that were owed and never
    /// written. Negative should not happen: it would mean audio was written for slots whose video
    /// was not, which is the fault this exists to prevent.
    /// </summary>
    public static long Imbalance(long videoFramesIn, long audioChunksIn) =>
        videoFramesIn - audioChunksIn;

    /// <summary>
    /// How far apart the finished file's two streams would be, in milliseconds.
    ///
    /// The figure that matters to somebody watching it back: an imbalance of frames is a
    /// lip-sync error of that many frame periods.
    /// </summary>
    public static double DriftMs(long videoFramesIn, long audioChunksIn, int frameRate) =>
        frameRate <= 0 ? 0 : Imbalance(videoFramesIn, audioChunksIn) * 1000.0 / frameRate;
}
