using Emerald.Core;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;

namespace Emerald.Video;

/// <summary>
/// An IP stream to record instead of the card's receiver.
///
/// <paramref name="Url"/> is anything ffmpeg can read live: <c>rtsp://</c>, <c>srt://</c>,
/// <c>rtmp://</c>, an <c>https://…m3u8</c> playlist - or a Twitch channel (<c>twitch.tv/name</c>
/// or <c>twitch:name</c>), which is turned into a playlist by streamlink because Twitch's own
/// address needs a token ffmpeg cannot get.
/// </summary>
public sealed record IpSource(string Url, string? StreamlinkPath = null)
{
    public bool IsTwitch =>
        Url.Contains("twitch.tv", StringComparison.OrdinalIgnoreCase) ||
        Url.StartsWith("twitch:", StringComparison.OrdinalIgnoreCase);

    /// <summary>The channel's page, for streamlink.</summary>
    public string TwitchPage
    {
        get
        {
            string u = Url.Trim();
            if (u.StartsWith("twitch:", StringComparison.OrdinalIgnoreCase))
                return "https://www.twitch.tv/" + u[7..].Trim().TrimStart('/');

            return u.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? u : "https://" + u;
        }
    }

    /// <summary>
    /// The address as it may be written down: scheme, host and the first part of the path. A stream
    /// key, a password or a passphrase is usually in the rest, and the record is not the place for it.
    /// </summary>
    public string Shown => Mask(Url);

    /// <summary>The address ffmpeg is given: the URL itself, or a Twitch channel's playlist from streamlink.</summary>
    public string? Resolve(out string? why)
    {
        why = null;
        if (!IsTwitch) return Url.Trim();

        string streamlink = string.IsNullOrWhiteSpace(StreamlinkPath) ? "streamlink" : StreamlinkPath!;

        try
        {
            var info = new ProcessStartInfo(streamlink)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            info.ArgumentList.Add("--stream-url");
            info.ArgumentList.Add(TwitchPage);
            info.ArgumentList.Add("best");

            using Process p = Process.Start(info)!;
            Task<string> output = p.StandardOutput.ReadToEndAsync();
            Task<string> errors = p.StandardError.ReadToEndAsync();

            if (!p.WaitForExit(20_000))
            {
                try { p.Kill(); } catch { }
                why = "streamlink did not answer for the Twitch channel in 20 s.";
                return null;
            }

            string link = output.Result.Trim().Split('\n').FirstOrDefault(l => l.StartsWith("http"))?.Trim() ?? "";
            if (link.Length > 0) return link;

            string said = (output.Result + " " + errors.Result).Trim();
            why = $"streamlink found no stream for {TwitchPage}" + (said.Length > 0 ? $": {(said.Length > 160 ? said[..160] + "…" : said)}" : " - is the channel live?");
            return null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            why = "a Twitch channel needs streamlink, which is not installed (free, streamlink.github.io) - " +
                  "or set its path in the settings.";
            return null;
        }
    }

    public static string Mask(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return "-";

        string u = url.Trim();
        if (u.StartsWith("twitch:", StringComparison.OrdinalIgnoreCase)) return u;

        if (!Uri.TryCreate(u, UriKind.Absolute, out Uri? uri))
            return u.Length > 24 ? u[..24] + "…" : u;

        string port = uri.IsDefaultPort || uri.Port < 0 ? "" : ":" + uri.Port;
        string[] parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        string path = parts.Length == 0 ? "" : "/" + parts[0] + (parts.Length > 1 || uri.Query.Length > 0 ? "/…" : "");

        return $"{uri.Scheme}://{uri.Host}{port}{path}";
    }
}

/// <summary>The ffmpeg that reads an IP stream for the recorder.</summary>
public static class IpReaderCommand
{
    /// <summary>
    /// For the deck's preview before recording: a small BGRA picture at a few frames a second on
    /// stdout - only enough to see that the stream is there and what is on it - and, unless
    /// <paramref name="withAudio"/> is false, stereo 48 kHz 16-bit on the pipe for the meters.
    /// </summary>
    public static IReadOnlyList<string> PreviewArguments(string url, int width, int height, int fps, string pipeName, bool withAudio)
    {
        var a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-y" };

        string lower = url.Trim().ToLowerInvariant();
        if (lower.StartsWith("http")) a.Add("-re");
        if (lower.StartsWith("rtsp")) a.AddRange(["-rtsp_transport", "tcp"]);

        a.AddRange(["-fflags", "+discardcorrupt+nobuffer", "-analyzeduration", "1000000", "-probesize", "1000000", "-i", url.Trim()]);

        a.AddRange(["-map", "0:v:0",
            "-vf", $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
                   $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2:black,fps={fps},format=bgra",
            "-f", "rawvideo", "pipe:1"]);

        if (withAudio)
            a.AddRange(["-map", "0:a:0", "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-flush_packets", "1",
                        "-f", "s16le", $@"\\.\pipe\{pipeName}"]);

        return a;
    }

