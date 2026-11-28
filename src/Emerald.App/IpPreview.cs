using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Emerald.Core;
using Emerald.Video;

namespace Emerald.App;

/// <summary>
/// A live look at an IP stream before it is recorded: what <see cref="RxPreview"/> is for the
/// card's receiver, with the same events, so the capture deck shows either the same way.
///
/// An ffmpeg child reads the stream at a quarter of 1080 and twelve frames a second - enough to
/// see that it is there and what is on it - with its sound for the meters and the speaker
/// monitor. MediaMTX lets any number of readers have a stream, but the deck stops this the moment
/// Record is pressed: from then on the recorder's own picture is shown. A stream that stops is
/// tried again every couple of seconds, for as long as the preview is wanted.
/// </summary>
public sealed class IpPreview : IDisposable
{
    private const int Width = 480, Height = 270, Fps = 12;
    private const int SampleRate = 48000;

    /// <summary>Sound handed to the meters in chunks of a 25 fps frame.</summary>
    private const int ChunkSamples = SampleRate / 25;

    private static readonly TimeSpan FirstPictureWait = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Stall = TimeSpan.FromSeconds(5);

    private Thread? _worker;
    private CancellationTokenSource? _cts;
    private volatile Process? _ffmpeg;

    public event Action<string, bool>? Status;
    public event Action<WriteableBitmap>? FrameReady;
    public event Action<CaptureFormat?>? FormatChanged;

    /// <summary>The stream's sound, raised on the preview's thread. A handler must not block.</summary>
    public event Action<SdiAudioReader>? Audio;

    private WriteableBitmap? _bitmap;
    private int _inFlight;

    public bool IsRunning => _worker is { IsAlive: true };

    public void Start(IpSource source, string ffmpeg)
    {
        Stop();

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        _worker = new Thread(() => Run(source, ffmpeg, ct)) { IsBackground = true, Name = "IP preview" };
        _worker.Start();
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _ffmpeg?.Kill(); } catch { }

