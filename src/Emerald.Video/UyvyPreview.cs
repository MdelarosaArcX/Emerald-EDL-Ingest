namespace Emerald.Video;

/// <summary>
/// Turns a slot's UYVY video into the BGRA image a confidence monitor needs.
///
/// Two things produce that picture — the shell's own preview when nothing is recording, and
/// the recorder itself once it has taken the receiver — and the hardware allows exactly one
/// open handle per input, so they can never both be reading it. Sharing the conversion is
/// what lets the picture carry on unbroken across the handover: the same pixels, by the same
/// arithmetic, whichever side is holding the channel.
///
/// <b>How much work this is, is the caller's choice.</b> The capture deck's monitor is a
/// 352-wide thumbnail and a quarter-size image is more than it can show; the playback deck's is
/// the thing an operator watches to judge what is going out, and a quarter-size image there is
/// visibly soft. So the decimation is a parameter rather than a constant, and the cost scales
/// with the output: a quarter-size 1080 frame is 130k pixels, a full one is 2M.
/// </summary>
public static class UyvyPreview
{
    /// <summary>
    /// The default reduction per axis — a quarter-size image, which is what a thumbnail wants.
    /// </summary>
    public const int Decimation = 4;

    /// <summary>
    /// Frames a second converted by default.
    ///
    /// <b>Raised from 12 to the full rate.</b> Twelve was chosen when the comment above it said
    /// converting cost a core, which was true of a full-size frame and never true of the
    /// quarter-size one this actually produces: 130k pixels of integer arithmetic is a fraction
    /// of a millisecond. What twelve bought was nothing, and what it cost was a picture that
    /// moved in visible steps and read as a freeze whenever anything else was going on.
    /// </summary>
    public const int TargetFps = 50;

    public static int OutputWidth(int sourceWidth, int decimation = Decimation) =>
        Math.Max(1, sourceWidth / Math.Max(1, decimation));

    public static int OutputHeight(int sourceHeight, int decimation = Decimation) =>
        Math.Max(1, sourceHeight / Math.Max(1, decimation));

    public static byte[] CreateBuffer(int sourceWidth, int sourceHeight,
                                      int decimation = Decimation) =>
        new byte[OutputWidth(sourceWidth, decimation) * OutputHeight(sourceHeight, decimation) * 4];

    /// <summary>
    /// One slot in every this many is worth converting at <paramref name="targetFps"/>.
    /// </summary>
    public static int ConvertEvery(int frameRate, int targetFps = TargetFps) =>
        Math.Max(1, frameRate / Math.Max(1, targetFps));

    /// <summary>
    /// UYVY to BGRA, taking one pixel out of every <paramref name="decimation"/> in each axis.
    /// BT.709 limited range, integer arithmetic — this runs on every previewed frame.
    ///
    /// <b>Chroma is read from the pair, not from the sampled byte.</b> UYVY packs two pixels as
    /// U Y0 V Y1, so only the even pixel of a pair begins on U. The previous version indexed
    /// straight off the sampled pixel and relied on the decimation being even to keep landing on
    /// one — true at 4, and silently wrong at 1 or 2, where every odd column would have read V
    /// as U and the next pair's U as V. That is a picture with its colours swapped on alternate
    /// columns, which is exactly the sort of thing that looks like a hardware fault.
    /// </summary>
    public static unsafe void ToBgra(IntPtr source, int srcWidth, byte[] dest, int outW, int outH,
                                     int decimation = Decimation)
    {
        int step = Math.Max(1, decimation);

        byte* src = (byte*)source;
        int srcStride = srcWidth * 2;

        fixed (byte* d = dest)
        {
            for (int oy = 0; oy < outH; oy++)
            {
                byte* row = src + (long)oy * step * srcStride;
                byte* outRow = d + (long)oy * outW * 4;

                for (int ox = 0; ox < outW; ox++)
                {
                    int sx = ox * step;

                    // The four bytes of the pair this pixel belongs to, then its own luma from
                    // within them.
                    byte* pair = row + (long)(sx >> 1) * 4;

                    int c = pair[1 + ((sx & 1) << 1)] - 16;
                    int du = pair[0] - 128;
                    int dv = pair[2] - 128;

                    int y = 1192 * c;
                    int r = (y + 1836 * dv) >> 10;
                    int g = (y - 218 * du - 546 * dv) >> 10;
                    int b = (y + 2166 * du) >> 10;

                    byte* o = outRow + ox * 4;
                    o[0] = Clamp(b);
                    o[1] = Clamp(g);
                    o[2] = Clamp(r);
                    o[3] = 255;
                }
            }
        }
    }

    private static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}