    /// <summary>
    /// Reads <paramref name="url"/> and gives raw UYVY at <paramref name="format"/> on stdout, and,
    /// unless <paramref name="withAudio"/> is false, stereo 48 kHz 16-bit on the named pipe.
    ///
    /// The picture is fitted inside the raster with black either side (a phone held upright stays
    /// upright), deinterlaced only where it is interlaced, and made exactly the deck's rate. Both
    /// halves are counted from the same zero: the frame rate filter starts at 0 and the sound is
    /// padded from 0, so frame n and sound chunk n are the same moment.
    /// </summary>
    public static IReadOnlyList<string> Arguments(string url, CaptureFormat format, string pipeName, bool withAudio)
    {
        var a = new List<string> { "-hide_banner", "-nostats", "-loglevel", "error", "-y" };

        string lower = url.Trim().ToLowerInvariant();

        // A playlist is read as fast as it downloads; at its own pace, it is live.
        if (lower.StartsWith("http")) a.Add("-re");
        if (lower.StartsWith("rtsp")) a.AddRange(["-rtsp_transport", "tcp"]);

        // A second of looking at the stream before reading it, not ffmpeg's five: that is how long
        // every connection, and every reconnection after a drop, shows black.
        a.AddRange(["-fflags", "+discardcorrupt", "-analyzeduration", "1000000", "-probesize", "1000000", "-i", url.Trim()]);

        int w = format.Width, h = format.Height;
        a.AddRange(["-map", "0:v:0",
            "-vf", $"bwdif=mode=send_frame:deint=interlaced," +
                   $"scale={w}:{h}:force_original_aspect_ratio=decrease," +
                   $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black,setsar=1," +
                   $"fps=fps={format.FrameRate}:start_time=0,format=uyvy422",
            "-f", "rawvideo", "pipe:1"]);

        if (withAudio)
            a.AddRange(["-map", "0:a:0", "-af", "aresample=48000:async=1:first_pts=0",
                        "-ac", "2", "-ar", "48000", "-c:a", "pcm_s16le", "-flush_packets", "1", "-f", "s16le",
                        $@"\\.\pipe\{pipeName}"]);

        return a;
    }
}

/// <summary>
/// An IP stream in the receiver's place. An ffmpeg child reads it; <see cref="FrameRegulator"/>
/// evens it out; <see cref="FrameClock"/> gives the frames out one per frame time, as the card's
/// slots would. A stream that stops shows its last picture for a second and then black, and is
/// reconnected by itself; the recording goes on through it.
/// </summary>
internal sealed class IpInput : ICaptureInput
{
    private const int SampleRate = 48000;

    /// <summary>How long to wait for the first picture before saying there is none.</summary>
    private static readonly TimeSpan FirstPictureWait = TimeSpan.FromSeconds(20);

    /// <summary>Nothing from ffmpeg for this long and it is started again.</summary>
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(5);

    private readonly IpSource _source;
    private readonly string _ffmpeg;
    private readonly int _rate;
    private readonly string _session;
    private readonly Action<string, bool> _say;

    private CaptureFormat? _format;
    private FrameRegulator? _regulator;
    private FrameClock? _clock;
    private int _audioBytes;

    private Thread? _reader;
    private readonly CancellationTokenSource _stop = new();
    private Process? _ffmpegProcess;
    private bool _noAudio;
    private long _lastData;
    private string? _lastError;

    private RegulatedFrame _current;
    private bool _lost;
    private long _saidAt;
    private long _saidRepeated, _saidDropped;

    public IpInput(IpSource source, string ffmpeg, int frameRate, string session, Action<string, bool> say)
    {
        _source = source;
        _ffmpeg = ffmpeg;
        _rate = frameRate;
        _session = session;
        _say = say;
    }

    public string Name => "the IP stream";

