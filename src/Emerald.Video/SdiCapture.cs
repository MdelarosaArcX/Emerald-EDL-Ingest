using Emerald.Core;
using Emerald.Deltacast;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Emerald.Video;

/// <summary>
/// Records an RX channel to disk as segmented A/V files, so the operator can play the
/// result back and measure lip-sync against what was transmitted.
///
/// Video and audio have to land in **one** muxed file for that to be any use, which needs
/// two inputs into a single ffmpeg — and a process has only one stdin. Video goes in on
/// stdin and audio through a Windows named pipe. ffmpeg does not open the second input
/// until the first has produced enough data to identify it, so video is written on its own
/// thread while the pipe connection is still pending; waiting on the connection first
/// deadlocks.
/// </summary>
public sealed class SdiCapture : IDisposable
{
    private const int SampleRate = 48000;
    private const uint IoTimeoutMs = 2000;

    /// <summary>About a third of a second of slack before frames start being dropped.</summary>
    private const int VideoQueueDepth = 8;
    private const int AudioQueueDepth = 200;

    private readonly object _gate = new();

    private Thread? _worker;
    private CancellationTokenSource? _cts;

    // Held so Stop() can tear the pipes down. Writes to ffmpeg's stdin and to the audio
    // pipe block when the encoder stops draining them, and a blocked write does not observe
    // the cancellation token — closing the streams is what makes it unwind.
    private Process? _ffmpeg;
    private NamedPipeServerStream? _audioPipe;

    public bool IsRunning => _worker is { IsAlive: true };
    public string? LastError { get; private set; }

    /// <summary>Raised for anything worth putting in the operator's log.</summary>
    public event Action<string, bool>? Message;   // (text, isProblem)

    /// <summary>
    /// A decimated BGRA frame off the receiver, about twelve a second, so the confidence
    /// monitor keeps a picture while this is recording.
    ///
    /// The card allows one open handle per input, so the shell's own preview has to let go
    /// before recording can start — and an operator watching a recording go out wants the
    /// picture most at exactly that moment. Rather than a second claim on the receiver that
    /// cannot be granted, the recorder passes on what it is already reading. The buffer is
    /// reused, so a handler must copy out of it before returning.
    /// </summary>
    public event Action<byte[], int, int>? PreviewFrame;   // (bgra, width, height)

    /// <summary>
    /// Raised once, on the capture thread, when the recording has stopped and ffmpeg has
    /// finalised its files — whether it ran to its frame limit, was stopped, or failed.
    ///
    /// A scheduled ingest cannot poll <see cref="IsRunning"/> for this: the worker exits
    /// well before the encoder has closed its containers, and a file measured in that gap
    /// reads as short or truncated.
    /// </summary>
    public event Action<CaptureResult>? Finished;

    /// <summary>
    /// Every frame off the receiver, raised on the capture thread.
    ///
    /// The arrays are freshly allocated per frame and become the handler's own to keep - unlike
    /// <see cref="PreviewFrame"/>, which hands out a reused buffer. What it shares with
    /// PreviewFrame is the contract that matters: <b>a handler must not block</b>. This runs
    /// inside the slot loop, which is the receiver's clock, and the card has four slots of
    /// headroom. Hand the frame to a queue and return.
    /// </summary>
    public event Action<CapturedFrame>? Frame;

    /// <summary>
    /// The format the receiver actually locked to, raised once, before any frame. The
    /// recording is built from the operator's chosen rate, which is not necessarily what is
    /// on the wire - anything that has to match the receiver exactly needs this instead.
    /// </summary>
    public event Action<CaptureFormat>? FormatDetected;

    /// <summary>
    /// Every slot's embedded audio, de-interleaved into channels, raised on the capture
    /// thread — for meters and for the speaker monitor.
    ///
    /// The reader is handed over rather than a copy, because both of those want the channels
    /// separately and this is the only point at which they exist that way. Its buffers are
    /// reused on the next slot, so a handler reads what it needs and returns; like every other
    /// event here, <b>it must not block</b>.
    /// </summary>
    public event Action<SdiAudioReader>? Audio;

