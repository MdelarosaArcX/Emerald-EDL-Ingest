using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

public class HoldNoticesTests
{
    private const string Hold = "The receiver has paused; holding the last frame.";

    [Fact]
    public void The_first_hold_and_its_return_to_air_are_said()
    {
        long now = 0;
        var notices = new HoldNotices(() => now);

        Assert.Equal(Hold, notices.Filter(DelayPhase.OnAir, DelayPhase.Holding, Hold));
        Assert.Equal("On air.", notices.Filter(DelayPhase.Holding, DelayPhase.OnAir, "On air."));
    }

    [Fact]
    public void Holds_soon_after_are_counted_not_said_and_the_next_one_said_carries_the_count()
    {
        long now = 0;
        var notices = new HoldNotices(() => now);

        notices.Filter(DelayPhase.OnAir, DelayPhase.Holding, Hold);
        notices.Filter(DelayPhase.Holding, DelayPhase.OnAir, "On air.");

        for (int i = 0; i < 3; i++)
        {
            now += 2_000;
            Assert.Equal("", notices.Filter(DelayPhase.OnAir, DelayPhase.Holding, Hold));
            Assert.Equal("", notices.Filter(DelayPhase.Holding, DelayPhase.OnAir, "On air."));
        }

        now += HoldNotices.QuietMs;
        string said = notices.Filter(DelayPhase.OnAir, DelayPhase.Holding, Hold);

        Assert.StartsWith(Hold, said);
        Assert.Contains("3 more short hold(s)", said);
    }

    [Fact]
    public void A_failure_during_a_quiet_hold_is_still_said()
    {
        long now = 0;
        var notices = new HoldNotices(() => now);

        notices.Filter(DelayPhase.OnAir, DelayPhase.Holding, Hold);
        notices.Filter(DelayPhase.Holding, DelayPhase.OnAir, "On air.");

        now += 1_000;
        notices.Filter(DelayPhase.OnAir, DelayPhase.Holding, Hold);

        const string failed = "The receiver has stopped delivering frames. Cutting to black.";
        Assert.Equal(failed, notices.Filter(DelayPhase.Holding, DelayPhase.Failed, failed));
    }
}
