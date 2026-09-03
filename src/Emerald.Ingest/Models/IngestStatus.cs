namespace Emerald.Ingest;

/// <summary>
/// Where an ingest job stands. The normal life of a job runs straight down this list:
///
///     Created -> Scheduled -> Waiting -> Recording -> Completed
///
/// with Cancelled and Failed available from anywhere that is not already finished.
/// </summary>
public enum IngestStatus
{
    /// <summary>Validated and built, but not yet handed to the scheduler.</summary>
    Created,

    /// <summary>Accepted by the scheduler and persisted; its start time is known.</summary>
    Scheduled,

    /// <summary>Armed, counting down to its actual start timecode.</summary>
    Waiting,

    /// <summary>The receiver is open and frames are going to disk.</summary>
    Recording,

    /// <summary>Recorded in full, the file verified and registered.</summary>
    Completed,

    /// <summary>Stopped by the operator, before or during the recording.</summary>
    Cancelled,

    /// <summary>Did not produce the recording that was asked for. <see cref="IngestJob.ErrorMessage"/> says why.</summary>
    Failed,
}

/// <summary>Thrown when a caller tries to move a job somewhere it cannot go.</summary>
public sealed class InvalidIngestTransitionException : Exception
{
    public InvalidIngestTransitionException(IngestStatus from, IngestStatus to)
        : base($"An ingest job cannot go from {from} to {to}.")
    {
        From = from;
        To = to;
    }

    public IngestStatus From { get; }
    public IngestStatus To { get; }
}

/// <summary>
/// The state machine, in one place.
///
/// This is deliberately strict. A job that quietly went from Completed back to Recording,
/// or from Cancelled to Completed, would be a recording nobody could account for — and in
/// a broadcast log an unaccountable recording is worse than a missing one. Every move is
/// checked, and an illegal one throws rather than being tidied up.
/// </summary>
public static class IngestStatusRules
{
    /// <summary>States a job never leaves.</summary>
    public static bool IsTerminal(IngestStatus status) =>
        status is IngestStatus.Completed or IngestStatus.Cancelled or IngestStatus.Failed;

    /// <summary>States in which the job still expects to do something.</summary>
    public static bool IsPending(IngestStatus status) =>
        status is IngestStatus.Created or IngestStatus.Scheduled or IngestStatus.Waiting;

    public static bool CanTransition(IngestStatus from, IngestStatus to)
    {
        if (from == to) return false;
        if (IsTerminal(from)) return false;

        // Cancelling and failing are always available while the job is still alive: a card
        // that disappears mid-record has to be able to end the job wherever it had got to.
        if (to is IngestStatus.Cancelled or IngestStatus.Failed) return true;

        return (from, to) switch
        {
            (IngestStatus.Created, IngestStatus.Scheduled) => true,
            (IngestStatus.Scheduled, IngestStatus.Waiting) => true,
            (IngestStatus.Waiting, IngestStatus.Recording) => true,
            (IngestStatus.Recording, IngestStatus.Completed) => true,
            _ => false,
        };
    }

    public static void EnsureCanTransition(IngestStatus from, IngestStatus to)
    {
        if (!CanTransition(from, to)) throw new InvalidIngestTransitionException(from, to);
    }

    /// <summary>The word the operator reads, which is the status in capitals but for Created.</summary>
    public static string Display(IngestStatus status) =>
        status == IngestStatus.Created ? "IDLE" : status.ToString().ToUpperInvariant();
}
