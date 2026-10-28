using Emerald.Core;
using Emerald.Deltacast;

namespace Emerald.Video;

/// <summary>
/// Live Edit's transmitter: the delayed recording out of a TX, with the timeline's cues played
/// into it as the recording reaches them - an insert's media before the frame it was dropped
/// at, a replacement over the frames it covers, a deleted scene skipped as though it had never
/// been recorded.
///
/// <b>One thread, one output.</b> Everything that reaches air goes through the single
/// <see cref="ISdiOutput"/> this opens, from the single thread that opened it - the delayed
/// frames and the cue media alike. That is what makes a cut between them a cut, at a frame, and
/// not two engines handing a card to each other. <see cref="ISdiOutput.PushFrame"/> blocks until
/// the card frees a slot, so the card is the clock, as it is for the playback deck's tidal lock.
///
/// <b>The delay is not fixed here.</b> An insert holds the recording back while its media plays,
/// so the delay grows by its length; a cut skips forward through the line, so it shrinks by the
/// range removed. The target lag handed to <see cref="DelayPacer"/> moves with them, so the pacer
/// holds the new delay rather than fighting back to the old one. The line was made with room for
/// the growth when going live; Live Edit refuses an insert that would need more.
///
/// <b>Media is opened ahead.</b> Starting ffmpeg takes long enough to show on air, so each cue's
/// decoders are started a few seconds before the recording reaches it - inside Live Edit's no
/// touch zone, so a cue being prepared can no longer be changed under it.
/// </summary>
public sealed class LiveAirTransmitter : IDisposable
{
    /// <summary>How far ahead of a cue its media is opened, in seconds.</summary>
    public const int PrepareSeconds = 6;

    private readonly uint _boardIndex;
    private readonly int[] _txChannels;
    private readonly DelayLine _line;
    private readonly VideoFormat _format;
    private readonly string _ffmpegPath;
    private readonly Func<IReadOnlyList<LiveCue>> _cues;
    private readonly Func<IReadOnlyList<AudioEdit>> _audioEdits;

    private Thread? _worker;
    private CancellationTokenSource? _cts;

    public DelayCounters Counters { get; } = new();

    /// <summary>Raised on the transmitter's thread; a UI subscriber must marshal.</summary>
    public event Action<DelayStatus>? Status;

    /// <summary>
    /// The delay being held right now, in frames: the delay gone live with, plus every insert
    /// played, less every cut made.
    /// </summary>
    public long TargetFrames => Interlocked.Read(ref _target);

    private long _target;

    /// <param name="cues">
    /// Read every frame for the current list of cues. Live Edit replaces the list whole on every
    /// edit, so each read is one consistent schedule.
    /// </param>
    /// <param name="audioEdits">
    /// Read every frame for the levels set on the recording's own sound. Not the cues' media:
    /// that plays at its own level.
    /// </param>
    public LiveAirTransmitter(uint boardIndex, IReadOnlyList<int> txChannels, DelayLine line, TimeSpan delay,
                              string ffmpegPath, Func<IReadOnlyList<LiveCue>> cues,
                              Func<IReadOnlyList<AudioEdit>>? audioEdits = null)
    {
        if (txChannels.Count == 0)
            throw new DelayLineException("No TX port was chosen to go live on.");

        if (!DelayFormats.TryMatch(line.Format, out VideoFormat? format, out string? problem))
            throw new DelayLineException(problem!);

        _boardIndex = boardIndex;
        _txChannels = txChannels.Distinct().ToArray();
        _line = line;
        _format = format!;
        _ffmpegPath = ffmpegPath;
        _cues = cues;
        _audioEdits = audioEdits ?? (() => Array.Empty<AudioEdit>());
        // Its own delay, not the line's size - the capture deck's line can hold far more.
        _target = (long)Math.Ceiling(Math.Max(0, delay.TotalSeconds) * _format.FrameRate);

        _line.AddRef();
    }

    private string Ports =>
        _txChannels.Length == 1
            ? $"TX{_txChannels[0]}"
            : string.Join(", ", _txChannels.Select(c => $"TX{c}"));

    /// <summary>
    /// Who holds the ports, by the name the decks show. Its own, so the playback deck's tidal
    /// lock and this can never take each other's ports or give each other's back.
    /// </summary>
    public static string OwnerFor(uint boardIndex) => $"live edit (board {boardIndex})";

    private string PortOwner => OwnerFor(_boardIndex);

