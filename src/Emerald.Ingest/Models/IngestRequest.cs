namespace Emerald.Ingest;

/// <summary>
/// Which of EOM and Duration the operator is driving. One of the pair is always derived
/// from the other; this says which way round, so a screen never has to guess and never
/// silently rewrites the field the operator is typing into.
/// </summary>
public enum IngestTimingMode
{
    /// <summary>The operator sets Duration; EOM follows.</summary>
    DurationControlsEom,

    /// <summary>The operator sets EOM; Duration follows.</summary>
    EomControlsDuration,
}

/// <summary>
/// Exactly what the operator filled in, before any of it has been believed.
///
/// The form hands one of these to <see cref="IIngestControllerService.Validate"/> and gets
/// back either a job or a list of reasons. Nothing between the two touches hardware.
/// </summary>
public sealed record IngestRequest
{
    public uint BoardIndex { get; init; }
    public string BoardName { get; init; } = "";
    public string Port { get; init; } = "";
    public int PortIndex { get; init; } = -1;

    public int FrameRate { get; init; } = 25;

    /// <summary>HH:MM:SS:FF, as typed.</summary>
    public string ReferenceTimecode { get; init; } = "";
    public string Som { get; init; } = "";
    public string Eom { get; init; } = "";
    public string Duration { get; init; } = "";

    public IngestTimingMode TimingMode { get; init; } = IngestTimingMode.DurationControlsEom;

    public string ClipName { get; init; } = "";
    public string Metadata { get; init; } = "";
    public string Directory { get; init; } = "";

    /// <summary>Recorded from the simulated receiver, because there is no card to record from.</summary>
    public bool Mock { get; init; }
}

/// <summary>
/// The outcome of checking a request: a job, or the reasons there is not one.
///
/// Problems are keyed by field so the form can put each one under the box that caused it,
/// and warnings are things worth saying out loud that are not refusals — a disk with room
/// for the recording but not much else.
/// </summary>
public sealed class IngestValidation
{
    private readonly List<(string Field, string Message)> _problems = new();
    private readonly List<string> _warnings = new();

    public IReadOnlyList<(string Field, string Message)> Problems => _problems;
    public IReadOnlyList<string> Warnings => _warnings;

    public bool IsValid => _problems.Count == 0;

    /// <summary>The job, present only when <see cref="IsValid"/>.</summary>
    public IngestJob? Job { get; internal set; }

    /// <summary>Estimated bytes the recording will occupy, when it could be worked out.</summary>
    public long? EstimatedBytes { get; internal set; }

    internal void Add(string field, string message) => _problems.Add((field, message));
    internal void Warn(string message) => _warnings.Add(message);

    /// <summary>The first problem for one field, or null when that field is fine.</summary>
    public string? For(string field) =>
        _problems.FirstOrDefault(p => p.Field == field).Message;

    /// <summary>Every problem, one per line, for the log.</summary>
    public IEnumerable<string> Messages => _problems.Select(p => p.Message);
}

/// <summary>The field names <see cref="IngestValidation"/> keys its problems by.</summary>
public static class IngestFields
{
    public const string Board = "board";
    public const string Port = "port";
    public const string Reference = "reference";
    public const string Som = "som";
    public const string Eom = "eom";
    public const string Duration = "duration";
    public const string ClipName = "clipName";
    public const string Directory = "directory";
    public const string Schedule = "schedule";
}
