using System.Runtime.InteropServices;

namespace Emerald.Deltacast;

/// <summary>
/// A configured, running TX channel on a DELTACAST board. Frames are handed over one at a
/// time with <see cref="PushFrame"/>, which blocks until the card frees a slot — that is
/// what paces playout, so the caller never needs its own timer.
/// </summary>
public sealed class SdiOutput : IDisposable
{
    private const uint BufferQueueDepth = 4;
    private const uint IoTimeoutMs = 2000;

    private IntPtr _board = IntPtr.Zero;
    private IntPtr _stream = IntPtr.Zero;
    private bool _started;

    public VideoFormat Format { get; }
    public uint BoardIndex { get; }
    public int TxChannel { get; }

    /// <summary>Frame size the card actually reports, which is what PushFrame writes.</summary>
    public int SlotBytes { get; private set; }

    private SdiOutput(uint boardIndex, int txChannel, VideoFormat format)
    {
        BoardIndex = boardIndex;
        TxChannel = txChannel;
        Format = format;
    }

    /// <summary>
    /// Opens and starts the TX channel. Throws <see cref="SdiOutputException"/> with the
    /// failing SDK call named, so the operator sees which step the board refused.
    /// </summary>
    public static SdiOutput Open(uint boardIndex, int txChannel, VideoFormat format)
    {
        var output = new SdiOutput(boardIndex, txChannel, format);

        try
        {
            output.OpenCore();
            return output;
        }
        catch
        {
            output.Dispose();
            throw;
        }
    }

