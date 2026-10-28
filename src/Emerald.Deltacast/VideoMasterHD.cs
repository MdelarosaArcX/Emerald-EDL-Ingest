using System.Runtime.InteropServices;

namespace Emerald.Deltacast;

/// <summary>
/// Minimal P/Invoke surface for the DELTACAST VideoMaster SDK, limited to what the
/// EDL Generator needs: enumerating boards and reading their RX/TX channel counts.
/// VideoMasterHD.dll ships into %WINDIR%\System32 with the driver, so the plain
/// DLL name resolves. It is 64-bit only -- the project is pinned to x64.
/// </summary>
public static class VideoMasterHD
{
    private const string Dll = "VideoMasterHD.dll";

    // VideoMasterHD_Core.h: #define ENUMBASE_CORE 0x01000000, then VHD_CORE_BOARDPROPERTY
    // is a plain sequential enum starting at that base.
    private const uint EnumBaseCore = 0x01000000;

    public const uint VHD_CORE_BP_BOARD_TYPE = EnumBaseCore + 4;
    public const uint VHD_CORE_BP_NB_RXCHANNELS = EnumBaseCore + 13;
    public const uint VHD_CORE_BP_NB_TXCHANNELS = EnumBaseCore + 14;

    private const uint EnumBaseSdi = 0x02000000;

    // Stream properties
    public const uint VHD_CORE_SP_TRANSFER_SCHEME = EnumBaseCore + 2;
    public const uint VHD_CORE_SP_IO_TIMEOUT = EnumBaseCore + 3;
    public const uint VHD_CORE_SP_TX_OUTPUT = EnumBaseCore + 4;
    public const uint VHD_CORE_SP_SLOTS_COUNT = EnumBaseCore + 5;
    public const uint VHD_CORE_SP_SLOTS_DROPPED = EnumBaseCore + 6;
    public const uint VHD_CORE_SP_BUFFERQUEUE_DEPTH = EnumBaseCore + 7;
    public const uint VHD_CORE_SP_BUFFER_PACKING = EnumBaseCore + 10;

    public const uint VHD_SDI_SP_VIDEO_STANDARD = EnumBaseSdi + 1;
    public const uint VHD_SDI_SP_INTERFACE = EnumBaseSdi + 5;

    // Stream types: the TX channels are not contiguous in the enum, and the gaps are not a
    // pattern that can be extrapolated. Taken from VideoMasterHD_Core.h, VHD_STREAMTYPE:
    //
    //   TX0=2  TX1=3    TX2=11  TX3=12    TX4=33  TX5=34  TX6=35  TX7=36    TX8..TX11=48..51
    //   RX0=0  RX1=1    RX2=9   RX3=10    RX4..RX7=20..23                   RX8..RX11=44..47
    //
    // The numbers in between belong to the coupled, modify and hardware-process stream types, so
    // guessing at the next one in a run would open something else entirely - on a transmitter,
    // the wrong output.
    public const uint VHD_ST_TX0 = 2;
    public const uint VHD_ST_TX1 = 3;
    public const uint VHD_ST_TX2 = 11;
    public const uint VHD_ST_TX3 = 12;
    public const uint VHD_ST_TX4 = 33;
    public const uint VHD_ST_TX5 = 34;
    public const uint VHD_ST_TX6 = 35;
    public const uint VHD_ST_TX7 = 36;
    public const uint VHD_ST_TX8 = 48;
    public const uint VHD_ST_TX9 = 49;
    public const uint VHD_ST_TX10 = 50;
    public const uint VHD_ST_TX11 = 51;

    public const uint VHD_SDI_STPROC_DISJOINED_VIDEO = EnumBaseSdi + 2;

    /// <summary>
    /// Video and ANC handled together. Embedded audio rides in ANC, so this — not
    /// DISJOINED_VIDEO — is the processing mode a stream must be opened with for
    /// VHD_SlotEmbedAudio to work; the video-only mode returns VHDERR_INVALIDSTREAM.
    /// </summary>
    public const uint VHD_SDI_STPROC_JOINED = EnumBaseSdi + 1;

    // VHD_AUDIOMODE / VHD_AUDIOFORMAT, both 0-based.
    public const uint VHD_AM_OFF = 0;
    public const uint VHD_AM_MONO = 1;
    public const uint VHD_AF_16 = 1;

    public const uint VHD_TRANSFER_SLAVED = 1;
    public const uint VHD_BUFPACK_VIDEO_YUV422_8 = 0;
    public const uint VHD_SDI_BT_VIDEO = 0;

