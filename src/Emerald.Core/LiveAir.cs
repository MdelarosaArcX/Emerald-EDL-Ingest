namespace Emerald.Core;

public enum LiveAirState
{
    /// <summary>Not in use.</summary>
    Off,

    /// <summary>Asked for, waiting for the capture deck to be recording so there is a feed to delay.</summary>
    Armed,

    /// <summary>The delay is filling; black is going out until it is full.</summary>
    CountingDown,

    /// <summary>The delayed feed - and whatever the timeline puts in it - is going out.</summary>
    OnAir,

    /// <summary>The recording stopped; the last of the delay is still playing out.</summary>
    Draining,

    /// <summary>Something stopped it. <see cref="LiveAir.Detail"/> says what.</summary>
    Failed,
}

/// <summary>
/// One instruction on Live Edit's timeline, as the transmitter needs it: what happens to air
/// when the delayed recording reaches a given frame.
///
/// In the recording's own frames - counted from its first frame, at its own rate - because that
/// is the number every frame in the delay carries. Live Edit's ruler is in frames of its day;
/// it converts before handing these over, so the transmitter never has to know what a day is.
/// </summary>
/// <param name="Id">Stable across edits, so a cue already played is never played twice.</param>
/// <param name="Mode">What it does. <see cref="IngestMode.Cut"/> has no media.</param>
/// <param name="MediaPath">The file to play to air; null for a cut.</param>
/// <param name="RecordingFrame">The frame of the recording it happens at.</param>
/// <param name="Frames">How many frames of air the media takes. Zero for a cut.</param>
/// <param name="Covers">How many frames of the recording it takes the place of (replace, overlay, fit) or removes (cut).</param>
/// <param name="Speed">The media's rate multiplier. 1 except for fit-to-fill.</param>
public sealed record LiveCue(
    string Id,
    IngestMode Mode,
    string? MediaPath,
    long RecordingFrame,
    long Frames,
    long Covers,
    double Speed = 1)
{
    public string Name => MediaPath is null ? "scene cut" : System.IO.Path.GetFileName(MediaPath);

    public long EndFrame => RecordingFrame + Covers;

    /// <summary>
    /// How much this cue changes the delay: an insert adds its length - the recording waits
    /// while it plays - and a cut takes its range away. Everything else lays over the recording
    /// and leaves the delay as it was.
    /// </summary>
    public long DelayChange => Mode switch
    {
        IngestMode.Insert => Frames,
        IngestMode.Cut => -Covers,
        _ => 0,
    };
}

/// <summary>
/// The rules for when a cue fires. Pure, so they can be tested without a card: the transmitter
/// asks this, frame by frame, with the recording frame it is about to send.
/// </summary>
public static class LiveCues
{
    /// <summary>
    /// The cue that should take over before <paramref name="recordingFrame"/> goes out, or null
    /// when that frame goes out as it is.
    ///
    /// An insert fires once the recording reaches its frame - it goes in before that frame. A
    /// cut fires when the frame about to go out is inside the range it removes. The modes that
    /// lay over the recording fire for a frame inside what they cover, so one reached late (a
    /// cue added just behind the transmitter, a resync) still plays the part that is left rather
    /// than nothing. The earliest such cue wins, and a cue in <paramref name="done"/> never fires
    /// again.
    /// </summary>
    public static LiveCue? Due(long recordingFrame, IEnumerable<LiveCue> cues, IReadOnlySet<string> done)
    {
        LiveCue? best = null;

        foreach (LiveCue cue in cues)
        {
            if (done.Contains(cue.Id) || cue.RecordingFrame > recordingFrame) continue;

            bool fires = cue.Mode switch
            {
                // Only at its own frame or just past it: an insert discovered a long way behind
                // the transmitter belongs to material that has already gone out, and playing it
                // now would put it in the wrong place.
                IngestMode.Insert => recordingFrame - cue.RecordingFrame <= InsertGrace,
                _ => recordingFrame < cue.EndFrame,
            };

            if (fires && (best is null || cue.RecordingFrame < best.RecordingFrame)) best = cue;
        }

        return best;
    }

    /// <summary>
    /// How far past its frame an insert may be found and still play: a few frames, for a
    /// transmitter that skipped one to hold its delay at exactly that moment.
    /// </summary>
    public const long InsertGrace = 5;

