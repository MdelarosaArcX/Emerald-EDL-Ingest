using System.IO;
using Emerald.Core;
using Emerald.Video;
using Xunit;

namespace Emerald.Video.Tests;

/// <summary>
/// Where a recording's segments land.
///
/// This exists because of a real fault on the plant: two recorders were running into one store,
/// one writing folders and one writing loose timestamped files into the root, and from the store
/// there was no way to tell which recording a root-level segment belonged to. The cause was a
/// caller that built the encoder command without <see cref="RecordingNames"/> — which silently
/// selects the scheme from before folders existed.
///
/// So the thing worth pinning is not the exact command. It is that a segmented recording given
/// names writes into its own folder and never into the root, and that the fallback is reachable
/// only by asking for it.
/// </summary>
public sealed class RecordingLandsInAFolderTests
{
    private const string Store = "C:\\store";

    private static RecordingProfile Profile() => RecordingProfile.From(new AppSettings
    {
        CaptureFrameRate = 25,
        RecordingSegmentSeconds = 120,
        RecordingAudioSampleRate = 48000,
    });

    private static List<string> Args(RecordingNames? names) => Profile()
        .EncoderArguments(
            new CaptureFormat("1080i50", 1920, 1080, 25),
            pipeName: "emerald-test",
            folder: Store,
            namePrefix: "media",
            inputSampleRate: 48000,
            singleFile: false,
            startTimecode: null,
            extraAudio: null,
            embeddedPairs: 1,
            names: names)
        .ToList();

    /// <summary>The output paths on the command, which are the arguments that are media files.</summary>
    private static List<string> Outputs(List<string> args) => args
        .Where(a => a.StartsWith(Store, StringComparison.OrdinalIgnoreCase))
        .ToList();

    [Fact]
    public void A_named_recording_writes_every_output_inside_its_own_folder()
    {
        var names = RecordingSequence.Next(new DateTime(2026, 10, 6, 15, 28, 7), "media");

        List<string> outputs = Outputs(Args(names));

        Assert.NotEmpty(outputs);

        foreach (string output in outputs)
        {
            // The recording's folder is the parent of the file, not the store root.
            string? parent = Path.GetFileName(Path.GetDirectoryName(output));

            Assert.Equal(names.Folder, parent);
        }
    }

    [Fact]
    public void A_named_recording_puts_nothing_in_the_root_of_the_store()
    {
        var names = RecordingSequence.Next(new DateTime(2026, 10, 6, 15, 28, 7), "media");

        foreach (string output in Outputs(Args(names)))
        {
            string folder = Path.GetDirectoryName(output)!;

            // low/ and high/ are the profile's own output folders; the root of either is where
            // the stray files turned up, and nothing may be written straight into them.
            Assert.NotEqual(
                Path.GetFullPath(RecordingProfile.FolderFor(RecordingProfile.LowRes, Store)),
                Path.GetFullPath(folder));

            Assert.NotEqual(
                Path.GetFullPath(RecordingProfile.FolderFor(RecordingProfile.HighRes, Store)),
                Path.GetFullPath(folder));
        }
    }

    [Fact]
    public void A_named_recording_does_not_ask_ffmpeg_for_a_timestamped_filename()
    {
        // -strftime and a sequence number are mutually exclusive: under strftime %d is the day
        // of the month, so a pattern asking for both writes every segment to one filename.
        var names = RecordingSequence.Next(new DateTime(2026, 10, 6, 15, 28, 7), "media");

        List<string> args = Args(names);

        Assert.DoesNotContain("-strftime", args);
        Assert.Contains("-segment_start_number", args);
    }

    [Fact]
    public void A_named_recording_numbers_its_segments()
    {
        var names = RecordingSequence.Next(new DateTime(2026, 10, 6, 15, 28, 7), "media");

        foreach (string output in Outputs(Args(names)))
            Assert.Contains("%d", Path.GetFileName(output));
    }

    [Fact]
    public void Two_recordings_started_apart_cannot_write_to_the_same_folder()
    {
        // This is what keeps two recorders apart on disk even when both are running: each one
        // names its own folder from the moment it rolled.
        var first = RecordingSequence.Next(new DateTime(2026, 10, 6, 15, 28, 7), "media");
        var second = RecordingSequence.Next(new DateTime(2026, 10, 6, 15, 31, 2), "media");

        Assert.NotEqual(first.Folder, second.Folder);
    }

    [Fact]
    public void The_scheme_from_before_folders_is_only_reachable_by_asking_for_it()
    {
        // Kept for the old callers and the tests that pin the old command - but a caller has to
        // pass no names to get it, and no production caller does any more.
        List<string> args = Args(names: null);

        Assert.Contains("-strftime", args);

        foreach (string output in Outputs(args))
            Assert.Contains("%Y-%m-%d", Path.GetFileName(output));
    }
}