    public void Start()
    {
        if (_worker is { IsAlive: true }) return;

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        _worker = new Thread(() => Run(ct))
        {
            IsBackground = true,
            Name = "live edit TX",
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

        DelayFrameBuffer[] buffers = { _line.NewBuffer(), _line.NewBuffer() };
        int next = 0;
        DelayFrameBuffer? onAir = null;

        var done = new HashSet<string>();
        var prepared = new Dictionary<string, Task<CueMedia?>>();
        CueMedia? playing = null;

        // Already the delay behind the recorder, when the line has been filling since before going
        // live: straight on air rather than black while it fills again.
        long readSeq = Math.Max(0, _line.FramesWritten - Interlocked.Read(ref _target));
        long lastRecording = -1;
        var phase = DelayPhase.Opening;

        int samples = AudioSource.SampleRate / _format.FrameRate;
        var left = new short[samples];
        var right = new short[samples];
        var fileChannels = new List<short[]> { left, right };
        // A little over a frame: how long to wait for a decoded frame before holding the last one.
        TimeSpan frameTime = TimeSpan.FromMilliseconds(1200.0 / _format.FrameRate);

        long lookahead = (long)PrepareSeconds * _format.FrameRate;

        try
        {
            Report(DelayPhase.Opening, $"Opening {Ports} on board {_boardIndex} to go live at {_format.Name}...", 0);

            // Refused rather than fought over: a port the playback deck or the EDL is already
            // sending on is somebody else's picture on air.
            if (!SdiPorts.Shared.TryHold(PortOwner, _txChannels.Select(c => new SdiPort(_boardIndex, c)).ToArray()))
            {
                string held = string.Join(", ", _txChannels
                    .Select(c => (Port: c, By: SdiPorts.Shared.HeldBy(new SdiPort(_boardIndex, c))))
                    .Where(p => p.By is not null && p.By != PortOwner)
                    .Select(p => $"TX{p.Port} ({p.By})"));

                Report(DelayPhase.Failed, $"Cannot go live: {held} already in use.", 0);
                return;
            }

            output = _txChannels.Length == 1
                ? SdiOutput.Open(_boardIndex, _txChannels[0], _format)
                : SdiOutputFan.Open(_boardIndex, _txChannels, _format);

            byte[] black = output.BlackFrame();

            while (!ct.IsCancellationRequested)
            {
                long written = _line.FramesWritten;
                long target = Interlocked.Read(ref _target);

                // ---------------------------------------------------- a cue's media on air
                if (playing is { } job)
                {
                    // The next frame, already decoded ahead - waited for only about a frame's
                    // time when, for a moment, it is not there yet.
                    bool got = job.Frames.TryTake(out byte[] frame, frameTime);
                    bool ended = !got && job.Frames.Finished;

                    if (got)
                    {
                        job.Audio.ReadFrame(left, right, job.Taken, 0);

                        if (!output.PushFrame(frame, fileChannels))
                        {
                            Report(DelayPhase.Failed, $"{Ports} stopped accepting frames.", written);
                            return;
                        }

                        // The frame just sent stays the one on air until the next replaces it,
                        // so the buffer before it can go back to be filled.
                        if (job.Last is { } previous) job.Frames.Return(previous);
                        job.Last = frame;
                        job.Taken++;
                    }
                    else if (!ended)
                    {
                        // Decoding has fallen behind for a moment. The picture on air again,
                        // silent, rather than black - and under an insert the recording waits
                        // a frame longer, so the delay takes the frame and nothing is skipped.
                        job.Stalls++;

                        if (!job.ConsumesRing) Interlocked.Increment(ref _target);

                        if (!output.PushFrame(job.Last ?? black))
                        {
                            Report(DelayPhase.Failed, $"{Ports} stopped accepting frames.", written);
                            return;
                        }
                    }

                    if (!ended)
                    {
                        Counters.FramesOut++;

                        // Under an insert the recording waits; under anything else it goes by
                        // unseen, a frame for every frame on air, so it is exactly where it
                        // should be when the media ends.
                        if (job.ConsumesRing) readSeq++;

                        LiveAir.Shared.SetPosition(
                            new LiveAirPosition(lastRecording, job.Cue.Id, job.StartOffset + job.Taken),
                            Interlocked.Read(ref _target));

                        Announce(ref phase, DelayPhase.OnAir, $"On air on {Ports}.", written);
                    }

                    if (job.Taken >= job.Length || ended)
                    {
                        // Media that ran out early made an insert shorter than planned, so the
                        // delay grew by less. Taken off the target, or the pacer would spend the
                        // next minutes repeating frames to make up a lag nobody asked for.
                        if (!job.ConsumesRing && job.Taken < job.Length)
                            Interlocked.Add(ref _target, -(job.Length - job.Taken));

                        Cue(job.Cue, job.Stalls > 0 ? LogLevel.Warn : LogLevel.Ok, "done",
                            $"EDL  {job.Cue.Name} finished on air after {new Timecode(job.Taken, _format.FrameRate)}" +
                            (job.Taken < job.Length ? $" - its media ran out {job.Length - job.Taken:N0} frames early" : "") +
                            (job.Stalls > 0
                                ? $" - decoding fell behind {job.Stalls:N0} time(s), and the frame on air was held for those"
                                : "") +
                            "; back to the recording.");

                        job.Dispose();
                        playing = null;
                    }

                    if (Counters.FramesOut % _format.FrameRate == 0) Report(phase, "", written);
                    continue;
                }

                // ---------------------------------------------------- the delayed recording
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
                        Announce(ref phase, DelayPhase.Finished, $"The delay has played out. {Counters}.", written);
                        return;

                    case PaceAction.Fail:
                        Announce(ref phase, DelayPhase.Failed,
                            "The receiver has stopped delivering frames. Cutting to black.", written);
                        return;

                    case PaceAction.Resync:
                        Counters.Resyncs++;
                        readSeq = Math.Max(0, written - target);
                        Report(DelayPhase.OnAir,
                            "Fell behind the recorder and re-established the delay - there is a cut in the output.",
                            written);

                        if (!output.PushFrame(black)) return;
                        continue;

                    case PaceAction.Repeat:
                        Counters.Repeats++;

                        Announce(ref phase,
                            _line.StalledMs >= DelayPacer.HoldingAfterMs ? DelayPhase.Holding : phase,
                            "The receiver has paused; holding the last frame.", written);

                        if (!output.PushFrame(onAir?.Video ?? black)) return;
                        continue;
                }

                DelayFrameBuffer into = buffers[next];

                switch (_line.TryRead(readSeq, into))
                {
                    case DelayReadResult.Ok:
                        break;

                    case DelayReadResult.EndOfLine:
                        Announce(ref phase, DelayPhase.Finished, $"The delay has played out. {Counters}.", written);
                        return;

                    default:
                        continue;
                }

                long recording = into.RecordingFrame;
                IReadOnlyList<LiveCue> cues = _cues();

                Prepare(recording, lookahead, cues, done, prepared);

                // ---------------------------------------------------- does a cue take over here?
                if (LiveCues.Due(recording, cues, done) is { } cue)
                {
                    done.Add(cue.Id);

                    if (cue.Mode == IngestMode.Cut)
                    {
                        // Straight to the far side of the removed range. The line holds it - it
                        // is between the transmitter and the recorder - so this is a read
                        // position moving, not anything waiting.
                        long jump = Math.Max(0, cue.EndFrame - recording);

                        readSeq += jump;
                        Interlocked.Add(ref _target, -jump);

                        Cue(cue, LogLevel.Ok, "cut",
                            $"EDL  scene cut on air - {new Timecode(jump, _format.FrameRate)} of the recording " +
                            $"skipped (frames {recording:N0} to {cue.EndFrame:N0}); the delay is " +
                            $"{new Timecode(Interlocked.Read(ref _target), _format.FrameRate)} now.");
                        continue;
                    }

                    // Frames of the covered range already gone by, for a cue reached late.
                    long late = cue.Mode == IngestMode.Insert ? 0 : recording - cue.RecordingFrame;
                    long frames = cue.Frames - late;

                    if (frames > 0 && TakeMedia(cue, late, prepared) is { } media)
                    {
                        media.Length = frames;
                        media.ConsumesRing = cue.Mode != IngestMode.Insert;

                        if (cue.Mode == IngestMode.Insert) Interlocked.Add(ref _target, cue.Frames);

                        playing = media;

                        Cue(cue, LogLevel.Ok, "playing",
                            $"EDL  {cue.Name} to air now ({cue.Mode.ToString().ToLowerInvariant()}, " +
                            $"{new Timecode(frames, _format.FrameRate)}) at recording frame {recording:N0}" +
                            (cue.Mode == IngestMode.Insert
                                ? $"; the delay grows to {new Timecode(Interlocked.Read(ref _target), _format.FrameRate)}."
                                : "."));

                        // The recording frame in hand has not gone out. For an insert it goes
                        // out after the media; for the rest the media has taken its place.
                        continue;
                    }

                    if (frames > 0)
                        Cue(cue, LogLevel.Error, "failed",
                            $"EDL  {cue.Name} could not be played to air - the recording carried on instead.");
                }

                // ---------------------------------------------------- the recording frame
                if (!PushWithAudio(output, into))
                {
                    Report(DelayPhase.Failed, $"{Ports} stopped accepting frames.", written);
                    return;
                }

                onAir = into;
                next ^= 1;
                lastRecording = recording;
                readSeq++;
                Counters.FramesOut++;

                if (action == PaceAction.Skip)
                {
                    Counters.Skips++;
                    readSeq++;
                }

                LiveAir.Shared.SetPosition(new LiveAirPosition(recording, null, 0), Interlocked.Read(ref _target));

                Announce(ref phase, _line.IsSealed ? DelayPhase.Draining : DelayPhase.OnAir,
                    _line.IsSealed
                        ? $"Playing out the last {Math.Max(0, _line.SealedAt - readSeq)} frames."
                        : $"On air on {Ports} of board {_boardIndex}.",
                    written);

                if (Counters.FramesOut % _format.FrameRate == 0) Report(phase, "", written);
            }
        }
        catch (SdiOutputException ex)
        {
            Report(DelayPhase.Failed, ex.Message, _line.FramesWritten);
        }
        catch (Exception ex)
        {
            Report(DelayPhase.Failed, $"Live Edit's output failed: {ex.Message}", _line.FramesWritten);
        }
        finally
        {
            playing?.Dispose();

            foreach (Task<CueMedia?> task in prepared.Values)
                _ = task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); });

            output?.Dispose();
            SdiPorts.Shared.ReleaseAll(PortOwner);
            _line.Release();
        }
    }

    // ------------------------------------------------------------------ cue media

    /// <summary>A cue's decoders, and how far through them the transmitter is.</summary>
    private sealed class CueMedia : IDisposable
    {
        public required LiveCue Cue { get; init; }

        /// <summary>The picture, decoded ahead of the transmitter from the moment the cue is prepared.</summary>
        public required FrameReadahead Frames { get; init; }

        public required AudioSource Audio { get; init; }

        /// <summary>Frames into the media it was opened at.</summary>
        public long StartOffset { get; init; }

        /// <summary>How many frames of media this cue plays.</summary>
        public long Length { get; set; }

        /// <summary>Frames of media sent so far - and so where its sound is read from.</summary>
        public long Taken { get; set; }

        /// <summary>The frame on air now, held back from the reader until the next replaces it.</summary>
        public byte[]? Last { get; set; }

        /// <summary>Times decoding fell behind and the frame on air was held instead.</summary>
        public long Stalls { get; set; }

        public bool ConsumesRing { get; set; }

        public void Dispose()
        {
            Frames.Dispose();
            Audio.Dispose();
        }
    }

    /// <summary>Starts the decoders for cues coming up, and lets go of ones taken off the timeline.</summary>
    private void Prepare(long recording, long lookahead, IReadOnlyList<LiveCue> cues,
                         HashSet<string> done, Dictionary<string, Task<CueMedia?>> prepared)
    {
        foreach (LiveCue cue in LiveCues.ToPrepare(recording, lookahead, cues, done))
        {
            if (prepared.ContainsKey(cue.Id)) continue;

            prepared[cue.Id] = Task.Run(() => Open(cue, 0));

            Cue(cue, LogLevel.Info, "prepared",
                $"EDL  {cue.Name} cued - opening its media, on air in " +
                $"{new Timecode(Math.Max(0, cue.RecordingFrame - recording), _format.FrameRate)}.");
        }

        // A cue removed from the timeline after its media was opened: close the decoders now
        // rather than leave two ffmpegs running for something that will never play.
        foreach (string id in prepared.Keys.Where(id => !done.Contains(id) && cues.All(c => c.Id != id)).ToList())
        {
            _ = prepared[id].ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); });
            prepared.Remove(id);
        }
    }

    /// <summary>
    /// The cue's decoders, ready to read from <paramref name="late"/> frames in: the prepared
    /// ones when they are at the start and ready, otherwise opened now.
    /// </summary>
    private CueMedia? TakeMedia(LiveCue cue, long late, Dictionary<string, Task<CueMedia?>> prepared)
    {
        if (prepared.Remove(cue.Id, out Task<CueMedia?>? task))
        {
            if (late == 0)
            {
                // Normally long finished. A slow disk is waited on for a moment, which holds the
                // last frame on air rather than cutting in without the media.
                try { if (task.Wait(TimeSpan.FromSeconds(3)) && task.Result is { } ready) return PreRolled(ready); }
                catch { /* fall through and try again directly */ }
            }
            else
            {
                _ = task.ContinueWith(t => { if (t.IsCompletedSuccessfully) t.Result?.Dispose(); });
            }
        }

        return Open(cue, late) is { } opened ? PreRolled(opened) : null;
    }

    /// <summary>
    /// A cue's media with a little decoded before it cuts in. One prepared on time already has a
    /// second in hand and this costs nothing; one opened at the last moment waits up to a
    /// moment for half a second of picture, holding the frame on air, rather than starting on a
    /// queue that would run dry after its first frame.
    /// </summary>
    private CueMedia PreRolled(CueMedia media)
    {
        media.Frames.WaitFor(Math.Max(1, _format.FrameRate / 2), TimeSpan.FromSeconds(3));
        return media;
    }

    private CueMedia? Open(LiveCue cue, long late)
    {
        if (cue.MediaPath is not { } path) return null;

        // Frames of the media rather than of the recording: a stretched cue gets through its
        // media faster than the recording goes by.
        TimeSpan? seek = late > 0
            ? TimeSpan.FromSeconds(late * cue.Speed / _format.FrameRate)
            : null;

        FrameSource? video = null;

        try
        {
            video = FrameSource.Open(_ffmpegPath, path, _format, seek, cue.Speed);
            AudioSource audio = AudioSource.Open(_ffmpegPath, path, _format.FrameRate, -1, seek, cue.Speed);

            // Decoding ahead from now: by the time the cue is due, its first second is in hand.
            var ahead = new FrameReadahead(video, _format.FrameBytes, _format.FrameRate);

            return new CueMedia { Cue = cue, Frames = ahead, Audio = audio, StartOffset = late };
        }
        catch (Exception ex)
        {
            video?.Dispose();

            ActivityLog.Shared.Error(LogSource.LiveEdit,
                $"EDL  {cue.Name} could not be opened for air: {ex.Message}",
                @event: "liveair.cue.openfailed", file: path);

            return null;
        }
    }

    // ------------------------------------------------------------------ audio

    private short[] _left = Array.Empty<short>();
    private short[] _right = Array.Empty<short>();
    private readonly List<short[]> _channels = new(2) { Array.Empty<short>(), Array.Empty<short>() };

    /// <summary>The level the last frame ended at, so the next one starts from it.</summary>
    private double _gain = 1;

    /// <summary>
    /// The recording's stereo, split into the two mono channels the card embeds, at whatever level
    /// Live Edit has set on this frame.
    ///
    /// A change of level is ramped across the frame rather than made at its first sample: a step
    /// from full to nothing between two samples is a click on air.
    /// </summary>
    private bool PushWithAudio(ISdiOutput output, DelayFrameBuffer frame)
    {
        int samples = frame.AudioBytes / 4;

        double from = _gain;
        double to = AudioEdits.Linear(AudioEdits.GainDbAt(frame.RecordingFrame, _audioEdits()));
        _gain = to;

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

        if (from != 1 || to != 1)
        {
            for (int i = 0; i < samples; i++)
            {
                double gain = from + (to - from) * (i + 1) / samples;
                _left[i] = (short)Math.Clamp(Math.Round(_left[i] * gain), short.MinValue, short.MaxValue);
                _right[i] = (short)Math.Clamp(Math.Round(_right[i] * gain), short.MinValue, short.MaxValue);
            }
        }

        return output.PushFrame(frame.Video, _channels);
    }

    // ------------------------------------------------------------------ reporting

    private void Cue(LiveCue cue, LogLevel level, string what, string message) =>
        ActivityLog.Shared.Write(LogSource.LiveEdit, level, message,
                                 @event: $"liveair.cue.{what}", correlation: cue.Id, file: cue.MediaPath);

    private void Announce(ref DelayPhase phase, DelayPhase now, string message, long buffered)
    {
        if (phase == now) return;

        phase = now;
        Report(now, message, buffered);
    }

    private void Report(DelayPhase phase, string message, long buffered)
    {
        if (message.Length > 0)
        {
            ActivityLog.Shared.Write(
                LogSource.LiveEdit,
                phase switch
                {
                    DelayPhase.Failed => LogLevel.Error,
                    DelayPhase.Holding or DelayPhase.Draining => LogLevel.Warn,
                    DelayPhase.OnAir => LogLevel.Ok,
                    _ => LogLevel.Info,
                },
                $"LIVE  {message}",
                @event: $"liveair.{phase.ToString().ToLowerInvariant()}");
        }

        Status?.Invoke(new DelayStatus(phase, message, buffered, Interlocked.Read(ref _target), Counters.FramesOut));
    }

    public void Dispose() => Stop();
}
