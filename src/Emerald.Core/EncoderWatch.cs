namespace Emerald.Core;

/// <summary>What has just changed about the encoder, if anything.</summary>
public enum EncoderState
{
    /// <summary>Nothing worth saying.</summary>
    Running,

    /// <summary>It has refused every frame for long enough to be a fault rather than a hiccup.</summary>
    Stalled,

    /// <summary>It was stalled, and has taken a frame again.</summary>
    Recovered,
}

/// <summary>
/// Notices an encoder that is alive and no longer recording.
///
/// <b>Why watching for ffmpeg to exit is not enough.</b> When a disk fills, ffmpeg ends the output
/// that failed and the process stays up, with its pipes full. On 3 November that cost four minutes:
/// every frame was dropped, from 11:44 until somebody pressed Stop at 11:48, and the only sign was a
/// count in the log. The reason ffmpeg gave was printed after the recording had ended.
///
/// Kept apart from the slot loop because that needs a card to run, and this is only counting.
/// </summary>
public sealed class EncoderWatch
{
    /// <summary>Long enough that a slow disk or an encoder starting up does not raise the alarm.</summary>
    public const int StallSeconds = 10;

    /// <summary>A recording writes about 70 GB an hour, so this is a little under ten minutes.</summary>
    public const long LowDiskBytes = 10L << 30;

    private readonly int _stallFrames;
    private int _refused;
    private bool _stalled;
    private int _diskWarned;

    public EncoderWatch(int frameRate) => _stallFrames = Math.Max(1, frameRate) * StallSeconds;

    /// <summary>One frame offered to the encoder, and whether it was taken.</summary>
    public EncoderState Note(bool taken)
    {
        if (taken)
        {
            _refused = 0;

            if (!_stalled) return EncoderState.Running;

            _stalled = false;
            return EncoderState.Recovered;
        }

        if (_stalled || ++_refused < _stallFrames) return EncoderState.Running;

        _stalled = true;
        return EncoderState.Stalled;
    }

    /// <summary>
    /// True the first time the disk is found low. Said once, not every thirty seconds until it
    /// fills, and said again only if the space came back and then went.
    /// </summary>
    public bool NoteDiskSpace(long freeBytes)
    {
        if (freeBytes >= LowDiskBytes)
        {
            Volatile.Write(ref _diskWarned, 0);
            return false;
        }

        return Interlocked.Exchange(ref _diskWarned, 1) == 0;
    }
}
