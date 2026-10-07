using System.Runtime.InteropServices;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// Turning a receiver's UYVY into a picture.
///
/// The case that matters here is chroma at a decimation of 1 or 2. UYVY packs two pixels as
/// U Y0 V Y1, so only the even pixel of a pair starts on U — and the previous version indexed
/// chroma straight off the sampled pixel, which is correct at 4 and swaps U and V on every odd
/// column at 1. Nothing caught that because nothing ever asked for a decimation of 1.
/// </summary>
public sealed class UyvyPreviewTests
{
    /// <summary>A UYVY frame built from a function of the pixel position, in unmanaged memory.</summary>
    private static IntPtr Frame(int width, int height, Func<int, int, (byte Y, byte U, byte V)> at,
                                out IntPtr handle)
    {
        var bytes = new byte[width * height * 2];

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x += 2)
            {
                (byte y0, byte u, byte v) = at(x, y);
                (byte y1, _, _) = at(x + 1, y);

                int o = y * width * 2 + x * 2;

                bytes[o + 0] = u;
                bytes[o + 1] = y0;
                bytes[o + 2] = v;
                bytes[o + 3] = y1;
            }
        }

        handle = Marshal.AllocHGlobal(bytes.Length);
        Marshal.Copy(bytes, 0, handle, bytes.Length);
        return handle;
    }

    private static (byte B, byte G, byte R) Pixel(byte[] bgra, int outW, int x, int y)
    {
        int o = (y * outW + x) * 4;
        return (bgra[o], bgra[o + 1], bgra[o + 2]);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void Grey_comes_out_grey_at_every_decimation(int decimation)
    {
        // Y=128 with neutral chroma is mid grey. If U and V are read from the wrong bytes the
        // result is still grey here - which is why the next test exists - but a frame that is
        // not grey at all would be a luma fault, and this separates the two.
        const int w = 64, h = 16;

        IntPtr frame = Frame(w, h, (_, _) => (128, 128, 128), out IntPtr handle);

        try
        {
            int outW = UyvyPreview.OutputWidth(w, decimation);
            int outH = UyvyPreview.OutputHeight(h, decimation);
            byte[] dest = UyvyPreview.CreateBuffer(w, h, decimation);

            UyvyPreview.ToBgra(frame, w, dest, outW, outH, decimation);

            for (int y = 0; y < outH; y++)
            for (int x = 0; x < outW; x++)
            {
                (byte b, byte g, byte r) = Pixel(dest, outW, x, y);

                // Within a count or two of 128: this is integer arithmetic on a limited-range
                // signal, not a reversible transform.
                Assert.InRange(b, 125, 131);
                Assert.InRange(g, 125, 131);
                Assert.InRange(r, 125, 131);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(handle);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    public void A_red_frame_is_red_and_not_blue_at_every_decimation(int decimation)
    {
        // The actual regression. Red is high V and low U; reading them the wrong way round
        // produces blue, and at a decimation of 1 the old code did that on every odd column.
        const int w = 64, h = 16;

        IntPtr frame = Frame(w, h, (_, _) => (81, 90, 240), out IntPtr handle);

        try
        {
            int outW = UyvyPreview.OutputWidth(w, decimation);
            int outH = UyvyPreview.OutputHeight(h, decimation);
            byte[] dest = UyvyPreview.CreateBuffer(w, h, decimation);

            UyvyPreview.ToBgra(frame, w, dest, outW, outH, decimation);

            for (int y = 0; y < outH; y++)
            for (int x = 0; x < outW; x++)
            {
                (byte b, byte g, byte r) = Pixel(dest, outW, x, y);

                Assert.True(r > 200, $"column {x} came out r={r}, not red");
                Assert.True(b < 80, $"column {x} came out b={b}, so U and V are swapped");
                Assert.True(g < 110, $"column {x} came out g={g}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(handle);
        }
    }

    [Fact]
    public void Every_pixel_of_a_vertical_bar_pattern_lands_where_it_should_at_full_resolution()
    {
        // At a decimation of 1 the output is the input, pixel for pixel, so a pattern that
        // alternates per column proves the luma indexing within a pair as well as between them.
        const int w = 16, h = 4;

        IntPtr frame = Frame(w, h, (x, _) => ((byte)(x % 2 == 0 ? 235 : 16), 128, 128),
                             out IntPtr handle);

        try
        {
            byte[] dest = UyvyPreview.CreateBuffer(w, h, 1);

            UyvyPreview.ToBgra(frame, w, dest, w, h, 1);

            for (int x = 0; x < w; x++)
            {
                (byte b, _, _) = Pixel(dest, w, x, 0);

                if (x % 2 == 0) Assert.True(b > 230, $"column {x} should be white, got {b}");
                else Assert.True(b < 25, $"column {x} should be black, got {b}");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(handle);
        }
    }

    [Fact]
    public void The_output_size_follows_the_decimation()
    {
        Assert.Equal(1920, UyvyPreview.OutputWidth(1920, 1));
        Assert.Equal(960, UyvyPreview.OutputWidth(1920, 2));
        Assert.Equal(480, UyvyPreview.OutputWidth(1920, 4));

        Assert.Equal(1080, UyvyPreview.OutputHeight(1080, 1));
        Assert.Equal(270, UyvyPreview.OutputHeight(1080, 4));

        // The buffer is four bytes a pixel of the OUTPUT, not the source - getting this wrong
        // either wastes megabytes a frame or overruns.
        Assert.Equal(1920 * 1080 * 4, UyvyPreview.CreateBuffer(1920, 1080, 1).Length);
        Assert.Equal(480 * 270 * 4, UyvyPreview.CreateBuffer(1920, 1080, 4).Length);
    }

    [Fact]
    public void A_nonsense_decimation_is_treated_as_one_rather_than_dividing_by_zero()
    {
        Assert.Equal(1920, UyvyPreview.OutputWidth(1920, 0));
        Assert.Equal(1920, UyvyPreview.OutputWidth(1920, -4));
        Assert.Equal(1920 * 1080 * 4, UyvyPreview.CreateBuffer(1920, 1080, 0).Length);
    }

    [Fact]
    public void Every_frame_is_converted_at_the_full_rate_and_fewer_when_asked()
    {
        // The default no longer drops frames, which is what made the picture step.
        Assert.Equal(1, UyvyPreview.ConvertEvery(25));
        Assert.Equal(1, UyvyPreview.ConvertEvery(50));

        Assert.Equal(2, UyvyPreview.ConvertEvery(25, targetFps: 12));

        // A nonsense target clamps to one frame a second rather than dividing by zero, so at 25
        // fps that is every 25th slot.
        Assert.Equal(25, UyvyPreview.ConvertEvery(25, targetFps: 0));
    }
}
