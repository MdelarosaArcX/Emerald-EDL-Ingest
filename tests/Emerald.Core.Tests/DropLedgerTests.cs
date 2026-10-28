using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The record of what a message lost, stall by stall.
///
/// Every drop is now logged as it happens rather than at a throttled interval, which makes the
/// accounting the thing that has to be right: a stall counted twice doubles the reported loss, a
/// stall missed hides it, and a transmitter that catches up a little must not have frames it
/// failed to transmit forgiven. None of that is visible by reading a play loop.
/// </summary>
public sealed class DropLedgerTests
{
    [Fact]
    public void A_message_that_keeps_up_has_nothing_to_report()
    {
        var ledger = new DropLedger();

        // Asked every frame, answering zero every time.
        for (int i = 0; i < 100; i++) Assert.Equal(0, ledger.Record(0));

        Assert.Equal(0, ledger.Total);
        Assert.Equal(0, ledger.Stalls);
        Assert.Equal("no frames dropped", ledger.Summary(25));
    }

    [Fact]
    public void A_stall_is_reported_once_with_what_it_cost()
    {
        var ledger = new DropLedger();

        Assert.Equal(12, ledger.Record(12));

        // Asked again at the same shortfall: nothing new happened, so nothing is reported.
        Assert.Equal(0, ledger.Record(12));
        Assert.Equal(0, ledger.Record(12));

        Assert.Equal(12, ledger.Total);
        Assert.Equal(1, ledger.Stalls);
    }

    [Fact]
    public void A_second_stall_reports_only_its_own_frames()
    {
        var ledger = new DropLedger();

        ledger.Record(12);

        // The shortfall is now 20 in total, so this stall cost 8 - not 20.
        Assert.Equal(8, ledger.Record(20));

        Assert.Equal(20, ledger.Total);
        Assert.Equal(2, ledger.Stalls);
    }

    [Fact]
    public void The_reported_losses_add_up_to_the_total()
    {
        // The invariant that makes the log trustworthy: adding up every DROP line in the record
        // must come to the figure on the closing line.
        var ledger = new DropLedger();

        long reported = 0;

        foreach (long shortfall in new long[] { 3, 3, 7, 7, 7, 19, 40, 40, 41 })
            reported += ledger.Record(shortfall);

        Assert.Equal(ledger.Total, reported);
        Assert.Equal(41, ledger.Total);
    }

    [Fact]
    public void Catching_up_forgives_nothing_and_reports_nothing()
    {
        var ledger = new DropLedger();

        ledger.Record(50);

        // The engine makes up some ground, so the shortfall against the clock falls. Those
        // frames still did not go out: the total holds, and the recovery is not an event.
        Assert.Equal(0, ledger.Record(30));
        Assert.Equal(0, ledger.Record(10));

        Assert.Equal(50, ledger.Total);
        Assert.Equal(1, ledger.Stalls);

        // And going worse than the old high-water mark counts only the part beyond it.
        Assert.Equal(5, ledger.Record(55));
        Assert.Equal(55, ledger.Total);
        Assert.Equal(2, ledger.Stalls);
    }

    [Fact]
    public void A_nonsense_shortfall_is_ignored_rather_than_counted()
    {
        var ledger = new DropLedger();

        Assert.Equal(0, ledger.Record(0));
        Assert.Equal(0, ledger.Record(-5));
        Assert.Equal(0, ledger.Stalls);
    }

    [Fact]
    public void One_message_is_never_judged_on_anothers_stalls()
    {
        var ledger = new DropLedger();

        ledger.Record(100);
        ledger.Reset();

        Assert.Equal(0, ledger.Total);
        Assert.Equal(0, ledger.Stalls);
        Assert.Equal("no frames dropped", ledger.Summary(25));

        // And the next message's first stall is its own full cost, not the difference from the
        // last message's total.
        Assert.Equal(4, ledger.Record(4));
    }

    [Fact]
    public void The_summary_says_the_frames_the_stalls_and_the_time()
    {
        var ledger = new DropLedger();

        ledger.Record(25);
        ledger.Record(50);

        string said = ledger.Summary(25);

        Assert.Contains("50", said);        // frames
        Assert.Contains("2", said);         // stalls
        Assert.Contains("2.00 s", said);    // 50 frames at 25 fps
    }

    [Fact]
    public void The_time_lost_follows_the_frame_rate()
    {
        var ledger = new DropLedger();
        ledger.Record(50);

        Assert.Contains("2.00 s", ledger.Summary(25));
        Assert.Contains("1.00 s", ledger.Summary(50));

        // A nonsense rate must not divide by zero and must still say something.
        Assert.Contains("50", ledger.Summary(0));
    }
}
