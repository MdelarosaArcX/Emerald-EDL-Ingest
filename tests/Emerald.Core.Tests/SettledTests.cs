using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// Reporting a reading only once it has stopped moving.
///
/// The case this was written for: an audio pair count alternating between one and none about
/// twice a second, filling the capture log with two lines repeating and nothing else. A record
/// that does that is worse than useless — the flapping is the fault, and it is one fact rather
/// than two hundred lines.
/// </summary>
public sealed class SettledTests
{
    private static readonly DateTime T0 = new(2026, 10, 13, 9, 0, 0, DateTimeKind.Utc);

    private static Settled<int> Watcher() => new(TimeSpan.FromSeconds(2));

    [Fact]
    public void The_first_reading_is_the_starting_point_and_not_a_change()
    {
        var settled = Watcher();

        // The panel filling in is not the feed changing, and a line saying so on every start
        // would be noise at the one moment the record is most read.
        Assert.False(settled.Offer(1, T0));
        Assert.Equal(1, settled.Current);
    }

    [Fact]
    public void A_steady_reading_is_never_reported_again()
    {
        var settled = Watcher();
        settled.Offer(1, T0);

        for (int s = 1; s < 100; s++)
            Assert.False(settled.Offer(1, T0.AddSeconds(s)));
    }

    [Fact]
    public void A_change_is_reported_once_it_has_held_long_enough()
    {
        var settled = Watcher();
        settled.Offer(1, T0);

        // Still settling. The clock runs from when the new value first appeared - half a second
        // in - not from when the old one was last reported, so it is due at 2.5.
        Assert.False(settled.Offer(0, T0.AddSeconds(0.5)));
        Assert.False(settled.Offer(0, T0.AddSeconds(1.5)));
        Assert.False(settled.Offer(0, T0.AddSeconds(2)));

        // Held for the settling time: now it is a change worth writing down.
        Assert.True(settled.Offer(0, T0.AddSeconds(2.5)));
        Assert.Equal(0, settled.Current);

        // And not again.
        Assert.False(settled.Offer(0, T0.AddSeconds(5)));
    }

    [Fact]
    public void A_value_that_flaps_back_is_never_reported_at_all()
    {
        // The exact observed fault: one, none, one, none, twice a second. Not one line.
        var settled = Watcher();
        settled.Offer(1, T0);

        double at = 0;

        for (int i = 0; i < 50; i++)
        {
            at += 0.25;
            Assert.False(settled.Offer(0, T0.AddSeconds(at)));

            at += 0.25;
            Assert.False(settled.Offer(1, T0.AddSeconds(at)));
        }

        Assert.Equal(1, settled.Current);
    }

    [Fact]
    public void The_flapping_is_counted_so_it_can_be_reported_as_one_fact()
    {
        var settled = Watcher();
        settled.Offer(1, T0);

        double at = 0;

        for (int i = 0; i < 10; i++)
        {
            at += 0.25;
            settled.Offer(0, T0.AddSeconds(at));

            at += 0.25;
            settled.Offer(1, T0.AddSeconds(at));
        }

        // Ten excursions away and back.
        Assert.Equal(10, settled.Flaps);
    }

    [Fact]
    public void Taking_the_flaps_clears_them_so_nothing_is_counted_twice()
    {
        var settled = Watcher();
        settled.Offer(1, T0);

        settled.Offer(0, T0.AddSeconds(0.25));
        settled.Offer(1, T0.AddSeconds(0.5));

        Assert.Equal(1, settled.TakeFlaps());
        Assert.Equal(0, settled.TakeFlaps());
        Assert.Equal(0, settled.Flaps);
    }

    [Fact]
    public void A_genuine_change_after_flapping_is_still_reported()
    {
        // Flapping must not make the watcher deaf: once the reading really does settle on
        // something else, that is the thing worth knowing.
        var settled = Watcher();
        settled.Offer(1, T0);

        settled.Offer(0, T0.AddSeconds(0.25));
        settled.Offer(1, T0.AddSeconds(0.5));
        settled.Offer(0, T0.AddSeconds(0.75));

        Assert.True(settled.Offer(0, T0.AddSeconds(2.75)));
        Assert.Equal(0, settled.Current);
    }

    [Fact]
    public void A_reading_that_moves_on_again_while_settling_starts_over()
    {
        var settled = Watcher();
        settled.Offer(1, T0);

        settled.Offer(2, T0.AddSeconds(0.5));

        // A third value before the second had settled: the clock restarts on this one.
        Assert.False(settled.Offer(3, T0.AddSeconds(1.5)));
        Assert.False(settled.Offer(3, T0.AddSeconds(3.0)));
        Assert.True(settled.Offer(3, T0.AddSeconds(3.5)));

        Assert.Equal(3, settled.Current);
    }

    [Fact]
    public void A_zero_settling_time_reports_a_change_at_once()
    {
        // For a caller that wants every change: the behaviour should degrade to the old one
        // rather than to something surprising.
        var settled = new Settled<int>(TimeSpan.Zero);

        settled.Offer(1, T0);

        Assert.True(settled.Offer(0, T0));
        Assert.True(settled.Offer(1, T0));
    }
}
