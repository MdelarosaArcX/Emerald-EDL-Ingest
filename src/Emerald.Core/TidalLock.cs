namespace Emerald.Core;

/// <summary>
/// Where a delayed transmission starts in the recording.
///
/// A fixed delay starts wherever the recording is, that far behind it. The other two start at
/// the recording's first frame - the beginning of its first segment - so the programme goes out
/// from its top rather than from some point a delay ago, and the delay is whatever that makes it.
/// </summary>
public enum DelayStart
{
    /// <summary>A fixed delay, starting that far behind the recorder.</summary>
    Fixed,

    /// <summary>
    /// From the first frame of the recording, straight away: the delay is however long the
    /// recording has been running when the transmission starts.
    /// </summary>
    FromFirstSegment,

    /// <summary>
    /// From the first frame of the recording, once its first segment has been recorded: the
    /// delay is a segment's length - or, armed after that, however long it has been running,
    /// which is starting from the first segment straight away.
    /// </summary>
    WhenFirstSegmentReady,
}

public enum TidalLockState
{
    /// <summary>Not in use.</summary>
    Off,

    /// <summary>Armed, waiting for the capture deck to start recording.</summary>
    Armed,

    /// <summary>Recording has started; the delay is filling before anything goes to air.</summary>
    CountingDown,

    /// <summary>The delayed feed is going out of the TX.</summary>
    OnAir,

    /// <summary>
    /// The recording has stopped, and the delay is still playing out what is left of it.
    /// A whole delay's worth of programme is still in the line and has not been to air yet —
    /// stopping the recording deliberately does not stop the transmission.
    /// </summary>
    Draining,

    /// <summary>Something stopped it. <see cref="TidalLock.Detail"/> says what.</summary>
    Failed,
}

/// <summary>
/// Tidal lock: the capture deck's receiver, put to air a fixed time later.
///
/// The two halves of it live in different windows — the capture deck starts the recording,
/// the playback deck puts it on the transmitter — and this is what joins them. It carries no
/// video and does no work: it holds the handful of facts each half needs about the other,
/// and tells them when those change.
///
/// The delay itself lives in the line, which is a ring of raw frames written by the recorder
/// and read by the transmitter a fixed number of frames behind. This class only ever sees it
/// through <see cref="IDelayLine"/>, because the line belongs to Emerald.Video and this
/// project depends on nothing.
///
/// The countdown is measured in <b>frames buffered</b> rather than against the station clock.
/// That is not a detail: the receiver's own cadence is the truth about how much delay exists,
/// so the countdown stays honest through a timecode outage, and it cannot claim the feed is
/// ready a moment before the frames to fill it have actually arrived.
/// </summary>
public sealed class TidalLock
{
    /// <summary>The default gap between the receiver and the transmitter.</summary>
    /// <summary>
    /// The delay offered before anybody chooses one.
    ///
    /// Two minutes. This is the compliance window — how long somebody has to notice something
    /// and stop it reaching air — so the default should be the one a plant would want if nobody
    /// thought about it, not the smallest one that works.
    /// </summary>
    public static readonly TimeSpan DefaultDelay = TimeSpan.FromMinutes(2);

    /// <summary>The one instance the two decks talk through.</summary>
    public static TidalLock Shared { get; } = new();

    private readonly object _gate = new();

    private TidalLockState _state = TidalLockState.Off;

    public TidalLockState State { get { lock (_gate) return _state; } }

    /// <summary>How far behind the receiver the transmitter runs.</summary>
    public TimeSpan Delay { get; private set; } = DefaultDelay;

    /// <summary>Where the transmission starts in the recording. See <see cref="DelayStart"/>.</summary>
    public DelayStart Start { get; private set; }

    /// <summary>The line itself, once the recorder has rolled and there is one.</summary>
    public IDelayLine? Line { get; private set; }

    /// <summary>The station timecode the recording rolled at, when there was a clock to read.</summary>
    public Timecode? RecordingStarted { get; private set; }

    /// <summary>
    /// When the feed is expected to reach air. Reported, and useful in a log, but not what
    /// anything waits on — the line filling is.
    /// </summary>
    public Timecode? CueAt { get; private set; }

    /// <summary>Whatever the operator should be told about the current state.</summary>
    public string Detail { get; private set; } = "";

    /// <summary>Raised on the caller's thread whenever anything above changes.</summary>
    public event Action<TidalLock>? Changed;