    public CaptureFormat? Open(CancellationToken ct, out string? problem)
    {
        problem = null;

        _format = CaptureFormat.ForIp(_rate);
        _audioBytes = SampleRate / _format.FrameRate * 2 * 2;

        // A third of a second in hand, at most two thirds, and a second of the last picture
        // before black.
        int prebuffer = Math.Max(2, _format.FrameRate / 3);
        _regulator = new FrameRegulator(_format.FrameBytes, prebuffer, prebuffer * 2, _format.FrameRate);

        Info($"IP INPUT connecting to {_source.Shown} - made into {_format.Name}.", "ipinput.connecting");

        _reader = new Thread(ReadLoop) { IsBackground = true, Name = "IP input reader" };
        _reader.Start();

        var waited = Stopwatch.StartNew();
        while (_regulator.Queued == 0)
        {
            if (ct.IsCancellationRequested) { problem = "stopped before the IP stream gave a picture."; return null; }

            if (waited.Elapsed > FirstPictureWait)
            {
                problem = $"no picture from the IP stream {_source.Shown} after {FirstPictureWait.TotalSeconds:F0} s" +
                          (_lastError is { } e ? $" - {e}" : " - is it sending, and is the address right?");
                return null;
            }

            Thread.Sleep(50);
        }

        ActivityLog.Shared.Ok(LogSource.Capture,
            $"IP INPUT receiving {_source.Shown}" + (_noAudio ? " - it carries no sound; silence is recorded." : "."),
            @event: "ipinput.connected", correlation: _session);

        return _format;
    }

    public void Configure() { }

    public string? Start()
    {
        _clock = new FrameClock(_format!.FrameRate);
        _saidAt = Environment.TickCount64;
        return null;
    }

    /// <summary>A stream gives one pair: CH 1-2.</summary>
    public int DetectPairs(SdiAudioReader audio, CancellationToken ct) => 1;

    public bool Lock()
    {
        _clock!.Wait();
        _current = _regulator!.Pull();

        if (_current.Kind == RegulatedKind.Black && !_lost)
        {
            _lost = true;
            ActivityLog.Shared.Warn(LogSource.Capture,
                $"IP INPUT LOST - no picture from {_source.Shown}; recording black and silence until it is back.",
                @event: "ipinput.lost", correlation: _session);
            _say("IP stream: no picture - recording black until it is back.", true);
        }
        else if (_current.Kind == RegulatedKind.Fresh && _lost)
        {
            _lost = false;
            ActivityLog.Shared.Ok(LogSource.Capture, $"IP INPUT BACK - picture from {_source.Shown} again.",
                @event: "ipinput.back", correlation: _session);
            _say("IP stream: picture back.", false);
        }

        // Every half minute, if the network cost anything: frames shown twice, frames let go.
        if (Environment.TickCount64 - _saidAt > 30_000)
        {
            _saidAt = Environment.TickCount64;
            long repeated = _regulator.Repeated, dropped = _regulator.Dropped;

            if (repeated != _saidRepeated || dropped != _saidDropped)
            {
                ActivityLog.Shared.Info(LogSource.Capture,
                    $"IP INPUT {repeated - _saidRepeated} frame(s) shown again while the stream was late, " +
                    $"{dropped - _saidDropped} let go when it came in a rush, in the last 30 s " +
                    $"({repeated} and {dropped} so far).",
                    @event: "ipinput.stats", correlation: _session,
                    detail: $"fresh {_regulator.Fresh}\nrepeated {repeated}\nblack {_regulator.Blacks}\n" +
                            $"dropped {dropped}\nrefills {_regulator.Underruns}\nclock slips {_clock.Slipped}");

                _saidRepeated = repeated;
                _saidDropped = dropped;
            }
        }

        return true;
    }

    public bool Picture(out IntPtr buffer, out uint size)
    {
        buffer = Marshal.UnsafeAddrOfPinnedArrayElement(_current.Video, 0);
        size = (uint)_current.Video.Length;
        return true;
    }

    public int ReadAudio(SdiAudioReader audio) =>
        _current.Audio is { } chunk ? audio.Load(chunk, chunk.Length, 2) : 0;

    public void Unlock() { }

    // ------------------------------------------------------------------ the reader

    private void ReadLoop()
    {
        CancellationToken ct = _stop.Token;

        while (!ct.IsCancellationRequested)
        {
            string? url = Resolve(out string? why);

            if (url is null)
            {
                _lastError = why;
                _say($"IP stream: {why}", true);
                if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(5))) return;
                continue;
            }

