using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

public class LowMemoryWatchTests
{
    private const long GB = 1L << 30;

    [Fact]
    public void Plenty_free_says_nothing()
    {
        var watch = new LowMemoryWatch();

        Assert.Null(watch.Check(8 * GB));
        Assert.Equal(LowMemoryWatch.Level.Normal, watch.Current);
    }

    [Fact]
    public void Falling_through_each_threshold_is_said_once()
    {
        var watch = new LowMemoryWatch();

        Assert.Equal(LowMemoryWatch.Level.Low, watch.Check(2 * GB + GB / 2));
        Assert.Null(watch.Check(2 * GB));
        Assert.Equal(LowMemoryWatch.Level.Critical, watch.Check(GB));
        Assert.Null(watch.Check(GB / 2));
    }

    [Fact]
    public void Hovering_at_a_threshold_does_not_fill_the_log()
    {
        var watch = new LowMemoryWatch();

        Assert.Equal(LowMemoryWatch.Level.Low, watch.Check(LowMemoryWatch.LowBytes - 1));

        // Back over the line, but not by the hysteresis: still low, nothing said.
        Assert.Null(watch.Check(LowMemoryWatch.LowBytes + 1));
        Assert.Null(watch.Check(LowMemoryWatch.LowBytes - 1));

        Assert.Equal(LowMemoryWatch.Level.Normal,
                     watch.Check(LowMemoryWatch.LowBytes + LowMemoryWatch.HysteresisBytes));
    }

    [Fact]
    public void Recovering_from_critical_goes_to_wherever_memory_now_is()
    {
        var watch = new LowMemoryWatch();
        watch.Check(GB);

        Assert.Equal(LowMemoryWatch.Level.Low, watch.Check(2 * GB + GB / 2));
        Assert.Equal(LowMemoryWatch.Level.Normal, watch.Check(5 * GB));

        watch.Check(GB);
        Assert.Equal(LowMemoryWatch.Level.Normal, watch.Check(6 * GB));
    }
}