        _worker?.Join(TimeSpan.FromSeconds(3));
        _cts?.Dispose();
        _cts = null;
        _worker = null;
    }

    public void Dispose() => Stop();

    // ------------------------------------------------------------------ worker

    private void Run(IpSource source, string ffmpeg, CancellationToken ct)
    {
        bool noAudio = false;

        while (!ct.IsCancellationRequested)
        {
            Report($"IP stream {source.Shown} - connecting...", false, log: false);

            string? url = source.Resolve(out string? why);
            string? stopped;

            if (url is null)
            {
                stopped = why;
            }
            else
            {
                bool hadAudio = !noAudio;
                stopped = ReadOnce(url, ffmpeg, ref noAudio, ct, out bool gotPicture);

                if (ct.IsCancellationRequested) return;
                if (hadAudio && noAudio && !gotPicture) continue;   // no sound: straight back without it
            }

            ReportFormat(null);

            // A problem on the deck's status line, which writes a sentence down once, not every retry.
            Report($"IP stream {source.Shown}: no picture" + (stopped is null ? "" : $" - {stopped}") + ". Trying again...",
                   true, log: false);

            if (ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(2))) return;
        }
    }

    /// <summary>One connection, until it stops. Why it stopped, if ffmpeg said.</summary>
    private string? ReadOnce(string url, string ffmpeg, ref bool noAudio, CancellationToken ct, out bool gotPicture)
    {
        gotPicture = false;
        string pipeName = $"emerald_ippv_{Environment.ProcessId}_{Guid.NewGuid():N}"[..44];
        bool withAudio = !noAudio;

        using var pipe = withAudio
            ? new NamedPipeServerStream(pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 1 << 18, 0)
            : null;

        var info = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string a in IpReaderCommand.PreviewArguments(url, Width, Height, Fps, pipeName, withAudio)) info.ArgumentList.Add(a);

        using Process ff = Process.Start(info) ?? throw new InvalidOperationException("Could not start ffmpeg for the IP preview.");
        _ffmpeg = ff;
        MemoryGauges.NameChild(ff.Id, "IP preview");

        string? lastError = null;
        bool sawNoAudio = false;
        ff.ErrorDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            string line = e.Data.Trim();
            lastError = line.Length > 160 ? line[..160] + "…" : line;

            if (line.Contains("matches no streams", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("does not contain any stream", StringComparison.OrdinalIgnoreCase))
                sawNoAudio = true;
        };
        ff.BeginErrorReadLine();

        long lastData = Environment.TickCount64;
        bool any = false;

        using var watchdog = new Timer(_ =>
        {
            long allowed = (long)(Volatile.Read(ref any) ? Stall : FirstPictureWait).TotalMilliseconds;
            if (Environment.TickCount64 - Interlocked.Read(ref lastData) > allowed) { try { ff.Kill(); } catch { } }
        }, null, 1000, 1000);

        Thread? sound = null;
        if (pipe is not null)
        {
            sound = new Thread(() => ReadSound(pipe, ct)) { IsBackground = true, Name = "IP preview sound" };
            sound.Start();
        }

        Stream picture = ff.StandardOutput.BaseStream;
        var buffers = new[] { new byte[Width * Height * 4], new byte[Width * Height * 4] };
        int next = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                try { picture.ReadExactly(buffers[next]); }
                catch { break; }

                Interlocked.Exchange(ref lastData, Environment.TickCount64);

                if (!any)
                {
                    Volatile.Write(ref any, true);
                    gotPicture = true;
                    ReportFormat(new CaptureFormat("IP stream", Width, Height, Fps));
                    Report("IP stream - live preview. Recording starts when Record is pressed.", false, log: false);
                }

                if (Publish(buffers[next])) next ^= 1;
            }
        }
        finally
        {
            try { if (!ff.HasExited) ff.Kill(); } catch { }
            try { ff.WaitForExit(2000); } catch { }
            try { pipe?.Dispose(); } catch { }
            sound?.Join(1000);
            _ffmpeg = null;
        }

        if (sawNoAudio) noAudio = true;
        return lastError;
    }

    private void ReadSound(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var reader = new SdiAudioReader(SdiAudioReader.MaxChannels, 25);
        var chunk = new byte[ChunkSamples * 4];

        try
        {
            pipe.WaitForConnectionAsync(ct).Wait(ct);

            while (!ct.IsCancellationRequested)
            {
                pipe.ReadExactly(chunk);
                reader.Load(chunk, chunk.Length, 2);

                try { Audio?.Invoke(reader); }
                catch { /* a meter that throws must not stop the preview */ }
            }
        }
        catch
        {
            // The pipe closed: ffmpeg stopped, or the preview did.
        }
    }

    /// <summary>To the UI thread, at most one frame in flight - as <see cref="RxPreview"/> does, for the same reasons.</summary>
    private bool Publish(byte[] bgra)
    {
        if (Interlocked.Exchange(ref _inFlight, 1) == 1) return false;

        if (Application.Current?.Dispatcher is not { } dispatcher)
        {
            _inFlight = 0;
            return false;
        }

        dispatcher.BeginInvoke(() =>
        {
            try
            {
                _bitmap ??= new WriteableBitmap(Width, Height, 96, 96, PixelFormats.Bgra32, null);
                _bitmap.WritePixels(new Int32Rect(0, 0, Width, Height), bgra, Width * 4, 0);
                FrameReady?.Invoke(_bitmap);
            }
            finally
            {
                Volatile.Write(ref _inFlight, 0);
            }
        }, DispatcherPriority.Render);

        return true;
    }

    private void Report(string text, bool problem, bool log = true)
    {
        if (log) ActivityLog.Shared.Write(LogSource.Preview, problem ? LogLevel.Warn : LogLevel.Info, text);
        Application.Current?.Dispatcher.BeginInvoke(() => Status?.Invoke(text, problem));
    }

    private void ReportFormat(CaptureFormat? format) =>
        Application.Current?.Dispatcher.BeginInvoke(() => FormatChanged?.Invoke(format));
}