    private void OpenCore()
    {
        Check("VHD_OpenBoardHandle", VideoMasterHD.VHD_OpenBoardHandle(BoardIndex, ref _board, IntPtr.Zero, 0));

        int setupLock = 0;

        // JOINED, not DISJOINED_VIDEO: embedded audio travels in ANC, and a video-only
        // stream rejects VHD_SlotEmbedAudio with VHDERR_INVALIDSTREAM.
        Check("VHD_OpenStreamHandle",
            VideoMasterHD.VHD_OpenStreamHandle(_board, VideoMasterHD.TxStreamType(TxChannel),
                VideoMasterHD.VHD_SDI_STPROC_JOINED, ref setupLock, ref _stream, IntPtr.Zero));

        Check("VHD_SDI_SP_VIDEO_STANDARD",
            VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_SDI_SP_VIDEO_STANDARD, Format.VideoStandard));
        Check("VHD_SDI_SP_INTERFACE",
            VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_SDI_SP_INTERFACE, Format.Interface));
        Check("VHD_CORE_SP_TRANSFER_SCHEME",
            VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_CORE_SP_TRANSFER_SCHEME, VideoMasterHD.VHD_TRANSFER_SLAVED));
        Check("VHD_CORE_SP_BUFFER_PACKING",
            VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_CORE_SP_BUFFER_PACKING, VideoMasterHD.VHD_BUFPACK_VIDEO_YUV422_8));
        Check("VHD_CORE_SP_BUFFERQUEUE_DEPTH",
            VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_CORE_SP_BUFFERQUEUE_DEPTH, BufferQueueDepth));
        Check("VHD_CORE_SP_IO_TIMEOUT",
            VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_CORE_SP_IO_TIMEOUT, IoTimeoutMs));
        Check("VHD_CORE_SP_TX_OUTPUT",
            VideoMasterHD.VHD_SetStreamProperty(_stream, VideoMasterHD.VHD_CORE_SP_TX_OUTPUT, 0));

        Check("VHD_StartStream", VideoMasterHD.VHD_StartStream(_stream));
        _started = true;

        SlotBytes = Format.FrameBytes;
    }

    /// <summary>
    /// Hands one frame to the card, blocking until a slot frees up. Returns false if the
    /// card refused the slot (usually the IO timeout expiring), which ends playout.
    /// When <paramref name="left"/> and <paramref name="right"/> are supplied they are
    /// embedded as audio group 1, channels 1-2, in the same slot.
    /// </summary>
    public bool PushFrame(byte[] frame, short[]? left = null, short[]? right = null)
    {
        IntPtr slot = IntPtr.Zero;
        if (VideoMasterHD.VHD_LockSlotHandle(_stream, ref slot) != VideoMasterHD.VHDERR_NOERROR)
            return false;

        try
        {
            IntPtr buffer = IntPtr.Zero;
            uint size = 0;

            if (VideoMasterHD.VHD_GetSlotBuffer(slot, VideoMasterHD.VHD_SDI_BT_VIDEO, ref buffer, ref size)
                != VideoMasterHD.VHDERR_NOERROR || buffer == IntPtr.Zero)
                return false;

            SlotBytes = (int)size;
            Marshal.Copy(frame, 0, buffer, Math.Min(frame.Length, (int)size));

            if (left is not null && right is not null) EmbedAudio(slot, left, right);

            return true;
        }
        finally
        {
            VideoMasterHD.VHD_UnlockSlotHandle(slot);
        }
    }

    // VHD_AUDIOINFO x64 layout, verified against the SDK on a DELTA-3G. It is built by
    // hand at explicit offsets because the C struct nests two #pragma pack(1) blocks
    // inside an 8-byte-aligned outer struct, which the default marshaller does not
    // reproduce faithfully.
    private const int AudioInfoBytes = 1600;   // 4 groups x 400
    private const int GroupChannelsOffset = 64;
    private const int ChannelBytes = 80;
    private const int ChannelMode = 0;
    private const int ChannelFormat = 4;
    private const int ChannelDataSize = 68;
    private const int ChannelData = 72;

    private IntPtr _audioInfo = IntPtr.Zero;
    private IntPtr _leftBuffer = IntPtr.Zero;
    private IntPtr _rightBuffer = IntPtr.Zero;
    private int _audioCapacity;

    private void EmbedAudio(IntPtr slot, short[] left, short[] right)
    {
        int bytes = left.Length * sizeof(short);

        if (_audioInfo == IntPtr.Zero)
            _audioInfo = Marshal.AllocHGlobal(AudioInfoBytes);

        if (_audioCapacity < bytes)
        {
            if (_leftBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_leftBuffer);
            if (_rightBuffer != IntPtr.Zero) Marshal.FreeHGlobal(_rightBuffer);
            _leftBuffer = Marshal.AllocHGlobal(bytes);
            _rightBuffer = Marshal.AllocHGlobal(bytes);
            _audioCapacity = bytes;
        }

        Marshal.Copy(left, 0, _leftBuffer, left.Length);
        Marshal.Copy(right, 0, _rightBuffer, right.Length);

        // Everything zeroed leaves the other groups and channels VHD_AM_OFF, and
        // GroupCtrlValid false so the API builds the 48 kHz control packet itself.
        for (int i = 0; i < AudioInfoBytes; i += 8) Marshal.WriteInt64(_audioInfo, i, 0);

        WriteChannel(_audioInfo + GroupChannelsOffset, _leftBuffer, bytes);
        WriteChannel(_audioInfo + GroupChannelsOffset + ChannelBytes, _rightBuffer, bytes);

        VideoMasterHD.VHD_SlotEmbedAudio(slot, _audioInfo);
    }

    private static void WriteChannel(IntPtr channel, IntPtr data, int bytes)
    {
        Marshal.WriteInt32(channel, ChannelMode, (int)VideoMasterHD.VHD_AM_MONO);
        Marshal.WriteInt32(channel, ChannelFormat, (int)VideoMasterHD.VHD_AF_16);
        Marshal.WriteInt32(channel, ChannelDataSize, bytes);
        Marshal.WriteIntPtr(channel, ChannelData, data);
    }

    public (uint Output, uint Dropped) SlotCounters()
    {
        uint output = 0, dropped = 0;
        VideoMasterHD.VHD_GetStreamProperty(_stream, VideoMasterHD.VHD_CORE_SP_SLOTS_COUNT, ref output);
        VideoMasterHD.VHD_GetStreamProperty(_stream, VideoMasterHD.VHD_CORE_SP_SLOTS_DROPPED, ref dropped);
        return (output, dropped);
    }

    private byte[]? _blackFrame;

    /// <summary>
    /// A frame of legal black in UYVY: Y = 16, chroma centred at 128. Y = 0 would be
    /// below-black and illegal on SDI; 128 chroma is the neutral point.
    ///
    /// Built once and handed out thereafter. Audio-only playout pushes this on every frame,
    /// so rebuilding a 4 MB array each time would churn ~100 MB/s through the large object
    /// heap. Callers must treat the result as read-only.
    /// </summary>
    public byte[] BlackFrame()
    {
        if (_blackFrame is not null) return _blackFrame;

        var frame = new byte[Format.FrameBytes];

        for (int i = 0; i < frame.Length; i += 4)
        {
            frame[i] = 128;     // U
            frame[i + 1] = 16;  // Y0
            frame[i + 2] = 128; // V
            frame[i + 3] = 16;  // Y1
        }

        return _blackFrame = frame;
    }

    private static void Check(string call, uint rc)
    {
        if (rc != VideoMasterHD.VHDERR_NOERROR)
            throw new SdiOutputException($"{call} failed (error {rc}).");
    }

    public void Dispose()
    {
        if (_started)
        {
            VideoMasterHD.VHD_StopStream(_stream);
            _started = false;
        }

        if (_stream != IntPtr.Zero)
        {
            VideoMasterHD.VHD_CloseStreamHandle(_stream);
            _stream = IntPtr.Zero;
        }

        if (_board != IntPtr.Zero)
        {
            VideoMasterHD.VHD_CloseBoardHandle(_board);
            _board = IntPtr.Zero;
        }

        if (_audioInfo != IntPtr.Zero) { Marshal.FreeHGlobal(_audioInfo); _audioInfo = IntPtr.Zero; }
        if (_leftBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_leftBuffer); _leftBuffer = IntPtr.Zero; }
        if (_rightBuffer != IntPtr.Zero) { Marshal.FreeHGlobal(_rightBuffer); _rightBuffer = IntPtr.Zero; }
        _audioCapacity = 0;
    }
}

public sealed class SdiOutputException(string message) : Exception(message);
