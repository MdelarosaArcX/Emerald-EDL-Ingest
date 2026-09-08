using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// Recording a feed that carries more than one stereo pair.
///
/// SDI has room for eight pairs and the EDL puts a language on each, so a return feed of a
/// four-language message has four pairs on it. Recording only the first was losing three of
/// them. What matters here is that they come back as separate tracks, in wire order, and that
/// a feed with one pair is encoded exactly the way it always was.
/// </summary>
public sealed class RecordingPairsTests
{
    private static readonly CaptureFormat Hd = new("1080p25", 1920, 1080, 25, 0);

    private static List<string> Args(int pairs, IReadOnlyList<CaptureAudioTrack>? extra = null) =>
        RecordingProfile.Default
            .EncoderArguments(Hd, "pipe", @"C:\out", "clip", 48000,
                              singleFile: true, startTimecode: null, extraAudio: extra, embeddedPairs: pairs)
            .ToList();

    /// <summary>The value passed to the flag immediately after <paramref name="flag"/>.</summary>
    private static string? After(List<string> args, string flag)
    {
        int i = args.IndexOf(flag);
        return i >= 0 && i + 1 < args.Count ? args[i + 1] : null;
    }

    [Fact]
    public void One_pair_is_encoded_exactly_as_it_always_was()
    {
        List<string> args = Args(1);

        // Straight through as stereo, no filter graph, no renaming: the ordinary case must
        // not have changed shape at all.
        Assert.Equal("2", After(args, "-ac"));
        Assert.DoesNotContain("-filter_complex", args);
        Assert.Contains("1:a:0", args);
    }

    [Fact]
    public void Four_pairs_open_the_pipe_as_eight_channels()
    {
        Assert.Equal("8", After(Args(4), "-ac"));
    }

    [Fact]
    public void Each_pair_becomes_its_own_stereo_track()
    {
        List<string> args = Args(4);
        string graph = After(args, "-filter_complex")!;

        // Pair n takes channels 2n and 2n+1 off the pipe. Getting this wrong silently swaps
        // languages, which nothing downstream can detect.
        Assert.Contains("pan=stereo|c0=c0|c1=c1", graph);
        Assert.Contains("pan=stereo|c0=c2|c1=c3", graph);
        Assert.Contains("pan=stereo|c0=c4|c1=c5", graph);
        Assert.Contains("pan=stereo|c0=c6|c1=c7", graph);
    }

    [Fact]
    public void Every_pair_is_split_once_per_output_file()
    {
        List<string> args = Args(4);
        string graph = After(args, "-filter_complex")!;

        // A filter output may be mapped only once, and the master and the proxy both want
        // every pair - so each pan is split as many ways as there are output files.
        int outputs = RecordingProfile.Outputs.Count;

        Assert.Equal(4, graph.Split("asplit=" + outputs).Length - 1);

        for (int p = 0; p < 4; p++)
            for (int o = 0; o < outputs; o++)
                Assert.Contains($"[p{p}o{o}]", args);
    }

    [Fact]
    public void The_tracks_are_mapped_in_wire_order_for_every_output()
    {
        List<string> args = Args(4);
        int outputs = RecordingProfile.Outputs.Count;

        for (int o = 0; o < outputs; o++)
        {
            var mapped = new List<int>();

            for (int i = 0; i < args.Count - 1; i++)
            {
                if (args[i] != "-map") continue;

                for (int p = 0; p < 4; p++)
                    if (args[i + 1] == $"[p{p}o{o}]") mapped.Add(p);
            }

            Assert.Equal(new[] { 0, 1, 2, 3 }, mapped);
        }
    }

    [Fact]
    public void Each_track_is_named_for_the_channels_it_came_off()
    {
        List<string> args = Args(3);

        Assert.Contains("title=Original 1 (CH 1-2)", args);
        Assert.Contains("title=Original 2 (CH 3-4)", args);
        Assert.Contains("title=Original 3 (CH 5-6)", args);
    }

    [Fact]
    public void A_single_pair_keeps_the_plain_name_when_beds_make_naming_necessary()
    {
        List<string> args = Args(1, new[] { new CaptureAudioTrack("English", @"C:\a.wav") });

        // With one pair there are no channels to disambiguate, so it is just "Original".
        Assert.Contains($"title={RecordingProfile.OriginalAudioLabel}", args);
        Assert.DoesNotContain("title=Original 1 (CH 1-2)", args);
    }

    [Fact]
    public void Added_beds_come_after_every_embedded_pair()
    {
        List<string> args = Args(3, new[]
        {
            new CaptureAudioTrack("Commentary", @"C:\a.wav"),
            new CaptureAudioTrack("Clean", @"C:\b.wav"),
        });

        // Three pairs occupy tracks 0-2, so the beds are 3 and 4. Numbering them from 1, as
        // the single-pair case does, would overwrite the embedded languages' names.
        Assert.Contains("-metadata:s:a:3", args);
        Assert.Contains("title=Commentary", args);
        Assert.Contains("-metadata:s:a:4", args);
        Assert.Contains("title=Clean", args);
    }

    [Fact]
    public void Only_the_first_track_is_the_default_one()
    {
        List<string> args = Args(4);

        Assert.Equal("default", After(args, "-disposition:a:0"));
        Assert.Equal("0", After(args, "-disposition:a:1"));
        Assert.Equal("0", After(args, "-disposition:a:2"));
        Assert.Equal("0", After(args, "-disposition:a:3"));
    }

    [Fact]
    public void More_pairs_than_SDI_carries_are_clamped_rather_than_attempted()
    {
        Assert.Equal((SdiAudioReader.MaxPairs * 2).ToString(), After(Args(99), "-ac"));
    }

    [Fact]
    public void Shortest_is_only_for_looped_beds_not_for_embedded_pairs()
    {
        // The embedded pairs end when the picture does; they are not looped and nothing has
        // to be truncated against them.
        Assert.DoesNotContain("-shortest", Args(4));
        Assert.Contains("-shortest", Args(4, new[] { new CaptureAudioTrack("Bed", @"C:\a.wav") }));
    }

    [Fact]
    public void Every_input_is_declared_before_the_filter_graph_and_the_maps()
    {
        List<string> args = Args(2, new[] { new CaptureAudioTrack("Bed", @"C:\a.wav") });

        int lastInput = args.LastIndexOf("-i");
        int graph = args.IndexOf("-filter_complex");
        int firstMap = args.IndexOf("-map");

        Assert.True(lastInput < graph, "the filter graph must come after every input");
        Assert.True(graph < firstMap, "the maps must come after the graph that defines them");
    }
}
