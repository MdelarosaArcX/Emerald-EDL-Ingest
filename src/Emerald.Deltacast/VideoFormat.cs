namespace Emerald.Deltacast;

/// <summary>
/// The SDI format frames are played out in. Everything is normalised to 1080-line HD:
/// ffmpeg scales and pads whatever the source is, so any media plays on any board without
/// the operator having to match formats by hand.
/// </summary>
public sealed record VideoFormat(
    string Name,
    uint VideoStandard,
    uint Interface,
    int Width,
    int Height,
    int FrameRate)
{
    /// <summary>Bytes per frame in UYVY (VHD_BUFPACK_VIDEO_YUV422_8): two bytes per pixel.</summary>
    public int FrameBytes => Width * Height * 2;

    // VHD_VIDEOSTANDARD values, from VideoMasterHD_Sdi.h.
    private const uint Std1080p25 = 0;
    private const uint Std1080p30 = 1;
    private const uint Std1080p24 = 8;
    private const uint Std1080p60 = 9;
    private const uint Std1080p50 = 10;

    // VHD_INTERFACE values. 1080p at 50/60 exceeds the 1.5 Gbps HD link and needs 3G.
    private const uint InterfaceHd292 = 2;
    private const uint Interface3GA = 4;

    /// <summary>
    /// Picks the output standard from the timecode frame rate. Rates the SDI standards do
    /// not cover fall back to 1080p25, since a running output beats no output at all.
    /// </summary>
    public static VideoFormat ForFrameRate(int frameRate) => frameRate switch
    {
        24 => new VideoFormat("1080p24", Std1080p24, InterfaceHd292, 1920, 1080, 24),
        30 => new VideoFormat("1080p30", Std1080p30, InterfaceHd292, 1920, 1080, 30),
        50 => new VideoFormat("1080p50", Std1080p50, Interface3GA, 1920, 1080, 50),
        60 => new VideoFormat("1080p60", Std1080p60, Interface3GA, 1920, 1080, 60),
        _ => new VideoFormat("1080p25", Std1080p25, InterfaceHd292, 1920, 1080, 25),
    };

    public bool IsExactMatchFor(int frameRate) => frameRate == FrameRate;
}
