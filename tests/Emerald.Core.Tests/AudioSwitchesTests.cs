using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>Which of the recording's sound tracks plays, and from where.</summary>
public class AudioSwitchesTests
{
    private static readonly IReadOnlyList<AudioSwitch> None = Array.Empty<AudioSwitch>();

    [Fact]
    public void Nothing_chosen_plays_the_first_track()
    {
        Assert.Equal(0, AudioSwitches.TrackAt(0, None));
        Assert.Equal(0, AudioSwitches.TrackAt(long.MaxValue, None));
    }

    [Fact]
    public void A_track_plays_from_its_frame_until_the_next_change()
    {
        IReadOnlyList<AudioSwitch> set = AudioSwitches.Set(AudioSwitches.Set(None, 100, 2), 200, 1);

        Assert.Equal(0, AudioSwitches.TrackAt(99, set));
        Assert.Equal(2, AudioSwitches.TrackAt(100, set));
        Assert.Equal(2, AudioSwitches.TrackAt(199, set));
        Assert.Equal(1, AudioSwitches.TrackAt(200, set));
        Assert.Equal(1, AudioSwitches.TrackAt(1_000_000, set));
    }

    [Fact]
    public void One_placed_before_a_change_already_there_leaves_it_there()
    {
        IReadOnlyList<AudioSwitch> set = AudioSwitches.Set(AudioSwitches.Set(None, 200, 1), 100, 3);

        Assert.Equal(new[] { new AudioSwitch(100, 3), new AudioSwitch(200, 1) }, set);
    }

    [Fact]
    public void One_that_changes_nothing_is_not_kept_and_the_same_frame_is_replaced()
    {
        Assert.Empty(AudioSwitches.Set(None, 100, 0));

        IReadOnlyList<AudioSwitch> set = AudioSwitches.Set(None, 100, 2);
        Assert.Equal(set, AudioSwitches.Set(set, 150, 2));
        Assert.Equal(new[] { new AudioSwitch(100, 3) }, AudioSwitches.Set(set, 100, 3));
        Assert.Empty(AudioSwitches.Set(set, 100, 0));
    }

    [Fact]
    public void Taking_one_off_lets_the_track_before_it_play_on()
    {
        IReadOnlyList<AudioSwitch> set = AudioSwitches.Set(AudioSwitches.Set(None, 100, 2), 200, 1);

        Assert.Equal(1, AudioSwitches.TrackAt(250, AudioSwitches.Remove(set, 100)));
        Assert.Equal(2, AudioSwitches.TrackAt(250, AudioSwitches.Remove(set, 200)));
    }

    [Fact]
    public void What_has_aired_stays_and_what_is_wanted_holds_from_there_on()
    {
        IReadOnlyList<AudioSwitch> air = AudioSwitches.Set(None, 100, 1);

        // Wanted: track 2 since before air was told, and track 3 later on.
        IReadOnlyList<AudioSwitch> wanted = AudioSwitches.Set(AudioSwitches.Set(None, 50, 2), 900, 3);
        IReadOnlyList<AudioSwitch> sent = AudioSwitches.From(500, air, wanted);

        Assert.Equal(0, AudioSwitches.TrackAt(60, sent));
        Assert.Equal(1, AudioSwitches.TrackAt(499, sent));
        Assert.Equal(2, AudioSwitches.TrackAt(500, sent));
        Assert.Equal(3, AudioSwitches.TrackAt(900, sent));
        Assert.Equal(500, AudioSwitches.FirstDifference(air, sent));

        // The same thing wanted as air has: nothing to change.
        Assert.Null(AudioSwitches.FirstDifference(air, AudioSwitches.From(500, air, air)));
    }

    [Fact]
    public void Works_before_the_recording_rolled_as_well()
    {
        // Frames of the recording are negative for a change placed before it rolled.
        Assert.Equal(1, AudioSwitches.TrackAt(0, AudioSwitches.Set(None, -500, 1)));
    }

    [Fact]
    public void The_first_difference_is_where_what_plays_first_differs()
    {
        IReadOnlyList<AudioSwitch> a = AudioSwitches.Set(None, 100, 2);

        Assert.Null(AudioSwitches.FirstDifference(a, a.ToList()));
        Assert.Equal(100, AudioSwitches.FirstDifference(None, a));
        Assert.Equal(300, AudioSwitches.FirstDifference(a, AudioSwitches.Set(a, 300, 1)));
        Assert.Equal(100, AudioSwitches.FirstDifference(a, AudioSwitches.Set(None, 120, 2)));
    }
}
