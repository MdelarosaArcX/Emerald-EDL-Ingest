namespace Emerald.Core;

/// <summary>What one stage of the chain is doing, in the shape a strip across a page wants.</summary>
public readonly record struct StageState(string Headline, string Detail, LogLevel Level)
{
    public static readonly StageState Idle = new("idle", "", LogLevel.Info);
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
    }

    public void ClearEdl() => Edl = StageState.Idle;
    public void ClearOnAir() => OnAir = StageState.Idle;
    public void ClearIngest() => Ingest = StageState.Idle;
}