    /// <summary>
    /// Cues whose media should be opened now, so the decoder is running before they fire:
    /// anything with media, not done, starting within <paramref name="lookahead"/> frames.
    /// </summary>
    public static IEnumerable<LiveCue> ToPrepare(long recordingFrame, long lookahead,
                                                 IEnumerable<LiveCue> cues, IReadOnlySet<string> done) =>
        cues.Where(c => c.MediaPath is not null && !done.Contains(c.Id) &&
                        c.RecordingFrame + Math.Max(c.Covers, 1) > recordingFrame &&
                        c.RecordingFrame - recordingFrame <= lookahead);

    /// <summary>The delay once every cue has played: the base plus what inserts add, less what cuts remove.</summary>
    public static long DelayWith(long baseFrames, IEnumerable<LiveCue> cues) =>
        baseFrames + cues.Sum(c => c.DelayChange);
}

/// <summary>Where the transmitter is: a frame of the recording, or a frame into a cue's media.</summary>
/// <param name="RecordingFrame">The last recording frame sent, or about to resume after a cue.</param>
/// <param name="CueId">The cue whose media is going out, or null when the recording is.</param>
/// <param name="CueOffset">Frames into that cue's media.</param>
public readonly record struct LiveAirPosition(long RecordingFrame, string? CueId, long CueOffset);

/// <summary>
/// Going live from Live Edit: the capture deck's receiver put to air a delay later, with the
/// timeline's ingest media and scene cuts played into it as they come up - an EDL that the
/// timeline writes.
///
/// It is the playback deck's tidal lock in shape - armed here, a delay line opened by the
/// capture deck, a transmitter that reports back - and deliberately <b>not</b> the same object.
/// The playback deck's tidal lock is untouched by any of this: the two can be used one at a
/// time or side by side on different ports, and neither can change the other's state.
///
/// <b>Room for inserts.</b> A delay line is a fixed ring. An insert holds the recording back
/// while it plays, so the delay grows by its length, and the ring has to have been made big
/// enough for that at the start. <see cref="Headroom"/> is that extra, chosen when going live;
/// an insert that would need more is refused in Live Edit rather than lapping the ring on air.
/// </summary>
public sealed class LiveAir
{
    public static LiveAir Shared { get; } = new();

    private readonly object _gate = new();

    private LiveAirState _state = LiveAirState.Off;

    public LiveAirState State { get { lock (_gate) return _state; } }

    /// <summary>The delay chosen when going live, before any insert or cut changes it.</summary>
    public TimeSpan Delay { get; private set; }

    /// <summary>Where the transmission starts in the recording. See <see cref="DelayStart"/>.</summary>
    public DelayStart Start { get; private set; }

    /// <summary>Settles the delay for a start from the first segment, once the line is known. See <see cref="TidalLock.Resolve"/>.</summary>
    public void Resolve(TimeSpan delay)
    {
        lock (_gate) Delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
    }

    /// <summary>How much more than the delay the ring holds, for inserts to grow it into.</summary>
    public TimeSpan Headroom { get; private set; }

    public uint BoardIndex { get; private set; }

    public IReadOnlyList<int> Ports { get; private set; } = Array.Empty<int>();

    /// <summary>The delay line, once the capture deck has opened one.</summary>
    public IDelayLine? Line { get; private set; }

    /// <summary>When the recording on air rolled - what its frame numbers count from.</summary>
    public DateTime? RecordingStartedAt { get; private set; }

    public string Detail { get; private set; } = "";

    public event Action<LiveAir>? Changed;

    /// <summary>True while anything is going out or about to.</summary>
    public bool Active => State is LiveAirState.CountingDown or LiveAirState.OnAir or LiveAirState.Draining;

    // ------------------------------------------------------------------ the timeline's cues

    private IReadOnlyList<LiveCue> _cues = Array.Empty<LiveCue>();

    /// <summary>
    /// The cues as Live Edit last laid them out. Replaced whole on every edit, so the transmitter
    /// always reads one consistent list.
    /// </summary>
    public IReadOnlyList<LiveCue> Cues
    {
        get { lock (_gate) return _cues; }
    }

    public void SetCues(IReadOnlyList<LiveCue> cues)
    {
        lock (_gate) _cues = cues;
    }

    private IReadOnlyList<AudioEdit> _audioEdits = Array.Empty<AudioEdit>();

    /// <summary>
    /// The levels set on the recording's own sound, in frames of the recording, as Live Edit
    /// last laid them out. Replaced whole on every edit, like the cues.
    /// </summary>
    public IReadOnlyList<AudioEdit> AudioEdits
    {
        get { lock (_gate) return _audioEdits; }
    }

