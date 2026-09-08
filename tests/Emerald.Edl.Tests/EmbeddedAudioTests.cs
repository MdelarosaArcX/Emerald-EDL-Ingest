using Emerald.Edl;
using Emerald.Media;
using Xunit;

namespace Emerald.Edl.Tests;

/// <summary>
/// Naming the languages inside a clip, and carrying the choice through to playout.
///
/// A multi-language master is one file with several audio streams. What the operator sees in
/// the Audio Tracks panel comes from the stream's own tags, and what reaches ffmpeg is the
/// stream's ordinal - so these two have to agree about which is which.
/// </summary>
public sealed class EmbeddedAudioTests
{
    [Fact]
    public void A_stream_that_names_itself_is_called_what_it_says()
    {
        var stream = new MediaAudioStream(0, "aac", 2, "eng", "English");

        Assert.Equal("English", stream.Label);
    }

    [Fact]
    public void A_stream_with_no_title_falls_back_to_its_language()
    {
        var stream = new MediaAudioStream(1, "pcm_s16le", 2, "ara", null);

        Assert.Equal("ara", stream.Label);
    }

    [Fact]
    public void An_unlabelled_stream_is_named_by_its_position_counting_from_one()
    {
        // Index is zero-based because ffmpeg's 0:a:N is; the operator counts from one.
        Assert.Equal("Track 3", new MediaAudioStream(2, "aac", 2, null, null).Label);
    }

    [Fact]
    public void Undetermined_is_not_a_language_and_does_not_become_a_name()
    {
        // "und" is what a muxer writes when it was told nothing. It is not a label.
        Assert.Equal("Track 1", new MediaAudioStream(0, "aac", 2, "und", null).Label);
    }

    [Fact]
    public void The_detail_line_says_enough_to_tell_two_streams_apart()
    {
        string detail = new MediaAudioStream(1, "aac", 6, "fra", "French").Detail;

        Assert.Contains("stream 2", detail);
        Assert.Contains("aac", detail);
        Assert.Contains("6ch", detail);
        Assert.Contains("fra", detail);
    }

    [Fact]
    public void A_files_track_count_leads_the_media_summary()
    {
        var info = new MediaInfo(
            Path: @"C:\clips\master.mov",
            Duration: TimeSpan.FromSeconds(60),
            StartTimecode: new Emerald.Core.Timecode(0, 25),
            HasEmbeddedTimecode: false,
            HasAudio: true,
            VideoCodec: "prores",
            Width: 1920,
            Height: 1080,
            AudioStreams: new[]
            {
                new MediaAudioStream(0, "aac", 2, "eng", "English"),
                new MediaAudioStream(1, "aac", 2, "ara", "Arabic"),
            });

        Assert.Contains("2 audio tracks", info.Summary(25));
    }

    [Fact]
    public void A_file_probed_by_an_older_build_still_reports_a_track_list()
    {
        // AudioStreams is optional on the record, so anything constructing MediaInfo without
        // it must still be enumerable rather than throwing on a null.
        var info = new MediaInfo(@"C:\clips\x.mov", TimeSpan.Zero,
            new Emerald.Core.Timecode(0, 25), false, false, "h264", 1920, 1080);

        Assert.Empty(info.Audio);
        Assert.Contains("no audio", info.Summary(25));
    }

    [Fact]
    public void A_bed_of_its_own_takes_whichever_stream_the_file_prefers()
    {
        var track = new AudioTrack("Commentary", new[] { @"C:\audio\comm.wav" });

        Assert.Equal(-1, track.StreamIndex);
    }

    [Fact]
    public void Languages_from_one_master_are_the_same_file_at_different_streams()
    {
        string[] files = { @"C:\clips\master.mov" };

        var english = new AudioTrack("English", files, 0);
        var arabic = new AudioTrack("Arabic", files, 1);

        Assert.Equal(english.Files, arabic.Files);
        Assert.NotEqual(english.StreamIndex, arabic.StreamIndex);
    }
}
