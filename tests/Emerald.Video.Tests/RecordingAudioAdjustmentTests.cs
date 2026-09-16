using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// Gain and sync on an added track.
///
/// Both are applied by the encoder rather than anywhere the operator can hear, so what the
/// filtergraph says is the only thing that decides what ends up in the file. A graph ffmpeg
/// refuses takes the whole recording down with it, and a graph it accepts but that names the
/// wrong stream silently records the wrong sound — neither is visible until someone opens a
/// segment, which is why the string is checked here.
/// </summary>
public class RecordingAudioAdjustmentTests
{
    private static readonly CaptureFormat Format = CaptureFormat.FromStandard(0, 25);   // 1080p25

    private static List<string> Arguments(int pairs, params CaptureAudioTrack[] extra) =>
        RecordingProfile.Default
            .EncoderArguments(Format, "pipe", @"C:\ingest", "CLIP", 48000,
                              singleFile: true, startTimecode: "01:00:00:00",
                              extraAudio: extra.Length == 0 ? null : extra,
                              embeddedPairs: pairs)
            .ToList();

    private static string Graph(List<string> args)
    {
        int at = args.IndexOf("-filter_complex");
        return at < 0 ? "" : args[at + 1];
    }

    private static List<string> Maps(List<string> args)
    {
        var maps = new List<string>();
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i] == "-map") maps.Add(args[i + 1]);

        return maps;
    }

    [Fact]
    public void A_track_nobody_has_touched_is_still_mapped_straight_from_its_input()
    {
        List<string> args = Arguments(1, new CaptureAudioTrack("English", @"C:\a\en.wav"));

        Assert.Equal("", Graph(args));
        Assert.Equal(new[] { "0:v:0", "1:a:0", "2:a:0", "0:v:0", "1:a:0", "2:a:0" }, Maps(args));
    }

    [Fact]
    public void A_lift_becomes_a_volume_filter_in_decibels()
    {
        List<string> args = Arguments(1, new CaptureAudioTrack("English", @"C:\a\en.wav", GainDb: 3));

        Assert.Equal("[2:a]volume=3dB,asplit=2[b0o0][b0o1]", Graph(args));
    }

    [Fact]
    public void A_cut_keeps_its_sign()
    {
        List<string> args = Arguments(1, new CaptureAudioTrack("English", @"C:\a\en.wav", GainDb: -6.5));

        Assert.Contains("volume=-6.5dB", Graph(args));
    }

    [Fact]
    public void Holding_the_sound_back_delays_it()
    {
        List<string> args = Arguments(1, new CaptureAudioTrack("English", @"C:\a\en.wav", OffsetMs: 120));

        Assert.Equal("[2:a]adelay=120:all=1,asplit=2[b0o0][b0o1]", Graph(args));
    }

    /// <summary>
    /// The picture is already going to air through the delay line while this is being
    /// adjusted, so pulling the sound earlier can only mean taking it off the front of the
    /// bed. Trimming without resetting the timestamps would leave the gap it was meant to
    /// close still there.
    /// </summary>
    [Fact]
    public void Pulling_the_sound_earlier_trims_the_front_of_the_bed_and_restamps_it()
    {
        List<string> args = Arguments(1, new CaptureAudioTrack("English", @"C:\a\en.wav", OffsetMs: -250));

        Assert.Equal("[2:a]atrim=start=0.25,asetpts=N/SR/TB,asplit=2[b0o0][b0o1]", Graph(args));
    }

    [Fact]
    public void An_adjusted_track_comes_out_of_the_graph_once_for_each_output_file()
    {
        List<string> args = Arguments(1, new CaptureAudioTrack("English", @"C:\a\en.wav", GainDb: 2));

        // A filter output may be mapped only once, so the proxy and the master take different
        // labels off the same asplit.
        Assert.Equal(new[] { "0:v:0", "1:a:0", "[b0o0]", "0:v:0", "1:a:0", "[b0o1]" }, Maps(args));
    }

    [Fact]
    public void Adjusted_and_untouched_tracks_sit_side_by_side_in_one_recording()
    {
        List<string> args = Arguments(1,
            new CaptureAudioTrack("English", @"C:\a\en.wav"),
            new CaptureAudioTrack("Arabic", @"C:\a\ar.wav", GainDb: -3, OffsetMs: 40));

        Assert.Equal("[3:a]adelay=40:all=1,volume=-3dB,asplit=2[b1o0][b1o1]", Graph(args));
        Assert.Equal(new[] { "0:v:0", "1:a:0", "2:a:0", "[b1o0]", "0:v:0", "1:a:0", "2:a:0", "[b1o1]" },
                     Maps(args));
    }

    /// <summary>
    /// The pairs and the beds have to share one graph: ffmpeg takes a single
    /// <c>-filter_complex</c>, and a second one replaces the first rather than adding to it.
    /// A four-language feed with an adjusted bed would otherwise lose either the languages or
    /// the bed, depending on which was passed last.
    /// </summary>
    [Fact]
    public void Splitting_the_pairs_and_adjusting_a_bed_happen_in_the_same_graph()
    {
        List<string> args = Arguments(2, new CaptureAudioTrack("English", @"C:\a\en.wav", GainDb: 4));

        Assert.Single(args.Where(a => a == "-filter_complex"));

        string graph = Graph(args);
        Assert.Contains("pan=stereo|c0=c0|c1=c1", graph);
        Assert.Contains("pan=stereo|c0=c2|c1=c3", graph);
        Assert.Contains("[2:a]volume=4dB", graph);

        Assert.Equal(new[] { "0:v:0", "[p0o0]", "[p1o0]", "[b0o0]", "0:v:0", "[p0o1]", "[p1o1]", "[b0o1]" },
                     Maps(args));
    }

    [Fact]
    public void Gain_and_sync_are_held_to_what_the_deck_offers()
    {
        Assert.Contains("volume=12dB",
                        Graph(Arguments(1, new CaptureAudioTrack("x", @"C:\a\x.wav", GainDb: 400))));

        Assert.Contains("adelay=500:all=1",
                        Graph(Arguments(1, new CaptureAudioTrack("x", @"C:\a\x.wav", OffsetMs: 9000))));
    }

    /// <summary>
    /// The beds are looped and never end, so something has to decide the length of the file.
    /// Without this a segmented recording would keep writing after the receiver stopped.
    /// </summary>
    [Fact]
    public void The_picture_still_decides_the_length_when_a_track_is_adjusted()
    {
        Assert.Contains("-shortest",
                        Arguments(1, new CaptureAudioTrack("x", @"C:\a\x.wav", GainDb: 2)));
    }
}
