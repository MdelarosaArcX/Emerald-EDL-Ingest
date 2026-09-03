using System.IO;
using System.Windows;
using System.Windows.Media;
using Emerald.Core;

namespace Emerald.Ingest;

/// <summary>
/// The rows the ingest screens bind to.
///
/// Emerald's windows bind to flattened row objects rather than to domain models — the EDL's
/// queue and the deck's clip strip both work this way — so the presentation decisions
/// (which brush, which wording, how a size reads) live in one place per list instead of
/// being spread through the XAML as converters. These follow that convention.
/// </summary>
public sealed record IngestQueueRow(
    Guid Id,
    string Position,
    string StartTimecode,
    string ClipName,
    string Status,
    string Detail,
    Brush Accent,
    bool CanCancel,
    bool CanRemove)
{
    public Visibility CancelVisibility => CanCancel ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => CanRemove ? Visibility.Visible : Visibility.Collapsed;

    public static IngestQueueRow From(IngestJob job, int position)
    {
        (string label, string brush) = job.Status switch
        {
            IngestStatus.Recording => ("RECORDING", "Ok"),
            IngestStatus.Waiting => ("WAITING", "Warn"),
            IngestStatus.Scheduled => ("SCHEDULED", "Info"),
            IngestStatus.Created => ("IDLE", "Muted"),
            IngestStatus.Completed => ("COMPLETED", "Ok"),
            IngestStatus.Cancelled => ("CANCELLED", "Muted"),
            _ => ("FAILED", "Bad"),
        };

        string detail = job.Status switch
        {
            IngestStatus.Recording => job.ProgressText,
            IngestStatus.Completed => job.FilePath is { } p
                ? $"{Path.GetFileName(p)}   {Bytes(job.FileSize)}"
                : $"records {job.RecordedLength}",
            _ when job.ErrorMessage is { Length: > 0 } e => e,
            _ => $"som {job.Som}   dur {job.Duration}   eom {job.Eom}   records {job.RecordedLength}",
        };

        return new IngestQueueRow(
            Id: job.Id,
            Position: position.ToString(),
            StartTimecode: job.ActualStartTimecode,
            ClipName: job.ClipName + (job.Mock ? "  (simulated)" : ""),
            Status: label,
            Detail: $"{job.BoardIndex}. {job.Port}   {detail}",
            Accent: Palette.Brush(brush),
            CanCancel: !IngestStatusRules.IsTerminal(job.Status),
            CanRemove: IngestStatusRules.IsTerminal(job.Status));
    }

    private static string Bytes(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):F1} GB"
        : $"{bytes / (double)(1L << 20):F0} MB";
}

/// <summary>One completed recording, as "Recent Ingests" and the history list show it.</summary>
public sealed record IngestRecordingRow(
    Guid Id,
    string FileName,
    string Timing,
    string Size,
    Brush Accent)
{
    public static IngestRecordingRow From(IngestRecording recording)
    {
        string name = recording.FilePath.Length > 0
            ? Path.GetFileName(recording.FilePath)
            : "(no file)";

        return new IngestRecordingRow(
            Id: recording.Id,
            FileName: name + (recording.Mock ? "  (simulated)" : ""),
            Timing: $"{recording.ActualStartTimecode} - {recording.Length}",
            Size: recording.SizeText,
            Accent: Palette.Brush(recording.Status switch
            {
                IngestStatus.Completed => "Ok",
                IngestStatus.Cancelled => "Muted",
                _ => "Bad",
            }));
    }
}

/// <summary>One finished job, for the history window.</summary>
public sealed record IngestHistoryRow(
    Guid Id,
    string ClipName,
    string Status,
    string When,
    string Timing,
    string Detail,
    Brush Accent)
{
    public static IngestHistoryRow From(IngestJob job)
    {
        string brush = job.Status switch
        {
            IngestStatus.Completed => "Ok",
            IngestStatus.Cancelled => "Muted",
            _ => "Bad",
        };

        string detail = job.ErrorMessage is { Length: > 0 } error
            ? error
            : job.FilePath ?? "";

        return new IngestHistoryRow(
            Id: job.Id,
            ClipName: job.ClipName + (job.Mock ? "  (simulated)" : ""),
            Status: IngestStatusRules.Display(job.Status),
            When: (job.CompletedAt ?? job.CreatedAt).ToString("yyyy-MM-dd HH:mm:ss"),
            Timing: $"{job.BoardIndex}. {job.Port}   ref {job.ReferenceTimecode}   " +
                    $"som {job.Som}   dur {job.Duration}   eom {job.Eom}",
            Detail: detail,
            Accent: Palette.Brush(brush));
    }
}

/// <summary>
/// The application's brushes, looked up by name. Emerald keeps its palette in App.xaml so
/// every module reads the same one; this is how a row object reaches it without each list
/// growing a converter of its own.
/// </summary>
internal static class Palette
{
    public static Brush Brush(string key) =>
        Application.Current?.TryFindResource(key) as Brush ?? Brushes.White;
}
