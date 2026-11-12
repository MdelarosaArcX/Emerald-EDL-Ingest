using Emerald.Core;
using Xunit;

namespace Emerald.Ingest.Tests;

/// <summary>The Monitors' INGEST card, which nothing used to write.</summary>
public sealed class IngestStageTests
{
    private static readonly DateTime Now = new(2026, 11, 12, 10, 0, 0);

    private static IngestJob Job(string name, IngestStatus status, DateTime? at = null) => new()
    {
        ClipName = name,
        BoardIndex = 0,
        Port = "RX1",
        Status = status,
        ScheduledAt = at,
        Duration = "00:30:00:00",
    };

    [Fact]
    public void An_empty_queue_is_idle() =>
        Assert.Equal(StageState.Idle, IngestStage.Of(Array.Empty<IngestJob>(), Now));

    [Fact]
    public void Booked_jobs_name_the_next_one()
    {
        StageState stage = IngestStage.Of(new[]
        {
            Job("LATER", IngestStatus.Scheduled, Now.AddHours(2)),
            Job("NEXT", IngestStatus.Waiting, Now.AddMinutes(5)),
        }, Now);

        Assert.Equal("2 booked", stage.Headline);
        Assert.Contains("next NEXT at 10:05:00", stage.Detail);
        Assert.Contains("RX1", stage.Detail);
    }

    [Fact]
    public void A_recording_job_wins()
    {
        StageState stage = IngestStage.Of(new[]
        {
            Job("NEXT", IngestStatus.Scheduled, Now.AddHours(1)),
            Job("NOW", IngestStatus.Recording, Now.AddMinutes(-1)),
        }, Now);

        Assert.Equal("RECORDING", stage.Headline);
        Assert.Contains("NOW", stage.Detail);
        Assert.Contains("1 booked", stage.Detail);
        Assert.Equal(LogLevel.Ok, stage.Level);
    }

    [Fact]
    public void A_failure_shows_until_something_else_is_booked()
    {
        IngestJob failed = Job("BAD", IngestStatus.Failed);
        failed.ErrorMessage = "no signal";
        failed.CompletedAt = Now.AddMinutes(-2);

        StageState stage = IngestStage.Of(new[] { failed }, Now);
        Assert.Equal("FAILED", stage.Headline);
        Assert.Contains("no signal", stage.Detail);

        Assert.Equal("1 booked",
            IngestStage.Of(new[] { failed, Job("NEXT", IngestStatus.Scheduled, Now.AddHours(1)) }, Now).Headline);
    }

    [Fact]
    public void The_controller_writes_the_card_and_clears_it_when_closed()
    {
        var pipeline = new PipelineState();

        // Not started: the scheduler's own thread would try to roll the job. Booking it is
        // enough to raise the queue change the card follows.
        var controller = new IngestControllerService(
            new AppSettings(), new FakeClock("20:00:00:00"), new MockIngestHardware(),
            store: new InMemoryStore(), recorderFactory: () => new StubRecorder(),
            registrar: new StubRegistrar(), pipeline: pipeline);

        controller.Scheduler.Enqueue(Job("BOOKED", IngestStatus.Created, DateTime.Now.AddHours(3)));

        Assert.Equal("1 booked", pipeline.Ingest.Headline);

        controller.Dispose();
        Assert.Equal(StageState.Idle, pipeline.Ingest);
    }
}
