using Emerald.Core;
using Emerald.Edl;
using Xunit;

namespace Emerald.Edl.Tests;

/// <summary>
/// Per-language level, mute and solo.
///
/// Every language is on air at once, on its own SDI pair, so "which one am I listening to" is
/// a question the operator answers downstream — except when checking a message before it goes
/// out, when soloing one is the only way to hear it. These are changes to what is
/// <em>transmitted</em>, unlike the deck's monitor volume, which is why they are set on the
/// engine rather than on a sound device.
/// </summary>
public sealed class TrackLevelTests : IDisposable
{
    private readonly TimecodeService _clock = new();
    private readonly PlayoutService _playout;

    public TrackLevelTests() => _playout = new PlayoutService(_clock, ffmpegPath: null);

    [Fact]
    public void A_track_starts_at_the_files_own_level()
    {
        Assert.Equal(0, _playout.GetTrackGainDb(0));
        Assert.False(_playout.IsTrackMuted(0));
    }

    [Fact]
    public void Each_track_keeps_its_own_level()
    {
        _playout.SetTrackGainDb(0, 3);
        _playout.SetTrackGainDb(1, -6);

        Assert.Equal(3, _playout.GetTrackGainDb(0));
        Assert.Equal(-6, _playout.GetTrackGainDb(1));
        Assert.Equal(0, _playout.GetTrackGainDb(2));
    }

    [Fact]
    public void A_level_beyond_what_is_useful_is_clamped_rather_than_refused()
    {
        _playout.SetTrackGainDb(0, 100);
        Assert.Equal(PlayoutService.MaxGainDb, _playout.GetTrackGainDb(0));

        _playout.SetTrackGainDb(0, -500);
        Assert.Equal(PlayoutService.MinGainDb, _playout.GetTrackGainDb(0));
    }

    [Fact]
    public void A_track_index_off_the_end_is_ignored_rather_than_throwing()
    {
        // The list is edited while a message is on air, so an index can briefly be stale.
        _playout.SetTrackGainDb(PlayoutService.MaxAudioTracks, 6);
        _playout.SetTrackMuted(-1, true);

        Assert.Equal(0, _playout.GetTrackGainDb(PlayoutService.MaxAudioTracks));
        Assert.False(_playout.IsTrackMuted(-1));
    }

    [Fact]
    public void Solo_mutes_every_other_track_and_not_the_one_soloed()
    {
        _playout.SoloTrack(1, trackCount: 4);

        Assert.True(_playout.IsTrackMuted(0));
        Assert.False(_playout.IsTrackMuted(1));
        Assert.True(_playout.IsTrackMuted(2));
        Assert.True(_playout.IsTrackMuted(3));
    }

    [Fact]
    public void Solo_leaves_tracks_that_are_not_on_the_list_alone()
    {
        // Four languages are loaded; the other four engine slots are not tracks at all and
        // muting them would be meaningless state left behind for the next message.
        _playout.SoloTrack(0, trackCount: 4);

        for (int i = 4; i < PlayoutService.MaxAudioTracks; i++)
            Assert.False(_playout.IsTrackMuted(i));
    }

    [Fact]
    public void Clearing_the_solo_brings_every_language_back()
    {
        _playout.SoloTrack(2, trackCount: 4);
        _playout.SoloTrack(-1, trackCount: 4);

        for (int i = 0; i < PlayoutService.MaxAudioTracks; i++)
            Assert.False(_playout.IsTrackMuted(i));
    }

    [Fact]
    public void Soloing_a_second_track_moves_the_solo_rather_than_adding_one()
    {
        _playout.SoloTrack(0, trackCount: 3);
        _playout.SoloTrack(2, trackCount: 3);

        Assert.True(_playout.IsTrackMuted(0));
        Assert.True(_playout.IsTrackMuted(1));
        Assert.False(_playout.IsTrackMuted(2));
    }

    [Fact]
    public void A_meter_reads_silent_before_anything_has_played()
    {
        // Not zero dB, which is full scale: an un-played track must not draw a full bar.
        Assert.Equal(PlayoutService.SilenceDb, _playout.GetTrackPeakDb(0));
    }

    [Fact]
    public void Muting_a_track_does_not_forget_the_level_it_was_set_to()
    {
        _playout.SetTrackGainDb(1, 6);
        _playout.SetTrackMuted(1, true);
        _playout.SetTrackMuted(1, false);

        Assert.Equal(6, _playout.GetTrackGainDb(1));
    }

    public void Dispose()
    {
        _playout.Dispose();
        _clock.Dispose();
    }
}