    public void SetAudioEdits(IReadOnlyList<AudioEdit> edits)
    {
        lock (_gate) _audioEdits = edits;
    }

    // ------------------------------------------------------------------ where it is

    private LiveAirPosition? _position;
    private long _delayFrames;

    /// <summary>Where the transmitter is, every frame. Null when nothing is going out.</summary>
    public LiveAirPosition? Position
    {
        get { lock (_gate) return _position; }
    }

    /// <summary>The delay the transmitter is holding right now, in frames of the recording.</summary>
    public long DelayFrames
    {
        get { lock (_gate) return _delayFrames; }
    }

    public void SetPosition(LiveAirPosition position, long delayFrames)
    {
        lock (_gate)
        {
            _position = position;
            _delayFrames = delayFrames;
        }
    }

    private void ClearPosition()
    {
        _position = null;
        _delayFrames = 0;
    }

    // ------------------------------------------------------------------ transitions

    public void Arm(TimeSpan delay, TimeSpan headroom, uint boardIndex, IReadOnlyList<int> ports,
                    DelayStart start = DelayStart.Fixed)
    {
        lock (_gate)
        {
            Delay = delay < TimeSpan.Zero ? TimeSpan.Zero : delay;
            Headroom = headroom < TimeSpan.Zero ? TimeSpan.Zero : headroom;
            Start = start;
            BoardIndex = boardIndex;
            Ports = ports.ToArray();
            Line = null;
            RecordingStartedAt = null;
            ClearPosition();
            _state = LiveAirState.Armed;
            Detail = $"Going live - waiting for the capture deck to record. {TidalLock.DescribeStart(start, Delay)}, " +
                     $"room for {TidalLock.Describe(Headroom)} of inserts.";
        }

        Announce(LiveAirState.Armed, LogLevel.Ok);
    }

    /// <summary>The capture deck has opened a line for this. From here the transmitter can start.</summary>
    public void RecordingRolled(IDelayLine line, DateTime? recordingStartedAt)
    {
        lock (_gate)
        {
            if (_state != LiveAirState.Armed) return;

            Line = line;
            RecordingStartedAt = recordingStartedAt;
            _state = LiveAirState.CountingDown;
            Detail = $"Recording is feeding the delay; on air once {TidalLock.Describe(Delay)} has been buffered.";
        }

        Announce(LiveAirState.CountingDown, LogLevel.Ok);
    }

    public void WentToAir()
    {
        lock (_gate)
        {
            if (_state != LiveAirState.CountingDown) return;

            _state = LiveAirState.OnAir;
            Detail = $"Live from Live Edit, {TidalLock.Describe(Delay)} behind the receiver.";
        }

        Announce(LiveAirState.OnAir, LogLevel.Ok);
    }

    /// <summary>The recording stopped. What is in the line still plays out.</summary>
    public void RecordingStopped()
    {
        LiveAirState now;

        lock (_gate)
        {
            switch (_state)
            {
                case LiveAirState.OnAir:
                case LiveAirState.CountingDown:
                    _state = LiveAirState.Draining;
                    Detail = "Recording stopped - playing out what is left of the delay.";
                    break;

                default:
                    return;
            }

            now = _state;
        }

        Announce(now, LogLevel.Warn);
    }

    public void Fail(string detail)
    {
        lock (_gate)
        {
            if (_state == LiveAirState.Off) return;

            _state = LiveAirState.Failed;
            Line = null;
            ClearPosition();
            Detail = detail;
        }

        Announce(LiveAirState.Failed, LogLevel.Error);
    }

    /// <summary>Ends it: the operator stopped, the line ran out, or the window closed.</summary>
    public void Stop(string reason)
    {
        lock (_gate)
        {
            if (_state == LiveAirState.Off) return;

            _state = LiveAirState.Off;
            Line = null;
            RecordingStartedAt = null;
            _cues = Array.Empty<LiveCue>();
            _audioEdits = Array.Empty<AudioEdit>();
            ClearPosition();
            Detail = reason;
        }

        Announce(LiveAirState.Off, LogLevel.Warn);
    }

    private void Announce(LiveAirState state, LogLevel level)
    {
        ActivityLog.Shared.Write(LogSource.LiveEdit, level, $"LIVE  {Detail}",
                                 @event: $"liveair.{state.ToString().ToLowerInvariant()}");

        Changed?.Invoke(this);
    }
}
