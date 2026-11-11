using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emerald.Core;

/// <summary>Every step in the life of an edit, from the marks to playback taking it.</summary>
public enum EdlAction
{
    /// <summary>The tidal lock, or Live Edit's own transmission, changed state.</summary>
    LockChanged,
    MarkIn,
    MarkOut,
    EditCreated,
    EditModified,
    EditDeleted,

    /// <summary>The first EDL revision this session.</summary>
    EdlGenerated,

    /// <summary>A revision after the first, superseding the one before it.</summary>
    EdlUpdated,

    /// <summary>A revision handed to playback, with what playback said.</summary>
    EdlSent,

    /// <summary>An edit's first frame reached the no edit zone.</summary>
    NoEditZone,
    ConformStarted,
    PlaybackAcknowledged,
    PlaybackRejected,
    OperationFailed,
}

/// <summary>
/// One line of the EDL log. Structured, so a whole edit can be followed by its
/// <see cref="EditId"/> and a whole send by its <see cref="TransactionId"/> without reading
/// sentences.
///
/// Frame numbers are frames of the day at <see cref="Rate"/>, as on Live Edit's ruler.
/// <see cref="StartFrame"/> and <see cref="EndFrame"/> are the first and last selected frames,
/// both included; <see cref="MarkOut"/> is where the out mark was set, one past the last.
/// </summary>
public sealed record EdlLogEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string OperationId { get; init; } = "";
    public EdlAction Action { get; init; }
    public string? EditId { get; init; }
    public string? TransactionId { get; init; }
    public string LockState { get; init; } = "";
    public string Message { get; init; } = "";

    public int? Rate { get; init; }
    public IReadOnlyList<string> SourceFiles { get; init; } = Array.Empty<string>();
    public string? MarkIn { get; init; }
    public string? MarkOut { get; init; }
    public long? StartFrame { get; init; }
    public long? EndFrame { get; init; }
    public long? DurationFrames { get; init; }
    public double? DurationSeconds { get; init; }

    /// <summary>
    /// Every selected frame, as inclusive runs. A marked range is one run; it is written as a run
    /// rather than number by number because an hour is ninety thousand of them, and a run is the
    /// same list, exactly.
    /// </summary>
    public IReadOnlyList<EdlFrameRun> SelectedFrames { get; init; } = Array.Empty<EdlFrameRun>();

    /// <summary>The same frames by capture file: which file, and which of its frames.</summary>
    public IReadOnlyList<EdlSourceRange> Sources { get; init; } = Array.Empty<EdlSourceRange>();
    public string? Audio { get; init; }

    public long? PreviousVersion { get; init; }
    public long? UpdatedVersion { get; init; }
    public string? ConformStatus { get; init; }
    public string? PlaybackResponse { get; init; }

    /// <summary>What playback is told to do, in words: "black and silence from recording frame ...".</summary>
    public string? Instruction { get; init; }
    public string? Error { get; init; }

    /// <summary>The selection's marks, frames and sources, copied in.</summary>
    public EdlLogEntry With(EdlSelection? selection) => selection is not { } s ? this : this with
    {
        Rate = s.Rate,
        SourceFiles = s.SourceFiles,
        MarkIn = s.MarkInTimecode,
        MarkOut = s.MarkOutTimecode,
        StartFrame = s.FirstFrame,
        EndFrame = s.LastFrame,
        DurationFrames = s.DurationFrames,
        DurationSeconds = Math.Round(s.DurationSeconds, 3),
        SelectedFrames = s.DurationFrames > 0 ? new[] { new EdlFrameRun(s.FirstFrame, s.LastFrame) } : Array.Empty<EdlFrameRun>(),
        Sources = s.Sources,
        Audio = s.Audio,
    };
}

/// <summary>An unbroken run of frames, both ends included.</summary>
public readonly record struct EdlFrameRun(long First, long Last);

/// <summary>
/// The EDL log: every step of every edit, written three ways - kept in memory for the window and
/// for tests, put in the activity log so it shows with everything else, and appended whole, as one
/// JSON object a line, to <c>edl-yyyyMMdd.jsonl</c> beside the activity log.
///
/// The third is the one to trace an edit with. The activity log trims long detail, and an edit
/// across a hundred segments has a long list of sources; this file never trims.
/// </summary>
public sealed class EdlLogger
{
    public static EdlLogger Shared { get; } = new(ActivityLog.Shared, () => ActivityLog.Shared.Folder);