    /// <summary>How many frames have been taken off the receiver so far, for a progress display.</summary>
    public long FramesRecorded => Interlocked.Read(ref _framesRecorded);

    private long _framesRecorded;

    /// <summary>
    /// Records the receiver named in <paramref name="request"/>. Build one with
    /// <see cref="RecordingSetup.TryBuild"/>, which is where a missing ffmpeg or an
    /// impossible codec is caught, so by this point there is nothing left to validate.
    /// </summary>
    public void Start(CaptureRequest request)
    {
        Stop();

        CaptureRequest sanitised = request with { NamePrefix = SanitisePrefix(request.NamePrefix) };

        LastError = null;
        Interlocked.Exchange(ref _framesRecorded, 0);

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        _worker = new Thread(() => Run(sanitised, ct))
        {
            IsBackground = true,
            Name = "SDI capture",
        };

        _worker.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();

        // Give the loop a moment to notice the token on its own, so the encoder gets a clean
        // end-of-stream and finalises the file properly.
        if (_worker is { IsAlive: true } && !_worker.Join(TimeSpan.FromSeconds(2)))
        {
            // Still going: it is parked in a write the token cannot interrupt. Closing the
            // far end turns that into an exception the worker can unwind from.
            try { _ffmpeg?.StandardInput.BaseStream.Close(); } catch { }
            try { _audioPipe?.Dispose(); } catch { }
            _worker.Join(TimeSpan.FromSeconds(6));
        }

        _cts?.Dispose();
        _cts = null;
        _worker = null;
    }

    // ------------------------------------------------------------------ worker

