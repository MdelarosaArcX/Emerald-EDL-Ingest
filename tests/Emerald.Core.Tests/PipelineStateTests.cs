using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// Whether the plant is recording, as a question rather than a caption.
///
/// The playback deck decides from this whether to offer "no delay" at all, so a flag left
/// set after a recording ended is an operator being offered a delay they cannot inherit —
/// and one left clear during a recording is the option they came for being missing.
/// </summary>
public class PipelineStateTests
{
    private static PipelineState New() => new();

    [Fact]
    public void Nothing_is_recording_to_begin_with()
    {
        Assert.False(New().Recording);
    }

    [Fact]
    public void Clearing_the_capture_stage_also_says_the_recording_has_stopped()
    {
        PipelineState state = New();

        state.Recording = true;
        state.Capture = new StageState("RECORDING", "CLIP from board 0", LogLevel.Ok);

        state.ClearCapture();

        Assert.False(state.Recording);
        Assert.Equal(StageState.Idle, state.Capture);
    }

    [Fact]
    public void A_change_is_announced_once_and_a_repeat_is_not_announced_at_all()
    {
        PipelineState state = New();
        int changes = 0;
        state.Changed += () => changes++;

        state.Recording = true;
        state.Recording = true;

        Assert.Equal(1, changes);

        state.Recording = false;

        Assert.Equal(2, changes);
    }

    /// <summary>
    /// The comment on <see cref="PipelineState"/> is explicit that subscribers are called
    /// outside the lock, because a subscriber that takes a lock of its own while this one is
    /// held is how a UI thread and a capture thread deadlock over a status label. This is that
    /// promise, checked: reading the state from inside the notification must not block.
    /// </summary>
    [Fact]
    public void A_subscriber_can_read_the_state_from_inside_the_notification()
    {
        PipelineState state = New();
        bool? seen = null;

        state.Changed += () => seen = state.Recording;

        state.Recording = true;

        Assert.True(seen);
    }
}

/// <summary>
/// The frame ledger: both sides write their half, the page reads the whole, and a recording
/// ending clears only the recorder's half — the transmitter is still draining the last delay.
/// </summary>
public class FrameLedgerTests
{
    [Fact]
    public void Each_side_writes_its_own_half_without_disturbing_the_other()
    {
        var state = new PipelineState();

        state.SetRecorded(1000, 3, 1);
        state.SetAired(900, 2, 4, 0);

        FrameLedger frames = state.Frames;

        Assert.Equal(1000, frames.Recorded);
        Assert.Equal(3, frames.NotEncoded);
        Assert.Equal(1, frames.NotDelayed);
        Assert.Equal(900, frames.Aired);
        Assert.Equal(2, frames.Repeated);
        Assert.Equal(4, frames.Skipped);
    }

    [Fact]
    public void What_was_lost_is_a_number_on_each_side()
    {
        var frames = new FrameLedger(Recorded: 1000, NotEncoded: 3, NotDelayed: 1,
                                     Aired: 900, Repeated: 2, Skipped: 4, Resyncs: 0);

        Assert.Equal(3, frames.LostToDisk);
        Assert.Equal(5, frames.LostToAir);
    }

    [Fact]
    public void A_recording_ending_clears_the_recorder_side_only()
    {
        var state = new PipelineState();

        state.SetRecorded(1000, 3, 1);
        state.SetAired(900, 2, 4, 0);
        state.ClearCapture();

        FrameLedger frames = state.Frames;

        Assert.Equal(0, frames.Recorded);
        Assert.Equal(0, frames.NotEncoded);
        Assert.Equal(900, frames.Aired);
        Assert.Equal(4, frames.Skipped);
    }

    /// <summary>The counts change twice a second; announcing each would redraw every strip for nothing.</summary>
    [Fact]
    public void Writing_the_ledger_does_not_announce_a_change()
    {
        var state = new PipelineState();
        int changes = 0;
        state.Changed += () => changes++;

        state.SetRecorded(1, 0, 0);
        state.SetAired(1, 0, 0, 0);
        state.SetAiredFrame(1);

        Assert.Equal(0, changes);
    }

