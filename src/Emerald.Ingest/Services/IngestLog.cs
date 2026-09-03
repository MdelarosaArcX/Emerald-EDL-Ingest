namespace Emerald.Ingest;

public enum IngestLogLevel { Info, Ok, Warn, Error }

public sealed record IngestLogEntry(DateTime At, string Message, IngestLogLevel Level)
{
    public string Time => At.ToString("HH:mm:ss");
}

/// <summary>
/// Where the ingest services say what they are doing.
///
/// Emerald's convention is that a module raises its narration as an event and whichever
/// window owns it puts that on screen — <see cref="Emerald.Video.SdiCapture.Message"/>
/// works exactly this way. The ingest services follow it rather than reaching for a
/// logging framework the rest of the solution does not use, so the operator's activity log
/// and the record of what happened are the same thing.
/// </summary>
public interface IIngestLog
{
    void Write(string message, IngestLogLevel level = IngestLogLevel.Info);

    /// <summary>Raised on whichever thread wrote the entry; a UI subscriber must marshal.</summary>
    event Action<IngestLogEntry>? Entry;
}

public sealed class IngestLog : IIngestLog
{
    public event Action<IngestLogEntry>? Entry;

    public void Write(string message, IngestLogLevel level = IngestLogLevel.Info) =>
        Entry?.Invoke(new IngestLogEntry(DateTime.Now, message, level));
}
