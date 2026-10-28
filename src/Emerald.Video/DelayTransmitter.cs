using Emerald.Core;
using Emerald.Deltacast;

namespace Emerald.Video;

public enum DelayPhase
{
    Opening,

    /// <summary>Holding black while the delay accumulates. This is the countdown.</summary>
    Filling,

    OnAir,

    /// <summary>The receiver has gone quiet; holding the last frame.</summary>
    Holding,

    /// <summary>The recording has stopped; playing out what is left.</summary>
    Draining,

    Finished,
    Failed,
}

public readonly record struct DelayStatus(
    DelayPhase Phase,
    string Message,
    long Buffered,
    long Target,
    long FramesOut);

/// <summary>Everything that went other than smoothly, for the log and for the operator.</summary>
public sealed class DelayCounters
{
    public long FramesOut;
    public long Repeats;
    public long Skips;
    public long Resyncs;

    public override string ToString() =>
        $"{FramesOut} out, {Repeats} repeated, {Skips} skipped, {Resyncs} resync(s)";
}

/// <summary>
/// Drains a <see cref="DelayLine"/> to a transmitter, a fixed number of frames behind the
/// recorder.
///
/// One thread does everything: read the slot the policy points at, hand it to the card, ask
/// again. <see cref="SdiOutput.PushFrame"/> blocks until the card frees a slot, so the
/// transmitter is the clock and there is no timer anywhere in the feature. That also means
/// this is the only thread that ever touches the output, which is what
/// <see cref="SdiOutput"/> requires.
///
/// The decisions are not made here. Every frame asks <see cref="DelayPacer.Decide"/> what to
/// do, so the behaviour that matters on air — underrun, drift, being lapped, draining after
/// the recording stops — is a pure function that can be tested without a card, and this is
/// three calls in a row.
/// </summary>
public sealed class DelayTransmitter : IDisposable
{
    private readonly uint _boardIndex;

    /// <summary>
    /// Every TX port this delay goes out of, on the one board.
    ///
    /// More than one is the ordinary case on a plant feeding a transmitter and a monitoring
    /// chain from the same delayed picture. They are driven as a group because there is one read
    /// of the ring per frame and it must reach all of them - two readers would be two delays.
    /// </summary>
    private readonly int[] _txChannels;
    private readonly DelayLine _line;
    private readonly VideoFormat _format;
    private readonly TimeSpan _delay;

    /// <summary>The delay in frames - this transmitter's, whatever size the line it reads is.</summary>
    private readonly long _targetFrames;

    private Thread? _worker;
    private CancellationTokenSource? _cts;

    public DelayCounters Counters { get; } = new();

    /// <summary>Raised on the pump thread; a UI subscriber must marshal.</summary>
    public event Action<DelayStatus>? Status;

    /// <summary>
    /// How many frames the recorder cuts each file at, so the pump can say which part of
    /// the recording is reaching air. Zero means it does not know and says nothing about it.
    /// </summary>
    private readonly long _segmentFrames;

    /// <summary>
    /// Throws <see cref="DelayLineException"/> when the receiver's format cannot be
    /// transmitted as it stands. Checked here as well as at arming time, because this is the
    /// last point before the card is opened and nothing beyond it can refuse safely.
    /// </summary>
    public DelayTransmitter(uint boardIndex, IReadOnlyList<int> txChannels, DelayLine line,
                            TimeSpan delay, int segmentSeconds = 0)
    {
        if (txChannels.Count == 0)
            throw new DelayLineException("No TX port was selected for the delayed feed.");

        if (!DelayFormats.TryMatch(line.Format, out VideoFormat? format, out string? problem))
            throw new DelayLineException(problem!);

        _boardIndex = boardIndex;
        _txChannels = txChannels.Distinct().ToArray();
        _line = line;
        _format = format!;
        _delay = delay;
        _targetFrames = (long)Math.Ceiling(delay.TotalSeconds * _format.FrameRate);
        _segmentFrames = segmentSeconds > 0 ? (long)segmentSeconds * _format.FrameRate : 0;

        _line.AddRef();
    }

