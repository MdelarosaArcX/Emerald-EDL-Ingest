using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Emerald.Core;
using Emerald.Deltacast;
using Emerald.Video;

namespace Emerald.App;

/// <summary>
/// A live look at what is arriving on an SDI receiver, for the shell's confidence monitor.
///
/// This is a monitor, not a playout path, so it deliberately does less work than it could:
/// frames are decimated 4:1 in both axes during the YUV to RGB pass and only about twelve a
/// second are converted at all. Converting 1920x1080 UYVY at full rate in managed code costs
/// a core to produce a picture nobody is measuring — all this has to show is that signal is
/// present and what is on it.
///
/// It holds a <b>yielding</b> <see cref="RxLease"/>: when the EDL starts recording the same
/// input, the preview is revoked and closes its stream, then comes back on its own once
/// recording stops.
/// </summary>
public sealed class RxPreview : IDisposable
{
    private const uint IoTimeoutMs = 1000;

    private readonly object _gate = new();

    private Thread? _worker;
    private CancellationTokenSource? _cts;
    private RxLease? _lease;

    private uint _board;
    private int _channel = -1;
    private bool _yielded;

    /// <summary>Status line for the shell: what the preview is doing, and whether it is a problem.</summary>
    public event Action<string, bool>? Status;

    /// <summary>Raised on the UI thread with each converted frame.</summary>
    public event Action<WriteableBitmap>? FrameReady;

    /// <summary>
    /// The standard the receiver is locked to, or null while there is nothing on the wire.
    /// The status line already says as much in prose; this is the same fact in a form the
    /// deck can put in a field.
    /// </summary>
    public event Action<CaptureFormat?>? FormatChanged;

    /// <summary>
    /// Every slot's embedded audio, de-interleaved, raised on the preview thread - for the
    /// deck's meters and its speaker monitor.
    ///
    /// Subscribed to before <see cref="Start"/>: the reader is built once per lock, so a
    /// handler attached later is not picked up until the signal is re-detected. The buffers
    /// are reused on the next slot and <b>a handler must not block</b>.
    /// </summary>
    public event Action<SdiAudioReader>? Audio;

    private WriteableBitmap? _bitmap;
    private int _bitmapWidth, _bitmapHeight;

    /// <summary>
    /// How much smaller than the source this preview's picture is, per axis.
    ///
    /// 4 is a quarter-size image, which is all a 352-wide thumbnail can show. The playback deck
    /// asks for less reduction because its monitor is the picture an operator judges the output
    /// by, and a quarter-size image there is visibly soft.
    ///
    /// Takes effect the next time the receiver is locked, which is also when the buffers are
    /// sized — set it before <see cref="Start"/>.
    /// </summary>
    public int Decimation { get; set; } = UyvyPreview.Decimation;

    /// <summary>
    /// Frames a second this preview converts. The default is above any broadcast rate, so every
    /// frame is converted and the picture moves as smoothly as the wire.
    /// </summary>
    public int TargetFps { get; set; } = UyvyPreview.TargetFps;

    public RxPreview()
    {
        RxLease.Freed += OnChannelFreed;
    }

    public bool IsRunning => _worker is { IsAlive: true };

    public void Start(uint board, int channel)
    {
        Stop();

        lock (_gate)
        {
            _board = board;
            _channel = channel;
            _yielded = false;
        }

        Launch();
    }

