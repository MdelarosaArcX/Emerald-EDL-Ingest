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

    /// <summary>
    /// Says it twice: to whoever built this log, as it always has, and to the application
    /// record.
    ///
    /// The second one matters because this object does not outlive the window. Toggling
    /// SIMULATE rebuilds the controller and with it this log, and the whole ingest history
    /// went with it — even though the jobs themselves are in a database. Now the narration
    /// survives the rebuild, the window closing, and the process exiting.
    /// </summary>
    public void Write(string message, IngestLogLevel level = IngestLogLevel.Info)
    {
        Emerald.Core.ActivityLog.Shared.Write(
            Emerald.Core.LogSource.Ingest,
            level switch
            {
                IngestLogLevel.Ok => Emerald.Core.LogLevel.Ok,
                IngestLogLevel.Warn => Emerald.Core.LogLevel.Warn,
                IngestLogLevel.Error => Emerald.Core.LogLevel.Error,
                _ => Emerald.Core.LogLevel.Info,
            },
            message);

        Entry?.Invoke(new IngestLogEntry(DateTime.Now, message, level));
    }
}
