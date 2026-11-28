using Emerald.Core;
using Emerald.Deltacast;
using System.Diagnostics;
using System.IO.Pipes;

namespace Emerald.Video;

/// <summary>What the IP stream of air is set to.</summary>
/// <param name="Url">Where it is sent: a MediaMTX path, by RTSP (rtsp://host:8554/onair), SRT or RTMP.</param>
/// <param name="AirWithoutCard">No SDI board: air is paced by the computer's clock and goes out only as the IP stream.</param>
public sealed record WebStreamSettings(bool Enabled, string Url, int BitrateKbps, bool AirWithoutCard, string? FfmpegPath)
{
    /// <summary>What the settings say.</summary>
    public static WebStreamSettings From(AppSettings settings) =>
        new(settings.WebStreamEnabled, settings.WebStreamUrl, settings.WebStreamBitrateKbps, settings.AirWithoutCard, settings.FfmpegPath);

    /// <summary>
    /// Written into a copy of the settings. Every window that keeps its own copy (Live Edit does)
    /// takes the stream's settings when they change anywhere, so its next save keeps them.
    /// </summary>
    public void WriteTo(AppSettings settings)
    {
        settings.WebStreamEnabled = Enabled;
        settings.WebStreamUrl = Url;
        settings.WebStreamBitrateKbps = BitrateKbps;
        settings.AirWithoutCard = AirWithoutCard;
    }
}

/// <summary>
/// What is on air, sent to the network as well as to the card, for phones and browsers.
///
/// Every air output - the straight delay, Live Edit's air, the playback deck - goes through
/// <see cref="AirOutputs.Open"/>, which wraps it so each frame sent to the card is offered here
/// too: the same frames, so the same picture and the same edits as on air. Offering copies the
/// frame into a small store and returns; it never waits, so nothing here can slow air. When the
/// store is full a frame is let go and counted.
///
/// One encoder for the whole run of air, with its own clock: it goes on through a change from one
/// output to another (Live Edit handing over to playback), sending the last picture briefly and then
/// black, so viewers keep one unbroken stream. It is H.264 on the video card where there is one,
/// with AAC and Opus from CH 1-2 (AAC for HLS, Opus for WebRTC), sent to MediaMTX, which serves it.
/// If the encoder or MediaMTX stops, it is started again by itself; air does not notice.
/// </summary>
public sealed class WebStream
{
    public static WebStream Shared { get; } = new();

    private const int SampleRate = 48000;

    private readonly object _gate = new();
    private WebStreamSettings _settings = new(false, "", 4500, false, null);

    private Thread? _thread;
    private CancellationTokenSource? _stop;

    // The air output feeding it now, the format it sends, and the store between them.
    private object? _owner;
    private long _ownerSeen;
    private VideoFormat? _format;
    private FrameRegulator? _store;
    private int _generation;
    private long _seq;

    private long _sent, _offered, _ignored;

    /// <summary>"off", "starting", "sending", "reconnecting", with why.</summary>
    public string State { get; private set; } = "off";

    /// <summary>Raised when <see cref="State"/> changes, on whatever thread changed it.</summary>
    public event Action? Changed;

    public bool Enabled => Volatile.Read(ref _settings).Enabled;

    /// <summary>No SDI board: outputs pace themselves and air is the IP stream alone.</summary>
    public bool AirWithoutCard => Volatile.Read(ref _settings).AirWithoutCard;

    public long Sent => Interlocked.Read(ref _sent);
    public long Dropped => _store?.Dropped ?? 0;

    /// <summary>The settings it is running on: the one place they are current, whichever window changed them.</summary>
    public WebStreamSettings Current => Volatile.Read(ref _settings);

    /// <summary>Raised after every <see cref="Configure"/>, on the thread that called it.</summary>
    public event Action? Configured;