    private void Launch()
    {
        uint board;
        int channel;

        lock (_gate)
        {
            board = _board;
            channel = _channel;
            if (channel < 0) return;
        }

        try
        {
            _lease = RxLease.Acquire(board, channel, "preview", yielding: true, onRevoked: Revoke);
        }
        catch (RxBusyException busy)
        {
            Report(busy.Message, true);
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        _worker = new Thread(() => Run(board, channel, ct))
        {
            IsBackground = true,
            Name = "RX preview",
        };

        _worker.Start();
    }

    /// <summary>
    /// Called by the lease when recording claims this input. It must have closed the stream
    /// before it returns, because the recorder opens the channel the moment this comes back.
    /// </summary>
    private void Revoke()
    {
        lock (_gate) _yielded = true;

        Report("Preview released - this input is recording.", false);
        StopWorker();
    }

    private void OnChannelFreed(uint board, int channel)
    {
        bool mine;

        lock (_gate) mine = _yielded && board == _board && channel == _channel;
        if (!mine) return;

        lock (_gate) _yielded = false;

        // The recorder has let go; take the input back.
        Launch();
    }

    public void Stop()
    {
        lock (_gate) { _channel = -1; _yielded = false; }
        StopWorker();
    }

    private void StopWorker()
    {
        _cts?.Cancel();
        _worker?.Join(TimeSpan.FromSeconds(3));
        _cts?.Dispose();
        _cts = null;
        _worker = null;

        _lease?.Dispose();
        _lease = null;
    }

    public void Dispose()
    {
        RxLease.Freed -= OnChannelFreed;
        Stop();
    }

    // ------------------------------------------------------------------ worker

    private void Run(uint board, int channel, CancellationToken ct)
    {
        IntPtr brd = IntPtr.Zero, strm = IntPtr.Zero;

        try
        {
            uint rc = VideoMasterHD.VHD_OpenBoardHandle(board, ref brd, IntPtr.Zero, 0);
            if (rc != 0) { Report($"Preview: cannot open board {board} (error {rc}).", true); return; }

            int setupLock = 0;

            // JOINED, so the slots carry ANC and the embedded audio can be read. The preview
            // used to open video only, on the reasoning that audio was the recorder's business
            // - which left the deck's meters with nothing to show unless a recording was
            // running, and no way to hear what was arriving at all.
            rc = VideoMasterHD.VHD_OpenStreamHandle(brd, VideoMasterHD.RxStreamType(channel),
                    VideoMasterHD.VHD_SDI_STPROC_JOINED, ref setupLock, ref strm, IntPtr.Zero);

            if (rc != 0)
            {
                Report(rc == 18
                    ? $"Preview: RX{channel} is open in another application - close dCARE to see it here."
                    : $"Preview: cannot open RX{channel} (error {rc}).", true);
                return;
            }

            // A monitor that gives up the first time nothing is on the wire is no use: the
            // input is routinely dark between messages. Keep watching, and pick the signal
            // up whenever it appears — or comes back after being lost.
            bool reportedDark = false;

            while (!ct.IsCancellationRequested)
            {
                uint std = 0;
                bool locked = false;

                for (int attempt = 0; attempt < 20 && !ct.IsCancellationRequested; attempt++)
                {
                    if (VideoMasterHD.VHD_GetStreamPropertyEx(
                            strm, VideoMasterHD.VHD_SDI_SP_VIDEO_STANDARD, 1, ref std) == 0)
                    {
                        locked = true;
                        break;
                    }

                    Thread.Sleep(100);
                }

                if (!locked)
                {
                    // Said once per dark period, not once every two seconds.
                    if (!reportedDark)
                    {
                        Report($"No signal on RX{channel}.", true);
                        ReportFormat(null);
                        reportedDark = true;
                    }
                    continue;
                }

                reportedDark = false;

                CaptureFormat format = CaptureFormat.FromStandard(std, 25);

                VideoMasterHD.VHD_SetStreamProperty(strm, VideoMasterHD.VHD_SDI_SP_VIDEO_STANDARD, std);
                VideoMasterHD.VHD_SetStreamProperty(strm, VideoMasterHD.VHD_CORE_SP_TRANSFER_SCHEME,
                                                    VideoMasterHD.VHD_TRANSFER_SLAVED);
                VideoMasterHD.VHD_SetStreamProperty(strm, VideoMasterHD.VHD_CORE_SP_BUFFER_PACKING,
                                                    VideoMasterHD.VHD_BUFPACK_VIDEO_YUV422_8);
                VideoMasterHD.VHD_SetStreamProperty(strm, VideoMasterHD.VHD_CORE_SP_BUFFERQUEUE_DEPTH, 4);
                VideoMasterHD.VHD_SetStreamProperty(strm, VideoMasterHD.VHD_CORE_SP_IO_TIMEOUT, IoTimeoutMs);

                rc = VideoMasterHD.VHD_StartStream(strm);

                if (rc != 0)
                {
                    Report($"Preview: cannot start RX{channel} (error {rc}).", true);
                    return;
                }

                Report($"RX{channel} locked to {format.Name}.", false);
                ReportFormat(format);

                try { Pump(strm, format, ct); }
                finally { VideoMasterHD.VHD_StopStream(strm); }

                // Pump only returns when the signal went away or the preview was stopped;
                // either way the next pass re-detects, in case the format changed with it.
                if (!ct.IsCancellationRequested)
                {
                    Report($"Signal lost on RX{channel}.", true);
                    ReportFormat(null);
                }
            }
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested) Report($"Preview stopped: {ex.Message}", true);
        }
        finally
        {
            if (strm != IntPtr.Zero) { VideoMasterHD.VHD_StopStream(strm); VideoMasterHD.VHD_CloseStreamHandle(strm); }
            if (brd != IntPtr.Zero) VideoMasterHD.VHD_CloseBoardHandle(brd);
        }
    }