    public const uint VHDERR_NOERROR = 0;

    /// <summary>How many channels of each direction this layer can name a stream type for.</summary>
    public const int MaxChannels = 12;

    /// <summary>
    /// Whether a channel number can be turned into a stream type at all.
    ///
    /// Asked before a port is offered to an operator. A board reports how many channels it has,
    /// and that count is not a promise that this layer knows the enum value for each of them —
    /// it used to stop at four, so a card with seven outputs had three of them listed on the
    /// playback deck that could not be opened.
    /// </summary>
    public static bool Supports(int channel) => channel is >= 0 and < MaxChannels;

    /// <summary>RX channel numbers are as scattered through VHD_STREAMTYPE as the TX ones.</summary>
    public static uint RxStreamType(int channel) => channel switch
    {
        0 => 0, 1 => 1, 2 => 9, 3 => 10,
        4 => 20, 5 => 21, 6 => 22, 7 => 23,
        8 => 44, 9 => 45, 10 => 46, 11 => 47,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel,
                 $"Only RX0-RX{MaxChannels - 1} can be named as a stream type."),
    };

    /// <summary>Maps a TX channel number to its VHD_STREAMTYPE value.</summary>
    public static uint TxStreamType(int channel) => channel switch
    {
        0 => VHD_ST_TX0,
        1 => VHD_ST_TX1,
        2 => VHD_ST_TX2,
        3 => VHD_ST_TX3,
        4 => VHD_ST_TX4,
        5 => VHD_ST_TX5,
        6 => VHD_ST_TX6,
        7 => VHD_ST_TX7,
        8 => VHD_ST_TX8,
        9 => VHD_ST_TX9,
        10 => VHD_ST_TX10,
        11 => VHD_ST_TX11,
        _ => throw new ArgumentOutOfRangeException(nameof(channel), channel,
                 $"Only TX0-TX{MaxChannels - 1} can be named as a stream type."),
    };

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_GetApiInfo(ref uint pApiVersion, ref uint pNbBoards);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern IntPtr VHD_GetBoardModel(uint boardIndex);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_OpenBoardHandle(uint boardIndex, ref IntPtr pBrdHandle,
                                                  IntPtr onStateChangeEvent, uint stateChangeMask);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_CloseBoardHandle(IntPtr brdHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_GetBoardProperty(IntPtr brdHandle, uint property, ref uint pValue);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_OpenStreamHandle(IntPtr brdHandle, uint strmType, uint processingMode,
                                                   ref int pSetupLock, ref IntPtr pStrmHandle, IntPtr onDataReadyEvent);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_CloseStreamHandle(IntPtr strmHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_SetStreamProperty(IntPtr strmHandle, uint property, uint value);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_GetStreamProperty(IntPtr strmHandle, uint property, ref uint pValue);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_StartStream(IntPtr strmHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_StopStream(IntPtr strmHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_LockSlotHandle(IntPtr strmHandle, ref IntPtr pSlotHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_UnlockSlotHandle(IntPtr slotHandle);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_GetSlotBuffer(IntPtr slotHandle, uint bufferType,
                                                ref IntPtr ppBuffer, ref uint pBufferSize);

    [DllImport("VideoMasterHD_Audio.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_SlotEmbedAudio(IntPtr slotHandle, IntPtr pAudioInfo);

    [DllImport("VideoMasterHD_Audio.dll", CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_SlotExtractAudio(IntPtr slotHandle, IntPtr pAudioInfo);

    [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
    public static extern uint VHD_GetStreamPropertyEx(IntPtr strmHandle, uint property, int allowSignalDetection, ref uint pValue);

    public static string GetBoardModelString(uint boardIndex)
    {
        IntPtr p = VHD_GetBoardModel(boardIndex);
        return p == IntPtr.Zero ? $"Board {boardIndex}" : Marshal.PtrToStringAnsi(p) ?? $"Board {boardIndex}";
    }

    /// <summary>VHD_BOARDTYPE values, from VideoMasterHD_Core.h.</summary>
    public static string BoardTypeName(uint type) => type switch
    {
        0 => "DELTA-hd",
        5 => "Mixed interfaces",
        6 => "DELTA-3G",
        7 => "DELTA-key 3G",
        8 => "DELTA-h4k",
        10 => "DELTA-asi",
        11 => "DELTA-ip",
        12 => "DELTA-h4k2",
        13 => "FLEX-dp",
        14 => "FLEX-sdi",
        15 => "DELTA-12G",
        16 => "FLEX-hmi",
        _ => $"Type {type}",
    };
}
