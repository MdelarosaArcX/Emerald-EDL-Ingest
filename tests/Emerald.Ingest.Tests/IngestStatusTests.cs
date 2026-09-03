using Xunit;

namespace Emerald.Ingest.Tests;

/// <summary>
/// The state machine. A job that could move anywhere would make the history meaningless,
/// so the illegal moves are worth as many tests as the legal ones.
/// </summary>
public class IngestStatusTests
{
    [Theory]
    [InlineData(IngestStatus.Created, IngestStatus.Scheduled)]
    [InlineData(IngestStatus.Scheduled, IngestStatus.Waiting)]
    [InlineData(IngestStatus.Waiting, IngestStatus.Recording)]
    [InlineData(IngestStatus.Recording, IngestStatus.Completed)]
    public void The_normal_run_is_allowed(IngestStatus from, IngestStatus to) =>
        Assert.True(IngestStatusRules.CanTransition(from, to));

    [Theory]
    [InlineData(IngestStatus.Created)]
    [InlineData(IngestStatus.Scheduled)]
    [InlineData(IngestStatus.Waiting)]
    [InlineData(IngestStatus.Recording)]
    public void Anything_unfinished_can_be_cancelled_or_failed(IngestStatus from)
    {
        Assert.True(IngestStatusRules.CanTransition(from, IngestStatus.Cancelled));
        Assert.True(IngestStatusRules.CanTransition(from, IngestStatus.Failed));
    }

    [Theory]
    [InlineData(IngestStatus.Completed)]
    [InlineData(IngestStatus.Cancelled)]
    [InlineData(IngestStatus.Failed)]
    public void A_finished_job_never_moves_again(IngestStatus terminal)
    {
        Assert.True(IngestStatusRules.IsTerminal(terminal));

        foreach (IngestStatus to in Enum.GetValues<IngestStatus>())
            Assert.False(IngestStatusRules.CanTransition(terminal, to));
    }

    [Theory]
    [InlineData(IngestStatus.Created, IngestStatus.Recording)]   // never without being armed
    [InlineData(IngestStatus.Created, IngestStatus.Completed)]   // never without recording
    [InlineData(IngestStatus.Scheduled, IngestStatus.Completed)]
    [InlineData(IngestStatus.Waiting, IngestStatus.Completed)]
    [InlineData(IngestStatus.Recording, IngestStatus.Waiting)]   // never backwards
    [InlineData(IngestStatus.Waiting, IngestStatus.Scheduled)]
    public void Skipping_or_reversing_a_step_is_refused(IngestStatus from, IngestStatus to)
    {
        Assert.False(IngestStatusRules.CanTransition(from, to));
        Assert.Throws<InvalidIngestTransitionException>(() => IngestStatusRules.EnsureCanTransition(from, to));
    }

    [Fact]
    public void A_job_does_not_transition_to_where_it_already_is() =>
        Assert.False(IngestStatusRules.CanTransition(IngestStatus.Waiting, IngestStatus.Waiting));

    [Fact]
    public void Pending_covers_exactly_the_states_that_still_expect_to_record()
    {
        Assert.True(IngestStatusRules.IsPending(IngestStatus.Created));
        Assert.True(IngestStatusRules.IsPending(IngestStatus.Scheduled));
        Assert.True(IngestStatusRules.IsPending(IngestStatus.Waiting));

        Assert.False(IngestStatusRules.IsPending(IngestStatus.Recording));
        Assert.False(IngestStatusRules.IsPending(IngestStatus.Completed));
    }
}