    private void Run(CaptureRequest request, CancellationToken ct)
    {
        string folder = request.Folder;
        uint boardIndex = request.BoardIndex;
        int rxChannel = request.RxChannel;

        IntPtr board = IntPtr.Zero, stream = IntPtr.Zero;
        Process? ffmpeg = null;
        NamedPipeServerStream? audioPipe = null;
        SdiAudioReader? audio = null;
        RxLease? lease = null;

        // Reported to Finished once the encoder has closed its files.
        long framesTaken = 0;
        bool reachedLimit = false;
        CaptureFormat? recordedFormat = null;

        try
        {
            Directory.CreateDirectory(folder);

            // Claim the receiver before touching it. A live preview on the same input holds
            // a yielding lease and is closed by this call; anything else is a real conflict.
            try
            {
                lease = RxLease.Acquire(boardIndex, rxChannel, "recording");
            }
            catch (RxBusyException busy)
            {
                Fail($"Capture: {busy.Message}");
                return;
            }

            uint rc = VideoMasterHD.VHD_OpenBoardHandle(boardIndex, ref board, IntPtr.Zero, 0);
            if (rc != 0) { Fail($"Capture: cannot open board {boardIndex} (error {rc})."); return; }

            int setupLock = 0;

            // JOINED so the slots carry ANC, which is where embedded audio lives.
            rc = VideoMasterHD.VHD_OpenStreamHandle(board, VideoMasterHD.RxStreamType(rxChannel),
                    VideoMasterHD.VHD_SDI_STPROC_JOINED, ref setupLock, ref stream, IntPtr.Zero);

            if (rc != 0)
            {
                Fail(rc == 18
                    ? $"Capture: RX{rxChannel} is already open in another application - close dCARE and try again."
                    : $"Capture: cannot open RX{rxChannel} (error {rc}).");
                return;
            }

            // The receiver may not have locked yet at the moment capture starts, so give it
            // a little time rather than giving up on a single look.
            uint std = 0;
            uint detectRc = 0;
            bool locked = false;

            for (int attempt = 0; attempt < 20 && !ct.IsCancellationRequested; attempt++)
            {
                detectRc = VideoMasterHD.VHD_GetStreamPropertyEx(
                    stream, VideoMasterHD.VHD_SDI_SP_VIDEO_STANDARD, 1, ref std);

                if (detectRc == 0) { locked = true; break; }
                Thread.Sleep(100);
            }

            if (!locked)
            {
                Fail($"Capture: no signal on RX{rxChannel} after 2s (detect error {detectRc}) - nothing to record.");
                return;
            }

            CaptureFormat format = CaptureFormat.FromStandard(std, request.FrameRate);
            recordedFormat = format;

            // Announced before any frame, so anything that has to match the receiver exactly
            // - a delay line sizing its ring - can be built from what is really on the wire
            // rather than from the rate the operator picked.
            try { FormatDetected?.Invoke(format); }
            catch (Exception ex) { Fail($"Capture: {ex.Message}"); return; }

            VideoMasterHD.VHD_SetStreamProperty(stream, VideoMasterHD.VHD_SDI_SP_VIDEO_STANDARD, std);
            VideoMasterHD.VHD_SetStreamProperty(stream, VideoMasterHD.VHD_CORE_SP_BUFFER_PACKING,
                                                VideoMasterHD.VHD_BUFPACK_VIDEO_YUV422_8);
            VideoMasterHD.VHD_SetStreamProperty(stream, VideoMasterHD.VHD_CORE_SP_TRANSFER_SCHEME,
                                                VideoMasterHD.VHD_TRANSFER_SLAVED);
            VideoMasterHD.VHD_SetStreamProperty(stream, VideoMasterHD.VHD_CORE_SP_BUFFERQUEUE_DEPTH, 4);
            VideoMasterHD.VHD_SetStreamProperty(stream, VideoMasterHD.VHD_CORE_SP_IO_TIMEOUT, IoTimeoutMs);

            (int proxyWidth, int proxyHeight) = RecordingProfile.HalfRaster(format);

            Report($"Capture: RX{rxChannel} locked to {format.Name}, recording {request.Profile.SegmentSeconds}s " +
                   $"segments to {folder} - {proxyWidth}x{proxyHeight} {RecordingProfile.LowRes.VideoLabel} proxy " +
                   $"and {RecordingProfile.HighRes.VideoLabel} master");

            rc = VideoMasterHD.VHD_StartStream(stream);
            if (rc != 0) { Fail($"Capture: StartStream failed (error {rc})."); return; }

            audio = new SdiAudioReader(SdiAudioReader.MaxChannels, format.FrameRate);

            // How many stereo pairs the feed is carrying has to be settled before the encoder
            // starts, because the number of channels on the audio pipe is fixed for the life
            // of the process. So the first slots are read for their audio alone, before a
            // frame is recorded. A feed on channels 1-2 records exactly as it always did.
            int pairs = request.AudioPairs ?? DetectPairs(stream, audio, ct);
            pairs = Math.Clamp(pairs, 1, SdiAudioReader.MaxPairs);

            Report(pairs == 1
                ? "Capture: audio on channels 1-2."
                : $"Capture: audio on {pairs} pairs (channels 1-{pairs * 2}), one track each.");

            string pipeName = $"edlcap_{Environment.ProcessId}_{Guid.NewGuid():N}";
            audioPipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1,
                                                  PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

            ffmpeg = StartEncoder(request, pipeName, format, pairs);
            _ffmpeg = ffmpeg;
            _audioPipe = audioPipe;
            Task connect = audioPipe.WaitForConnectionAsync(ct);

            int recordChannels = pairs * 2;

            var frame = new byte[format.FrameBytes];
            int silenceBytes = SampleRate / format.FrameRate * recordChannels * 2;
            var interleaved = new byte[silenceBytes * 4];

            // Preview tap. Two buffers used alternately, so the frame being handed to the UI
            // is never the one being written; converting every slot would cost a core for a
            // picture nobody is measuring.
            int previewWidth = UyvyPreview.OutputWidth(format.Width);
            int previewHeight = UyvyPreview.OutputHeight(format.Height);
            int previewEvery = UyvyPreview.ConvertEvery(format.FrameRate);
            var previewBuffers = new[]
            {
                UyvyPreview.CreateBuffer(format.Width, format.Height),
                UyvyPreview.CreateBuffer(format.Width, format.Height),
            };
            int previewNext = 0;
            Stream videoOut = ffmpeg.StandardInput.BaseStream;

            // Video and audio are written by separate threads. A frame is far larger than a
            // pipe buffer, so a video write blocks until ffmpeg drains it — and ffmpeg will
            // not drain more video until it has the matching audio to interleave. Writing
            // both from one thread deadlocks after a handful of frames.
            using var videoQueue = new BlockingCollection<byte[]>(VideoQueueDepth);
            using var audioQueue = new BlockingCollection<byte[]>(AudioQueueDepth);

            long dropped = 0;

            var videoWriter = new Thread(() =>
            {
                try { foreach (byte[] b in videoQueue.GetConsumingEnumerable()) videoOut.Write(b, 0, b.Length); }
                catch { /* torn down */ }
                finally { try { videoOut.Close(); } catch { } }
            })
            { IsBackground = true, Name = "capture video writer" };

            var audioWriter = new Thread(() =>
            {
                try { foreach (byte[] b in audioQueue.GetConsumingEnumerable()) audioPipe.Write(b, 0, b.Length); }
                catch { /* torn down */ }
                finally { try { audioPipe.Dispose(); } catch { } }
            })
            { IsBackground = true, Name = "capture audio writer" };

            videoWriter.Start();
            audioWriter.Start();

            long frames = 0;
            bool pipeReady = false;
            int lockFailures = 0;
            bool warnedNoSlots = false;
            var started = Stopwatch.StartNew();

            Report($"Capture: stream started on RX{rxChannel}, waiting for slots...");

            // Frames off the receiver are the clock a frame limit is counted on. A wall-clock
            // stop would end the file a frame or two either side of where the operator asked,
            // and on an ingest that is the difference between catching the last word and not.
            while (!ct.IsCancellationRequested &&
                   (request.FrameLimit is not { } limit || frames < limit))
            {
                IntPtr slot = IntPtr.Zero;
                if (VideoMasterHD.VHD_LockSlotHandle(stream, ref slot) != 0)
                {
                    lockFailures++;

                    if (!warnedNoSlots && frames == 0 && started.Elapsed > TimeSpan.FromSeconds(3))
                    {
                        warnedNoSlots = true;
                        Fail($"Capture: no slots arriving from RX{rxChannel} after {lockFailures} lock timeouts.");
                    }

                    continue;
                }

                try
                {
                    IntPtr buffer = IntPtr.Zero;
                    uint size = 0;

                    // The frame and its audio, held until both are in hand: the tap wants
                    // them together, and they are only genuinely the same frame while this
                    // slot is locked.
                    byte[]? tapVideo = null;
                    int tapVideoBytes = 0;

                    if (VideoMasterHD.VHD_GetSlotBuffer(slot, VideoMasterHD.VHD_SDI_BT_VIDEO, ref buffer, ref size) == 0
                        && buffer != IntPtr.Zero)
                    {
                        int take = Math.Min(frame.Length, (int)size);
                        var copy = new byte[take];
                        Marshal.Copy(buffer, copy, 0, take);

                        // The same array goes to both consumers. Neither writes to it, so the
                        // delay line costs no second copy of a four-megabyte frame.
                        tapVideo = copy;
                        tapVideoBytes = take;

                        // Never block the slot loop: a stalled encoder must cost frames, not
                        // back up into the receiver and cause overruns.
                        if (!videoQueue.TryAdd(copy)) dropped++;

                        if (PreviewFrame is not null && size >= (uint)format.FrameBytes &&
                            frames % previewEvery == 0)
                        {
                            UyvyPreview.ToBgra(buffer, format.Width, previewBuffers[previewNext],
                                               previewWidth, previewHeight);

                            PreviewFrame.Invoke(previewBuffers[previewNext], previewWidth, previewHeight);
                            previewNext ^= 1;
                        }

                        frames++;
                        Interlocked.Exchange(ref _framesRecorded, frames);
                        if (frames == 1) Report("Capture: first frame received.");
                    }

                    // ffmpeg only opens the audio pipe once video has identified input 0.
                    if (!pipeReady && connect.IsCompleted)
                    {
                        pipeReady = true;
                        Report("Capture: encoder attached to the audio pipe.");
                    }

                    // Audio is extracted for whoever wants it. It used to be pulled only once
                    // ffmpeg had attached its pipe, which is fine for a recording that starts
                    // when the encoder is ready — but a delay line needs the audio that came
                    // with frame zero, and the encoder attaches a few frames in.
                    Action<CapturedFrame>? tap = Frame;
                    Action<SdiAudioReader>? listener = Audio;
                    byte[]? tapAudio = null;
                    int tapAudioBytes = 0;
                    bool audioReal = false;

                    if (pipeReady || tap is not null || listener is not null)
                    {
                        int samples = audio.Read(slot);
                        int bytes = samples > 0 ? audio.Interleave(interleaved, recordChannels) : 0;
                        audioReal = bytes > 0;

                        // Meters and the speaker monitor read the de-interleaved channels
                        // directly, before anything is copied for the encoder — this is the
                        // only place the audio exists as separate channels, and both of them
                        // want it that way round.
                        if (listener is not null)
                        {
                            try { listener.Invoke(audio); }
                            catch { /* a meter that throws must not stop a recording */ }
                        }

                        // A frame's worth of audio goes out even when the slot carried none —
                        // black during a cue has no embedded audio, and a muxer starved on one
                        // input stops draining the other, which deadlocks the whole capture.
                        // Silence keeps the two inputs advancing together, and a transmitter
                        // needs continuous audio for the same reason.
                        if (bytes <= 0)
                        {
                            bytes = silenceBytes;
                            Array.Clear(interleaved, 0, bytes);
                        }

                        var chunk = new byte[bytes];
                        Buffer.BlockCopy(interleaved, 0, chunk, 0, bytes);

                        if (pipeReady) audioQueue.TryAdd(chunk);

                        tapAudio = chunk;
                        tapAudioBytes = bytes;
                    }

                    // Last, with both halves of the frame in hand. The handler must not block:
                    // this is the receiver's clock.
                    if (tap is not null && tapVideo is not null)
                    {
                        try
                        {
                            tap.Invoke(new CapturedFrame(tapVideo, tapVideoBytes,
                                                         tapAudio, tapAudioBytes, audioReal,
                                                         frames - 1));
                        }
                        catch
                        {
                            // A subscriber that throws must not take the recording down with it.
                        }
                    }
                }
                finally
                {
                    VideoMasterHD.VHD_UnlockSlotHandle(slot);
                }

                if (frames > 0 && frames % (format.FrameRate * 30) == 0)
                    Report($"Capture: {new Timecode(frames, format.FrameRate)} recorded");
            }

            framesTaken = frames;
            reachedLimit = request.FrameLimit is { } wanted && frames >= wanted;

            videoQueue.CompleteAdding();
            audioQueue.CompleteAdding();
            videoWriter.Join(TimeSpan.FromSeconds(5));
            audioWriter.Join(TimeSpan.FromSeconds(5));
            string tally = dropped > 0 ? frames + " frames, " + dropped + " dropped" : frames + " frames";
            Report($"Capture: stopped after {new Timecode(frames, format.FrameRate)} ({tally}).");

            lock (_encoderErrors)
                foreach (string e in _encoderErrors.Take(5)) Fail("Capture encoder: " + e);
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            // Torn down mid-write; nothing to report.
            _ = ex;
        }
        catch (Exception ex)
        {
            Fail($"Capture failed: {ex.Message}");
        }
        finally
        {
            try { ffmpeg?.StandardInput.BaseStream.Close(); } catch { }
            try { audioPipe?.Dispose(); } catch { }
            try { if (ffmpeg is { HasExited: false }) ffmpeg.WaitForExit(5000); } catch { }
            try { if (ffmpeg is { HasExited: false }) ffmpeg.Kill(true); } catch { }
            ffmpeg?.Dispose();

            if (stream != IntPtr.Zero) { VideoMasterHD.VHD_StopStream(stream); VideoMasterHD.VHD_CloseStreamHandle(stream); }
            if (board != IntPtr.Zero) VideoMasterHD.VHD_CloseBoardHandle(board);

            _ffmpeg = null;
            _audioPipe = null;

            lease?.Dispose();

            audio?.Dispose();

            // Last, and only once the encoder has exited: by here the containers are closed,
            // so a caller may measure the files it was promised.
            try
            {
                Finished?.Invoke(new CaptureResult(
                    Request: request,
                    Frames: framesTaken,
                    ReachedFrameLimit: reachedLimit,
                    Format: recordedFormat,
                    Error: LastError));
            }
            catch
            {
                // A subscriber that throws must not take the capture thread down with it.
            }
        }
    }

