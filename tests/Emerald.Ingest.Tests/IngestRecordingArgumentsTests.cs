using Emerald.Video;
using Xunit;

namespace Emerald.Ingest.Tests;

/// <summary>
/// What the encoder is actually told.
///
/// The ingest's whole contract with ffmpeg is these arguments: one file per output, named
/// by the operator, stopped on a frame count, and stamped with the media start timecode.
/// A clip that came back segmented, or unnamed, or without a timecode track would each be
/// a silent failure that only turned up when somebody tried to edit from it.
/// </summary>
public class IngestRecordingArgumentsTests
{
    private static readonly CaptureFormat Format = CaptureFormat.FromStandard(0, 25);   // 1080p25

    private static List<string> Arguments(string? startTimecode, bool singleFile = true) =>
        RecordingProfile.Default
            .EncoderArguments(Format, "pipe", @"C:\ingest", "CLIP_TEST", 48000, singleFile, startTimecode)
            .ToList();

    [Fact]
    public void The_media_start_timecode_is_stamped_into_every_output()
    {
        List<string> args = Arguments("01:00:00:00");

        // Once per output - the ProRes master and the H.264 proxy are the same pictures and
        // must agree about where they start.
        Assert.Equal(RecordingProfile.Outputs.Count, args.Count(a => a == "-timecode"));

        for (int i = 0; i < args.Count; i++)
            if (args[i] == "-timecode") Assert.Equal("01:00:00:00", args[i + 1]);
    }

    [Fact]
    public void The_stamp_is_whatever_som_was_set_to_and_is_not_rewritten()
    {
        Assert.Contains("20:57:26:00", Arguments("20:57:26:00"));
        Assert.Contains("00:00:00:00", Arguments("00:00:00:00"));
    }

    [Fact]
    public void Without_a_start_timecode_nothing_is_stamped() =>
        Assert.DoesNotContain("-timecode", Arguments(null));

    [Fact]
    public void A_segmented_capture_is_never_stamped()
    {
        // Every segment would carry the same start, which is worse than carrying none.
        Assert.DoesNotContain("-timecode", Arguments("01:00:00:00", singleFile: false));
    }

    [Fact]
    public void An_ingest_writes_one_file_per_output_under_the_operators_name()
    {
        List<string> args = Arguments("01:00:00:00");

        Assert.DoesNotContain("segment", args);
        Assert.Contains(@"C:\ingest\high\CLIP_TEST.mov", args);
        Assert.Contains(@"C:\ingest\low\CLIP_TEST.mp4", args);
    }

    [Fact]
    public void A_continuous_capture_still_writes_timestamped_segments()
    {
        List<string> args = Arguments(null, singleFile: false);

        Assert.Contains("segment", args);
        Assert.Contains(@"C:\ingest\high\CLIP_TEST_%Y-%m-%d_%H-%M-%S.mov", args);
    }

    [Fact]
    public void The_planned_outputs_are_the_files_the_encoder_is_given()
    {
        var request = new CaptureRequest(
            FfmpegPath: "ffmpeg.exe", BoardIndex: 0, RxChannel: 0, Folder: @"C:\ingest",
            FrameRate: 25, NamePrefix: "CLIP_TEST", Profile: RecordingProfile.Default,
            FrameLimit: 250, SingleFile: true, StartTimecode: "01:00:00:00");

        // The controller checks these paths before scheduling, so they have to be the same
        // ones ffmpeg will open - otherwise the "never overwrite a clip" check guards
        // nothing.
        List<string> args = Arguments("01:00:00:00");
        foreach (string path in request.OutputPaths) Assert.Contains(path, args);
    }
}
