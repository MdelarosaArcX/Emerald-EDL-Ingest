namespace Emerald.Core;

/// <summary>What one stage of the chain is doing, in the shape a strip across a page wants.</summary>
public readonly record struct StageState(string Headline, string Detail, LogLevel Level)
{
    public static readonly StageState Idle = new("idle", "", LogLevel.Info);
}

/// <summary>
/// The frame counts on both sides of the plant, so a drop can be seen as a number rather than
/// suspected from a picture.
///
/// <see cref="Recorded"/> is the recorder's own count of frames taken off the receiver.
/// <see cref="NotEncoded"/> is how many of those never reached the encoder because it could
/// not keep up; <see cref="NotDelayed"/> the same for the delay ring. On the other side,
/// <see cref="Aired"/> is what the transmitter has actually sent, <see cref="Repeated"/> how
/// often it had nothing new and held the last picture, and <see cref="Skipped"/> how many
/// frames it dropped to shed drift. A plant with nothing wrong reads: aired = recorded minus
/// the delay, everything else zero.
/// </summary>
public readonly record struct FrameLedger(
    long Recorded,
    long NotEncoded,
    long NotDelayed,
    long Aired,
    long Repeated,
    long Skipped,
    long Resyncs)
{
    public static readonly FrameLedger Empty = default;

    /// <summary>Frames lost on the way to the disk.</summary>
    public long LostToDisk => NotEncoded;

    /// <summary>Frames that existed at the receiver and were never put to air.</summary>
    public long LostToAir => NotDelayed + Skipped;
}

/// <summary>
/// The plant right now, from the receiver to the transmitter.
///
/// The monitoring page answers two different questions and they need two different things.
/// "What happened" is <see cref="ActivityLog"/> — a stream, ordered, kept. "What is happening"
/// is this: a handful of fields, overwritten, read on a timer.
///
/// Deriving the second from the first was the obvious idea and is a bad one. A strip built by
/// replaying log lines is a strip that lies the moment a line is missed or a module starts
/// while the page is closed — and a status panel that says a recording is running when it
/// stopped ten minutes ago is worse than no panel.
///
/// So each stage writes its own state at the same moments it narrates. <see cref="TidalLock"/>
/// already works exactly this way and is read directly rather than mirrored here.
///
/// Every field is written from a module thread and read from the UI timer, so each is guarded
/// by the one lock. They are small and rarely written; contention is not a consideration.
/// </summary>
public sealed class PipelineState
{
    /// <summary>The application's one view of the plant.</summary>
    public static PipelineState Shared { get; } = new();

    private readonly object _gate = new();

    private StageState _capture = StageState.Idle;
    private StageState _edl = StageState.Idle;
    private StageState _air = StageState.Idle;
    private StageState _ingest = StageState.Idle;

    private bool _recording;

    private FrameLedger _frames;

    /// <summary>Raised whenever any stage changes, on the caller's thread. Subscribers marshal.</summary>
    public event Action? Changed;

    /// <summary>The receiver: recording or not, and what of.</summary>
    public StageState Capture
    {
        get { lock (_gate) return _capture; }
        set { Set(ref _capture, value); }
    }

    /// <summary>
    /// Whether the receiver is recording right now.
    ///
    /// <see cref="Capture"/> says the same thing in words, but words are for drawing and this
    /// is asked as a question: the playback deck offers "no delay" only when there is already
    /// a recording to take the delay against, because with nothing rolling it is an option to
    /// put a feed to air that does not exist yet.
    /// </summary>
    public bool Recording
    {
        get { lock (_gate) return _recording; }
        set
        {
            lock (_gate)
            {
                if (_recording == value) return;
                _recording = value;
            }

            Changed?.Invoke();
        }
    }


    /// <summary>
    /// The frame counts, read on the strip\x27s timer. Written once a second by each side, and
    /// deliberately without raising <see cref="Changed"/>: two writers a second across every
    /// subscriber would be a lot of redrawing for a number the timer picks up anyway.
    /// </summary>
    public FrameLedger Frames
    {
        get { lock (_gate) return _frames; }
    }

    /// <summary>The recorder\x27s side: what it has taken and what it could not hand on.</summary>
    public void SetRecorded(long recorded, long notEncoded, long notDelayed)
    {
        lock (_gate)
            _frames = _frames with { Recorded = recorded, NotEncoded = notEncoded, NotDelayed = notDelayed };
    }

    /// <summary>The transmitter\x27s side: what went out, and what it had to do to keep up.</summary>
    public void SetAired(long aired, long repeated, long skipped, long resyncs)
    {
        lock (_gate)
            _frames = _frames with { Aired = aired, Repeated = repeated, Skipped = skipped, Resyncs = resyncs };
    }

    /// <summary>A recording has ended; the next one starts its count from nothing.</summary>
    public void ClearRecorded()
    {
        lock (_gate) _frames = _frames with { Recorded = 0, NotEncoded = 0, NotDelayed = 0 };
    }

    public void ClearAired()
    {
        lock (_gate) _frames = _frames with { Aired = 0, Repeated = 0, Skipped = 0, Resyncs = 0 };
    }

    /// <summary>The EDL queue: how many are waiting and what is cued.</summary>
    public StageState Edl
    {
        get { lock (_gate) return _edl; }
        set { Set(ref _edl, value); }
    }

    /// <summary>The transmitter: what is on it.</summary>
    public StageState OnAir
    {
        get { lock (_gate) return _air; }
        set { Set(ref _air, value); }
    }

    /// <summary>The ingest queue: what is booked and when the next one rolls.</summary>
    public StageState Ingest
    {
        get { lock (_gate) return _ingest; }
        set { Set(ref _ingest, value); }
    }

    private void Set(ref StageState field, StageState value)
    {
        lock (_gate)
        {
            if (field == value) return;   // a record struct: this is a real comparison
            field = value;
        }

        // Outside the lock. A subscriber that took another lock while holding this one is how
        // a UI thread and a capture thread deadlock over a status label.
        Changed?.Invoke();
    }

    /// <summary>
    /// Puts a stage back to idle. Called when a module closes, because a strip that kept
    /// showing a dead window's last state would be exactly the lie this design avoids.
    /// </summary>
    public void ClearCapture()
    {
        Recording = false;
        Capture = StageState.Idle;
        ClearRecorded();
    }

    public void ClearEdl() => Edl = StageState.Idle;
    public void ClearOnAir() => OnAir = StageState.Idle;
    public void ClearIngest() => Ingest = StageState.Idle;
}