    /// <summary>
    /// Keeps a caller-supplied name to what a filename can hold. An operator's title is
    /// free text, and ffmpeg is handed the result as a path.
    /// </summary>
    public static string SanitisePrefix(string namePrefix)
    {
        var clean = new string((namePrefix ?? "")
            .Trim()
            .Select(c => Path.GetInvalidFileNameChars().Contains(c) || c == '%' ? '_' : c)
            .ToArray());

        return clean.Length == 0 ? "capture" : clean[..Math.Min(clean.Length, 60)];
    }

    private readonly List<string> _encoderErrors = new();

    private Process StartEncoder(CaptureRequest request, string pipeName, CaptureFormat format, int audioPairs)
    {
        var info = new ProcessStartInfo(request.FfmpegPath)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        // The command itself is the profile's to compose: the deck offers those choices and
        // the EDL records with whatever they were left on.
        foreach (string a in request.Profile.EncoderArguments(
                     format, pipeName, request.Folder, request.NamePrefix, SampleRate,
                     request.SingleFile, request.StartTimecode, request.ExtraAudio, audioPairs))
            info.ArgumentList.Add(a);

        Process ff = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg for capture.");

        // Kept, not discarded: a silent encoder failure is otherwise indistinguishable from
        // a working recording that happens to produce an empty file.
        ff.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            lock (_encoderErrors) { if (_encoderErrors.Count < 20) _encoderErrors.Add(e.Data.Trim()); }
        };
        ff.BeginErrorReadLine();
        return ff;
    }

    /// <summary>
    /// Counts the stereo pairs on the wire, by reading the first slots for their audio alone.
    ///
    /// It has to be settled here because the encoder's channel count is fixed once ffmpeg
    /// starts, and nothing else knows how many languages the feed is carrying. Two seconds of
    /// looking is generous; a feed that has produced no audio by then is recorded as the
    /// single pair it has always been, which is also the right answer for silence.
    ///
    /// The slots read here are discarded. At 25 fps that is well under a second of picture,
    /// spent before the recording is considered to have started.
    /// </summary>
    private static int DetectPairs(IntPtr stream, SdiAudioReader reader, CancellationToken ct)
    {
        var looking = Stopwatch.StartNew();
        int best = 0;

        while (looking.Elapsed < TimeSpan.FromSeconds(2) && !ct.IsCancellationRequested)
        {
            IntPtr slot = IntPtr.Zero;
            if (VideoMasterHD.VHD_LockSlotHandle(stream, ref slot) != 0) continue;

            try
            {
                if (reader.Read(slot) > 0) best = Math.Max(best, reader.PairsPresent());
            }
            finally
            {
                VideoMasterHD.VHD_UnlockSlotHandle(slot);
            }

            // A pair can be momentarily quiet without being absent, so several slots are read
            // rather than believing the first one that carries anything.
            if (best > 0 && looking.Elapsed > TimeSpan.FromMilliseconds(300)) break;
        }

        return Math.Max(1, best);
    }

    private void Report(string text) => Message?.Invoke(text, false);

    private void Fail(string text)
    {
        LastError = text;
        Message?.Invoke(text, true);
    }

    public void Dispose() => Stop();
}

