using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>The recorder's picture, handed to Live Edit's big screen.</summary>
public sealed class RecordingPictureTests
{
    private static byte[] Frame(byte value) => Enumerable.Repeat(value, 16).ToArray();

    /// <summary>The recorder pays nothing while nobody is watching.</summary>
    [Fact]
    public void Nothing_is_kept_while_nobody_watches()
    {
        var picture = new RecordingPicture();
        long seen = -1;

        picture.Offer(Frame(1), 2, 2, 0);

        Assert.False(picture.TryRead(ref seen, (_, _, _, _) => { }));
    }

    /// <summary>Only the newest frame is shown, and the same one is not drawn twice.</summary>
    [Fact]
    public void The_newest_frame_is_read_once()
    {
        var picture = new RecordingPicture();
        using var watch = picture.Watch();
        long seen = -1;

        picture.Offer(Frame(1), 2, 2, 10);
        picture.Offer(Frame(2), 2, 2, 11);

        byte got = 0;
        long frame = -1;
        Assert.True(picture.TryRead(ref seen, (bgra, _, _, f) => { got = bgra[0]; frame = f; }));
        Assert.Equal(2, got);
        Assert.Equal(11, frame);

        Assert.False(picture.TryRead(ref seen, (_, _, _, _) => { }));
    }

    /// <summary>The buffer offered is the recorder's and is reused; what is held is a copy.</summary>
    [Fact]
    public void The_offered_buffer_is_copied()
    {
        var picture = new RecordingPicture();
        using var watch = picture.Watch();
        long seen = -1;

        byte[] buffer = Frame(5);
        picture.Offer(buffer, 2, 2, 0);
        buffer[0] = 9;

        byte got = 0;
        picture.TryRead(ref seen, (bgra, _, _, _) => got = bgra[0]);
        Assert.Equal(5, got);
    }

    /// <summary>Once the last watcher goes, offers stop being copied; a stopped recording shows nothing.</summary>
    [Fact]
    public void Stopping_and_clearing()
    {
        var picture = new RecordingPicture();

        var watch = picture.Watch();
        Assert.True(picture.Watched);
        watch.Dispose();
        watch.Dispose();
        Assert.False(picture.Watched);

        using var again = picture.Watch();
        long seen = -1;
        picture.Offer(Frame(1), 2, 2, 0);
        picture.Clear();

        Assert.False(picture.TryRead(ref seen, (_, _, _, _) => { }));
    }
}