    /// <summary>The ports, as a phrase - "TX1" or "TX1, TX3" - for anything that says where.</summary>
    private string Ports =>
        _txChannels.Length == 1
            ? $"TX{_txChannels[0]}"
            : string.Join(", ", _txChannels.Select(c => $"TX{c}"));

    public void Start()
    {
        if (_worker is { IsAlive: true }) return;

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        _worker = new Thread(() => Run(ct))
        {
            IsBackground = true,
            Name = "tidal lock TX",
            // A real-time loop, as the EDL's playout thread is: it should not lose the CPU
            // to background work.
            Priority = ThreadPriority.AboveNormal,
        };

        _worker.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();

        if (_worker is { IsAlive: true }) _worker.Join(TimeSpan.FromSeconds(5));

        _cts?.Dispose();
        _cts = null;
        _worker = null;
    }

    // ------------------------------------------------------------------ the pump

    private void Run(CancellationToken ct)
    {
        ISdiOutput? output = null;

        // Two buffers, used alternately. A read that fails half-way has already scribbled on
        // the one it was filling, so the frame being held for a repeat must never be that one.
        DelayFrameBuffer[] buffers = { _line.NewBuffer(), _line.NewBuffer() };
        int next = 0;
        DelayFrameBuffer? onAir = null;

        // The target is this transmitter's own delay, not the line's size: a line the capture deck
        // keeps filling for as long as it records can be far longer than the delay asked for.
        long target = _targetFrames;

        // Where the delay already is, when the line has been filling since before this was armed:
        // straight on air, the delay behind the recorder, rather than black while it fills again.
        long readSeq = Math.Max(0, _line.FramesWritten - target);
        long segmentOnAir = 0;
        bool firstPictureSaid = false;
        var phase = DelayPhase.Opening;

        try
        {
            Report(DelayPhase.Opening,
                $"Opening {Ports} on board {_boardIndex} at {_format.Name}...", 0);

            // Said before the open, so the decks can grey these ports out while they are ours.
            // A courtesy for the UI and not a lock - the driver decides whether the open works,
            // and that is the next line.
            SdiPorts.Shared.TryHold(PortOwner,
                _txChannels.Select(c => new SdiPort(_boardIndex, c)).ToArray());

            output = _txChannels.Length == 1
                ? SdiOutput.Open(_boardIndex, _txChannels[0], _format)
                : SdiOutputFan.Open(_boardIndex, _txChannels, _format);
            byte[] black = output.BlackFrame();

            while (!ct.IsCancellationRequested)
            {
                long written = _line.FramesWritten;

                PaceAction action = DelayPacer.Decide(
                    written, readSeq, target, _line.SlotCount,
                    _line.IsSealed, _line.SealedAt, _line.StalledMs, _format.FrameRate);

                switch (action)
                {
                    case PaceAction.Fill:
                        Announce(ref phase, DelayPhase.Filling,
                            $"Filling the delay: {written} of {target} frames.", written);

                        if (!output.PushFrame(black)) return;
                        continue;

                    case PaceAction.Finish:
                        Announce(ref phase, DelayPhase.Finished,
                            $"The delay has played out. {Counters}.", written);
                        return;

                    case PaceAction.Fail:
                        Announce(ref phase, DelayPhase.Failed,
                            "The receiver has stopped delivering frames. Cutting to black.", written);
                        return;

                    case PaceAction.Resync:
                        // The reader was left far enough behind that the frames it wanted
                        // have been overwritten. This is a visible cut, and the operator has
                        // to know the delay was re-established rather than maintained.
                        Counters.Resyncs++;
                        readSeq = Math.Max(0, written - target);
                        Announce(ref phase, DelayPhase.OnAir,
                            $"Fell behind the recorder and re-established the delay - " +
                            $"there is a cut in the output. ({Counters.Resyncs} so far.)", written);

                        if (!output.PushFrame(black)) return;
                        continue;

                    case PaceAction.Repeat:
                        // Nothing new to send. Repeat the picture, but not the sound: playing
                        // the same samples again clicks, and one silent frame does not.
                        Counters.Repeats++;

                        Announce(ref phase,
                            _line.StalledMs >= DelayPacer.HoldingAfterMs ? DelayPhase.Holding : phase,
                            "The receiver has paused; holding the last frame.", written);

                        if (!output.PushFrame(onAir?.Video ?? black)) return;
                        continue;
                }

                // Advance or Skip: both send the frame at the read position.
                DelayFrameBuffer into = buffers[next];

                switch (_line.TryRead(readSeq, into))
                {
                    case DelayReadResult.Ok:
                        break;

                    case DelayReadResult.EndOfLine:
                        Announce(ref phase, DelayPhase.Finished,
                            $"The delay has played out. {Counters}.", written);
                        return;

                    default:
                        // The policy and the line disagreed, which means the writer moved
                        // between the two. Ask again rather than guessing.
                        continue;
                }

                if (!PushWithAudio(output, into))
                {
                    // Which port, when there are several: "the card stopped accepting frames" is
                    // not actionable when four are open and one has gone.
                    string which = output is SdiOutputFan { FailedChannel: >= 0 } fan
                        ? $"TX{fan.FailedChannel}"
                        : Ports;

                    Report(DelayPhase.Failed, $"{which} stopped accepting frames.", written);
                    return;
                }

                // The frame just sent is this far into the recording - counted by the recorder,
                // not by the line, which may have been opened part-way through. What that means
                // for the operator is said here, before the counters move on.
                AnnounceRecordingPosition(into.RecordingFrame, ref segmentOnAir);
                PipelineState.Shared.SetAiredFrame(into.RecordingFrame);

                onAir = into;
                next ^= 1;
                readSeq++;
                Counters.FramesOut++;

                if (action == PaceAction.Skip)
                {
                    // The delay has grown past the deadband. Drop one frame to bring it back;
                    // at most one a frame, so it is never visible as a jump.
                    Counters.Skips++;
                    readSeq++;
                }

                // The first time is the one the operator is waiting for: the recording has
                // reached the transmitter. Coming back on air after a hold is not that, and
                // is said as what it is.
                string onAirText = firstPictureSaid
                    ? "On air."
                    : $"On air: the recording is now going out on {Ports} of " +
                      $"board {_boardIndex}, {Describe(_delay)} behind the receiver. " +
                      "The playback deck's RX is showing it.";

                Announce(ref phase, _line.IsSealed ? DelayPhase.Draining : DelayPhase.OnAir,
                    _line.IsSealed
                        ? $"Playing out the last {Math.Max(0, _line.SealedAt - readSeq)} frames."
                        : onAirText,
                    written);

                if (phase == DelayPhase.OnAir) firstPictureSaid = true;

                // Once a second, so the countdown and the frame count on screen keep up
                // without the log being buried.
                if (Counters.FramesOut % _format.FrameRate == 0)
                {
                    Report(phase, "", written);
                    PublishCounts(into.RecordingFrame, written);
                }
            }
        }
        catch (SdiOutputException ex)
        {
            Report(DelayPhase.Failed, ex.Message, _line.FramesWritten);
        }
        catch (Exception ex)
        {
            Report(DelayPhase.Failed, $"Tidal lock output failed: {ex.Message}", _line.FramesWritten);
        }
        finally
        {
            output?.Dispose();

            // In the finally, so a port is given back however this ended - including the paths
            // that throw. A port left marked busy after a crash would stay greyed out for the
            // rest of the session with nothing holding it.
            SdiPorts.Shared.ReleaseAll(PortOwner);

            _line.Release();
            PipelineState.Shared.ClearAired();
        }
    }