    /// <summary>The on-air and capture readouts: frame N of a recording is its start plus N.</summary>
    [Fact]
    public void A_recording_frame_is_its_start_plus_the_frames()
    {
        var start = new Timecode(14 * 3600L * 25, 25);   // 14:00:00:00

        Assert.Equal("14:15:00:00", PipelineState.TimecodeOf(start, 15 * 60 * 25)!.Value.ToString());
        Assert.Equal("14:00:00:00", PipelineState.TimecodeOf(start, 0)!.Value.ToString());
    }

    [Fact]
    public void A_recording_across_midnight_wraps_the_way_the_clock_does()
    {
        var start = new Timecode(24 * 3600L * 25 - 25, 25);   // 23:59:59:00

        Assert.Equal("00:00:01:00", PipelineState.TimecodeOf(start, 50)!.Value.ToString());
    }

    [Fact]
    public void No_start_or_no_frame_is_no_timecode()
    {
        Assert.Null(PipelineState.TimecodeOf(null, 100));
        Assert.Null(PipelineState.TimecodeOf(new Timecode(0, 25), -1));
    }

    /// <summary>
    /// The playback deck's ON AIR and Live Edit's mark are one figure. Live Edit counts at its own
    /// rate; adding the recording's frames to that count is what let the two drift apart, so the
    /// figure is a time of day and each converts it.
    /// </summary>
    [Fact]
    public void On_air_is_a_time_of_day_whatever_rate_reads_it()
    {
        var state = new PipelineState();

        state.RecordingStart = new Timecode(14 * 3600L * 50, 50);   // 14:00:00:00 at 50
        state.SetAiredFrame(15 * 60 * 50);                         // fifteen minutes in

        Assert.Equal(TimeSpan.FromHours(14) + TimeSpan.FromMinutes(15), state.OnAirTimeOfDay);

        // Read at 25, the way a ruler at another rate would: still 14:15:00:00, not 14:30.
        Assert.Equal(TimelineZoom.FrameOfDay(TimeSpan.FromHours(14.25), 25),
                     TimelineZoom.FrameOfDay(state.OnAirTimeOfDay!.Value, 25));
    }

    /// <summary>
    /// A recording rolled at 23:30 is on air at 00:15 the next day. The time of day wraps; the
    /// dated figure does not, and that is what keeps Live Edit's mark after 23:59 on a ruler the
    /// recording has run on past midnight.
    /// </summary>
    [Fact]
    public void On_air_past_midnight_keeps_its_date()
    {
        var state = new PipelineState();
        var rolled = new DateTime(2026, 10, 17, 23, 30, 0);

        state.RecordingStartedAt = rolled;
        state.RecordingStart = new Timecode(TimelineZoom.FrameOfDay(rolled, 25), 25);
        state.SetAiredFrame(45 * 60 * 25);   // forty-five minutes in

        Assert.Equal(new DateTime(2026, 10, 18, 0, 15, 0), state.OnAirAt);
        Assert.Equal(TimeSpan.FromMinutes(15), state.OnAirTimeOfDay);
    }

    [Fact]
    public void Nothing_on_air_is_no_time_of_day()
    {
        var state = new PipelineState();
        state.RecordingStart = new Timecode(0, 25);

        Assert.Null(state.OnAirTimeOfDay);
    }

    /// <summary>The per-frame position stands on its own, without the once-a-second counts.</summary>
    [Fact]
    public void The_aired_frame_moves_every_frame()
    {
        var state = new PipelineState();

        state.SetAiredFrame(22_500);
        Assert.Equal(22_500, state.AiredFrame);

        state.ClearAired();
        Assert.Null(state.AiredFrame);
    }
}