    /// <summary>Takes new settings: starts, stops or restarts the encoder as needed.</summary>
    public void Configure(WebStreamSettings settings)
    {
        WebStreamSettings was;

        lock (_gate)
        {
            was = _settings;
            Volatile.Write(ref _settings, settings);
        }

        bool restart = was.Enabled && settings.Enabled &&
                       (was.Url != settings.Url || was.BitrateKbps != settings.BitrateKbps || was.FfmpegPath != settings.FfmpegPath);

        if (!settings.Enabled || restart) StopThread();
        if (settings.Enabled && _thread is null) StartThread();

        try { Configured?.Invoke(); } catch { }
    }

    // ------------------------------------------------------------------ from the air outputs

    /// <summary>
    /// A frame that has just gone to air. Copied and queued; never waits. From one output at a time:
    /// a second output on air at once is not mixed in - the one that was there first keeps the stream
    /// until it has been quiet for a moment.
    /// </summary>
    internal void Offer(object owner, VideoFormat format, byte[] frame, IReadOnlyList<short[]>? audio)
    {
        if (!Volatile.Read(ref _settings).Enabled) return;

        long now = Environment.TickCount64;
        FrameRegulator? store;
        int generation;
        long seq;

        lock (_gate)
        {
            if (_owner is not null && !ReferenceEquals(_owner, owner) && now - _ownerSeen < 300)
            {
                _ignored++;
                return;
            }

            _owner = owner;
            _ownerSeen = now;

            // Another format: the encoder is made again for it, on its own thread.
            if (_format is null || _format.Width != format.Width || _format.Height != format.Height ||
                _format.FrameRate != format.FrameRate)
            {
                _format = format;
                _store = null;
                return;
            }

            store = _store;
            generation = _generation;
            seq = _seq++;
        }

        if (store is null || frame.Length < format.FrameBytes) return;

        byte[] copy = store.Rent();
        Buffer.BlockCopy(frame, 0, copy, 0, format.FrameBytes);

        int samples = SampleRate / Math.Max(1, format.FrameRate);
        var sound = new byte[samples * 4];

        if (audio is { Count: > 0 })
        {
            short[] left = audio[0];
            short[] right = audio.Count > 1 ? audio[1] : audio[0];
            int n = Math.Min(samples, Math.Min(left.Length, right.Length));

            for (int s = 0; s < n; s++)
            {
                int o = s * 4;
                sound[o] = (byte)left[s];
                sound[o + 1] = (byte)(left[s] >> 8);
                sound[o + 2] = (byte)right[s];
                sound[o + 3] = (byte)(right[s] >> 8);
            }
        }

        Interlocked.Increment(ref _offered);
        store.PushVideo(generation, seq, copy);
        store.PushAudio(generation, seq, sound);
    }

    /// <summary>An output going off air lets go of the stream at once, so the next one takes it straight away.</summary>
    internal void Release(object owner)
    {
        lock (_gate)
            if (ReferenceEquals(_owner, owner)) _owner = null;
    }

    // ------------------------------------------------------------------ the encoder

    private void StartThread()
    {
        _stop = new CancellationTokenSource();
        CancellationToken ct = _stop.Token;

        _thread = new Thread(() => Run(ct)) { IsBackground = true, Name = "IP stream of air" };
        _thread.Start();
    }

    private void StopThread()
    {
        _stop?.Cancel();
        _thread?.Join(5000);
        _stop?.Dispose();
        _stop = null;
        _thread = null;

        SetState("off");
    }