    /// <summary>
    /// How this transmitter names itself in <see cref="SdiPorts"/>.
    ///
    /// Per instance, not per class: two tidal locks on two boards are two owners, and one
    /// stopping must not give back the other's ports.
    /// </summary>
    private string PortOwner => $"tidal lock (board {_boardIndex})";



    // ------------------------------------------------------------------ the ledger

    private long _repeatsSaid, _skipsSaid, _troubleSaidAt;

    /// <summary>
    /// Once a second: the counts to the monitoring page, and a warning if the transmitter has
    /// had to repeat or skip since it last said so.
    ///
    /// A repeat is a frame that went out twice because nothing new had arrived — the picture
    /// froze for a frame. A skip is a frame that never went out, dropped to bring the delay
    /// back. Both are the operator's business the moment they happen, and both are said with
    /// the frame of the recording they happened at, rate-limited to one line every five
    /// seconds so a receiver that has gone away does not narrate every frame of its absence.
    /// </summary>
    private void PublishCounts(long recordingFrame, long written)
    {
        // Where the transmitter has actually got to in the recording - the number
        // the on-air marker should be drawn from, rather than one worked out from the clock.
        PipelineState.Shared.SetAired(Counters.FramesOut, Counters.Repeats, Counters.Skips,
                                      Counters.Resyncs, onAirFrame: recordingFrame);

        long repeats = Counters.Repeats - _repeatsSaid;
        long skips = Counters.Skips - _skipsSaid;

        if (repeats == 0 && skips == 0) return;
        if (Environment.TickCount64 - _troubleSaidAt < 5000) return;

        _troubleSaidAt = Environment.TickCount64;
        _repeatsSaid = Counters.Repeats;
        _skipsSaid = Counters.Skips;

        // Marked on the timeline too. This kind is not a hole in the recording - the material is
        // all there - but it is a moment when what went out was not quite what was recorded, and
        // that is worth being able to point at.
        DropMarks.Shared.Add(DateTime.Now, (int)(skips + repeats), DropKind.OnAir);

        var at = new Timecode(recordingFrame, _format.FrameRate);

        ActivityLog.Shared.Warn(LogSource.TidalLock,
            $"DROP  on air at frame {recordingFrame} of the recording ({at}): " +
            (skips > 0 ? $"{skips} frame(s) skipped to hold the delay" : "") +
            (skips > 0 && repeats > 0 ? ", " : "") +
            (repeats > 0 ? $"{repeats} frame(s) repeated because nothing new had arrived" : "") +
            $". {Counters.FramesOut} out of {written} recorded so far; {Counters}.",
            @event: "delay.trouble",
            detail: $"frame {recordingFrame}\nskipped now {skips}\nrepeated now {repeats}\n" +
                    $"skipped total {Counters.Skips}\nrepeated total {Counters.Repeats}\n" +
                    $"resyncs {Counters.Resyncs}\naired {Counters.FramesOut}\nrecorded {written}");
    }

