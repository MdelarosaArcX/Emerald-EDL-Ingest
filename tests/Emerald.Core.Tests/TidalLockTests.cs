using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The handshake between the two decks.
///
/// It is only a few fields, but it is what decides when a recording reaches the
/// transmitter — so the sequencing is worth pinning down rather than trusting to two
/// windows agreeing at run time.
/// </summary>
public class TidalLockTests
{
    private static Timecode At(string text, int rate = 25)
    {
        Assert.True(Timecode.TryParse(text, rate, out Timecode value, out string? error), error);
        return value;
    }

    /// <summary>A fresh lock, since the real one is a process-wide singleton.</summary>
    private static TidalLock New() => new();

    [Fact]
    public void A_new_lock_is_off_and_knows_nothing()
    {
        TidalLock lockState = New();

        Assert.Equal(TidalLockState.Off, lockState.State);
        Assert.Null(lockState.DelayFile);
        Assert.Null(lockState.CueAt);
    }

    [Fact]
    public void Arming_waits_for_the_recorder_rather_than_starting_anything()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));

        Assert.Equal(TidalLockState.Armed, lockState.State);
        Assert.Null(lockState.DelayFile);
        Assert.Null(lockState.CueAt);
    }

    [Fact]
    public void The_cue_is_the_roll_point_plus_the_delay()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled(@"E:\media\delay\capture.ts", At("20:57:26:00"));

        Assert.Equal(TidalLockState.CountingDown, lockState.State);
        Assert.Equal("20:58:26:00", lockState.CueAt!.Value.ToString());
        Assert.Equal(@"E:\media\delay\capture.ts", lockState.DelayFile);
    }

    [Theory]
    [InlineData(30, "20:57:56:00")]
    [InlineData(60, "20:58:26:00")]
    [InlineData(300, "21:02:26:00")]
    public void The_delay_is_whatever_was_armed(int seconds, string expected)
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromSeconds(seconds));
        lockState.RecordingRolled("delay.ts", At("20:57:26:00"));

        Assert.Equal(expected, lockState.CueAt!.Value.ToString());
    }

    [Fact]
    public void A_cue_past_midnight_wraps_rather_than_running_off_the_end()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled("delay.ts", At("23:59:30:00"));

        Assert.Equal("00:00:30:00", lockState.CueAt!.Value.ToString());
    }

    [Fact]
    public void A_recording_that_rolls_while_the_lock_is_off_is_ignored()
    {
        TidalLock lockState = New();
        lockState.RecordingRolled("delay.ts", At("20:57:26:00"));

        // Nothing was armed, so nothing is waiting for it - and nothing goes to air.
        Assert.Equal(TidalLockState.Off, lockState.State);
        Assert.Null(lockState.CueAt);
    }

    [Fact]
    public void Going_to_air_only_follows_a_countdown()
    {
        TidalLock lockState = New();

        lockState.WentToAir();
        Assert.Equal(TidalLockState.Off, lockState.State);

        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.WentToAir();
        Assert.Equal(TidalLockState.Armed, lockState.State);   // armed is not counting down

        lockState.RecordingRolled("delay.ts", At("20:57:26:00"));
        lockState.WentToAir();
        Assert.Equal(TidalLockState.OnAir, lockState.State);
    }

    [Fact]
    public void Stopping_the_recording_re_arms_and_forgets_the_file()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled("delay.ts", At("20:57:26:00"));
        lockState.WentToAir();

        lockState.RecordingStopped();

        // Still armed, so the next recording is picked up - but pointed at nothing, because
        // the file that was being followed is finished.
        Assert.Equal(TidalLockState.Armed, lockState.State);
        Assert.Null(lockState.DelayFile);
        Assert.Null(lockState.CueAt);
    }

    [Fact]
    public void A_failure_is_kept_rather_than_being_re_armed_by_a_stop()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.Fail("no transmitter selected");

        lockState.RecordingStopped();

        Assert.Equal(TidalLockState.Failed, lockState.State);
        Assert.Equal("no transmitter selected", lockState.Detail);
    }

    [Fact]
    public void Disarming_clears_everything()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled("delay.ts", At("20:57:26:00"));

        lockState.Disarm();

        Assert.Equal(TidalLockState.Off, lockState.State);
        Assert.Null(lockState.DelayFile);
        Assert.Null(lockState.RecordingStarted);
    }

    [Theory]
    [InlineData(25, 1500)]
    [InlineData(50, 3000)]
    public void The_delay_converts_to_frames_at_the_running_rate(int rate, long frames)
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));

        Assert.Equal(frames, lockState.DelayFrames(rate));
    }

    [Fact]
    public void Every_change_is_announced_once()
    {
        TidalLock lockState = New();
        int changes = 0;
        lockState.Changed += _ => changes++;

        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled("delay.ts", At("20:57:26:00"));
        lockState.WentToAir();
        lockState.Disarm();

        Assert.Equal(4, changes);
    }
}
