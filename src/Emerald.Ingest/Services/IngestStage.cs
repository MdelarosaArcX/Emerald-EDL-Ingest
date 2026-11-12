using Emerald.Core;

namespace Emerald.Ingest;

/// <summary>
/// The INGEST card on the Monitors, from the ingest queue.
///
/// Nothing used to write it, so the card said "idle" whatever the queue held. The controller
/// now writes it on every queue or job change, from this - pure, so it can be tested without
/// a scheduler.
/// </summary>
public static class IngestStage
{
    public static StageState Of(IReadOnlyList<IngestJob> jobs, DateTime now)
    {
        IngestJob[] recording = jobs.Where(j => j.Status == IngestStatus.Recording).ToArray();
        IngestJob[] pending = jobs.Where(j => IngestStatusRules.IsPending(j.Status))
                                  .OrderBy(j => j.ScheduledAt ?? DateTime.MaxValue)
                                  .ToArray();

        string later = pending.Length == 0 ? "" : $"  ·  {pending.Length} booked";

        if (recording.Length > 0)
        {
            IngestJob job = recording[0];
            string more = recording.Length > 1 ? $" and {recording.Length - 1} more" : "";

            return new StageState(
                recording.Length == 1 ? "RECORDING" : $"RECORDING {recording.Length}",
                $"{job.ClipName} on {Where(job)}, {job.Duration}{more}{later}",
                LogLevel.Ok);
        }

        if (pending.Length > 0)
        {
            IngestJob next = pending[0];
            string when = next.ScheduledAt is { } at
                ? at.Date == now.Date ? $"at {at:HH:mm:ss}" : $"{at:dd MMM HH:mm}"
                : $"at {next.ActualStartTimecode}";

            return new StageState(
                $"{pending.Length} booked",
                $"next {next.ClipName} {when} on {Where(next)}",
                LogLevel.Info);
        }

        // The last one to finish, if it failed: worth seeing until something else is booked.
        IngestJob? last = jobs.Where(j => j.CompletedAt is not null).MaxBy(j => j.CompletedAt);

        if (last is { Status: IngestStatus.Failed })
            return new StageState("FAILED", $"{last.ClipName}: {last.ErrorMessage}", LogLevel.Error);

        return StageState.Idle;
    }

    private static string Where(IngestJob job) =>
        job.Port.Length > 0 ? $"board {job.BoardIndex} {job.Port}" : $"board {job.BoardIndex}";
}