/// <summary>
/// How a recording ended. Raised on the capture thread once ffmpeg has finalised its
/// files, so a handler must not call <see cref="SdiCapture.Stop"/> — that thread is the
/// one Stop waits for.
/// </summary>
/// <param name="ReachedFrameLimit">
/// True when the recording ran the whole length it was asked for. False means it was
/// stopped early or failed, which for a scheduled ingest is a short clip, not a good one.
/// </param>
public sealed record CaptureResult(
    CaptureRequest Request,
    long Frames,
    bool ReachedFrameLimit,
    CaptureFormat? Format,
    string? Error)
{
    public bool Succeeded => Error is null && Frames > 0;
}

/// <summary>
/// One frame off the receiver, with the audio that arrived in the same slot.
///
/// Video and audio are picked up inside a single lock on the slot, so the two genuinely
/// belong to each other - which is the whole reason a delay line can be fed from here rather
/// than by pairing up two streams after the fact.
/// </summary>
/// <param name="AudioReal">
/// False when the slot carried no embedded audio and silence was substituted. The silence is
/// sent either way, because a transmitter needs continuous audio, but the distinction is
/// worth reporting rather than quietly transmitting nothing.
/// </param>
public readonly record struct CapturedFrame(
    byte[] Video, int VideoBytes,
    byte[]? Audio, int AudioBytes, bool AudioReal,
    long Sequence);

