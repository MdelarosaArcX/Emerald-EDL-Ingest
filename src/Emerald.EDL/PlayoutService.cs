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

/// <summary>
/// What the engine is doing, as it happens.
///
/// <paramref name="EntryId"/> and <paramref name="CurrentFilePath"/> are stamped by the engine
/// rather than passed in at every call site. The id is what ties a status back to the command
/// that caused it; the full path matters because <paramref name="CurrentFile"/> is only the
/// bare name, and two files called beds.wav in different folders are not the same media.
/// </summary>
public sealed record PlayoutStatus(
    PlayoutState State,
    string Message,
    long FramesOut = 0,
    long? FramesTotal = null,
    string? CurrentFile = null,
    string? EntryId = null,
    string? CurrentFilePath = null);

/// <summary>
/// One selectable audio track — a language — and the files behind it.
///
/// <paramref name="StreamIndex"/> is which audio stream of those files to take, counted among
/// the audio streams alone. A message whose languages are separate .wav files leaves it at -1
/// and gets each file's only track; a message whose languages are all embedded in one clip
/// has several tracks over the same file list, distinguished by this.
/// </summary>
public sealed record AudioTrack(string Label, IReadOnlyList<string> Files, int StreamIndex = -1);

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
    IReadOnlyList<AudioTrack>? AudioTracks = null)
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

    private readonly int[] _trackOffsetsMs = new int[MaxAudioTracks];

    /// <summary>
    /// Per-track gain, as a linear multiplier. 1 is the file as it is; stored as bits so it
    /// can be written from the UI thread and read from the play loop without a lock.
    /// </summary>
    private readonly long[] _trackGains = new long[MaxAudioTracks];

    /// <summary>Per-track mute. Solo is every other track muted, decided by the caller.</summary>
    private readonly int[] _trackMuted = new int[MaxAudioTracks];

    /// <summary>
    /// The last frame's peak for each track, as dBFS, for the meters.
    ///
    /// Taken after gain and mute, so the bar shows what is actually going to air rather than
    /// what is in the file — which is the whole point of watching it while trimming a level.
    /// </summary>
    private readonly long[] _trackPeaks = new long[MaxAudioTracks];

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

    /// <summary>Loudest and quietest a track can be set to: +12 dB up, silence down.</summary>
    public const double MaxGainDb = 12.0;
    public const double MinGainDb = -40.0;

    /// <summary>
    /// A track's level, in decibels. 0 is the file untouched; below <see cref="MinGainDb"/>
    /// is treated as silence, because there is no useful difference below that and a bar has
    /// to bottom out somewhere.
    ///
    /// Applied to the samples on their way to the card, so it is what goes to air — unlike
    /// the deck's monitor volume, which only changes what the operator hears.
    /// </summary>
    public double GetTrackGainDb(int track) =>
        track >= 0 && track < MaxAudioTracks
            ? BitConverter.Int64BitsToDouble(Interlocked.Read(ref _trackGains[track]))
            : 0;

    public void SetTrackGainDb(int track, double db)
    {
        if (track < 0 || track >= MaxAudioTracks) return;

        Interlocked.Exchange(ref _trackGains[track],
                             BitConverter.DoubleToInt64Bits(Math.Clamp(db, MinGainDb, MaxGainDb)));
    }

    public bool IsTrackMuted(int track) =>
        track >= 0 && track < MaxAudioTracks && Volatile.Read(ref _trackMuted[track]) != 0;

    /// <summary>
    /// Mutes a track on air. Every track is still decoded and still advanced — a muted bed
    /// that stopped reading would be at the wrong position the moment it came back — it is
    /// simply embedded as silence.
    /// </summary>
    public void SetTrackMuted(int track, bool muted)
    {
        if (track >= 0 && track < MaxAudioTracks) Volatile.Write(ref _trackMuted[track], muted ? 1 : 0);
    }

    /// <summary>
    /// Solos one track: it is unmuted and every other is muted. Passing -1 clears the solo and
    /// unmutes everything, which is how an operator gets back to hearing the whole message.
    /// </summary>
    public void SoloTrack(int track, int trackCount)
    {
        for (int i = 0; i < MaxAudioTracks; i++)
            SetTrackMuted(i, track >= 0 && i < trackCount && i != track);
    }

    /// <summary>
    /// The last frame's peak for a track, in dBFS, or <see cref="SilenceDb"/> when there was
    /// nothing. Read by the UI on its own timer; written by the play loop every frame.
    /// </summary>
    public double GetTrackPeakDb(int track) =>
        track >= 0 && track < MaxAudioTracks
            ? BitConverter.Int64BitsToDouble(Interlocked.Read(ref _trackPeaks[track]))
            : SilenceDb;

    /// <summary>The bottom of the meter scale, matching the deck's.</summary>
    public const double SilenceDb = -60.0;

    private Thread? _worker;
    private CancellationTokenSource? _cts;

    public event Action<PlayoutStatus>? Progress;
    public event Action? QueueChanged;

    public string? FfmpegPath => _ffmpegPath;

    public PlayoutService(TimecodeService timecode, string? ffmpegPath)
    {
        _timecode = timecode;
        _ffmpegPath = ffmpegPath;

        // Zero bits read back as 0 dB, which for a gain is exactly right and for a peak would
        // be a full meter before a frame has played.
        for (int i = 0; i < MaxAudioTracks; i++)
            _trackPeaks[i] = BitConverter.DoubleToInt64Bits(SilenceDb);
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
                Volatile.Write(ref _currentEntryId, entry?.Id);

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

            if (beds.Count > 0)
            {
                // The channel map is worth stating: it is what the operator patches against
                // downstream, and getting it wrong is silent until someone monitors the
                // wrong pair.
                string map = string.Join(", ",
                    beds.Select((b, i) => $"ch {i * 2 + 1}-{i * 2 + 2} \"{b.Label}\""));

                Report(new PlayoutStatus(PlayoutState.Playing,
                    $"Audio: {beds.Count} track(s), all on air - {map}.", 0, target));
            }
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
    /// Advances every bed by one frame and lays them out as SDI channels.
    ///
    /// <b>Every track is transmitted, all the time.</b> Track <c>n</c> occupies channels
    /// <c>2n+1</c> and <c>2n+2</c> — the first language on 1-2, the second on 3-4, and so on
    /// — so the choice of which language to listen to belongs to the router or the receiving
    /// device, not to Emerald. Each keeps its own live offset, which is why they are
    /// advanced individually rather than mixed.
    ///
    /// The channel list is allocated once per message and refilled in place: this runs every
    /// frame, and handing the card a fresh list 25 times a second would be pure garbage.
    /// </summary>
    private void AdvanceBeds(List<AudioBed> beds, List<short[]> channels)
    {
        for (int i = 0; i < beds.Count; i++)
        {
            beds[i].Advance(GetTrackOffset(i));

            // Level and mute are applied here, to the samples on their way to the card, and
            // the meter is read after them - so the bar shows what is going to air rather
            // than what is in the file. A muted track is still advanced, for the same reason
            // an unsent one is: stalling its decoder would leave it at the wrong position.
            double gainDb = GetTrackGainDb(i);
            bool muted = IsTrackMuted(i);

            double peak = muted ? SilenceDb : ApplyGain(beds[i], gainDb);
            Interlocked.Exchange(ref _trackPeaks[i], BitConverter.DoubleToInt64Bits(peak));

            // A bed past the card's channel count still advances — leaving it stalled would
            // put it at the wrong position if the limit ever changed — it simply is not sent.
            if (i * 2 + 1 >= channels.Count) continue;

            channels[i * 2] = muted ? beds[i].Silence : beds[i].Left;
            channels[i * 2 + 1] = muted ? beds[i].Silence : beds[i].Right;
        }
    }

    /// <summary>
    /// Scales one bed's frame in place and returns its peak in dBFS.
    ///
    /// In place because the buffers are the bed's own and are refilled every frame; a copy
    /// per track per frame would be pure garbage at 25 frames a second. Clamped rather than
    /// wrapped, so a track pushed past full scale distorts the way an overdriven desk does
    /// instead of inverting into noise.
    /// </summary>
    private static double ApplyGain(AudioBed bed, double gainDb)
    {
        double scale = gainDb == 0 ? 1.0
                     : gainDb <= MinGainDb ? 0.0
                     : Math.Pow(10, gainDb / 20.0);

        int peak = 0;

        for (int half = 0; half < 2; half++)
        {
            short[] samples = half == 0 ? bed.Left : bed.Right;

            for (int s = 0; s < samples.Length; s++)
            {
                int v = samples[s];

                if (scale != 1.0)
                {
                    v = (int)Math.Clamp(v * scale, short.MinValue, short.MaxValue);
                    samples[s] = (short)v;
                }

                int magnitude = v < 0 ? -v : v;
                if (magnitude > peak) peak = magnitude;
            }
        }

        return peak <= 0 ? SilenceDb : Math.Max(SilenceDb, 20.0 * Math.Log10(peak / 32768.0));
    }

    /// <summary>The channel buffer for one message: two entries per track, capped at the SDI limit.</summary>
    private static List<short[]> ChannelsFor(List<AudioBed> beds) =>
        new(new short[Math.Min(beds.Count * 2, SdiOutput.MaxAudioChannels)][]);

    /// <summary>Video drives the loop: the playlist wraps to fill the duration.</summary>
    private long PlayWithVideo(PlayoutEntry entry, SdiOutput output, List<AudioBed> beds,
                               ref byte[]? lastFrame, CancellationToken ct)
    {
        PlayoutRequest req = entry.Request;
        VideoFormat format = output.Format;
        IReadOnlyList<string> files = req.VideoFiles!;

        var frame = new byte[format.FrameBytes];
        List<short[]> channels = ChannelsFor(beds);
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

                // The moment this file reaches the transmitter, and the one place where both
                // the entry and the file are in scope. It is what makes "which messages used
                // this clip" a question with an answer.
                Report(new PlayoutStatus(PlayoutState.Playing, $"Playing {Path.GetFileName(file)}",
                    framesOut, target, Path.GetFileName(file), CurrentFilePath: file));

                while (!ct.IsCancellationRequested && (target is null || framesOut < target))
                {
                    if (!source.TryReadFrame(frame)) break;

                    AdvanceBeds(beds, channels);

                    if (!output.PushFrame(frame, channels))
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
        List<short[]> channels = ChannelsFor(beds);

        Report(new PlayoutStatus(PlayoutState.Playing, "No video selected - holding black screen.",
            0, target, "black"));

        while (!ct.IsCancellationRequested && (target is null || framesOut < target))
        {
            AdvanceBeds(beds, channels);

            if (!output.PushFrame(black, channels))
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
    private long FramesUntilCue(PlayoutRequest request) =>
        _timecode.TryGetCurrent(out Timecode now)
            ? FramesUntil(request.Start, now, request.FrameRate)
            : 0;

    /// <summary>
    /// Frames from <paramref name="now"/> until <paramref name="target"/> on a clock that
    /// wraps at midnight, and zero once the target is behind.
    ///
    /// "Behind" has to be a judgement, because a timecode that repeats every 24 hours cannot
    /// tell a start five seconds late from one 23 hours 59 minutes early. Half a day is the
    /// line: anything further ahead than that is read as having already gone.
    ///
    /// Public because the queue display counts down with it. The number on screen and the
    /// number the engine waits out are then the same number, which is the whole point.
    /// </summary>
    public static long FramesUntil(Timecode target, Timecode now, int frameRate)
    {
        if (frameRate <= 0) return 0;

        long perDay = 24L * 3600L * frameRate;
        long delta = ((target.TotalFrames - now.TotalFrames) % perDay + perDay) % perDay;

        return delta > perDay / 2 ? 0 : delta;
    }

    /// <summary>
    /// The one place everything this engine says goes through.
    ///
    /// Two things happen here rather than at forty call sites. The status is stamped with the
    /// entry that is running, so a line on the monitoring page can be traced back to the
    /// command that caused it — without that, the status stream and the queue are two
    /// unrelated things. And it is written to the application record.
    ///
    /// The once-a-second progress goes to the screen and not to the file: it carries no
    /// message, and a record made of heartbeats is a record with the events buried in it.
    /// </summary>
    private void Report(PlayoutStatus status)
    {
        string? entryId = Volatile.Read(ref _currentEntryId);

        if (status.EntryId is null && entryId is not null) status = status with { EntryId = entryId };

        if (status.Message.Length > 0)
        {
            ActivityLog.Shared.Write(
                LogSource.Playout,
                status.State switch
                {
                    PlayoutState.Failed => LogLevel.Error,
                    PlayoutState.WaitingForCue or PlayoutState.PostPlay or PlayoutState.Stopped => LogLevel.Warn,
                    PlayoutState.Playing or PlayoutState.Finished => LogLevel.Ok,
                    _ => LogLevel.Info,
                },
                status.Message,
                @event: $"playout.{status.State.ToString().ToLowerInvariant()}",
                correlation: status.EntryId,
                file: status.CurrentFilePath);
        }

        Progress?.Invoke(status);
    }

    /// <summary>
    /// Which entry the worker is on, so <see cref="Report"/> can stamp it.
    ///
    /// A field rather than a parameter on forty <c>Report</c> calls — and it covers the catch
    /// blocks too, which is where an id matters most and where threading one through by hand
    /// would certainly have been forgotten.
    /// </summary>
    private string? _currentEntryId;

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
        private readonly int _streamIndex;

        private AudioSource _source;
        private int _index;
        private long _position;

        public string Label { get; }
        public short[] Left { get; }
        public short[] Right { get; }

        /// <summary>A frame of silence, for a muted track. Never written to, so one is enough.</summary>
        public short[] Silence { get; }

        public AudioBed(string ffmpegPath, AudioTrack track, int frameRate)
        {
            _ffmpegPath = ffmpegPath;
            _files = track.Files;
            _frameRate = frameRate;
            _streamIndex = track.StreamIndex;
            Label = track.Label;

            int samplesPerFrame = AudioSource.SampleRate / frameRate;
            Left = new short[samplesPerFrame];
            Right = new short[samplesPerFrame];
            Silence = new short[samplesPerFrame];

            _source = _files.Count > 0
                ? AudioSource.Open(ffmpegPath, _files[0], frameRate, _streamIndex)
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
            _source = AudioSource.Open(_ffmpegPath, _files[_index], _frameRate, _streamIndex);
            _position = 0;

            // Disposal kills an ffmpeg process and joins the decoder thread, up to ~4 s.
            // Inline that would stall the play loop and drop frames, since PushFrame is
            // what paces playout — so the spent source is retired off-thread.
            Task.Run(spent.Dispose);
        }

        public void Dispose() => _source.Dispose();
    }
}