    private void Run(CancellationToken ct)
    {
        bool saidDown = false;

        while (!ct.IsCancellationRequested)
        {
            WebStreamSettings settings = Volatile.Read(ref _settings);
            string? ffmpeg = Ffmpeg.Locate(settings.FfmpegPath ?? "");

            if (ffmpeg is null)
            {
                SetState("ffmpeg was not found");
                if (ct.WaitHandle.WaitOne(10_000)) return;
                continue;
            }

            if (string.IsNullOrWhiteSpace(settings.Url))
            {
                SetState("no address to send to");
                if (ct.WaitHandle.WaitOne(2000)) return;
                continue;
            }

            // Air's format once something has been on air; 1080p25 black until then.
            VideoFormat format;
            FrameRegulator store;

            lock (_gate)
            {
                _format ??= VideoFormats.Default;
                format = _format;
                _store = store = new FrameRegulator(format.FrameBytes, 2, 6, Math.Max(1, format.FrameRate / 2));
                _generation = store.Reset();
                _seq = 0;
            }

            string? why = null;

            try
            {
                why = SendOnce(ffmpeg, settings, format, store, ct, ref saidDown);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                why = ex.Message;
            }

            if (ct.IsCancellationRequested) return;

            // Stopped for a new format: straight back.
            lock (_gate) if (!ReferenceEquals(_store, store)) continue;

            if (!saidDown)
            {
                saidDown = true;
                ActivityLog.Shared.Warn(LogSource.Playback,
                    $"IP STREAM stopped sending to {IpSource.Mask(settings.Url)}" + (why is null ? "" : $" ({why})") +
                    " - trying again every 3 s. Air is not affected.",
                    @event: "webstream.down");
            }

            SetState($"reconnecting{(why is null ? "" : " - " + why)}");
            if (ct.WaitHandle.WaitOne(3000)) return;
        }
    }

    /// <summary>One run of the encoder, until it stops or the format changes. Why it stopped, if it is known.</summary>
    private string? SendOnce(string ffmpeg, WebStreamSettings settings, VideoFormat format, FrameRegulator store,
                             CancellationToken ct, ref bool saidDown)
    {
        string pipeName = $"emerald_web_{Environment.ProcessId}_{Guid.NewGuid():N}"[..40];
        int samples = SampleRate / Math.Max(1, format.FrameRate);
        var silence = new byte[samples * 4];

        using var pipe = new NamedPipeServerStream(pipeName, PipeDirection.Out, 1, PipeTransmissionMode.Byte,
                                                   PipeOptions.Asynchronous, 0, 1 << 20);

        ProxyEncoder? gpu = WebStreamCommand.ChooseVideo(ffmpeg, format, settings.BitrateKbps);

        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardInput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in WebStreamCommand.Arguments(format, pipeName, settings.Url, settings.BitrateKbps, gpu))
            info.ArgumentList.Add(a);

        SetState("starting");

        using Process ff = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg for the IP stream.");
        MemoryGauges.NameChild(ff.Id, "IP stream encoder");

        string? lastError = null;
        ff.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastError = e.Data.Trim(); };
        ff.BeginErrorReadLine();

        using CancellationTokenRegistration onStop = ct.Register(() => { try { ff.Kill(); } catch { } });

        // ffmpeg opens the sound only once the picture has told it what input 0 is, so the picture
        // goes first while the pipe waits; the frames sent before it connected get silence then.
        Task connected = pipe.WaitForConnectionAsync(ct);

        Stream picture = ff.StandardInput.BaseStream;
        using var clock = new FrameClock(format.FrameRate);

        long videoWritten = 0, audioWritten = 0;
        long saidAt = Environment.TickCount64;
        bool saidUp = false;

        try
        {
            while (!ct.IsCancellationRequested && !ff.HasExited)
            {
                lock (_gate) if (!ReferenceEquals(_store, store)) return null;   // a new format

                clock.Wait();
                RegulatedFrame frame = store.Pull();

                if (connected.IsCompletedSuccessfully)
                {
                    while (audioWritten < videoWritten) { pipe.Write(silence); audioWritten++; }

                    pipe.Write(frame.Audio is { Length: > 0 } a && a.Length == silence.Length ? a : silence);
                    audioWritten++;
                }

                picture.Write(frame.Video, 0, format.FrameBytes);
                videoWritten++;
                Interlocked.Increment(ref _sent);

                if (!saidUp && videoWritten == format.FrameRate * 2L)
                {
                    saidUp = true;
                    saidDown = false;

                    ActivityLog.Shared.Ok(LogSource.Playback,
                        $"IP STREAM sending air to {IpSource.Mask(settings.Url)} at {format.Name}, " +
                        $"{settings.BitrateKbps} kbit/s, {(gpu?.Label ?? "H.264 on the CPU")}.",
                        @event: "webstream.sending");
                    SetState($"sending {format.Name}");
                }

                if (Environment.TickCount64 - saidAt > 60_000)
                {
                    saidAt = Environment.TickCount64;

                    if (store.Dropped > 0 || store.Repeated > 0)
                        ActivityLog.Shared.Info(LogSource.Playback,
                            $"IP STREAM {store.Dropped} frame(s) let go and {store.Repeated} shown twice so far " +
                            "(the stream's own clock against air's; air is not affected).",
                            @event: "webstream.stats",
                            detail: $"sent {Sent}\nfresh {store.Fresh}\nrepeated {store.Repeated}\n" +
                                    $"black {store.Blacks}\ndropped {store.Dropped}\nslips {clock.Slipped}\nignored {_ignored}");
                }
            }
        }
        catch (IOException)
        {
            // ffmpeg went: MediaMTX refused or went away.
        }
        finally
        {
            try { picture.Close(); } catch { }
            try { if (!ff.WaitForExit(3000)) ff.Kill(); } catch { }
        }

        return lastError is null ? null : (lastError.Length > 160 ? lastError[..160] + "…" : lastError);
    }

    private void SetState(string state)
    {
        if (State == state) return;
        State = state;

        try { Changed?.Invoke(); } catch { }
    }
}

