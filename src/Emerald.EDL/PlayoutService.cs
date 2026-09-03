using Emerald.Core;
using Emerald.Deltacast;
using Emerald.Video;
using Emerald.Media;
using System.IO;

namespace Emerald.Edl;

/// <summary>What the TX carries once a message has finished.</summary>
public enum PostPlay { BlackScreen, FreezeLastFrame }

public enum EntryState { Queued, Cued, Playing, Completed, Stopped, Failed }

public enum PlayoutState { Idle, Opening, WaitingForCue, Playing, PostPlay, Finished, Stopped, Failed }

public sealed record PlayoutStatus(
    PlayoutState State,
    string Message,
    long FramesOut = 0,
    long? FramesTotal = null,
    string? CurrentFile = null);

/// <summary>One selectable audio track — a language — and the files behind it.</summary>
public sealed record AudioTrack(string Label, IReadOnlyList<string> Files);

public sealed record PlayoutRequest(
    uint BoardIndex,
    string BoardModel,
    int TxChannel,
    IReadOnlyList<string>? VideoFiles,
    Timecode Start,
    long? DurationFrames,
    int FrameRate,
    Timecode Som,
    TimeSpan SeekOffset,
    PostPlay PostPlay,
    IReadOnlyList<AudioTrack>? AudioTracks = null,
    int DefaultTrack = 0)
{
    public bool HasVideo => VideoFiles is { Count: > 0 };
    public bool HasAudio => AudioTracks is { Count: > 0 };
}

/// <summary>One queued message, with the state the operator sees against it.</summary>
public sealed class PlayoutEntry
{
    public required string Id { get; init; }
    public required PlayoutRequest Request { get; init; }
    public required string MediaLabel { get; init; }

    public EntryState State { get; set; } = EntryState.Queued;
    public long FramesOut { get; set; }
    public string Detail { get; set; } = "";

    public Timecode Stop => Request.DurationFrames is { } d
        ? Request.Start.AddWrapping(d)
        : Request.Start;

    public string DurationLabel => Request.DurationFrames is { } d
        ? new Timecode(d, Request.FrameRate).ToString()
        : "open-ended";

    public string StopLabel => Request.DurationFrames is null ? "open-ended" : Stop.ToString();
}

/// <summary>
/// Runs a queue of messages out of one TX channel.
///
/// A single dedicated thread owns the output for the whole queue. That matters: a TX
/// channel cannot be opened twice, so handing each message its own output would make
/// back-to-back playout impossible. Holding one output also means the post-play fill of
/// message N keeps the line up until message N+1 cues, with no black flash between them.
///
/// The card's slot queue paces everything — <see cref="SdiOutput.PushFrame"/> blocks until
/// a slot frees — so there is no timer anywhere and no drift.
/// </summary>
public sealed class PlayoutService : IDisposable
{
    private readonly TimecodeService _timecode;
    private readonly string? _ffmpegPath;

    private readonly object _gate = new();
    private readonly List<PlayoutEntry> _entries = new();

    private bool _announcedHold;

    /// <summary>SDI carries 4 groups of 4 channels; 8 stereo tracks is the hard ceiling.</summary>
    public const int MaxAudioTracks = 8;

    private int _currentTrackIndex;
    private readonly int[] _trackOffsetsMs = new int[MaxAudioTracks];

    /// <summary>
    /// Which audio track is on air. The play loop re-reads this every frame, so changing it
    /// mid-message swaps the language on the next frame — every track is being decoded and
    /// advanced regardless, so the switch is just a choice of buffer. Video is untouched.
    /// </summary>
    public int CurrentTrackIndex
    {
        get => Volatile.Read(ref _currentTrackIndex);
        set => Volatile.Write(ref _currentTrackIndex, value);
    }

    /// <summary>
    /// Per-track audio offset in milliseconds, live. Each language keeps its own delay, so
    /// switching between them preserves whatever each was trimmed to.
    /// </summary>
    public int GetTrackOffset(int track) =>
        track >= 0 && track < MaxAudioTracks ? Volatile.Read(ref _trackOffsetsMs[track]) : 0;

    public void SetTrackOffset(int track, int offsetMs)
    {
        if (track >= 0 && track < MaxAudioTracks) Volatile.Write(ref _trackOffsetsMs[track], offsetMs);
    }

    private Thread? _worker;
    private CancellationTokenSource? _cts;

    public event Action<PlayoutStatus>? Progress;
    public event Action? QueueChanged;

    public string? FfmpegPath => _ffmpegPath;

