using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// What the transmitter does when the two ends of the delay disagree.
///
/// The receiver and the transmitter run on different oscillators, so over hours they will
/// drift apart; a recording can stop, a receiver can lose lock, and a stalled reader can be
/// lapped. Each of those has a right answer and several wrong ones, and none of them is
/// convenient to reproduce on a card — which is exactly why the policy is a pure function.
/// </summary>
public class DelayPacerTests
{
    private const int Rate = 25;
    private const long Target = 25 * 60;      // one minute
    private const long Slots = Target + 50;

    private static PaceAction Decide(long written, long read, bool isSealed = false,
                                     long sealedAt = 0, long stalledMs = 0) =>
        DelayPacer.Decide(written, read, Target, Slots, isSealed, sealedAt, stalledMs, Rate);

    [Fact]
    public void Nothing_goes_to_air_until_a_whole_delay_has_accumulated()
    {
        Assert.Equal(PaceAction.Fill, Decide(written: 0, read: 0));
        Assert.Equal(PaceAction.Fill, Decide(written: Target - 1, read: 0));
    }

    [Fact]
    public void Once_the_delay_is_there_it_plays()
    {
        Assert.Equal(PaceAction.Advance, Decide(written: Target, read: 0));
    }

    [Fact]
    public void Running_at_the_design_distance_just_advances()
    {
        Assert.Equal(PaceAction.Advance, Decide(written: 10_000, read: 10_000 - Target));
    }

    // ------------------------------------------------------------------ underrun

    [Fact]
    public void Catching_the_writer_repeats_rather_than_transmitting_nothing()
    {
        // The reader has drawn level: there is no next frame, and skipping forward would mean
        // transmitting a frame the recorder has not written.
        Assert.Equal(PaceAction.Repeat, Decide(written: 10_000, read: 10_000));
    }

    [Fact]
    public void A_receiver_that_has_gone_quiet_holds_for_a_while_and_then_gives_up()
    {
        Assert.Equal(PaceAction.Repeat, Decide(written: 10_000, read: 10_000, stalledMs: 500));
        Assert.Equal(PaceAction.Repeat, Decide(written: 10_000, read: 10_000, stalledMs: 9_000));

        // Ten seconds of a frozen picture is the outer bound of tolerable; past that, black
        // is the more honest thing to transmit.
        Assert.Equal(PaceAction.Fail, Decide(written: 10_000, read: 10_000, stalledMs: 10_001));
    }

    [Fact]
    public void A_sealed_line_that_has_run_dry_finishes_rather_than_failing()
    {
        // The recording stopped, so a stalled writer is expected, not a fault.
        Assert.Equal(PaceAction.Finish,
            Decide(written: 500, read: 500, isSealed: true, sealedAt: 500, stalledMs: 60_000));
    }

    // ------------------------------------------------------------------ lapping

    [Fact]
    public void Being_lapped_resyncs_instead_of_transmitting_a_torn_frame()
    {
        Assert.Equal(PaceAction.Resync, Decide(written: 10_000, read: 10_000 - Slots - 1));
    }

    [Fact]
    public void Lapping_is_checked_before_underrun_when_both_could_apply()
    {
        // After a long stall the reader can be both behind the ring and level with nothing;
        // the lap is the one that would put a bad frame on air, so it wins.
        Assert.Equal(PaceAction.Resync, Decide(written: 10_000, read: 5));
    }

    // ------------------------------------------------------------------ drift

    [Fact]
    public void Small_drift_is_left_alone()
    {
        long deadband = DelayPacer.Deadband(Rate);

        for (long off = -deadband; off <= deadband; off++)
        {
            long read = 10_000 - (Target + off);
            Assert.Equal(PaceAction.Advance, Decide(written: 10_000, read: read));
        }
    }

    [Fact]
    public void Drifting_long_sheds_a_frame_and_drifting_short_holds_one()
    {
        long deadband = DelayPacer.Deadband(Rate);

        // The delay has grown past the deadband: drop a frame to bring it back.
        Assert.Equal(PaceAction.Skip,
            Decide(written: 10_000, read: 10_000 - (Target + deadband + 1)));

        // The delay has shrunk: hold a frame to let it grow back.
        Assert.Equal(PaceAction.Repeat,
            Decide(written: 10_000, read: 10_000 - (Target - deadband - 1)));
    }

    [Fact]
    public void The_deadband_is_half_a_second_at_any_rate()
    {
        Assert.Equal(12, DelayPacer.Deadband(25));
        Assert.Equal(25, DelayPacer.Deadband(50));
        Assert.Equal(1, DelayPacer.Deadband(1));
    }

    // ------------------------------------------------------------------ draining

    [Fact]
    public void Stopping_the_recording_still_plays_out_everything_recorded()
    {
        // Sealed a minute ago; the last minute of programme is still in the ring and must
        // reach air rather than being cut off with the recording.
        Assert.Equal(PaceAction.Advance,
            Decide(written: 10_000, read: 10_000 - Target, isSealed: true, sealedAt: 10_000));

        Assert.Equal(PaceAction.Finish,
            Decide(written: 10_000, read: 10_000, isSealed: true, sealedAt: 10_000));
    }
}