/// <summary>
/// Raster and rate of whatever the RX is locked to, plus the SDI standard it came from.
///
/// <see cref="Standard"/> is kept because raster and rate alone do not identify a format:
/// 1080i50 and 1080p25 are both 1920x1080 counted at 25, and anything deciding whether a
/// frame may be transmitted has to be able to tell them apart. Losing that distinction is
/// how interlaced fields end up going out as progressive.
/// </summary>
public sealed record CaptureFormat(string Name, int Width, int Height, int FrameRate, uint Standard = uint.MaxValue)
{
    /// <summary>Used when the format did not come from the SDK's standard table.</summary>
    public const uint UnknownStandard = uint.MaxValue;

    public int FrameBytes => Width * Height * 2;

    /// <summary>True for the two standards the SDK reports as interlaced.</summary>
    public bool IsInterlaced => Standard is 2 or 3;

    /// <summary>VHD_VIDEOSTANDARD values, from VideoMasterHD_Sdi.h.</summary>
    public static CaptureFormat FromStandard(uint std, int fallbackRate) => std switch
    {
        0 => new("1080p25", 1920, 1080, 25, std),
        1 => new("1080p30", 1920, 1080, 30, std),
        2 => new("1080i50", 1920, 1080, 25, std),
        3 => new("1080i60", 1920, 1080, 30, std),
        4 => new("720p50", 1280, 720, 50, std),
        5 => new("720p60", 1280, 720, 60, std),
        6 => new("PAL", 720, 576, 25, std),
        7 => new("NTSC", 720, 486, 30, std),
        8 => new("1080p24", 1920, 1080, 24, std),
        9 => new("1080p60", 1920, 1080, 60, std),
        10 => new("1080p50", 1920, 1080, 50, std),
        _ => new($"standard {std}", 1920, 1080, fallbackRate, std),
    };
}