/// <summary>The ffmpeg that sends air as the IP stream.</summary>
public static class WebStreamCommand
{
    private static (string Ffmpeg, ProxyEncoder? Encoder)? _chosen;

    /// <summary>
    /// H.264 on the video card, or null for the CPU. A browser plays H.264; HEVC it may not. Found
    /// once - trying the card takes seconds - and kept.
    /// </summary>
    public static ProxyEncoder? ChooseVideo(string ffmpeg, VideoFormat format, int bitrateKbps)
    {
        if (_chosen is { } kept && kept.Ffmpeg == ffmpeg) return kept.Encoder;

        ProxyEncoder? chosen = Probe(ffmpeg, format, bitrateKbps);
        _chosen = (ffmpeg, chosen);
        return chosen;
    }

    private static ProxyEncoder? Probe(string ffmpeg, VideoFormat format, int bitrateKbps)
    {
        try
        {
            ProxyEncoder? card = GpuEncoder.Choose(ffmpeg, new CaptureFormat(format.Name, format.Width, format.Height, format.FrameRate),
                                                   bitrateKbps, out _);
            return card?.Vendor switch
            {
                GpuVendor.Nvidia => ProxyEncoder.H264Nvenc,
                GpuVendor.Intel => ProxyEncoder.H264Qsv,
                _ => null,
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// UYVY on stdin and stereo 16-bit on the pipe, out as H.264 with AAC (for HLS) and Opus (for
    /// WebRTC) to <paramref name="url"/>. A keyframe every second, no B-frames: what low-latency
    /// HLS and WebRTC want.
    /// </summary>
    public static IReadOnlyList<string> Arguments(VideoFormat format, string pipeName, string url, int bitrateKbps,
                                                  ProxyEncoder? gpu)
    {
        int rate = Math.Max(1, format.FrameRate);
        bitrateKbps = Math.Clamp(bitrateKbps, 500, 20000);

        var a = new List<string>
        {
            "-hide_banner", "-nostats", "-loglevel", "error",
            "-f", "rawvideo", "-pix_fmt", "uyvy422", "-s", $"{format.Width}x{format.Height}", "-r", rate.ToString(),
            "-i", "pipe:0",
            "-f", "s16le", "-ar", "48000", "-ac", "2", "-i", $@"\\.\pipe\{pipeName}",
            "-map", "0:v", "-map", "1:a", "-map", "1:a",
        };

        if (gpu is not null)
        {
            a.AddRange(gpu.Arguments(rate, bitrateKbps));
            a.AddRange(["-g", rate.ToString(), "-pix_fmt", gpu.PixelFormat]);
        }
        else
        {
            a.AddRange(["-c:v", "libx264", "-preset", "veryfast", "-tune", "zerolatency", "-profile:v", "high",
                        "-bf", "0", "-g", rate.ToString(),
                        "-b:v", $"{bitrateKbps}k", "-maxrate", $"{bitrateKbps}k", "-bufsize", $"{bitrateKbps * 2}k",
                        "-pix_fmt", "yuv420p"]);
        }

        a.AddRange(["-c:a:0", "aac", "-b:a:0", "128k", "-c:a:1", "libopus", "-b:a:1", "128k", "-ar", "48000"]);

        string lower = url.Trim().ToLowerInvariant();

        if (lower.StartsWith("rtsp")) a.AddRange(["-f", "rtsp", "-rtsp_transport", "tcp"]);
        else if (lower.StartsWith("rtmp")) a.AddRange(["-f", "flv"]);
        else a.AddRange(["-f", "mpegts"]);

        a.Add(url.Trim());
        return a;
    }
}

/// <summary>The formats the IP stream is sent in.</summary>
public static class VideoFormats
{
    /// <summary>What is sent before anything has been on air: 1080p25.</summary>
    public static VideoFormat Default { get; } = new("1080p25", 0, 0, 1920, 1080, 25);
}

/// <summary>
/// Opens an air output: the card's port or ports as always, or - with no SDI board - one paced by
/// the computer's clock. Either way wrapped so what goes to air goes to the IP stream too.
/// </summary>
public static class AirOutputs
{
    public static ISdiOutput Open(uint boardIndex, IReadOnlyList<int> txChannels, VideoFormat format,
                                  uint queueDepth = SdiOutput.DefaultQueueDepth)
    {
        ISdiOutput output = WebStream.Shared.AirWithoutCard
            ? new SoftClockOutput(format)
            : txChannels.Count == 1
                ? SdiOutput.Open(boardIndex, txChannels[0], format, queueDepth)
                : SdiOutputFan.Open(boardIndex, txChannels, format, queueDepth);

        return new WebStreamTap(output);
    }

    /// <summary>The card's own output under the IP stream's wrapper, for what only the card can say.</summary>
    public static ISdiOutput Card(ISdiOutput output) => output is WebStreamTap tap ? tap.Inner : output;
}

/// <summary>Sends to the card, then offers the same frame to the IP stream.</summary>
internal sealed class WebStreamTap(ISdiOutput inner) : ISdiOutput
{
    public ISdiOutput Inner => inner;

    public VideoFormat Format => inner.Format;

    public bool PushFrame(byte[] frame, IReadOnlyList<short[]>? audioChannels = null)
    {
        bool ok = inner.PushFrame(frame, audioChannels);

        try { WebStream.Shared.Offer(this, inner.Format, frame, audioChannels); }
        catch { /* the IP stream must never cost air a frame */ }

        return ok;
    }

    public byte[] BlackFrame() => inner.BlackFrame();

    public void Dispose()
    {
        WebStream.Shared.Release(this);
        inner.Dispose();
    }
}

/// <summary>
/// An air output with no card: takes each frame at the moment it is due, by the computer's clock,
/// as a card's free slot would. What goes to air then is the IP stream alone.
/// </summary>
public sealed class SoftClockOutput : ISdiOutput
{
    private readonly FrameClock _clock;
    private bool _started;

    public SoftClockOutput(VideoFormat format)
    {
        Format = format;
        _clock = new FrameClock(format.FrameRate);
    }

    public VideoFormat Format { get; }

    public bool PushFrame(byte[] frame, IReadOnlyList<short[]>? audioChannels = null)
    {
        if (!_started) { _started = true; _clock.Restart(); return true; }

        _clock.Wait();
        return true;
    }

    public byte[] BlackFrame()
    {
        var black = new byte[Format.FrameBytes];
        Uyvy.Black(black);
        return black;
    }

    public void Dispose() => _clock.Dispose();
}
