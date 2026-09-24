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
    private readonly int _txChannel;
    private readonly DelayLine _line;
    private readonly VideoFormat _format;
    private readonly TimeSpan _delay;

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
    public DelayTransmitter(uint boardIndex, int txChannel, DelayLine line, TimeSpan delay,
                            int segmentSeconds = 0)
    {
        if (!DelayFormats.TryMatch(line.Format, out VideoFormat? format, out string? problem))
            throw new DelayLineException(problem!);

        _boardIndex = boardIndex;
        _txChannel = txChannel;
        _line = line;
        _format = format!;
        _delay = delay;
        _segmentFrames = segmentSeconds > 0 ? (long)segmentSeconds * _format.FrameRate : 0;

        _line.AddRef();
    }

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
        SdiOutput? output = null;

        // Two buffers, used alternately. A read that fails half-way has already scribbled on
        // the one it was filling, so the frame being held for a repeat must never be that one.
        DelayFrameBuffer[] buffers = { _line.NewBuffer(), _line.NewBuffer() };
        int next = 0;
        DelayFrameBuffer? onAir = null;

        long readSeq = 0;
        long segmentOnAir = 0;
        bool firstPictureSaid = false;
        var phase = DelayPhase.Opening;

        try
        {
            Report(DelayPhase.Opening,
                $"Opening TX{_txChannel} on board {_boardIndex} at {_format.Name}...", 0);

            output = SdiOutput.Open(_boardIndex, _txChannel, _format);
            byte[] black = output.BlackFrame();

            while (!ct.IsCancellationRequested)
            {
                long written = _line.FramesWritten;

                PaceAction action = DelayPacer.Decide(
                    written, readSeq, _line.TargetFrames, _line.SlotCount,
                    _line.IsSealed, _line.SealedAt, _line.StalledMs, _format.FrameRate);

                switch (action)
                {
                    case PaceAction.Fill:
                        Announce(ref phase, DelayPhase.Filling,
                            $"Filling the delay: {written} of {_line.TargetFrames} frames.", written);

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
                        readSeq = Math.Max(0, written - _line.TargetFrames);
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

                if (!PushWithAudio(output, into)) return;

                // The frame just sent is readSeq frames into the recording. What that means
                // for the operator is said here, before the counters move on.
                AnnounceRecordingPosition(readSeq, ref segmentOnAir);

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
                    : $"On air: the first picture of the recording is now on TX{_txChannel} of " +
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
                    PublishCounts(readSeq, written);
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
            _line.Release();
            PipelineState.Shared.ClearAired();
        }
    }



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
    private void PublishCounts(long readSeq, long written)
    {
        PipelineState.Shared.SetAired(Counters.FramesOut, Counters.Repeats, Counters.Skips, Counters.Resyncs);

        long repeats = Counters.Repeats - _repeatsSaid;
        long skips = Counters.Skips - _skipsSaid;

        if (repeats == 0 && skips == 0) return;
        if (Environment.TickCount64 - _troubleSaidAt < 5000) return;

        _troubleSaidAt = Environment.TickCount64;
        _repeatsSaid = Counters.Repeats;
        _skipsSaid = Counters.Skips;

        var at = new Timecode(readSeq, _format.FrameRate);

        ActivityLog.Shared.Warn(LogSource.TidalLock,
            $"DROP  on air at frame {readSeq} of the recording ({at}): " +
            (skips > 0 ? $"{skips} frame(s) skipped to hold the delay" : "") +
            (skips > 0 && repeats > 0 ? ", " : "") +
            (repeats > 0 ? $"{repeats} frame(s) repeated because nothing new had arrived" : "") +
            $". {Counters.FramesOut} out of {written} recorded so far; {Counters}.",
            @event: "delay.trouble",
            detail: $"frame {readSeq}\nskipped now {skips}\nrepeated now {repeats}\n" +
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
            $"TX{_txChannel} of board {_boardIndex}. The playback deck's RX is showing it.",
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
    private bool PushWithAudio(SdiOutput output, DelayFrameBuffer frame)
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

        Status?.Invoke(new DelayStatus(phase, message, buffered, _line.TargetFrames, Counters.FramesOut));
    }

    public void Dispose() => Stop();
}
