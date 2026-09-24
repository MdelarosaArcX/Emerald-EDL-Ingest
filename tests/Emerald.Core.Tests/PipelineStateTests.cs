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

        Assert.Equal(0, changes);
    }
}
