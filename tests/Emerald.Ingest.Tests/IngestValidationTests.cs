using System.IO;
using Emerald.Core;
using Xunit;

namespace Emerald.Ingest.Tests;

/// <summary>
/// What the controller refuses, and why.
///
/// Validation is the whole of this module's safety: past this point a receiver gets opened
/// and a file gets written, and neither of those can be taken back. Each of these is a way
/// an operator can get it wrong that must not reach the hardware.
/// </summary>
public sealed class IngestValidationTests : IDisposable
{
    private readonly string _directory;
    private readonly FakeClock _clock = new("20:00:00:00");
    private readonly InMemoryStore _store = new();
    private readonly IngestControllerService _controller;

    public IngestValidationTests()
    {
        _directory = Path.Combine(Path.GetTempPath(), "emerald-ingest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);

        _controller = new IngestControllerService(
            new AppSettings(), _clock, new MockIngestHardware(),
            store: _store, recorderFactory: () => new StubRecorder(), registrar: new StubRegistrar());
    }

    /// <summary>A request that is correct in every way, for the tests to spoil one field of.</summary>
    private IngestRequest Good() => new()
    {
        BoardIndex = 0,
        BoardName = "DELTA-3G-elp-h-22",
        Port = "RX0",
        PortIndex = 0,
        FrameRate = 25,
        ReferenceTimecode = "20:57:26:00",
        Som = "00:01:00:00",
        Duration = "00:00:10:00",
        TimingMode = IngestTimingMode.DurationControlsEom,
        ClipName = "CLIP_TEST_0001",
        Directory = _directory,
    };

    [Fact]
    public void A_complete_request_produces_a_job_with_every_time_worked_out()
    {
        IngestValidation v = _controller.Validate(Good());

        Assert.True(v.IsValid, string.Join("; ", v.Messages));
        IngestJob job = v.Job!;

        Assert.Equal("20:57:26:00", job.ReferenceTimecode);
        Assert.Equal("20:57:36:00", job.Eom);
        Assert.Equal("00:00:10:00", job.Duration);
        Assert.Equal(IngestStatus.Created, job.Status);
        Assert.NotNull(job.ScheduledAt);
    }

    [Fact]
    public void The_recorder_rolls_on_the_start_timecode_itself_with_no_preroll()
    {
        // SOM is a minute, and it moves the roll point by nothing at all.
        IngestJob job = _controller.Validate(Good()).Job!;

        Assert.Equal(job.ReferenceTimecode, job.ActualStartTimecode);
        Assert.Equal("20:57:26:00", job.ActualStartTimecode);
    }

    [Fact]
    public void The_recording_is_exactly_as_long_as_the_duration()
    {
        IngestJob job = _controller.Validate(Good()).Job!;

        Assert.Equal("00:00:10:00", job.RecordedLength.ToString());
        Assert.Equal(job.DurationTimecode.TotalFrames, job.RecordedLengthFrames);
    }

    [Fact]
    public void Som_is_carried_through_as_the_media_start_timecode()
    {
        IngestJob job = _controller.Validate(Good() with { Som = "01:00:00:00" }).Job!;

        // Verbatim: it is the stamp the file will carry, not a time to be arithmetic'd.
        Assert.Equal("01:00:00:00", job.Som);
        Assert.Equal("20:57:26:00", job.ActualStartTimecode);
    }

    [Fact]
    public void Eom_can_drive_the_duration_instead()
    {
        IngestValidation v = _controller.Validate(Good() with
        {
            TimingMode = IngestTimingMode.EomControlsDuration,
            Eom = "20:58:26:00",
            Duration = "",
        });

        Assert.True(v.IsValid, string.Join("; ", v.Messages));
        Assert.Equal("00:01:00:00", v.Job!.Duration);
    }

    [Fact]
    public void An_unselected_port_is_refused()
    {
        IngestValidation v = _controller.Validate(Good() with { Port = "", PortIndex = -1 });

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.Port));
    }

    [Fact]
    public void An_unselected_board_is_refused()
    {
        IngestValidation v = _controller.Validate(Good() with { BoardName = "" });

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.Board));
    }

    [Theory]
    [InlineData("")]
    [InlineData("20:57:26")]
    [InlineData("24:00:00:00")]
    [InlineData("20:57:26:25")]   // frame 25 does not exist at 25 fps
    public void A_timecode_that_is_not_one_is_refused(string reference)
    {
        IngestValidation v = _controller.Validate(Good() with { ReferenceTimecode = reference });

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.Reference));
    }

    [Fact]
    public void An_invalid_som_is_refused_rather_than_treated_as_zero()
    {
        IngestValidation v = _controller.Validate(Good() with { Som = "00:00:00:99" });

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.Som));
    }

    [Fact]
    public void An_empty_som_means_the_clip_starts_at_zero()
    {
        IngestValidation v = _controller.Validate(Good() with { Som = "" });

        Assert.True(v.IsValid, string.Join("; ", v.Messages));
        Assert.Equal("00:00:00:00", v.Job!.Som);
    }

    [Fact]
    public void A_zero_duration_is_refused()
    {
        IngestValidation v = _controller.Validate(Good() with { Duration = "00:00:00:00" });

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.Duration));
    }

    [Fact]
    public void An_absurd_duration_is_refused()
    {
        IngestValidation v = _controller.Validate(Good() with { Duration = "20:00:00:00" });

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.Duration));
    }

    [Fact]
    public void An_eom_that_does_not_move_past_the_reference_is_refused()
    {
        IngestValidation v = _controller.Validate(Good() with
        {
            TimingMode = IngestTimingMode.EomControlsDuration,
            Eom = "20:57:26:00",
            Duration = "",
        });

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.Eom));
    }

    [Fact]
    public void A_directory_that_is_not_there_is_refused()
    {
        IngestValidation v = _controller.Validate(Good() with
        {
            Directory = Path.Combine(_directory, "nowhere"),
        });

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.Directory));
    }

    [Fact]
    public void A_clip_name_a_filename_cannot_hold_is_refused()
    {
        IngestValidation v = _controller.Validate(Good() with { ClipName = "bad/name" });

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.ClipName));
    }

    [Fact]
    public void A_clip_already_on_disk_is_never_overwritten()
    {
        // The master's own folder and extension, which is where the recorder would write.
        string folder = Path.Combine(_directory, Emerald.Video.RecordingProfile.HighRes.Folder);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "CLIP_TEST_0001.mov"), "already here");

        IngestValidation v = _controller.Validate(Good());

        Assert.False(v.IsValid);
        Assert.Contains("already exists", v.For(IngestFields.ClipName));
    }

    [Fact]
    public void A_clip_name_already_queued_for_the_same_directory_is_refused()
    {
        _controller.StartIngest(Good());

        IngestValidation second = _controller.Validate(Good());

        Assert.False(second.IsValid);
        Assert.Contains("already queued", second.For(IngestFields.ClipName));
    }

    [Fact]
    public void Two_ingests_on_one_receiver_at_the_same_time_are_refused()
    {
        _controller.StartIngest(Good());

        IngestValidation clash = _controller.Validate(Good() with { ClipName = "CLIP_TEST_0002" });

        Assert.False(clash.IsValid);
        Assert.NotNull(clash.For(IngestFields.Schedule));
    }

    [Fact]
    public void The_same_moment_on_a_different_receiver_is_fine()
    {
        _controller.StartIngest(Good());

        IngestValidation other = _controller.Validate(Good() with
        {
            ClipName = "CLIP_TEST_0002",
            Port = "RX1",
            PortIndex = 1,
        });

        Assert.True(other.IsValid, string.Join("; ", other.Messages));
    }

    [Fact]
    public void Without_a_realtime_timecode_nothing_can_be_scheduled()
    {
        _clock.Available = false;

        IngestValidation v = _controller.Validate(Good());

        Assert.False(v.IsValid);
        Assert.NotNull(v.For(IngestFields.Schedule));
    }

    [Fact]
    public void Starting_an_ingest_puts_it_on_the_queue_and_in_the_store()
    {
        IngestValidation v = _controller.StartIngest(Good());

        Assert.True(v.IsValid, string.Join("; ", v.Messages));
        Assert.Single(_controller.Queue());
        Assert.Equal(IngestStatus.Scheduled, _controller.Queue()[0].Status);
        Assert.Contains(_store.Jobs, j => j.Id == v.Job!.Id);
    }

    public void Dispose()
    {
        _controller.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch { /* a temp folder */ }
    }
}
