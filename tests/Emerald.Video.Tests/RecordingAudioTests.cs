using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// The encoder command when a recording carries more than the receiver's own sound.
///
/// An ingest that came back missing a language, or with the original replaced by one, is not
/// something anybody notices until they open the file — so the argument list is checked here
/// rather than trusted.
/// </summary>
public class RecordingAudioTests
{
    private static readonly CaptureFormat Format = CaptureFormat.FromStandard(0, 25);   // 1080p25

    private static List<string> Arguments(params CaptureAudioTrack[] extra) =>
        RecordingProfile.Default
            .EncoderArguments(Format, "pipe", @"C:\ingest", "CLIP", 48000,
                              singleFile: true, startTimecode: "01:00:00:00",
                              extraAudio: extra.Length == 0 ? null : extra)
            .ToList();

    private static CaptureAudioTrack Track(string label, string path) => new(label, path);

    /// <summary>Every "-map" argument, in order, as the value that follows it.</summary>
    private static List<string> Maps(List<string> args)
    {
        var maps = new List<string>();
        for (int i = 0; i < args.Count - 1; i++)
            if (args[i] == "-map") maps.Add(args[i + 1]);

        return maps;
    }

    [Fact]
    public void With_no_extra_tracks_nothing_about_the_command_changes()
    {
        List<string> args = Arguments();

        // Two outputs, each mapping the video and the receiver's audio, and nothing else.
        Assert.Equal(new[] { "0:v:0", "1:a:0", "0:v:0", "1:a:0" }, Maps(args));
        Assert.DoesNotContain("-stream_loop", args);
        Assert.DoesNotContain("-shortest", args);
        Assert.DoesNotContain("-metadata:s:a:0", args);
    }

    [Fact]
    public void The_receivers_own_audio_is_always_the_first_track()
    {
        List<string> args = Arguments(Track("English", @"C:\audio\en.wav"));

        // Per output: video, then the receiver's audio, then the bed. The original is never
        // displaced by a selection - that is the whole point of the feature.
        Assert.Equal(new[] { "0:v:0", "1:a:0", "2:a:0", "0:v:0", "1:a:0", "2:a:0" }, Maps(args));
    }

    [Fact]
    public void Every_selected_track_becomes_its_own_input_and_its_own_stream()
    {
        List<string> args = Arguments(
            Track("English", @"C:\audio\en.wav"),
            Track("Arabic", @"C:\audio\ar.wav"),
            Track("Clean", @"C:\audio\clean.wav"));

        Assert.Contains(@"C:\audio\en.wav", args);
        Assert.Contains(@"C:\audio\ar.wav", args);
        Assert.Contains(@"C:\audio\clean.wav", args);

        // Four audio streams per output: the original plus three.
        Assert.Equal(
            new[] { "0:v:0", "1:a:0", "2:a:0", "3:a:0", "4:a:0",
                    "0:v:0", "1:a:0", "2:a:0", "3:a:0", "4:a:0" },
            Maps(args));
    }

    [Fact]
    public void Each_track_is_named_so_it_can_be_told_apart_in_a_player()
    {
        List<string> args = Arguments(
            Track("English", @"C:\audio\en.wav"),
            Track("Arabic", @"C:\audio\ar.wav"));

        Assert.Contains($"title={RecordingProfile.OriginalAudioLabel}", args);
        Assert.Contains("title=English", args);
        Assert.Contains("title=Arabic", args);

        Assert.Contains("-metadata:s:a:0", args);
        Assert.Contains("-metadata:s:a:1", args);
        Assert.Contains("-metadata:s:a:2", args);
    }

    [Fact]
    public void The_original_is_the_track_that_plays_by_default()
    {
        List<string> args = Arguments(Track("English", @"C:\audio\en.wav"));

        int original = args.IndexOf("-disposition:a:0");
        Assert.True(original >= 0);
        Assert.Equal("default", args[original + 1]);

        int bed = args.IndexOf("-disposition:a:1");
        Assert.True(bed >= 0);
        Assert.Equal("0", args[bed + 1]);
    }

    /// <summary>
    /// The pairing that keeps the file the right length: the beds never end, so the picture
    /// is what "shortest" resolves to.
    /// </summary>
    [Fact]
    public void Beds_are_looped_and_the_picture_decides_the_length()
    {
        List<string> args = Arguments(
            Track("English", @"C:\audio\en.wav"),
            Track("Arabic", @"C:\audio\ar.wav"));

        // One loop per input, and it precedes the -i it belongs to.
        Assert.Equal(2, args.Count(a => a == "-stream_loop"));

        int loop = args.IndexOf("-stream_loop");
        Assert.Equal("-1", args[loop + 1]);
        Assert.Equal("-i", args[loop + 2]);
        Assert.Equal(@"C:\audio\en.wav", args[loop + 3]);

        // Once per output, or a short bed would truncate the recording instead of repeating.
        Assert.Equal(RecordingProfile.Outputs.Count, args.Count(a => a == "-shortest"));
    }

    [Fact]
    public void The_beds_are_inputs_not_outputs()
    {
        List<string> args = Arguments(Track("English", @"C:\audio\en.wav"));

        // Every -i must come before the first -map, or ffmpeg would read the bed as a file to
        // write rather than one to read.
        int lastInput = args.LastIndexOf("-i");
        int firstMap = args.IndexOf("-map");

        Assert.True(lastInput < firstMap, "inputs must all precede the first output mapping");
    }
}