    public PlayoutService(TimecodeService timecode, string? ffmpegPath)
    {
        _timecode = timecode;
        _ffmpegPath = ffmpegPath;
    }

    public IReadOnlyList<PlayoutEntry> Snapshot()
    {
        lock (_gate) return _entries.ToList();
    }

    public PlayoutEntry? Current
    {
        get { lock (_gate) return _entries.FirstOrDefault(e => e.State is EntryState.Playing or EntryState.Cued); }
    }

    public PlayoutEntry? NextUp
    {
        get { lock (_gate) return _entries.FirstOrDefault(e => e.State == EntryState.Queued); }
    }

    public int PendingCount
    {
        get { lock (_gate) return _entries.Count(e => e.State == EntryState.Queued); }
    }

    /// <summary>Adds a message to the back of the queue, starting the engine if it is idle.</summary>
    public void Enqueue(PlayoutEntry entry)
    {
        lock (_gate) _entries.Add(entry);
        QueueChanged?.Invoke();

        if (_worker is { IsAlive: true }) return;

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        _worker = new Thread(() => Run(ct))
        {
            IsBackground = true,
            Name = "SDI playout",
            // A real-time loop; it should not lose the CPU to background work.
            Priority = ThreadPriority.AboveNormal,
        };

        _worker.Start();
    }

    /// <summary>Stops the current message and abandons everything still queued.</summary>
    public void StopAll()
    {
        _cts?.Cancel();

        if (_worker is { IsAlive: true })
            _worker.Join(TimeSpan.FromSeconds(5));

        _cts?.Dispose();
        _cts = null;
        _worker = null;

        lock (_gate)
        {
            foreach (PlayoutEntry e in _entries.Where(e => e.State is EntryState.Queued or EntryState.Cued or EntryState.Playing))
                e.State = EntryState.Stopped;
        }

        QueueChanged?.Invoke();
    }

    public void ClearFinished()
    {
        lock (_gate)
            _entries.RemoveAll(e => e.State is EntryState.Completed or EntryState.Stopped or EntryState.Failed);

        QueueChanged?.Invoke();
    }

    // ------------------------------------------------------------------ worker

    private void Run(CancellationToken ct)
    {
        SdiOutput? output = null;
        (uint board, int tx)? openFor = null;
        byte[]? lastFrame = null;
        PostPlay fill = PostPlay.BlackScreen;

        try
        {
            if (_ffmpegPath is null)
            {
                Report(new PlayoutStatus(PlayoutState.Failed,
                    "ffmpeg was not found, so media cannot be decoded for SDI output."));
                FailAllQueued("ffmpeg unavailable");
                return;
            }

            while (!ct.IsCancellationRequested)
            {
                PlayoutEntry? entry = TakeNextQueued();

                if (entry is null)
                {
                    // Queue drained. Hold the post-play fill so the line stays up; the
                    // operator releases it with STOP.
                    if (output is null) return;
                    if (!HoldFill(output, fill, lastFrame, ct)) return;
                    continue;
                }

                PlayoutRequest req = entry.Request;

                try
                {
                    if (openFor != (req.BoardIndex, req.TxChannel))
                    {
                        output?.Dispose();
                        var format = VideoFormat.ForFrameRate(req.FrameRate);

                        Report(new PlayoutStatus(PlayoutState.Opening,
                            $"Opening TX{req.TxChannel} on board {req.BoardIndex} at {format.Name}..."));

                        output = SdiOutput.Open(req.BoardIndex, req.TxChannel, format);
                        openFor = (req.BoardIndex, req.TxChannel);
                        lastFrame = null;
                    }

                    fill = req.PostPlay;
                    PlayEntry(entry, output!, ref lastFrame, ct);
                }
                catch (SdiOutputException ex)
                {
                    entry.State = EntryState.Failed;
                    entry.Detail = ex.Message;
                    QueueChanged?.Invoke();
                    Report(new PlayoutStatus(PlayoutState.Failed, ex.Message));
                    return;
                }
                catch (Exception ex)
                {
                    entry.State = EntryState.Failed;
                    entry.Detail = ex.Message;
                    QueueChanged?.Invoke();
                    Report(new PlayoutStatus(PlayoutState.Failed, $"Playout error: {ex.Message}"));
                }
            }
        }
        finally
        {
            output?.Dispose();
            Report(new PlayoutStatus(PlayoutState.Stopped, "Output released."));
        }
    }

    private PlayoutEntry? TakeNextQueued()
    {
        lock (_gate) return _entries.FirstOrDefault(e => e.State == EntryState.Queued);
    }

