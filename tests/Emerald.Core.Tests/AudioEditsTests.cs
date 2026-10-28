using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// Levels set on the recording's sound from Live Edit's audio lane. The transmitter reads these
/// every frame, so the cases are the ones that would put the wrong level on air: overlaps,
/// edges, and silence.
/// </summary>
public sealed class AudioEditsTests
{
    private static readonly IReadOnlyList<AudioEdit> None = Array.Empty<AudioEdit>();

    [Fact]
    public void A_frame_with_nothing_set_is_at_its_own_level()
    {
        Assert.Equal(0, AudioEdits.GainDbAt(100, None));
        Assert.Equal(1, AudioEdits.Linear(0));
    }

    [Fact]
    public void A_level_holds_from_its_first_frame_up_to_but_not_including_its_end()
    {
        IReadOnlyList<AudioEdit> edits = AudioEdits.Set(None, 100, 200, -6);

        Assert.Equal(0, AudioEdits.GainDbAt(99, edits));
        Assert.Equal(-6, AudioEdits.GainDbAt(100, edits));
        Assert.Equal(-6, AudioEdits.GainDbAt(199, edits));
        Assert.Equal(0, AudioEdits.GainDbAt(200, edits));
    }

    [Fact]
    public void Deleted_sound_is_silence()
    {
        IReadOnlyList<AudioEdit> edits = AudioEdits.Set(None, 100, 200, double.NegativeInfinity);

        Assert.True(edits[0].Muted);
        Assert.Equal(0, AudioEdits.Linear(AudioEdits.GainDbAt(150, edits)));
    }

    [Fact]
    public void A_level_set_inside_another_splits_it_and_keeps_both_ends()
    {
        IReadOnlyList<AudioEdit> edits = AudioEdits.Set(AudioEdits.Set(None, 0, 300, -6), 100, 200, double.NegativeInfinity);

        Assert.Equal(3, edits.Count);
        Assert.Equal(-6, AudioEdits.GainDbAt(50, edits));
        Assert.True(double.IsNegativeInfinity(AudioEdits.GainDbAt(150, edits)));
        Assert.Equal(-6, AudioEdits.GainDbAt(250, edits));
    }

    [Fact]
    public void Back_to_zero_leaves_no_edit_and_neighbours_at_one_level_join()
    {
        IReadOnlyList<AudioEdit> edits = AudioEdits.Set(None, 0, 100, 3);
        edits = AudioEdits.Set(edits, 100, 200, 3);

        Assert.Single(edits);
        Assert.Equal(200, edits[0].Frames);

        Assert.Empty(AudioEdits.Set(edits, 0, 200, 0));
    }

    [Fact]
    public void Levels_are_held_to_the_range_but_silence_is_kept()
    {
        Assert.Equal(AudioEdits.MaxDb, AudioEdits.Clamp(40));
        Assert.Equal(AudioEdits.MinDb, AudioEdits.Clamp(-90));
        Assert.True(double.IsNegativeInfinity(AudioEdits.Clamp(double.NegativeInfinity)));
    }

    [Fact]
    public void A_piece_runs_between_the_cuts_either_side_or_to_the_recording_edge()
    {
        long[] cuts = { 100, 300 };

        Assert.Equal((100L, 300L), AudioEdits.PieceAt(150, cuts, 0, 500));
        Assert.Equal((0L, 100L), AudioEdits.PieceAt(50, cuts, 0, 500));
        Assert.Equal((300L, 500L), AudioEdits.PieceAt(300, cuts, 0, 500));
    }

    [Fact]
    public void The_first_difference_is_where_two_sets_of_levels_part()
    {
        IReadOnlyList<AudioEdit> a = AudioEdits.Set(None, 100, 200, -6);
        IReadOnlyList<AudioEdit> b = AudioEdits.Set(a, 150, 200, double.NegativeInfinity);

        Assert.Null(AudioEdits.FirstDifference(a, a));
        Assert.Equal(150, AudioEdits.FirstDifference(a, b));
        Assert.Equal(100, AudioEdits.FirstDifference(None, a));
    }
}
