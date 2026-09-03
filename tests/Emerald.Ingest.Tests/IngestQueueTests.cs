using Emerald.Core;
using Xunit;

namespace Emerald.Ingest.Tests;

/// <summary>
/// The queue and the scheduler, driven by hand.
///
/// The scheduler's own loop is not started here: these exercise what it does when it is
/// asked to, which is the part that has to be right. Timing across a live thread is tested
/// by the clock being moved, not by waiting.
/// </summary>
public class IngestQueueTests
{
    private static IngestJob Job(string clipName = "CLIP", string actualStart = "20:57:26:00",
                                 int portIndex = 0, DateTime? scheduledAt = null) => new()
    {
        ClipName = clipName,
        BoardIndex = 0,
        BoardName = "DELTA-3G",
        Port = $"RX{portIndex}",
        PortIndex = portIndex,
        FrameRate = 25,
        ReferenceTimecode = "20:57:26:00",
        Som = "00:01:00:00",
        Duration = "00:00:10:00",
        Eom = "20:57:36:00",
        ActualStartTimecode = actualStart,
        Directory = @"C:\ingest",
        ScheduledAt = scheduledAt ?? DateTime.Now.AddMinutes(5),
    };

    private static (IngestSchedulerService Scheduler, InMemoryStore Store, FakeClock Clock) Build(
        string now = "20:00:00:00")
    {
        StubRecorder.Created.Clear();

        var clock = new FakeClock(now);
        var store = new InMemoryStore();

        var scheduler = new IngestSchedulerService(
            clock, () => new StubRecorder(), store, new StubRegistrar(), new IngestLog());

        return (scheduler, store, clock);
    }

    [Fact]
    public void Enqueuing_schedules_the_job_and_persists_it()
    {
        (IngestSchedulerService scheduler, InMemoryStore store, _) = Build();
        IngestJob job = Job();

        scheduler.Enqueue(job);

        Assert.Equal(IngestStatus.Scheduled, job.Status);
        Assert.Single(scheduler.Snapshot());
        Assert.Contains(store.Jobs, j => j.Id == job.Id);
    }

    [Fact]
    public void The_same_job_is_never_queued_twice()
    {
        (IngestSchedulerService scheduler, _, _) = Build();
        IngestJob job = Job();

        scheduler.Enqueue(job);
        scheduler.Enqueue(job);

        Assert.Single(scheduler.Snapshot());
    }

    [Fact]
    public void A_waiting_job_can_be_cancelled()
    {
        (IngestSchedulerService scheduler, _, _) = Build();
        IngestJob job = Job();
        scheduler.Enqueue(job);

        Assert.True(scheduler.Cancel(job.Id));
        Assert.Equal(IngestStatus.Cancelled, job.Status);
    }

    [Fact]
    public void Cancelling_a_finished_job_does_nothing()
    {
        (IngestSchedulerService scheduler, _, _) = Build();
        IngestJob job = Job();
        scheduler.Enqueue(job);
        scheduler.Cancel(job.Id);

        Assert.False(scheduler.Cancel(job.Id));
    }

    [Fact]
    public void A_pending_job_is_never_removed_from_the_queue()
    {
        (IngestSchedulerService scheduler, _, _) = Build();
        IngestJob job = Job();
        scheduler.Enqueue(job);

        Assert.False(scheduler.Remove(job.Id));
        Assert.Single(scheduler.Snapshot());
    }

    [Fact]
    public void A_finished_job_can_be_cleared_away()
    {
        (IngestSchedulerService scheduler, _, _) = Build();
        IngestJob job = Job();
        scheduler.Enqueue(job);
        scheduler.Cancel(job.Id);

        Assert.True(scheduler.Remove(job.Id));
        Assert.Empty(scheduler.Snapshot());
    }

    [Fact]
    public void Jobs_are_listed_in_the_order_they_will_run()
    {
        (IngestSchedulerService scheduler, _, _) = Build();

        IngestJob later = Job("LATER", scheduledAt: DateTime.Now.AddMinutes(30));
        IngestJob sooner = Job("SOONER", portIndex: 1, scheduledAt: DateTime.Now.AddMinutes(5));

        scheduler.Enqueue(later);
        scheduler.Enqueue(sooner);

        Assert.Equal(new[] { "SOONER", "LATER" }, scheduler.Snapshot().Select(j => j.ClipName));
    }

    [Fact]
    public void A_finished_job_sorts_below_a_live_one()
    {
        (IngestSchedulerService scheduler, _, _) = Build();

        IngestJob done = Job("DONE", scheduledAt: DateTime.Now.AddMinutes(1));
        IngestJob pending = Job("PENDING", portIndex: 1, scheduledAt: DateTime.Now.AddMinutes(30));

        scheduler.Enqueue(done);
        scheduler.Enqueue(pending);
        scheduler.Cancel(done.Id);

        Assert.Equal(new[] { "PENDING", "DONE" }, scheduler.Snapshot().Select(j => j.ClipName));
    }

    [Fact]
    public void Nothing_is_recording_on_a_receiver_that_has_not_rolled()
    {
        (IngestSchedulerService scheduler, _, _) = Build();
        scheduler.Enqueue(Job());

        Assert.Null(scheduler.RecordingOn(0, 0));
    }

    [Fact]
    public void A_job_moved_illegally_throws_rather_than_being_tidied_up()
    {
        // The scheduler only ever moves a job through Move(), which checks; this is the
        // check itself, standing in for every path that reaches it.
        Assert.Throws<InvalidIngestTransitionException>(() =>
            IngestStatusRules.EnsureCanTransition(IngestStatus.Completed, IngestStatus.Recording));
    }

    [Fact]
    public void The_recorded_length_a_job_asks_for_is_its_duration()
    {
        IngestJob job = Job();

        Assert.Equal("00:00:10:00", job.RecordedLength.ToString());
        Assert.Equal(10 * 25, job.RecordedLengthFrames);
    }

    [Fact]
    public void A_job_reads_its_own_timecodes_back_at_its_own_rate()
    {
        IngestJob job = Job();
        job.FrameRate = 50;
        job.Duration = "00:00:10:00";

        Assert.Equal(500, job.DurationTimecode.TotalFrames);
        Assert.Equal(50, job.DurationTimecode.Rate);
    }

    [Fact]
    public void An_unreadable_timecode_on_a_job_reads_as_zero_rather_than_throwing()
    {
        IngestJob job = Job();
        job.Som = "nonsense";

        Assert.Equal(0, job.SomTimecode.TotalFrames);
    }
}
