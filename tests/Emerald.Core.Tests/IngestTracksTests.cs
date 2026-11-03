using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// An ingest item going out as video only or audio only. Which tracks go out changes what is
/// seen and heard, never where the item sits or how long it runs - an audio-only insert grows
/// the delay exactly as one with its picture does.
/// </summary>
public sealed class IngestTracksTests
{
    private static IngestPlan Plan(IngestMode mode, IngestTracks tracks, long ingest = 250,
                                   long? markIn = null, long? markOut = null)
    {
        Assert.True(IngestPlacement.TryPlan(mode, ingest, 1000, markIn, markOut,
                                            out IngestPlan plan, out string? problem, tracks),
                    problem);
        return plan;
    }

    [Fact]
    public void An_item_goes_out_with_both_tracks_unless_told_otherwise()
    {
        Assert.True(IngestPlacement.TryPlan(IngestMode.Overlay, 250, 1000, null, null, out IngestPlan plan, out _));

        Assert.Equal(IngestTracks.Both, plan.Tracks);
        Assert.True(plan.HasVideo);
        Assert.True(plan.HasAudio);
    }

    [Fact]
    public void An_audio_only_insert_grows_the_delay_by_its_whole_length()
    {
        IngestPlan both = Plan(IngestMode.Insert, IngestTracks.Both);
        IngestPlan audio = Plan(IngestMode.Insert, IngestTracks.AudioOnly);

        Assert.Equal(both with { Tracks = IngestTracks.AudioOnly }, audio);
        Assert.Equal(250, audio.ShiftsAfterBy);
        Assert.False(audio.HasVideo);
        Assert.True(audio.HasAudio);

        var cue = new LiveCue("a", audio.Mode, "x.mov", 1000, audio.Frames, audio.Covers, Tracks: audio.Tracks);
        Assert.Equal(250, cue.DelayChange);
        Assert.False(cue.HasVideo);
    }

    [Fact]
    public void Video_only_keeps_the_span_and_drops_the_sound()
    {
        IngestPlan plan = Plan(IngestMode.Replace, IngestTracks.VideoOnly, markIn: 1000, markOut: 1100);

        Assert.Equal((1000L, 100L, 100L), (plan.StartFrame, plan.Frames, plan.Covers));
        Assert.True(plan.HasVideo);
        Assert.False(plan.HasAudio);
    }

    [Fact]
    public void A_scene_cut_has_neither_track()
    {
        Assert.True(IngestPlacement.TryPlanCut(100, 200, out IngestPlan cut, out _));

        Assert.False(cut.HasVideo);
        Assert.False(cut.HasAudio);
    }

    [Fact]
    public void The_description_says_what_is_seen_and_heard_in_place_of_the_missing_track()
    {
        Assert.StartsWith("audio only, over black",
            IngestPlacement.Describe(Plan(IngestMode.Insert, IngestTracks.AudioOnly), 25));
        Assert.StartsWith("audio only, over the recording's picture",
            IngestPlacement.Describe(Plan(IngestMode.Overlay, IngestTracks.AudioOnly), 25));
        Assert.StartsWith("video only, silent",
            IngestPlacement.Describe(Plan(IngestMode.Insert, IngestTracks.VideoOnly), 25));
        Assert.StartsWith("video only, the recording's sound carries on",
            IngestPlacement.Describe(Plan(IngestMode.Overlay, IngestTracks.VideoOnly), 25));
    }
}