            int generation = _regulator!.Reset();
            bool hadAudio = !_noAudio;

            try { ReadOnce(url, generation, ct); }
            catch (Exception ex) when (!ct.IsCancellationRequested) { _lastError = ex.Message; }

            if (ct.IsCancellationRequested) return;

            // It has no sound: straight back, without it.
            if (hadAudio && _noAudio) continue;

            ActivityLog.Shared.Warn(LogSource.Capture,
                $"IP INPUT the stream from {_source.Shown} stopped" + (_lastError is { } e ? $" ({e})" : "") +
                " - connecting again in 1 s.",
                @event: "ipinput.reconnect", correlation: _session);

            if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(1))) return;
        }
    }

    private string? Resolve(out string? why) => _source.Resolve(out why);

    private void ReadOnce(string url, int generation, CancellationToken ct)
    {
        CaptureFormat format = _format!;
        FrameRegulator regulator = _regulator!;
        string pipeName = $"emerald_ip_{_session}_{generation}";
        bool withAudio = !_noAudio;

        using var pipe = withAudio
            ? new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                                        PipeOptions.Asynchronous, 1 << 20, 0)
            : null;

        var info = new ProcessStartInfo(_ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in IpReaderCommand.Arguments(url, format, pipeName, withAudio)) info.ArgumentList.Add(a);

        using Process ff = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg for the IP stream.");
        _ffmpegProcess = ff;
        MemoryGauges.NameChild(ff.Id, "IP input reader");

        ff.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;

            string line = e.Data.Trim();
            _lastError = Short(line);

            // A stream with no sound: ffmpeg refuses an output for sound it does not have. Read
            // again without it, and silence is recorded.
            if (line.Contains("matches no streams", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase))
                _noAudio = true;
        };
        ff.BeginErrorReadLine();

        Interlocked.Exchange(ref _lastData, Environment.TickCount64);

        // Nothing for too long: ffmpeg is stopped, and the loop connects again.
        using var watchdog = new Timer(_ =>
        {
            long quiet = Environment.TickCount64 - Interlocked.Read(ref _lastData);
            long allowed = regulator.Fresh == 0 && regulator.Queued == 0 ? (long)FirstPictureWait.TotalMilliseconds : (long)Stall.TotalMilliseconds;

            if (quiet > allowed) { try { ff.Kill(); } catch { } }
        }, null, 1000, 1000);

        using CancellationTokenRegistration onStop = ct.Register(() => { try { ff.Kill(); } catch { } });

        Thread? sound = null;

        if (pipe is not null)
        {
            sound = new Thread(() => ReadSound(pipe, generation, ct)) { IsBackground = true, Name = "IP input sound" };
            sound.Start();
        }

        Stream picture = ff.StandardOutput.BaseStream;
        long seq = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                byte[] frame = regulator.Rent();

                try
                {
                    picture.ReadExactly(frame, 0, format.FrameBytes);
                }
                catch
                {
                    regulator.Return(frame);
                    break;
                }

                Interlocked.Exchange(ref _lastData, Environment.TickCount64);
                regulator.PushVideo(generation, seq++, frame);
            }
        }
        finally
        {
            try { if (!ff.HasExited) ff.Kill(); } catch { }
            try { ff.WaitForExit(3000); } catch { }

            try { pipe?.Dispose(); } catch { }
            sound?.Join(2000);

            _ffmpegProcess = null;
        }
    }

    private void ReadSound(NamedPipeServerStream pipe, int generation, CancellationToken ct)
    {
        try
        {
            pipe.WaitForConnectionAsync(ct).Wait(ct);

            long seq = 0;

            while (!ct.IsCancellationRequested)
            {
                var chunk = new byte[_audioBytes];
                pipe.ReadExactly(chunk, 0, chunk.Length);
                _regulator!.PushAudio(generation, seq++, chunk);
            }
        }
        catch
        {
            // The pipe closed: ffmpeg stopped, or this connection is over.
        }
    }

    private static string Short(string text) => text.Length > 160 ? text[..160] + "…" : text;

    private void Info(string text, string @event) =>
        ActivityLog.Shared.Info(LogSource.Capture, text, @event: @event, correlation: _session);

    public void Dispose()
    {
        _stop.Cancel();
        try { _ffmpegProcess?.Kill(); } catch { }

        _reader?.Join(5000);
        _clock?.Dispose();
        _stop.Dispose();
    }
}
