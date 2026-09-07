namespace Emerald.Core;

public enum TidalLockState
{
    /// <summary>Not in use.</summary>
    Off,

    /// <summary>Armed, waiting for the capture deck to start recording.</summary>
    Armed,

    /// <summary>Recording has started; the delay is counting down before playout cues.</summary>
    CountingDown,

    /// <summary>The delayed feed is going out of the TX.</summary>
    OnAir,

    /// <summary>Something stopped it. <see cref="TidalLock.Detail"/> says what.</summary>
    Failed,
}

/// <summary>
/// Tidal lock: the capture deck's receiver, put to air a fixed time later.
///
/// The two halves of it live in different windows — the capture deck starts the recording,
/// the playback deck puts it on the transmitter — and this is what joins them. It carries no
/// video and does no work: it holds the handful of facts the playback deck needs (which file
/// the recorder is writing, at what timecode it started, how far behind to run) and tells it
/// when they change.
///
/// The delay itself is not held anywhere. The recorder writes a continuous MPEG-TS as it
/// records, and playout opens that file a minute later and reads it at transmission rate —
/// so the gap between the write head and the read head <i>is</i> the delay, and it stays put
/// on its own because the card paces the reader. A minute of 1080p raw would be six
/// gigabytes of memory; this is a file handle and a head start.
/// </summary>
public sealed class TidalLock
{
    /// <summary>The default gap between the receiver and the transmitter.</summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMinutes(1);

    /// <summary>The one instance the two decks talk through.</summary>
    public static TidalLock Shared { get; } = new();

    private readonly object _gate = new();

    private TidalLockState _state = TidalLockState.Off;

    public TidalLockState State { get { lock (_gate) return _state; } }

    /// <summary>How far behind the receiver the transmitter runs.</summary>
    public TimeSpan Delay { get; private set; } = DefaultDelay;

    /// <summary>The continuous file the recorder is writing, once there is one.</summary>
    public string? DelayFile { get; private set; }

    /// <summary>The station timecode the recording rolled at.</summary>
    public Timecode? RecordingStarted { get; private set; }

    /// <summary>The timecode playout should cue on: <see cref="RecordingStarted"/> + the delay.</summary>
    public Timecode? CueAt { get; private set; }

    /// <summary>Whatever the operator should be told about the current state.</summary>
    public string Detail { get; private set; } = "";

    /// <summary>Raised on the caller's thread whenever anything above changes.</summary>
    public event Action<TidalLock>? Changed;

    /// <summary>
    /// The playback deck asks for the lock. Nothing happens until the capture deck rolls —
    /// arming only says that when it does, its output should go to air a minute later.
    /// </summary>
    public void Arm(TimeSpan delay)
    {
        lock (_gate)
        {
            Delay = delay <= TimeSpan.Zero ? DefaultDelay : delay;
            _state = TidalLockState.Armed;
            DelayFile = null;
            RecordingStarted = null;
            CueAt = null;
            Detail = $"Armed - waiting for the capture deck to record. {Describe(Delay)} behind.";
        }

        Changed?.Invoke(this);
    }

    public void Disarm(string reason = "Tidal lock released.")
    {
        lock (_gate)
        {
            _state = TidalLockState.Off;
            DelayFile = null;
            RecordingStarted = null;
            CueAt = null;
            Detail = reason;
        }

        Changed?.Invoke(this);
    }

    /// <summary>
    /// The capture deck has rolled. This is the moment the countdown starts from, and the
    /// only place the cue time is worked out — the playback deck reads it rather than
    /// computing its own, so the two cannot disagree about when the minute is up.
    /// </summary>
    public void RecordingRolled(string delayFile, Timecode startedAt)
    {
        lock (_gate)
        {
            if (_state != TidalLockState.Armed) return;

            DelayFile = delayFile;
            RecordingStarted = startedAt;
            CueAt = startedAt.AddWrapping((long)Math.Round(Delay.TotalSeconds * startedAt.Rate));
            _state = TidalLockState.CountingDown;
            Detail = $"Recording rolled at {startedAt}; on air at {CueAt}.";
        }

        Changed?.Invoke(this);
    }

    /// <summary>The delayed feed has reached the transmitter.</summary>
    public void WentToAir()
    {
        lock (_gate)
        {
            if (_state != TidalLockState.CountingDown) return;

            _state = TidalLockState.OnAir;
            Detail = $"On air, {Describe(Delay)} behind the receiver.";
        }

        Changed?.Invoke(this);
    }

    public void Fail(string detail)
    {
        lock (_gate)
        {
            if (_state == TidalLockState.Off) return;

            _state = TidalLockState.Failed;
            Detail = detail;
        }

        Changed?.Invoke(this);
    }

    /// <summary>The capture deck has stopped recording; there is nothing more to delay.</summary>
    public void RecordingStopped()
    {
        lock (_gate)
        {
            if (_state is TidalLockState.Off or TidalLockState.Failed) return;

            _state = TidalLockState.Armed;
            DelayFile = null;
            RecordingStarted = null;
            CueAt = null;
            Detail = "Recording stopped - armed again, waiting for the next one.";
        }

        Changed?.Invoke(this);
    }

    /// <summary>Frames of delay at a given rate, which is what the playout is cued on.</summary>
    public long DelayFrames(int frameRate) =>
        frameRate <= 0 ? 0 : (long)Math.Round(Delay.TotalSeconds * frameRate);

    public static string Describe(TimeSpan delay) =>
        delay.TotalMinutes >= 1 && delay.TotalSeconds % 60 == 0
            ? $"{delay.TotalMinutes:F0} min"
            : $"{delay.TotalSeconds:F0} s";
}
