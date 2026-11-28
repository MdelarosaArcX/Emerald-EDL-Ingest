using Emerald.Deltacast;
using System.Diagnostics;

namespace Emerald.Video;

/// <summary>
/// Where a recording's frames come from: the SDI card's receiver, as always, or an IP stream.
///
/// The recorder asks it for one frame at a time - its picture and its sound - and everything after
/// that (the encoder, the delay line, the preview, the meters, midnight) is the same whichever it
/// is. One frame is locked at a time, and is the recorder's to read until it is unlocked.
/// </summary>
internal interface ICaptureInput : IDisposable
{
    /// <summary>What it is called in what the deck says: "RX1", "the IP stream".</summary>
    string Name { get; }

    /// <summary>
    /// Claims and opens it, and waits for a picture: the format it gives, or null with why not in
    /// <paramref name="problem"/>.
    /// </summary>
    CaptureFormat? Open(CancellationToken ct, out string? problem);

    /// <summary>Set up for the format, before anything is flowing.</summary>
    void Configure();

    /// <summary>Frames start flowing. Null, or why they could not.</summary>
    string? Start();

    /// <summary>How many stereo pairs it carries, settled before the encoder starts.</summary>
    int DetectPairs(SdiAudioReader audio, CancellationToken ct);

    /// <summary>Waits for the next frame and holds it. False when none came in time.</summary>
    bool Lock();

    /// <summary>The held frame's picture, valid until <see cref="Unlock"/>. False when it has none.</summary>
    bool Picture(out IntPtr buffer, out uint size);

    /// <summary>The held frame's sound into <paramref name="audio"/>: samples per channel, 0 for none.</summary>
    int ReadAudio(SdiAudioReader audio);

    void Unlock();
}

/// <summary>
/// The DELTACAST receiver: exactly the calls the recorder has always made on the card, in the same
/// order, moved here so an IP stream can stand in its place.
/// </summary>
internal sealed class SdiInput : ICaptureInput
{
    private const uint IoTimeoutMs = 2000;

    private readonly uint _boardIndex;
    private readonly int _rxChannel;
    private readonly int _fallbackRate;

    private IntPtr _board, _stream, _slot;
    private RxLease? _lease;
    private uint _std;

    public SdiInput(uint boardIndex, int rxChannel, int fallbackRate)
    {
        _boardIndex = boardIndex;
        _rxChannel = rxChannel;
        _fallbackRate = fallbackRate;
    }

    public string Name => $"RX{_rxChannel}";

    public CaptureFormat? Open(CancellationToken ct, out string? problem)
    {
        problem = null;

        // Claim the receiver before touching it. A live preview on the same input holds
        // a yielding lease and is closed by this call; anything else is a real conflict.
        try
        {
            _lease = RxLease.Acquire(_boardIndex, _rxChannel, "recording");
        }
        catch (RxBusyException busy)
        {
            problem = busy.Message;
            return null;
        }

        uint rc = VideoMasterHD.VHD_OpenBoardHandle(_boardIndex, ref _board, IntPtr.Zero, 0);
        if (rc != 0) { problem = $"cannot open board {_boardIndex} (error {rc})."; return null; }

        int setupLock = 0;

        // JOINED so the slots carry ANC, which is where embedded audio lives.
        rc = VideoMasterHD.VHD_OpenStreamHandle(_board, VideoMasterHD.RxStreamType(_rxChannel),
                VideoMasterHD.VHD_SDI_STPROC_JOINED, ref setupLock, ref _stream, IntPtr.Zero);

        if (rc != 0)
        {
            problem = rc == 18
                ? $"RX{_rxChannel} is already open in another application - close dCARE and try again."
                : $"cannot open RX{_rxChannel} (error {rc}).";
            return null;
        }

        // The receiver may not have locked yet at the moment capture starts, so give it
        // a little time rather than giving up on a single look.
        uint detectRc = 0;
        bool locked = false;

        for (int attempt = 0; attempt < 20 && !ct.IsCancellationRequested; attempt++)
        {
            detectRc = VideoMasterHD.VHD_GetStreamPropertyEx(
                _stream, VideoMasterHD.VHD_SDI_SP_VIDEO_STANDARD, 1, ref _std);

            if (detectRc == 0) { locked = true; break; }
            Thread.Sleep(100);
        }

        if (!locked)
        {
            problem = $"no signal on RX{_rxChannel} after 2s (detect error {detectRc}) - nothing to record.";
            return null;
        }

        return CaptureFormat.FromStandard(_std, _fallbackRate);
    }

    public void Configure()
    {
        VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_SDI_SP_VIDEO_STANDARD, _std);
        VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_CORE_SP_BUFFER_PACKING,
                                            VideoMasterHD.VHD_BUFPACK_VIDEO_YUV422_8);
        VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_CORE_SP_TRANSFER_SCHEME,
                                            VideoMasterHD.VHD_TRANSFER_SLAVED);
        VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_CORE_SP_BUFFERQUEUE_DEPTH, 4);
        VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_CORE_SP_IO_TIMEOUT, IoTimeoutMs);
    }

    public string? Start()
    {
        uint rc = VideoMasterHD.VHD_StartStream(_stream);
        return rc == 0 ? null : $"StartStream failed (error {rc}).";
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
    public int DetectPairs(SdiAudioReader reader, CancellationToken ct)
    {
        var looking = Stopwatch.StartNew();
        int best = 0;

        while (looking.Elapsed < TimeSpan.FromSeconds(2) && !ct.IsCancellationRequested)
        {
            IntPtr slot = IntPtr.Zero;
            if (VideoMasterHD.VHD_LockSlotHandle(_stream, ref slot) != 0) continue;

            try
            {
                if (reader.Read(slot) > 0) best = Math.Max(best, reader.PairsPresent());
            }
            finally
            {
                VideoMasterHD.VHD_UnlockSlotHandle(slot);
            }

            // Kept looking for the whole window rather than stopping at the first answer. A
            // pair is counted on having carried a sample that was not zero, and a language
            // falls silent between words - so the highest count over two seconds is a far
            // better guess at the track layout than whatever the first slot happened to hold.
        }

        return Math.Max(1, best);
    }

    public bool Lock()
    {
        _slot = IntPtr.Zero;
        return VideoMasterHD.VHD_LockSlotHandle(_stream, ref _slot) == 0;
    }

    public bool Picture(out IntPtr buffer, out uint size)
    {
        buffer = IntPtr.Zero;
        size = 0;

        return VideoMasterHD.VHD_GetSlotBuffer(_slot, VideoMasterHD.VHD_SDI_BT_VIDEO, ref buffer, ref size) == 0
               && buffer != IntPtr.Zero;
    }

    public int ReadAudio(SdiAudioReader audio) => audio.Read(_slot);

    public void Unlock() => VideoMasterHD.VHD_UnlockSlotHandle(_slot);

    public void Dispose()
    {
        if (_stream != IntPtr.Zero) { VideoMasterHD.VHD_StopStream(_stream); VideoMasterHD.VHD_CloseStreamHandle(_stream); }
        if (_board != IntPtr.Zero) VideoMasterHD.VHD_CloseBoardHandle(_board);

        _stream = IntPtr.Zero;
        _board = IntPtr.Zero;

        _lease?.Dispose();
        _lease = null;
    }
}