    private void FailAllQueued(string reason)
    {
        lock (_gate)
        {
            foreach (PlayoutEntry e in _entries.Where(e => e.State == EntryState.Queued))
            {
                e.State = EntryState.Failed;
                e.Detail = reason;
            }
        }

        QueueChanged?.Invoke();
    }

    private void PlayEntry(PlayoutEntry entry, SdiOutput output, ref byte[]? lastFrame, CancellationToken ct)
    {
        PlayoutRequest req = entry.Request;
        var format = output.Format;

        entry.State = EntryState.Cued;
        QueueChanged?.Invoke();

        WaitForCue(entry, output, req.PostPlay, lastFrame, ct);
        if (ct.IsCancellationRequested) { entry.State = EntryState.Stopped; QueueChanged?.Invoke(); return; }

        entry.State = EntryState.Playing;
        entry.Detail = "";          // clear the "cues in ..." countdown
        _announcedHold = false;     // re-arm the idle announcement for after this message
        QueueChanged?.Invoke();

        long? target = req.DurationFrames;
        long framesOut;

        Report(new PlayoutStatus(PlayoutState.Playing,
            target is { } t
                ? $"Playing out TX{req.TxChannel} for {new Timecode(t, req.FrameRate)} (stops at {entry.StopLabel})."
                : $"Playing out TX{req.TxChannel}, looping until stopped.",
            0, target));

        // Beds are opened after the cue, so audio starts with the first frame of the message
        // rather than draining silently through the cue hold.
        var beds = new List<AudioBed>();

        try
        {
            foreach (AudioTrack track in req.AudioTracks ?? Array.Empty<AudioTrack>())
                beds.Add(new AudioBed(_ffmpegPath!, track, req.FrameRate));

            CurrentTrackIndex = Math.Clamp(req.DefaultTrack, 0, Math.Max(0, beds.Count - 1));

            if (beds.Count > 0)
                Report(new PlayoutStatus(PlayoutState.Playing,
                    $"Audio: {beds.Count} track(s), on air \"{beds[CurrentTrackIndex].Label}\".",
                    0, target));
            else
                Report(new PlayoutStatus(PlayoutState.Playing, "Audio: none - video only, silent.", 0, target));

            framesOut = req.HasVideo
                ? PlayWithVideo(entry, output, beds, ref lastFrame, ct)
                : PlayAudioOnly(entry, output, beds, ct);
        }
        finally
        {
            foreach (AudioBed bed in beds) bed.Dispose();
        }

        if (entry.State == EntryState.Failed) return;

        entry.State = ct.IsCancellationRequested ? EntryState.Stopped : EntryState.Completed;
        QueueChanged?.Invoke();

        Report(new PlayoutStatus(
            ct.IsCancellationRequested ? PlayoutState.Stopped : PlayoutState.Finished,
            ct.IsCancellationRequested
                ? $"Stopped after {framesOut} frames."
                : $"Message complete - {framesOut} frames ({new Timecode(framesOut, req.FrameRate)}). " +
                  $"Post play: {Describe(req.PostPlay)}.",
            framesOut, target));
    }

    /// <summary>
    /// Advances every bed by one frame and returns the one that should go to the card.
    ///
    /// All beds advance, not just the one on air. A bed left un-advanced would stall its
    /// decoder against the ring buffer and sit at the wrong position, so switching to it
    /// would be neither instant nor sample-accurate. Advancing all of them reduces the
    /// switch to a choice of buffer, and lets each keep its own offset.
    /// </summary>
    private AudioBed? AdvanceBeds(List<AudioBed> beds)
    {
        if (beds.Count == 0) return null;

        for (int i = 0; i < beds.Count; i++)
            beds[i].Advance(GetTrackOffset(i));

        return beds[Math.Clamp(CurrentTrackIndex, 0, beds.Count - 1)];
    }

