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
    /// Throws <see cref="DelayLineException"/> when the receiver's format cannot be
    /// transmitted as it stands. Checked here as well as at arming time, because this is the
    /// last point before the card is opened and nothing beyond it can refuse safely.
    /// </summary>
    public DelayTransmitter(uint boardIndex, int txChannel, DelayLine line, TimeSpan delay)
    {
        if (!DelayFormats.TryMatch(line.Format, out VideoFormat? format, out string? problem))
            throw new DelayLineException(problem!);

        _boardIndex = boardIndex;
        _txChannel = txChannel;
        _line = line;
        _format = format!;
        _delay = delay;

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

                Announce(ref phase, _line.IsSealed ? DelayPhase.Draining : DelayPhase.OnAir,
                    _line.IsSealed
                        ? $"Playing out the last {Math.Max(0, _line.SealedAt - readSeq)} frames."
                        : "On air.",
                    written);

                // Once a second, so the countdown and the frame count on screen keep up
                // without the log being buried.
                if (Counters.FramesOut % _format.FrameRate == 0)
                    Report(phase, "", written);
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
        }
    }

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

    private void Report(DelayPhase phase, string message, long buffered) =>
        Status?.Invoke(new DelayStatus(phase, message, buffered, _line.TargetFrames, Counters.FramesOut));

    public void Dispose() => Stop();
}
