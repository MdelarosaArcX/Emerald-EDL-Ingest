namespace Emerald.Video;

/// <summary>
/// Turns a slot's UYVY video into the small BGRA image a confidence monitor needs.
///
/// Two things produce that picture — the shell's own preview when nothing is recording, and
/// the recorder itself once it has taken the receiver — and the hardware allows exactly one
/// open handle per input, so they can never both be reading it. Sharing the conversion is
/// what lets the picture carry on unbroken across the handover: the same pixels, by the same
/// arithmetic, whichever side is holding the channel.
/// </summary>
public static class UyvyPreview
{
    /// <summary>How much smaller than the source the preview image is, per axis.</summary>
    public const int Decimation = 4;

    /// <summary>Frames a second worth converting. The rest are locked and released untouched.</summary>
    public const int TargetFps = 12;

    public static int OutputWidth(int sourceWidth) => sourceWidth / Decimation;
    public static int OutputHeight(int sourceHeight) => sourceHeight / Decimation;

    public static byte[] CreateBuffer(int sourceWidth, int sourceHeight) =>
        new byte[OutputWidth(sourceWidth) * OutputHeight(sourceHeight) * 4];

    /// <summary>One slot in every this many is worth converting at <see cref="TargetFps"/>.</summary>
    public static int ConvertEvery(int frameRate) => Math.Max(1, frameRate / TargetFps);

    /// <summary>
    /// UYVY to BGRA, taking one pixel out of every <see cref="Decimation"/> in each axis.
    /// BT.709 limited range, integer arithmetic — this runs on every previewed frame.
    /// </summary>
    public static unsafe void ToBgra(IntPtr source, int srcWidth, byte[] dest, int outW, int outH)
    {
        byte* src = (byte*)source;
        int srcStride = srcWidth * 2;

        fixed (byte* d = dest)
        {
            for (int oy = 0; oy < outH; oy++)
            {
                byte* row = src + (long)oy * Decimation * srcStride;
                byte* outRow = d + (long)oy * outW * 4;

                for (int ox = 0; ox < outW; ox++)
                {
                    // Decimation is even, so the sampled pixel is always the first of a
                    // UYVY pair and carries its own chroma.
                    byte* p = row + (long)ox * Decimation * 2;

                    int c = p[1] - 16;
                    int du = p[0] - 128;
                    int dv = p[2] - 128;

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