    private void Pump(IntPtr strm, CaptureFormat format, CancellationToken ct)
    {
        // Built only when somebody is listening: allocating sixteen channel buffers and
        // asking the card for ANC audio on every slot is work a deck with no meters up does
        // not need to do.
        Action<SdiAudioReader>? listener = Audio;
        using SdiAudioReader? audio = listener is null
            ? null
            : new SdiAudioReader(SdiAudioReader.MaxChannels, format.FrameRate);

        // Read once per lock, so changing the setting takes effect on the next re-detect rather
        // than halfway through a frame.
        int decimation = Decimation;

        int outW = UyvyPreview.OutputWidth(format.Width, decimation);
        int outH = UyvyPreview.OutputHeight(format.Height, decimation);

        // Two buffers, used alternately: the worker fills one while the UI thread is still
        // reading the other. A fresh array per frame would be a large-object allocation per
        // frame, which at a full-size preview is 8 MB a time.
        //
        // Two is only enough because at most one frame is ever in flight to the UI - see
        // Publish. With an unbounded dispatcher queue it was not: three frames queued meant the
        // worker was overwriting the buffer the first one had not been read from yet.
        var buffers = new[] { UyvyPreview.CreateBuffer(format.Width, format.Height, decimation),
                              UyvyPreview.CreateBuffer(format.Width, format.Height, decimation) };
        int next = 0;

        // Every frame must still be locked and released or the card's queue backs up.
        int convertEvery = UyvyPreview.ConvertEvery(format.FrameRate, TargetFps);
        long slot = 0;

        // A lock timeout on its own means nothing — the source may just have paused. Several
        // in a row means the signal is gone, and the caller should go back to detecting so a
        // source that returns in a different format is picked up correctly.
        const int LostAfterTimeouts = 5;
        int timeouts = 0;

        while (!ct.IsCancellationRequested)
        {
            IntPtr slotHandle = IntPtr.Zero;

            if (VideoMasterHD.VHD_LockSlotHandle(strm, ref slotHandle) != 0)
            {
                if (++timeouts >= LostAfterTimeouts) return;
                continue;
            }

            timeouts = 0;
            try
            {
                // Audio first, and on every slot. Picture is decimated because nobody is
                // measuring a thumbnail; sound cannot be - a meter fed one frame in four
                // misses the transients, and a monitor fed one frame in four is a stutter.
                if (audio is not null && listener is not null)
                {
                    audio.Read(slotHandle);

                    try { listener.Invoke(audio); }
                    catch { /* a meter that throws must not stop the monitor */ }
                }

                if (slot++ % convertEvery != 0) continue;

                IntPtr buffer = IntPtr.Zero;
                uint size = 0;

                if (VideoMasterHD.VHD_GetSlotBuffer(slotHandle, VideoMasterHD.VHD_SDI_BT_VIDEO,
                                                    ref buffer, ref size) != 0 || buffer == IntPtr.Zero)
                    continue;

                if (size < (uint)format.FrameBytes) continue;

                UyvyPreview.ToBgra(buffer, format.Width, buffers[next], outW, outH, decimation);
            }
            finally
            {
                VideoMasterHD.VHD_UnlockSlotHandle(slotHandle);
            }

            // Dropped rather than queued if the UI thread has not caught up. See Publish.
            if (Publish(buffers[next], outW, outH)) next ^= 1;
        }
    }

    /// <summary>1 while a frame is on its way to the UI thread and has not been drawn yet.</summary>
    private int _inFlight;

    /// <summary>
    /// Hands a converted frame to the UI thread, or drops it.
    ///
    /// <b>At most one frame is ever in flight, and this is the fix for the freezing.</b> Every
    /// converted frame used to be posted with BeginInvoke unconditionally, onto a dispatcher
    /// shared with two 25 ms timers, a growing log panel and whatever else the operator had
    /// open. When that thread fell behind, the posts queued without limit: latency grew without
    /// bound, so the picture lagged further and further behind the wire and then jumped — which
    /// is what a freeze followed by a skip actually is.
    ///
    /// Worse, the two conversion buffers are reused in turn, which is only safe while one frame
    /// is outstanding. With three queued, the worker was already overwriting the buffer the
    /// oldest callback had yet to read, so a late frame could be drawn half old and half new.
    ///
    /// Dropping is the right answer for a monitor: the next frame is a fiftieth of a second
    /// away and more current than the one being discarded. Nothing recorded or transmitted is
    /// affected - this is the picture on the glass.
    /// </summary>
    /// <returns>True if the frame was taken, so the caller moves to the other buffer.</returns>
    private bool Publish(byte[] rgb, int w, int h)
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
                if (_bitmap is null || _bitmapWidth != w || _bitmapHeight != h)
                {
                    _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                    _bitmapWidth = w;
                    _bitmapHeight = h;
                }

                _bitmap.WritePixels(new Int32Rect(0, 0, w, h), rgb, w * 4, 0);
                FrameReady?.Invoke(_bitmap);
            }
            finally
            {
                // In a finally: a handler that throws must not wedge the preview shut for the
                // rest of the session.
                Volatile.Write(ref _inFlight, 0);
            }
        }, DispatcherPriority.Render);

        return true;
    }

    private void Report(string text, bool problem)
    {
        // Straight to the record, on the thread that noticed. The window is told through the
        // dispatcher as before, but a preview that lost its signal while nobody had the deck
        // open is still worth knowing about afterwards.
        ActivityLog.Shared.Write(LogSource.Preview, problem ? LogLevel.Warn : LogLevel.Info, text);

        Application.Current?.Dispatcher.BeginInvoke(() => Status?.Invoke(text, problem));
    }

    private void ReportFormat(CaptureFormat? format) =>
        Application.Current?.Dispatcher.BeginInvoke(() => FormatChanged?.Invoke(format));
}