    /// <summary>Video drives the loop: the playlist wraps to fill the duration.</summary>
    private long PlayWithVideo(PlayoutEntry entry, SdiOutput output, List<AudioBed> beds,
                               ref byte[]? lastFrame, CancellationToken ct)
    {
        PlayoutRequest req = entry.Request;
        VideoFormat format = output.Format;
        IReadOnlyList<string> files = req.VideoFiles!;

        var frame = new byte[format.FrameBytes];
        long framesOut = 0;
        long? target = req.DurationFrames;
        int fileIndex = 0;
        int barrenPasses = 0;

        while (!ct.IsCancellationRequested && (target is null || framesOut < target))
        {
            string file = files[fileIndex];
            long framesFromThisFile = 0;

            // The SOM is an in-point on the media source, so it applies to the first file
            // on the first pass only; loops and later files play from their own start.
            TimeSpan? seek = fileIndex == 0 && framesOut == 0 && req.SeekOffset > TimeSpan.Zero
                ? req.SeekOffset
                : null;

            try
            {
                using var source = FrameSource.Open(_ffmpegPath!, file, format, seek);

                Report(new PlayoutStatus(PlayoutState.Playing, $"Playing {Path.GetFileName(file)}",
                    framesOut, target, Path.GetFileName(file)));

                while (!ct.IsCancellationRequested && (target is null || framesOut < target))
                {
                    if (!source.TryReadFrame(frame)) break;

                    AudioBed? onAir = AdvanceBeds(beds);

                    if (!output.PushFrame(frame, onAir?.Left, onAir?.Right))
                    {
                        Fail(entry, "The card stopped accepting frames.", framesOut, target);
                        return framesOut;
                    }

                    framesOut++;
                    framesFromThisFile++;
                    entry.FramesOut = framesOut;

                    if (framesOut % req.FrameRate == 0)
                    {
                        QueueChanged?.Invoke();
                        Report(new PlayoutStatus(PlayoutState.Playing, "", framesOut, target, Path.GetFileName(file)));
                    }
                }

                // Keep the final frame in case post-play is a freeze.
                if (framesFromThisFile > 0) lastFrame = (byte[])frame.Clone();

                // A file that ended before the duration was satisfied is worth explaining:
                // it is either genuinely short, or the decode failed and said so on stderr.
                if (target is not null && framesOut < target)
                {
                    string why = source.Diagnostics is { } d ? $" - ffmpeg said: {d}" : " (end of file)";
                    Report(new PlayoutStatus(PlayoutState.Playing,
                        $"{Path.GetFileName(file)} supplied {framesFromThisFile} frame(s){why}",
                        framesOut, target));
                }
            }
            catch (Exception ex)
            {
                Report(new PlayoutStatus(PlayoutState.Playing,
                    $"Skipping {Path.GetFileName(file)}: {ex.Message}", framesOut, target));
            }

            barrenPasses = framesFromThisFile > 0 ? 0 : barrenPasses + 1;
            if (barrenPasses >= files.Count)
            {
                Fail(entry, req.SeekOffset > TimeSpan.Zero
                    ? $"No frames could be decoded. The SOM seek was {req.SeekOffset.TotalSeconds:F1}s in - " +
                      "check the SOM against the length of the media."
                    : "No frames could be decoded from any file in the media source.",
                    framesOut, target);
                return framesOut;
            }

            fileIndex = (fileIndex + 1) % files.Count;
        }

        return framesOut;
    }

    /// <summary>
    /// No video selected: hold black and carry the audio. With nothing decoding picture the
    /// duration is what ends it, and the card's slot queue still does the pacing.
    /// </summary>
    private long PlayAudioOnly(PlayoutEntry entry, SdiOutput output, List<AudioBed> beds, CancellationToken ct)
    {
        PlayoutRequest req = entry.Request;
        long? target = req.DurationFrames;
        long framesOut = 0;

        byte[] black = output.BlackFrame();

        Report(new PlayoutStatus(PlayoutState.Playing, "No video selected - holding black screen.",
            0, target, "black"));

        while (!ct.IsCancellationRequested && (target is null || framesOut < target))
        {
            AudioBed? onAir = AdvanceBeds(beds);

            if (!output.PushFrame(black, onAir?.Left, onAir?.Right))
            {
                Fail(entry, "The card stopped accepting frames.", framesOut, target);
                return framesOut;
            }

            framesOut++;
            entry.FramesOut = framesOut;

            if (framesOut % req.FrameRate == 0)
            {
                QueueChanged?.Invoke();
                Report(new PlayoutStatus(PlayoutState.Playing, "", framesOut, target, "black"));
            }
        }

        return framesOut;
    }

    private void Fail(PlayoutEntry entry, string detail, long framesOut, long? target)
    {
        entry.State = EntryState.Failed;
        entry.Detail = detail;
        QueueChanged?.Invoke();
        Report(new PlayoutStatus(PlayoutState.Failed, detail, framesOut, target));
    }