    // ------------------------------------------------------------------ where in the recording

    /// <summary>
    /// The segment a frame belongs to, when it is a new one — or null when it is the same
    /// segment as last time, the first (which the on-air line announces), or the segment
    /// length is not known.
    ///
    /// Pure, and public, for the same reason <see cref="DelayPacer.Decide"/> is: the pump
    /// cannot run without a card, and what it says on air should be checkable without one.
    /// <paramref name="segmentOnAir"/> is the caller's memory of the last one announced and
    /// is moved on here, so a resync that jumps forward announces once rather than once per
    /// segment skipped.
    /// </summary>
    public static long? SegmentReached(long readSeq, long segmentFrames, ref long segmentOnAir)
    {
        if (segmentFrames <= 0) return null;

        long segment = readSeq / segmentFrames + 1;
        if (segment <= segmentOnAir) return null;

        segmentOnAir = segment;

        return segment == 1 ? null : segment;
    }

    /// <summary>
    /// Says when the next segment's worth of the recording reaches the transmitter.
    ///
    /// The operator asked for this in exactly these terms: the first picture goes to air, and
    /// then every two minutes the next file's worth of it does. The frame just sent is
    /// <paramref name="readSeq"/> frames into the recording, and the recorder cuts a file
    /// every <see cref="_segmentFrames"/>, so crossing a multiple of that is the next segment
    /// arriving at the TX — and at the playback deck's RX, which is looking at the TX.
    ///
    /// Said in the recording's own time rather than by file name, because ffmpeg cuts on a
    /// keyframe near the boundary rather than on it, and a name the operator then cannot find
    /// on the disk is worse than a time they can.
    /// </summary>
    private void AnnounceRecordingPosition(long readSeq, ref long segmentOnAir)
    {
        if (SegmentReached(readSeq, _segmentFrames, ref segmentOnAir) is not { } segment) return;

        var from = new Timecode((segment - 1) * _segmentFrames, _format.FrameRate);

        ActivityLog.Shared.Ok(LogSource.TidalLock,
            $"Segment {segment} of the recording (from {from}) is now going to air on " +
            $"{Ports} of board {_boardIndex}. The playback deck's RX is showing it.",
            @event: "delay.segment",
            detail: $"segment {segment}\nfrom {from}\nframe {readSeq}");
    }