    /// <summary>
    /// Writes the transition down and then tells whoever is listening, in that order and both
    /// outside the lock.
    ///
    /// The lock used to narrate only through <see cref="Changed"/>, which meant the record of
    /// it was whatever a subscriber happened to write — and the only subscriber that wrote
    /// anything was the playback deck. Close that window and a delay that never reached air
    /// left no trace anywhere, which is the one thing about this feature nobody can afford to
    /// be unable to reconstruct afterwards.
    /// </summary>
    private void Announce(TidalLockState state, LogLevel level)
    {
        ActivityLog.Shared.Write(LogSource.TidalLock, level, Detail,
                                 @event: $"tidallock.{state.ToString().ToLowerInvariant()}");

        Changed?.Invoke(this);
    }

    // ------------------------------------------------------------------ the countdown

    /// <summary>Frames the recorder has put into the line.</summary>
    public long FramesBuffered => Line?.FramesWritten ?? 0;

    /// <summary>
    /// The delay in frames, at the line's rate. This lock's own, and not the line's size: the
    /// capture deck keeps one line filling for as long as it records, sized for the longest delay
    /// anything might ask for, and every lock reading it reads its own distance behind.
    /// </summary>
    public long TargetFrames => Line is { } line ? DelayFrames(line.FrameRate) : 0;

    /// <summary>
    /// Frames still needed before the delay is full and the feed can start - none at all when
    /// the line was already filling before this was armed, which is the ordinary case now.
    /// </summary>
    public long FramesToAir =>
        Line is { } line ? Math.Max(0, TargetFrames - line.FramesWritten) : 0;

    /// <summary>
    /// How full the delay is, 0 to 1, for a progress bar.
    ///
    /// A line with no delay to fill is complete the moment it exists, so it reads full rather
    /// than empty — a bar stuck at nothing while the feed is already on air would be saying
    /// the opposite of what is happening.
    /// </summary>
    public double FillFraction =>
        Line is not { } line ? 0
        : TargetFrames <= 0 ? 1
        : Math.Clamp(line.FramesWritten / (double)TargetFrames, 0, 1);

    /// <summary>How long until the feed reaches air, from the line's own frame rate.</summary>
    public TimeSpan TimeToAir =>
        Line is { FrameRate: > 0 } line
            ? TimeSpan.FromSeconds(FramesToAir / (double)line.FrameRate)
            : Delay;

    // ------------------------------------------------------------------ transitions

    /// <summary>
    /// The playback deck asks for the lock. Nothing happens until the capture deck rolls —
    /// arming only says that when it does, its output should go to air a delay later.
    ///
    /// <b>Zero is a delay.</b> It means the transmitter takes the receiver's frames as they
    /// arrive, which is what an operator wants when re-arming part-way through a recording
    /// that is already running rather than waiting out another fill. Only a negative — which
    /// nothing can mean — falls back to the default.
    /// </summary>
    public void Arm(TimeSpan delay, DelayStart start = DelayStart.Fixed)
    {
        lock (_gate)
        {
            Delay = delay < TimeSpan.Zero ? DefaultDelay : delay;
            Start = start;
            _state = TidalLockState.Armed;
            Line = null;
            RecordingStarted = null;
            CueAt = null;
            Detail = $"Armed - waiting for the capture deck to record. {DescribeStart(start, Delay)}.";
        }

        Announce(TidalLockState.Armed, LogLevel.Ok);
    }

