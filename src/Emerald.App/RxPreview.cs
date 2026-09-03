using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
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

    private WriteableBitmap? _bitmap;
    private int _bitmapWidth, _bitmapHeight;

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

            // Video only: the preview has no use for ANC, and the lighter processing mode
            // leaves the audio path entirely to the recorder.
            rc = VideoMasterHD.VHD_OpenStreamHandle(brd, VideoMasterHD.RxStreamType(channel),
                    VideoMasterHD.VHD_SDI_STPROC_DISJOINED_VIDEO, ref setupLock, ref strm, IntPtr.Zero);

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
        int outW = UyvyPreview.OutputWidth(format.Width);
        int outH = UyvyPreview.OutputHeight(format.Height);
        // Two buffers, used alternately: the worker fills one while the UI thread is still
        // reading the other. A fresh array per frame would be a half-megabyte large-object
        // allocation twelve times a second for a thumbnail.
        var buffers = new[] { UyvyPreview.CreateBuffer(format.Width, format.Height),
                              UyvyPreview.CreateBuffer(format.Width, format.Height) };
        int next = 0;

        // Every frame must still be locked and released or the card's queue backs up, but
        // only every Nth is worth converting and marshalling to the UI thread.
        int convertEvery = UyvyPreview.ConvertEvery(format.FrameRate);
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
                if (slot++ % convertEvery != 0) continue;

                IntPtr buffer = IntPtr.Zero;
                uint size = 0;

                if (VideoMasterHD.VHD_GetSlotBuffer(slotHandle, VideoMasterHD.VHD_SDI_BT_VIDEO,
                                                    ref buffer, ref size) != 0 || buffer == IntPtr.Zero)
                    continue;

                if (size < (uint)format.FrameBytes) continue;

                UyvyPreview.ToBgra(buffer, format.Width, buffers[next], outW, outH);
            }
            finally
            {
                VideoMasterHD.VHD_UnlockSlotHandle(slotHandle);
            }

            Publish(buffers[next], outW, outH);
            next ^= 1;
        }
    }

    private void Publish(byte[] rgb, int w, int h)
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (_bitmap is null || _bitmapWidth != w || _bitmapHeight != h)
            {
                _bitmap = new WriteableBitmap(w, h, 96, 96, PixelFormats.Bgra32, null);
                _bitmapWidth = w;
                _bitmapHeight = h;
            }

            _bitmap.WritePixels(new Int32Rect(0, 0, w, h), rgb, w * 4, 0);
            FrameReady?.Invoke(_bitmap);
        });
    }

    private void Report(string text, bool problem) =>
        Application.Current?.Dispatcher.BeginInvoke(() => Status?.Invoke(text, problem));

    private void ReportFormat(CaptureFormat? format) =>
        Application.Current?.Dispatcher.BeginInvoke(() => FormatChanged?.Invoke(format));
}