    private static string Describe(TimeSpan delay) => TidalLock.Describe(delay);

    // ------------------------------------------------------------------ audio

    private short[] _left = Array.Empty<short>();
    private short[] _right = Array.Empty<short>();
    private readonly List<short[]> _channels = new(2) { Array.Empty<short>(), Array.Empty<short>() };

    /// <summary>
    /// Splits the frame's interleaved stereo into the two mono channels the card wants, and
    /// sends it. The arrays are exactly as long as there are samples, because
    /// <see cref="SdiOutput"/> takes their length as the amount of audio to embed.
    /// </summary>
    private bool PushWithAudio(ISdiOutput output, DelayFrameBuffer frame)
    {
        int samples = frame.AudioBytes / 4;

        if (samples <= 0) return output.PushFrame(frame.Video);

        if (_left.Length != samples)
        {
            _left = new short[samples];
            _right = new short[samples];
            _channels[0] = _left;
            _channels[1] = _right;
        }

        byte[] audio = frame.Audio;

        for (int i = 0; i < samples; i++)
        {
            int o = i * 4;
            _left[i] = (short)(audio[o] | (audio[o + 1] << 8));
            _right[i] = (short)(audio[o + 2] | (audio[o + 3] << 8));
        }

        return output.PushFrame(frame.Video, _channels);
    }

    // ------------------------------------------------------------------ reporting

    /// <summary>Reports only when the phase actually changes; this runs every frame.</summary>
    private void Announce(ref DelayPhase phase, DelayPhase now, string message, long buffered)
    {
        if (phase == now) return;

        phase = now;
        Report(now, message, buffered);
    }

    private void Report(DelayPhase phase, string message, long buffered)
    {
        // The empty-message call is the once-a-second nudge that keeps the countdown and the
        // frame count on screen moving. It carries nothing to say, and writing it down put
        // over a thousand blank lines an hour into the record — exactly what the comment at
        // the call site was trying to avoid. The screen still gets it; the record does not.
        if (message.Length > 0)
        {
            ActivityLog.Shared.Write(
                LogSource.TidalLock,
                phase switch
                {
                    DelayPhase.Failed => LogLevel.Error,
                    DelayPhase.Holding or DelayPhase.Draining => LogLevel.Warn,
                    DelayPhase.OnAir => LogLevel.Ok,
                    _ => LogLevel.Info,
                },
                message,
                @event: $"delay.{phase.ToString().ToLowerInvariant()}");
        }

        Status?.Invoke(new DelayStatus(phase, message, buffered, _targetFrames, Counters.FramesOut));
    }

    public void Dispose() => Stop();
}
