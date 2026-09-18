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
