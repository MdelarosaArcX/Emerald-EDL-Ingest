using Emerald.Core;

namespace Emerald.Ingest.Tests;

/// <summary>A clock that says exactly what the test told it to say.</summary>
internal sealed class FakeClock : IIngestClock
{
    private Timecode _now;

    public FakeClock(string timecode = "20:00:00:00", int rate = 25, bool available = true)
    {
        FrameRate = rate;
        Available = available;
        Timecode.TryParse(timecode, rate, out _now, out _);
    }

    public bool Available { get; set; }
    public int FrameRate { get; }
    public bool IsLocked => Available;
    public bool IsMock => true;
    public string StatusText => Available ? $"FAKE - LOCKED - {FrameRate} fps" : "offline";

    public void Set(string timecode) => Timecode.TryParse(timecode, FrameRate, out _now, out _);

    public bool TryGetCurrent(out Timecode now)
    {
        now = _now;
        return Available;
    }
}

/// <summary>The job store, without a file.</summary>
internal sealed class InMemoryStore : IIngestStore
{
    public List<IngestJob> Jobs { get; } = new();
    public List<IngestRecording> Recordings { get; } = new();

    public void Initialise() { }

    public void Save(IngestJob job)
    {
        if (!Jobs.Any(j => j.Id == job.Id)) Jobs.Add(job);
    }

    public void SaveRecording(IngestRecording recording)
    {
        Recordings.RemoveAll(r => r.Id == recording.Id);
        Recordings.Add(recording);
    }

    public IReadOnlyList<IngestJob> LoadUnfinished() =>
        Jobs.Where(j => !IngestStatusRules.IsTerminal(j.Status)).ToList();

    public IReadOnlyList<IngestJob> History(int limit = 200) =>
        Jobs.Where(j => IngestStatusRules.IsTerminal(j.Status)).Take(limit).ToList();

    public IReadOnlyList<IngestRecording> RecentRecordings(int limit = 20) =>
        Recordings.Where(r => r.Status == IngestStatus.Completed).Take(limit).ToList();

    public IReadOnlyList<IngestRecording> RecordingsFor(Guid jobId) =>
        Recordings.Where(r => r.IngestJobId == jobId).ToList();

    public bool ClipNameTaken(string directory, string clipName, Guid exceptJobId) =>
        Jobs.Any(j => j.Id != exceptJobId && j.ClipName == clipName && j.Directory == directory
                      && !IngestStatusRules.IsTerminal(j.Status));
}

/// <summary>A registrar that reports whatever the test wants, without touching a disk.</summary>
internal sealed class StubRegistrar : IIngestMediaRegistrar
{
    public IngestStatus Outcome { get; set; } = IngestStatus.Completed;

    public IngestRecording Register(IngestJob job, IngestRecorderResult result, Timecode startTimecode, bool cancelled)
        => new()
        {
            IngestJobId = job.Id,
            ActualStartTimecode = startTimecode.ToString(),
            ActualEndTimecode = startTimecode.AddWrapping(result.Frames).ToString(),
            FilePath = result.MasterPath ?? "",
            Frames = result.Frames,
            FrameRate = job.FrameRate,
            Status = cancelled ? IngestStatus.Cancelled : Outcome,
        };
}

/// <summary>A recorder that records nothing and finishes when the test says so.</summary>
internal sealed class StubRecorder : IIngestRecorder
{
    public static readonly List<StubRecorder> Created = new();

    public StubRecorder() => Created.Add(this);

    public string? Problem { get; set; }
    public bool Started { get; private set; }
    public bool Stopped { get; private set; }
    public bool Disposed { get; private set; }
    public IngestJob? Job { get; private set; }

    public bool IsMock => true;
    public bool IsRunning => Started && !Stopped;
    public long FramesRecorded { get; set; }

    public event Action<string, IngestLogLevel>? Message;
    public event Action<IngestRecorderResult>? Finished;

    public bool TryStart(IngestJob job, out string? problem)
    {
        Job = job;
        problem = Problem;

        if (Problem is not null) return false;

        Started = true;
        Message?.Invoke("stub recorder rolling", IngestLogLevel.Info);
        return true;
    }

    public void Stop() => Stopped = true;

    /// <summary>Reports the recording as over, the way a real recorder does when ffmpeg exits.</summary>
    public void Complete(bool ranToLength = true, string? error = null) =>
        Finished?.Invoke(new IngestRecorderResult(
            Job!.Id, ranToLength, FramesRecorded, "master.mov", "proxy.mp4", error));

    public void Dispose() => Disposed = true;
}