    /// <summary>How many entries are kept in memory. The file keeps them all.</summary>
    public const int Keep = 5000;

    private static readonly JsonSerializerOptions Json = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ActivityLog? _activity;
    private readonly Func<string?> _folder;
    private readonly List<EdlLogEntry> _entries = new();
    private readonly object _gate = new();

    // Appends run one after another, off the caller's thread: the window logs from its UI thread,
    // and a slow disk must not be felt there.
    private Task _writing = Task.CompletedTask;

    /// <summary>
    /// Appends that may wait on the disk at once. Each holds its whole entry, so a disk that has
    /// stopped answering would otherwise be an entry kept in memory for every edit made since.
    /// </summary>
    public const int PendingLimit = 2000;

    private int _pending;
    private long _notWritten;

    /// <summary>Entries not written to the file because too many were already waiting. Still in the activity record.</summary>
    public long NotWritten => Interlocked.Read(ref _notWritten);

    public EdlLogger(ActivityLog? activity = null, Func<string?>? folder = null)
    {
        _activity = activity;
        _folder = folder ?? (static () => null);
    }

    /// <summary>What the locks are doing, asked for every entry that does not say itself.</summary>
    public Func<string> LockState { get; set; } = static () => "";

    public event Action<EdlLogEntry>? Logged;

    public IReadOnlyList<EdlLogEntry> Entries
    {
        get { lock (_gate) return _entries.ToArray(); }
    }

    public EdlLogEntry Write(EdlLogEntry entry)
    {
        entry = entry with
        {
            Timestamp = entry.Timestamp == default ? DateTimeOffset.Now : entry.Timestamp,
            OperationId = entry.OperationId.Length > 0 ? entry.OperationId : Guid.NewGuid().ToString("N"),
            LockState = entry.LockState.Length > 0 ? entry.LockState : SafeLockState(),
        };

        lock (_gate)
        {
            _entries.Add(entry);
            if (_entries.Count > Keep) _entries.RemoveRange(0, _entries.Count - Keep);
        }

        string json = ToJson(entry);

        _activity?.Write(LogSource.LiveEdit, LevelOf(entry), $"EDL  {entry.Message}",
                         @event: $"edl.{JsonNamingPolicy.CamelCase.ConvertName(entry.Action.ToString())}",
                         correlation: entry.EditId ?? entry.TransactionId, detail: json);

        if (_folder() is { } folder) Append(folder, entry.Timestamp, json);

        foreach (Delegate handler in Logged?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<EdlLogEntry>)handler).Invoke(entry); }
            catch { /* a display is not worth an edit */ }
        }

        return entry;
    }

    public static string ToJson(EdlLogEntry entry) => JsonSerializer.Serialize(entry, Json);

    /// <summary>Waits for every append so far to reach the disk. For tests and for closing.</summary>
    public void Flush(TimeSpan timeout)
    {
        Task pending;
        lock (_gate) pending = _writing;

        try { pending.Wait(timeout); }
        catch (AggregateException) { }
    }

    private void Append(string folder, DateTimeOffset at, string json)
    {
        string path = Path.Combine(folder, $"edl-{at:yyyyMMdd}.jsonl");

        if (Interlocked.Increment(ref _pending) > PendingLimit)
        {
            Interlocked.Decrement(ref _pending);
            Interlocked.Increment(ref _notWritten);
            return;
        }

        lock (_gate)
        {
            _writing = _writing.ContinueWith(_ =>
            {
                try { File.AppendAllText(path, json + Environment.NewLine); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
                finally { Interlocked.Decrement(ref _pending); }
            }, TaskScheduler.Default);
        }
    }

    private string SafeLockState()
    {
        try { return LockState(); }
        catch { return ""; }
    }

    private static LogLevel LevelOf(EdlLogEntry entry) => entry.Action switch
    {
        EdlAction.OperationFailed => LogLevel.Error,
        EdlAction.PlaybackRejected => LogLevel.Warn,
        EdlAction.PlaybackAcknowledged or EdlAction.EditCreated or EdlAction.ConformStarted => LogLevel.Ok,
        _ => LogLevel.Info,
    };
}
