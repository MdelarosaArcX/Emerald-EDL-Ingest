using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The handshake between the two decks.
///
/// It is only a few fields, but it is what decides when a recording reaches the transmitter
/// and — just as importantly — when it stops. The sequencing is worth pinning down rather
/// than trusting to two windows agreeing at run time.
/// </summary>
public class TidalLockTests
{
    /// <summary>
    /// A delay line that is not one. That this is a dozen lines is the point: the interface
    /// exists so the coordination can be reasoned about without a six-gigabyte file.
    /// </summary>
    private sealed class FakeDelayLine : IDelayLine
    {
        public int Width => 1920;
        public int Height => 1080;
        public int FrameRate { get; init; } = 25;
        public long FramesWritten { get; set; }
        public long TargetFrames { get; init; } = 1500;
        public bool IsSealed { get; set; }
        public long SealedAt { get; set; }
        public string Path => @"C:\delay\fake.ring";
    }

    private static Timecode At(string text, int rate = 25)
    {
        Assert.True(Timecode.TryParse(text, rate, out Timecode value, out string? error), error);
        return value;
    }

    /// <summary>A fresh lock, since the real one is a process-wide singleton.</summary>
    private static TidalLock New() => new();

    // ------------------------------------------------------------------ arming

    [Fact]
    public void A_new_lock_is_off_and_knows_nothing()
    {
        TidalLock lockState = New();

        Assert.Equal(TidalLockState.Off, lockState.State);
        Assert.Null(lockState.Line);
        Assert.Null(lockState.CueAt);
    }

    [Fact]
    public void Arming_waits_for_the_recorder_rather_than_starting_anything()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));

        Assert.Equal(TidalLockState.Armed, lockState.State);
        Assert.Null(lockState.Line);
    }

    [Fact]
    public void A_recording_that_rolls_while_the_lock_is_off_is_ignored()
    {
        TidalLock lockState = New();
        lockState.RecordingRolled(new FakeDelayLine(), At("20:57:26:00"));

        // Nothing was armed, so nothing is waiting for it - and nothing goes to air.
        Assert.Equal(TidalLockState.Off, lockState.State);
        Assert.Null(lockState.Line);
    }

    // ------------------------------------------------------------------ the countdown

    [Fact]
    public void The_countdown_is_measured_in_frames_buffered_not_against_the_clock()
    {
        var line = new FakeDelayLine { TargetFrames = 1500 };
        TidalLock lockState = New();

        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled(line, At("20:57:26:00"));

        Assert.Equal(1500, lockState.FramesToAir);
        Assert.Equal(0, lockState.FillFraction);

        line.FramesWritten = 750;
        Assert.Equal(750, lockState.FramesToAir);
        Assert.Equal(0.5, lockState.FillFraction);
        Assert.Equal(TimeSpan.FromSeconds(30), lockState.TimeToAir);

        line.FramesWritten = 1500;
        Assert.Equal(0, lockState.FramesToAir);
        Assert.Equal(1, lockState.FillFraction);
    }

    [Fact]
    public void An_overfilled_line_does_not_report_a_negative_countdown()
    {
        var line = new FakeDelayLine { TargetFrames = 1500, FramesWritten = 9000 };
        TidalLock lockState = New();

        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled(line, At("20:57:26:00"));

        Assert.Equal(0, lockState.FramesToAir);
        Assert.Equal(1, lockState.FillFraction);
    }

    [Fact]
    public void The_cue_timecode_is_reported_when_there_is_a_clock_to_read()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled(new FakeDelayLine(), At("20:57:26:00"));

        Assert.Equal("20:58:26:00", lockState.CueAt!.Value.ToString());
    }

    [Fact]
    public void Without_a_station_clock_it_still_runs_on_the_frames()
    {
        // A timecode outage must not stop the delay: the receiver's own cadence is the truth
        // about how much of it exists.
        var line = new FakeDelayLine { TargetFrames = 250 };
        TidalLock lockState = New();

        lockState.Arm(TimeSpan.FromSeconds(10));
        lockState.RecordingRolled(line, startedAt: null);

        Assert.Equal(TidalLockState.CountingDown, lockState.State);
        Assert.Null(lockState.CueAt);
        Assert.Equal(250, lockState.FramesToAir);
    }

    [Fact]
    public void A_cue_past_midnight_wraps_rather_than_running_off_the_end()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled(new FakeDelayLine(), At("23:59:30:00"));

        Assert.Equal("00:00:30:00", lockState.CueAt!.Value.ToString());
    }

    // ------------------------------------------------------------------ going to air

    [Fact]
    public void Going_to_air_only_follows_a_countdown()
    {
        TidalLock lockState = New();

        lockState.WentToAir();
        Assert.Equal(TidalLockState.Off, lockState.State);

        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.WentToAir();
        Assert.Equal(TidalLockState.Armed, lockState.State);   // armed is not counting down

        lockState.RecordingRolled(new FakeDelayLine(), At("20:57:26:00"));
        lockState.WentToAir();
        Assert.Equal(TidalLockState.OnAir, lockState.State);
    }

    // ------------------------------------------------------------------ draining

    /// <summary>
    /// The behaviour that would be easiest to get wrong, and would cost the last minute of
    /// every programme if it were.
    /// </summary>
    [Fact]
    public void Stopping_the_recording_while_on_air_drains_rather_than_stopping()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled(new FakeDelayLine(), At("20:57:26:00"));
        lockState.WentToAir();

        lockState.RecordingStopped();

        // A whole minute of programme is still in the line. It has not been to air yet.
        Assert.Equal(TidalLockState.Draining, lockState.State);
        Assert.NotNull(lockState.Line);
    }

    [Fact]
    public void The_drain_ends_by_re_arming_for_the_next_recording()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled(new FakeDelayLine(), At("20:57:26:00"));
        lockState.WentToAir();
        lockState.RecordingStopped();

        lockState.DrainComplete();

        Assert.Equal(TidalLockState.Armed, lockState.State);
        Assert.Null(lockState.Line);
    }

    [Fact]
    public void A_recording_stopped_before_anything_aired_ends_straight_away()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));
        lockState.RecordingRolled(new FakeDelayLine(), At("20:57:26:00"));

        lockState.RecordingStopped();

        // Nothing reached the transmitter, so there is nothing worth draining.
        Assert.Equal(TidalLockState.Armed, lockState.State);
        Assert.Null(lockState.Line);
    }

    [Fact]
    public void A_drain_can_only_complete_from_a_drain()
    {
        TidalLock lockState = New();
        lockState.Arm(TimeSpan.FromMinutes(1));

        lockState.DrainComplete();
        Assert.Equal(TidalLockState.Armed, lockState.State);
    }

    // ------------------------------------------------------------------ failure

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
        lockState.RecordingRolled(new FakeDelayLine(), At("20:57:26:00"));

        lockState.Disarm();

        Assert.Equal(TidalLockState.Off, lockState.State);
        Assert.Null(lockState.Line);
        Assert.Null(lockState.RecordingStarted);
    }

    // ------------------------------------------------------------------ housekeeping

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
        lockState.RecordingRolled(new FakeDelayLine(), At("20:57:26:00"));
        lockState.WentToAir();
        lockState.RecordingStopped();
        lockState.DrainComplete();
        lockState.Disarm();

        Assert.Equal(6, changes);
    }
}
