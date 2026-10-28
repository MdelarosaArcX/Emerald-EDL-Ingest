using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// When a cue on Live Edit's timeline takes over the air.
///
/// The transmitter asks this every frame with the recording frame it is about to send. Getting
/// it wrong is media to air at the wrong moment, twice, or not at all - so the cases are the
/// edges: exactly on the frame, a frame late, long after, and already played.
/// </summary>
public sealed class LiveCuesTests
{
    private static readonly IReadOnlySet<string> NoneDone = new HashSet<string>();

    private static LiveCue Insert(string id, long at, long frames = 4500) =>
        new(id, IngestMode.Insert, "insert.mov", at, frames, frames);

    private static LiveCue Overlay(string id, long at, long frames = 500) =>
        new(id, IngestMode.Overlay, "overlay.mov", at, frames, frames);

    private static LiveCue Cut(string id, long at, long covers) =>
        new(id, IngestMode.Cut, null, at, 0, covers);

    [Fact]
    public void Nothing_fires_before_its_frame()
    {
        Assert.Null(LiveCues.Due(999, new[] { Insert("a", 1000) }, NoneDone));
    }

    [Fact]
    public void An_insert_fires_on_its_frame()
    {
        Assert.Equal("a", LiveCues.Due(1000, new[] { Insert("a", 1000) }, NoneDone)?.Id);
    }

    /// <summary>A frame skipped to hold the delay must not lose the insert.</summary>
    [Fact]
    public void An_insert_found_a_frame_or_two_late_still_fires()
    {
        Assert.Equal("a", LiveCues.Due(1002, new[] { Insert("a", 1000) }, NoneDone)?.Id);
    }

    /// <summary>
    /// Long past, it belongs to material that has already gone out. Played now, it would go in
    /// the wrong place - a cue added behind the transmitter must not suddenly air.
    /// </summary>
    [Fact]
    public void An_insert_long_behind_the_transmitter_does_not_fire()
    {
        Assert.Null(LiveCues.Due(1000 + LiveCues.InsertGrace + 1, new[] { Insert("a", 1000) }, NoneDone));
    }

    [Fact]
    public void A_cue_already_played_never_fires_again()
    {
        var done = new HashSet<string> { "a" };

        Assert.Null(LiveCues.Due(1000, new[] { Insert("a", 1000) }, done));
    }

    /// <summary>An overlay reached part-way plays what is left of it rather than nothing.</summary>
    [Fact]
    public void An_overlay_fires_anywhere_inside_what_it_covers()
    {
        var cues = new[] { Overlay("o", 1000, 500) };

        Assert.Equal("o", LiveCues.Due(1250, cues, NoneDone)?.Id);
        Assert.Null(LiveCues.Due(1500, cues, NoneDone));
    }

    [Fact]
    public void A_cut_fires_inside_the_range_it_removes()
    {
        var cues = new[] { Cut("c", 1000, 300) };

        Assert.Equal("c", LiveCues.Due(1000, cues, NoneDone)?.Id);
        Assert.Null(LiveCues.Due(1300, cues, NoneDone));
    }

    [Fact]
    public void The_earliest_due_cue_wins()
    {
        var cues = new[] { Overlay("late", 1100, 500), Overlay("early", 1000, 500) };

        Assert.Equal("early", LiveCues.Due(1200, cues, NoneDone)?.Id);
    }

    /// <summary>The delay counter: two minutes, a three-minute insert, a thirty-second cut.</summary>
    [Fact]
    public void The_delay_grows_with_inserts_and_shrinks_with_cuts()
    {
        const int rate = 25;
        long baseDelay = 120 * rate;

        var cues = new[] { Insert("i", 1000, 180 * rate), Cut("c", 9000, 30 * rate), Overlay("o", 20_000) };

        Assert.Equal((120 + 180 - 30) * rate, LiveCues.DelayWith(baseDelay, cues));
    }

    [Fact]
    public void Media_is_opened_a_little_ahead_and_not_for_cuts()
    {
        var cues = new[] { Insert("soon", 1100), Insert("later", 5000), Cut("c", 1050, 100) };

        string[] ready = LiveCues.ToPrepare(1000, 150, cues, NoneDone).Select(c => c.Id).ToArray();

        Assert.Equal(new[] { "soon" }, ready);
    }
}
