namespace Emerald.Core;

/// <summary>
/// The picture the recorder is taking in, right now, for any window that wants to show it.
///
/// The capture deck converts a preview of every frame it records for its own monitor. Live Edit
/// lives in an assembly that knows nothing about the recorder, so the deck hands that same
/// preview to this, and Live Edit reads it from here - the one picture, made once.
///
/// <b>Only the newest frame is kept.</b> This is a monitor: a frame that was replaced before
/// anyone drew it is older than the one that replaced it, and there is nothing to catch up on.
///
/// <b>Nothing is copied while nobody is watching.</b> The offer runs on the capture thread, so
/// with no watcher it returns at once and the recorder pays nothing for this.
/// </summary>
public sealed class RecordingPicture
{
    public static RecordingPicture Shared { get; } = new();

    private readonly object _gate = new();
    private byte[] _bgra = Array.Empty<byte>();
    private int _width;
    private int _height;
    private long _frame = -1;
    private long _sequence;
    private int _watchers;

    /// <summary>True while at least one window is showing the picture.</summary>
    public bool Watched => Volatile.Read(ref _watchers) > 0;

    private IDelayLine? _line;

    /// <summary>
    /// The capture deck's ready delay line: the last minutes of the recording, raw, each frame
    /// stamped with its place in it. Live Edit shows the segment still being recorded from it,
    /// since no file of that segment can be opened until the recorder closes it. Null when no
    /// line is being kept ready.
    ///
    /// Whoever reads it must take a claim on it first - it is let go when the recording stops.
    /// </summary>
    public IDelayLine? Line
    {
        get => Volatile.Read(ref _line);
        set => Volatile.Write(ref _line, value);
    }

    /// <summary>
    /// Starts showing the picture. Dispose what comes back to stop - the recorder goes back to
    /// copying nothing once the last watcher has gone.
    /// </summary>
    public IDisposable Watch()
    {
        Interlocked.Increment(ref _watchers);
        return new Watcher(this);
    }

    /// <summary>
    /// From the recorder, with each converted frame and its number in the recording. Must not
    /// block - it is called from the capture thread - and does not keep the buffer it is given.
    /// </summary>
    public void Offer(byte[] bgra, int width, int height, long recordingFrame)
    {
        if (!Watched) return;

        lock (_gate)
        {
            if (_bgra.Length != bgra.Length) _bgra = new byte[bgra.Length];

            Buffer.BlockCopy(bgra, 0, _bgra, 0, bgra.Length);
            _width = width;
            _height = height;
            _frame = recordingFrame;
            _sequence++;
        }
    }

    /// <summary>The recording has stopped: what is held is no longer the picture coming in.</summary>
    public void Clear()
    {
        lock (_gate)
        {
            _bgra = Array.Empty<byte>();
            _frame = -1;
            _sequence++;
        }
    }

    /// <summary>
    /// Hands the newest frame to <paramref name="use"/>, if there is one newer than
    /// <paramref name="seen"/>, and moves <paramref name="seen"/> on. Held under the lock for the
    /// call, so <paramref name="use"/> copies the pixels and returns - it must not keep them.
    /// </summary>
    /// <returns>True if <paramref name="use"/> was called.</returns>
    public bool TryRead(ref long seen, Action<byte[], int, int, long> use)
    {
        lock (_gate)
        {
            if (_sequence == seen || _bgra.Length == 0) return false;

            seen = _sequence;
            use(_bgra, _width, _height, _frame);
            return true;
        }
    }

    private sealed class Watcher(RecordingPicture picture) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                Interlocked.Decrement(ref picture._watchers);
        }
    }
}