    /// <summary>
    /// Settles the delay for a lock that starts from the first segment, once the line it will
    /// read is known: how far behind the recorder its first frame is, or a segment's length,
    /// whichever the start asks for. Said before the line is handed over, so the transmitter is
    /// made with the delay it will actually hold.
    /// </summary>
    public void Resolve(TimeSpan delay)
    {
        lock (_gate) Delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    /// <summary>
    /// The delay, in frames, that starts a transmission where <paramref name="start"/> asks.
    ///
    /// A transmitter starts the delay behind the recorder - so a delay equal to everything
    /// recorded so far starts it at the recording's first frame, and a delay of a segment's
    /// length on a recording younger than that waits for the segment and then starts at its
    /// first frame. That is the whole of "from the first segment": choosing the delay that puts
    /// the start there.
    /// </summary>
    /// <param name="fixedFrames">The delay chosen, for <see cref="DelayStart.Fixed"/>.</param>
    /// <param name="writtenFrames">Frames the line already holds, counted from its first.</param>
    /// <param name="segmentFrames">A segment's length.</param>
    /// <param name="reachFrames">
    /// How far back the line can still reach. A recording longer than that has had its first
    /// frames overwritten, and the start is the oldest frame still held.
    /// </param>
    public static long FramesFor(DelayStart start, long fixedFrames, long writtenFrames,
                                 long segmentFrames, long reachFrames)
    {
        long reach = Math.Max(0, reachFrames);

        return start switch
        {
            DelayStart.FromFirstSegment => Math.Min(Math.Max(0, writtenFrames), reach),
            DelayStart.WhenFirstSegmentReady => Math.Min(Math.Max(segmentFrames, writtenFrames), reach),
            _ => fixedFrames,
        };
    }

    /// <summary>Where a lock starts, in words, for a log line or a status.</summary>
    public static string DescribeStart(DelayStart start, TimeSpan delay) => start switch
    {
        DelayStart.FromFirstSegment => "from the first segment of the recording",
        DelayStart.WhenFirstSegmentReady => "from the first segment, as soon as it is recorded",
        _ => $"{Describe(delay)} behind",
    };

    public void Disarm(string reason = "Tidal lock released.")
    {
        lock (_gate)
        {
            _state = TidalLockState.Off;
            Line = null;
            RecordingStarted = null;
            CueAt = null;
            Detail = reason;
        }

        Announce(TidalLockState.Off, LogLevel.Warn);
    }

    /// <summary>
    /// The capture deck has rolled and there is a line to fill. From here the playback deck
    /// watches the line rather than the clock.
    /// </summary>
    public void RecordingRolled(IDelayLine line, Timecode? startedAt)
    {
        lock (_gate)
        {
            if (_state != TidalLockState.Armed) return;

            Line = line;
            RecordingStarted = startedAt;

            CueAt = startedAt is { Rate: > 0 } at
                ? at.AddWrapping((long)Math.Round(Delay.TotalSeconds * at.Rate))
                : null;

            _state = TidalLockState.CountingDown;

            Detail = CueAt is { } cue
                ? $"Recording rolled at {startedAt}; on air at {cue}."
                : $"Recording rolled; on air once {Describe(Delay)} has been buffered.";
        }

        Announce(TidalLockState.CountingDown, LogLevel.Ok);
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

        Announce(TidalLockState.OnAir, LogLevel.Ok);
    }

    public void Fail(string detail)
    {
        lock (_gate)
        {
            if (_state == TidalLockState.Off) return;

            _state = TidalLockState.Failed;
            Detail = detail;
        }

        Announce(TidalLockState.Failed, LogLevel.Error);
    }

    /// <summary>
    /// The capture deck has stopped recording.
    ///
    /// If anything had reached air, this is <b>not</b> the end: a whole delay's worth of
    /// programme is still in the line, and cutting it off would lose the last minute of the
    /// recording. The lock goes to <see cref="TidalLockState.Draining"/> and the transmitter
    /// keeps going until the line runs out. Only a countdown that never made it to air ends
    /// straight away, because there is nothing worth draining.
    /// </summary>
    public void RecordingStopped()
    {
        TidalLockState stopped;

        lock (_gate)
        {
            switch (_state)
            {
                case TidalLockState.OnAir:
                    _state = TidalLockState.Draining;
                    Detail = $"Recording stopped - playing out the last {Describe(Delay)}.";
                    break;

                case TidalLockState.CountingDown:
                    _state = TidalLockState.Armed;
                    Line = null;
                    RecordingStarted = null;
                    CueAt = null;
                    Detail = "Recording stopped before the delay filled - armed again.";
                    break;

                default:
                    return;
            }

            stopped = _state;
        }

        Announce(stopped, LogLevel.Warn);
    }

    /// <summary>The line has run out; the last of the recording has been transmitted.</summary>
    public void DrainComplete()
    {
        lock (_gate)
        {
            if (_state != TidalLockState.Draining) return;

            _state = TidalLockState.Armed;
            Line = null;
            RecordingStarted = null;
            CueAt = null;
            Detail = "The delay has played out - armed again, waiting for the next recording.";
        }

        Announce(TidalLockState.Armed, LogLevel.Ok);
    }

    /// <summary>Frames of delay at a given rate.</summary>
    public long DelayFrames(int frameRate) =>
        frameRate <= 0 ? 0 : (long)Math.Round(Delay.TotalSeconds * frameRate);

    public static string Describe(TimeSpan delay) =>
        delay <= TimeSpan.Zero ? "no delay"
        : delay.TotalMinutes >= 1 && delay.TotalSeconds % 60 == 0
            ? $"{delay.TotalMinutes:F0} min"
            : $"{delay.TotalSeconds:F0} s";
}
