using System.IO;
using Emerald.Core;
using Xunit;

namespace Emerald.Ingest.Tests;

/// <summary>
/// The store, and what happens to a queue that outlived its process.
///
/// A booked ingest that quietly disappeared because Emerald was restarted is the failure
/// this module is least allowed to have, so recovery is tested as carefully as the
/// recording itself.
/// </summary>
public sealed class IngestPersistenceTests : IDisposable
{
    private readonly string _folder;
    private readonly string _database;

    public IngestPersistenceTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "emerald-ingest-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _database = Path.Combine(_folder, "ingest.db");
    }

    private SqliteIngestStore NewStore()
    {
        var store = new SqliteIngestStore(_database);
        store.Initialise();
        return store;
    }

    private static IngestJob Job(string clipName, IngestStatus status, DateTime? scheduledAt) => new()
    {
        ClipName = clipName,
        BoardIndex = 1,
        BoardName = "DELTA-12G-elp-h-20",
        Port = "RX2",
        PortIndex = 2,
        FrameRate = 25,
        ReferenceTimecode = "20:57:26:00",
        Som = "00:01:00:00",
        Eom = "21:12:26:00",
        Duration = "00:15:00:00",
        ActualStartTimecode = "20:56:26:00",
        Directory = @"E:\Ingest\Clips",
        Metadata = "Program: Live Show",
        Status = status,
        ScheduledAt = scheduledAt,
    };

    [Fact]
    public void A_job_survives_being_written_and_read_back()
    {
        IngestJob job = Job("CLIP_A", IngestStatus.Waiting, DateTime.Now.AddHours(1));
        NewStore().Save(job);

        IngestJob loaded = Assert.Single(NewStore().LoadUnfinished());

        Assert.Equal(job.Id, loaded.Id);
        Assert.Equal("CLIP_A", loaded.ClipName);
        Assert.Equal("DELTA-12G-elp-h-20", loaded.BoardName);
        Assert.Equal(2, loaded.PortIndex);
        Assert.Equal("20:56:26:00", loaded.ActualStartTimecode);
        Assert.Equal("00:15:00:00", loaded.Duration);
        Assert.Equal("Program: Live Show", loaded.Metadata);
        Assert.Equal(IngestStatus.Waiting, loaded.Status);
    }

    [Fact]
    public void Saving_the_same_job_again_updates_it_rather_than_duplicating_it()
    {
        SqliteIngestStore store = NewStore();
        IngestJob job = Job("CLIP_A", IngestStatus.Waiting, DateTime.Now.AddHours(1));

        store.Save(job);
        job.Status = IngestStatus.Recording;
        job.FramesRecorded = 500;
        store.Save(job);

        IngestJob loaded = Assert.Single(store.LoadUnfinished());
        Assert.Equal(IngestStatus.Recording, loaded.Status);
        Assert.Equal(500, loaded.FramesRecorded);
    }

    [Fact]
    public void Finished_jobs_are_history_and_unfinished_ones_are_not()
    {
        SqliteIngestStore store = NewStore();

        store.Save(Job("DONE", IngestStatus.Completed, null));
        store.Save(Job("WAITING", IngestStatus.Waiting, DateTime.Now.AddHours(1)));

        Assert.Equal("DONE", Assert.Single(store.History()).ClipName);
        Assert.Equal("WAITING", Assert.Single(store.LoadUnfinished()).ClipName);
    }

    [Fact]
    public void A_recording_row_keeps_what_was_actually_produced()
    {
        SqliteIngestStore store = NewStore();
        IngestJob job = Job("CLIP_A", IngestStatus.Completed, null);
        store.Save(job);

        store.SaveRecording(new IngestRecording
        {
            IngestJobId = job.Id,
            ActualStartTimecode = "20:56:26:00",
            ActualEndTimecode = "21:12:26:00",
            FilePath = @"E:\Ingest\Clips\high\CLIP_A.mov",
            FileSize = 2_100_000_000,
            Codec = "prores",
            Resolution = "1920x1080",
            FrameRate = 25,
            Frames = 24_000,
            Status = IngestStatus.Completed,
        });

        IngestRecording loaded = Assert.Single(store.RecordingsFor(job.Id));

        Assert.Equal("prores", loaded.Codec);
        Assert.Equal("1920x1080", loaded.Resolution);
        Assert.Equal(24_000, loaded.Frames);
        Assert.Equal("00:16:00:00", loaded.Length.ToString());
        Assert.Equal("2.0 GB", loaded.SizeText);
    }

    [Fact]
    public void A_queued_clip_name_is_seen_as_taken_and_a_finished_one_is_not()
    {
        SqliteIngestStore store = NewStore();

        store.Save(Job("CLIP_A", IngestStatus.Waiting, DateTime.Now.AddHours(1)));
        store.Save(Job("CLIP_B", IngestStatus.Completed, null));

        Assert.True(store.ClipNameTaken(@"E:\Ingest\Clips", "CLIP_A", Guid.NewGuid()));
        Assert.False(store.ClipNameTaken(@"E:\Ingest\Clips", "CLIP_B", Guid.NewGuid()));
        Assert.False(store.ClipNameTaken(@"E:\Somewhere\Else", "CLIP_A", Guid.NewGuid()));
    }

    // ------------------------------------------------------------------ recovery

    private IngestControllerService Controller(IIngestStore store) => new(
        new AppSettings(), new FakeClock("20:00:00:00"), new MockIngestHardware(),
        store: store, recorderFactory: () => new StubRecorder(), registrar: new StubRegistrar());

    [Fact]
    public void A_job_that_was_recording_when_emerald_stopped_is_failed_not_resumed()
    {
        var store = new InMemoryStore();
        IngestJob job = Job("INTERRUPTED", IngestStatus.Recording, DateTime.Now.AddMinutes(-5));
        store.Save(job);

        using IngestControllerService controller = Controller(store);
        controller.Initialise();

        Assert.Equal(IngestStatus.Failed, job.Status);
        Assert.Contains("Interrupted", job.ErrorMessage);
        Assert.Empty(controller.Queue());
    }

    [Fact]
    public void A_job_whose_moment_passed_while_emerald_was_down_is_failed_with_a_reason()
    {
        var store = new InMemoryStore();
        IngestJob job = Job("MISSED", IngestStatus.Waiting, DateTime.Now.AddHours(-2));
        store.Save(job);

        using IngestControllerService controller = Controller(store);
        controller.Initialise();

        Assert.Equal(IngestStatus.Failed, job.Status);
        Assert.Contains("while Emerald was not running", job.ErrorMessage);
    }

    [Fact]
    public void A_job_still_ahead_of_itself_is_put_back_on_the_queue()
    {
        var store = new InMemoryStore();
        IngestJob job = Job("STILL_AHEAD", IngestStatus.Waiting, DateTime.Now.AddHours(3));
        store.Save(job);

        using IngestControllerService controller = Controller(store);
        controller.Initialise();

        Assert.Equal(IngestStatus.Scheduled, job.Status);
        Assert.Equal("STILL_AHEAD", Assert.Single(controller.Queue()).ClipName);
    }

    [Fact]
    public void A_job_with_no_start_time_is_failed_rather_than_guessed_at()
    {
        var store = new InMemoryStore();
        IngestJob job = Job("NO_TIME", IngestStatus.Scheduled, scheduledAt: null);
        store.Save(job);

        using IngestControllerService controller = Controller(store);
        controller.Initialise();

        Assert.Equal(IngestStatus.Failed, job.Status);
        Assert.Empty(controller.Queue());
    }

    [Fact]
    public void Recovery_reaches_a_real_database_written_by_a_previous_session()
    {
        IngestJob job = Job("FROM_DISK", IngestStatus.Waiting, DateTime.Now.AddHours(4));
        NewStore().Save(job);

        // A second store over the same file is a second run of Emerald over the same machine.
        using IngestControllerService controller = Controller(NewStore());
        controller.Initialise();

        Assert.Equal("FROM_DISK", Assert.Single(controller.Queue()).ClipName);
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { /* a temp folder */ }
    }
}