    private void WaitForCue(PlayoutEntry entry, SdiOutput output, PostPlay fill, byte[]? lastFrame, CancellationToken ct)
    {
        PlayoutRequest req = entry.Request;
        long waitFrames = FramesUntilCue(req);

        if (waitFrames <= 0)
        {
            Report(new PlayoutStatus(PlayoutState.WaitingForCue,
                "Start timecode has already passed - starting immediately."));
            return;
        }

        var wait = new Timecode(waitFrames, req.FrameRate);
        entry.Detail = $"cues in {wait}";

        Report(new PlayoutStatus(PlayoutState.WaitingForCue,
            $"Cued on TX{req.TxChannel} for {req.Start} (in {wait}), holding {Describe(fill)}."));

        byte[] filler = FillFrame(output, fill, lastFrame);

        for (long i = 0; i < waitFrames && !ct.IsCancellationRequested; i++)
        {
            if (!output.PushFrame(filler))
            {
                Report(new PlayoutStatus(PlayoutState.Failed, "The card stopped accepting frames while cued."));
                return;
            }
        }
    }

    /// <summary>
    /// Holds the post-play fill on the output while the queue is empty. Returns false when
    /// the card gives up, which ends the engine.
    /// </summary>
    private bool HoldFill(SdiOutput output, PostPlay fill, byte[]? lastFrame, CancellationToken ct)
    {
        byte[] filler = FillFrame(output, fill, lastFrame);

        // This runs once a second for as long as the queue stays empty, so the message is
        // announced only on entering the hold - otherwise it would flood the log.
        if (!_announcedHold)
        {
            _announcedHold = true;
            Report(new PlayoutStatus(PlayoutState.PostPlay,
                $"Queue empty - holding {Describe(fill)} on TX until the next message is queued."));
        }

        // A second at a time, so a newly queued message is picked up promptly.
        for (int i = 0; i < output.Format.FrameRate && !ct.IsCancellationRequested; i++)
        {
            if (!output.PushFrame(filler)) return false;
            if (TakeNextQueued() is not null) return true;
        }

        return true;
    }

    private static byte[] FillFrame(SdiOutput output, PostPlay fill, byte[]? lastFrame) =>
        fill == PostPlay.FreezeLastFrame && lastFrame is not null ? lastFrame : output.BlackFrame();

    public static string Describe(PostPlay postPlay) =>
        postPlay == PostPlay.FreezeLastFrame ? "freeze on last frame" : "black screen";

    /// <summary>
    /// How many frames until the cue. A start timecode in the recent past cues immediately
    /// rather than waiting almost a full day for the clock to come round.
    /// </summary>
    private long FramesUntilCue(PlayoutRequest request)
    {
        if (!_timecode.TryGetCurrent(out Timecode now)) return 0;

        long perDay = 24L * 3600L * request.FrameRate;
        long delta = ((request.Start.TotalFrames - now.TotalFrames) % perDay + perDay) % perDay;

        return delta > perDay / 2 ? 0 : delta;
    }

    private void Report(PlayoutStatus status) => Progress?.Invoke(status);

    public void Dispose() => StopAll();

    /// <summary>
    /// One language's audio: its own ffmpeg process and ring buffer, its own position, and
    /// its own scratch buffers. Loops its file list so it fills however long the message runs.
    /// </summary>
    private sealed class AudioBed : IDisposable
    {
        private readonly string _ffmpegPath;
        private readonly IReadOnlyList<string> _files;
        private readonly int _frameRate;

        private AudioSource _source;
        private int _index;
        private long _position;

        public string Label { get; }
        public short[] Left { get; }
        public short[] Right { get; }

        public AudioBed(string ffmpegPath, AudioTrack track, int frameRate)
        {
            _ffmpegPath = ffmpegPath;
            _files = track.Files;
            _frameRate = frameRate;
            Label = track.Label;

            int samplesPerFrame = AudioSource.SampleRate / frameRate;
            Left = new short[samplesPerFrame];
            Right = new short[samplesPerFrame];

            _source = _files.Count > 0
                ? AudioSource.Open(ffmpegPath, _files[0], frameRate)
                : AudioSource.Silent(frameRate);
        }

        public void Advance(int offsetMs)
        {
            _source.ReadFrame(Left, Right, _position, offsetMs);
            _position += Left.Length;

            if (_files.Count > 0 && _source.Exhausted) Roll();
        }

        /// <summary>Moves to the next file in the track, wrapping so the bed never runs dry.</summary>
        private void Roll()
        {
            AudioSource spent = _source;

            _index = (_index + 1) % _files.Count;
            _source = AudioSource.Open(_ffmpegPath, _files[_index], _frameRate);
            _position = 0;

            // Disposal kills an ffmpeg process and joins the decoder thread, up to ~4 s.
            // Inline that would stall the play loop and drop frames, since PushFrame is
            // what paces playout — so the spent source is retired off-thread.
            Task.Run(spent.Dispose);
        }

        public void Dispose() => _source.Dispose();
    }
}
