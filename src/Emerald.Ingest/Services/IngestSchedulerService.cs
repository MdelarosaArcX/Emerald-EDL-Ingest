using System.Collections.Concurrent;
using System.IO;
using Emerald.Core;

namespace Emerald.Ingest;

/// <summary>
/// Holds the queue and decides when each job rolls.
///
/// The one thing this class exists to get right is <i>when</i>. Everything else — opening
/// the receiver, encoding, naming files, reading them back — belongs to something that
/// already existed. What did not exist is a component that watches the station clock and
/// puts a receiver into record on the frame an operator asked for, several jobs at a time,
/// across more than one board.
/// </summary>
public interface IIngestSchedulerService : IDisposable
{
    void Start();
    void Stop();

    /// <summary>Takes a validated job and arms it. The job is persisted before this returns.</summary>
    void Enqueue(IngestJob job);

    /// <summary>Stops a job, whether it is waiting or already recording. False if it is finished.</summary>
    bool Cancel(Guid jobId);

    /// <summary>Drops a cancelled or finished job off the queue display. Never touches a live one.</summary>
    bool Remove(Guid jobId);

    /// <summary>The queue as it stands, in the order jobs will run.</summary>
    IReadOnlyList<IngestJob> Snapshot();

    /// <summary>The job currently recording on a receiver, when there is one.</summary>
    IngestJob? RecordingOn(uint boardIndex, int portIndex);

    /// <summary>Raised on the scheduler thread whenever a job's state moves.</summary>
    event Action<IngestJob>? JobChanged;

    /// <summary>Raised on the scheduler thread when the queue's membership changes.</summary>
    event Action? QueueChanged;
}

public sealed class IngestSchedulerService : IIngestSchedulerService
{
    /// <summary>Half a frame at 25 fps, so a start is never missed by a whole one.</summary>
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Inside this much of the start time, the timecode clock takes over from the wall clock
    /// and the decision becomes frame-accurate. Outside it, the wall clock is what tells a
    /// job twenty hours away from one four hours missed — a 24-hour timecode cannot.
    /// </summary>
    private static readonly TimeSpan FrameAccurateWindow = TimeSpan.FromSeconds(3);

    /// <summary>
    /// How late a start may be and still be taken. Beyond this the preroll is gone and the
    /// clip would silently not be the one that was ordered, so the job fails instead.
    /// </summary>
    private const double MissedAfterSeconds = 5.0;

    /// <summary>Free space below which the operator is warned, and below which a record is stopped.</summary>
    private const long LowSpaceBytes = 5L * 1024 * 1024 * 1024;
    private const long CriticalSpaceBytes = 1L * 1024 * 1024 * 1024;

    private readonly IIngestClock _clock;
    private readonly Func<IIngestRecorder> _newRecorder;
    private readonly IIngestStore _store;
    private readonly ITimecodeCalculationService _calc;
    private readonly IIngestMediaRegistrar _registrar;
    private readonly IIngestLog _log;

    private readonly object _gate = new();
    private readonly List<IngestJob> _jobs = new();
    private readonly Dictionary<Guid, Running> _running = new();
    private readonly ConcurrentQueue<IngestRecorderResult> _completions = new();
    private readonly HashSet<Guid> _cancelling = new();

    private CancellationTokenSource? _cts;
    private Task? _loop;

    /// <summary>Throttles the "no timecode" complaint, which would otherwise fire fifty times a second.</summary>
    private DateTime _lastClockComplaint = DateTime.MinValue;

    private DateTime _lastDiskCheck = DateTime.MinValue;

    public IngestSchedulerService(
        IIngestClock clock,
        Func<IIngestRecorder> recorderFactory,
        IIngestStore store,
        IIngestMediaRegistrar registrar,
        IIngestLog log,
        ITimecodeCalculationService? calculator = null)
    {
        _clock = clock;
        _newRecorder = recorderFactory;
        _store = store;
        _registrar = registrar;
        _log = log;
        _calc = calculator ?? TimecodeCalculationService.Instance;
    }

    public event Action<IngestJob>? JobChanged;
    public event Action? QueueChanged;

    // ------------------------------------------------------------------ lifecycle

    public void Start()
    {
        if (_loop is not null) return;

        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => RunAsync(_cts.Token));
        _log.Write("Ingest scheduler started.");
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _loop?.Wait(TimeSpan.FromSeconds(3)); } catch { /* shutting down */ }
        _cts?.Dispose();
        _cts = null;
        _loop = null;
    }

    // ------------------------------------------------------------------ queue

    public void Enqueue(IngestJob job)
    {
        lock (_gate)
        {
            if (_jobs.Any(j => j.Id == job.Id)) return;
            _jobs.Add(job);
        }

        if (job.Status == IngestStatus.Created) Move(job, IngestStatus.Scheduled);
        else _store.Save(job);

        _log.Write($"Ingest {Short(job)} scheduled: {job.ClipName} on board {job.BoardIndex} {job.Port}, " +
                   $"rolls {job.ActualStartTimecode}, records {job.RecordedLength}.");

        QueueChanged?.Invoke();
    }

    public bool Cancel(Guid jobId)
    {
        IngestJob? job;
        Running? running = null;

        lock (_gate)
        {
            job = _jobs.FirstOrDefault(j => j.Id == jobId);
            if (job is null || IngestStatusRules.IsTerminal(job.Status)) return false;

            if (_running.TryGetValue(jobId, out Running? r))
            {
                running = r;
                _cancelling.Add(jobId);
            }
        }

        if (running is not null)
        {
            // The recording is stopped here; the job is not marked Cancelled until the
            // recorder reports back, so the status always matches what is on disk.
            _log.Write($"Ingest {Short(job)} cancelling - stopping the recording.", IngestLogLevel.Warn);
            running.Recorder.Stop();
            return true;
        }

        Move(job, IngestStatus.Cancelled, "Cancelled by the operator.");
        _log.Write($"Ingest {Short(job)} cancelled before it rolled.", IngestLogLevel.Warn);
        QueueChanged?.Invoke();
        return true;
    }

    public bool Remove(Guid jobId)
    {
        lock (_gate)
        {
            IngestJob? job = _jobs.FirstOrDefault(j => j.Id == jobId);

            // A job that has not finished is never dropped off the queue: losing a booked
            // ingest quietly is the failure this module is least allowed to have.
            if (job is null || !IngestStatusRules.IsTerminal(job.Status)) return false;

            _jobs.Remove(job);
        }

        QueueChanged?.Invoke();
        return true;
    }

    public IReadOnlyList<IngestJob> Snapshot()
    {
        lock (_gate)
        {
            return _jobs
                .OrderBy(j => IngestStatusRules.IsTerminal(j.Status))
                .ThenBy(j => j.ScheduledAt ?? DateTime.MaxValue)
                .ThenBy(j => j.CreatedAt)
                .ToList();
        }
    }

    public IngestJob? RecordingOn(uint boardIndex, int portIndex)
    {
        lock (_gate)
        {
            return _running.Values
                .Select(r => r.Job)
                .FirstOrDefault(j => j.BoardIndex == boardIndex && j.PortIndex == portIndex);
        }
    }

    // ------------------------------------------------------------------ the loop

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                DrainCompletions();
                EvaluateWaiting();
                UpdateRunning();
            }
            catch (Exception ex)
            {
                // The scheduler thread never dies: a queue that stopped being looked at is
                // indistinguishable, from the operator's side, from a queue that is empty.
                _log.Write($"Ingest scheduler error: {ex.Message}", IngestLogLevel.Error);
            }

            try { await Task.Delay(Tick, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private void EvaluateWaiting()
    {
        List<IngestJob> pending;

        lock (_gate)
            pending = _jobs.Where(j => j.Status is IngestStatus.Scheduled or IngestStatus.Waiting).ToList();

        if (pending.Count == 0) return;

        bool haveClock = _clock.TryGetCurrent(out Timecode now);

        if (!haveClock)
        {
            if (DateTime.Now - _lastClockComplaint > TimeSpan.FromSeconds(30))
            {
                _lastClockComplaint = DateTime.Now;
                _log.Write($"{pending.Count} ingest job(s) waiting, but there is no realtime timecode to " +
                           "start them against.", IngestLogLevel.Warn);
            }

            return;
        }

        foreach (IngestJob job in pending)
        {
            if (job.Status == IngestStatus.Scheduled) Move(job, IngestStatus.Waiting);

            switch (Due(job, now))
            {
                case Readiness.Waiting:
                    break;

                case Readiness.Due:
                    TryRoll(job, now);
                    break;

                case Readiness.Missed:
                    Move(job, IngestStatus.Failed,
                         $"Start timecode {job.ActualStartTimecode} passed before the recording could roll.");
                    _log.Write($"Ingest {Short(job)} FAILED - {job.ErrorMessage}", IngestLogLevel.Error);
                    QueueChanged?.Invoke();
                    break;
            }
        }
    }

    private enum Readiness { Waiting, Due, Missed }

    /// <summary>
    /// Whether a job should roll now.
    ///
    /// Two clocks, deliberately. The wall clock says which day the start belongs to, which a
    /// timecode that repeats every 24 hours cannot; the timecode then says which frame. Only
    /// inside <see cref="FrameAccurateWindow"/> does the timecode get a vote, so a job booked
    /// for tomorrow at the same time as one that was missed this morning is never confused
    /// with it.
    /// </summary>
    private Readiness Due(IngestJob job, Timecode now)
    {
        if (job.ScheduledAt is { } scheduled)
        {
            TimeSpan away = scheduled - DateTime.Now;
            if (away > FrameAccurateWindow) return Readiness.Waiting;
            if (away.TotalSeconds < -MissedAfterSeconds) return Readiness.Missed;
        }

        int rate = job.FrameRate > 0 ? job.FrameRate : 25;
        long late = FramesLate(job, now);

        if (late < 0) return Readiness.Waiting;
        return late > (long)(MissedAfterSeconds * rate) ? Readiness.Missed : Readiness.Due;
    }

    /// <summary>
    /// How many frames have passed since the job's start timecode. Negative while the start
    /// is still ahead. Folded to the nearer way round the day, so a start twenty-three hours
    /// "ahead" reads as an hour behind, which is what it is.
    /// </summary>
    private long FramesLate(IngestJob job, Timecode now)
    {
        int rate = job.FrameRate > 0 ? job.FrameRate : 25;
        long perDay = 24L * 3600L * rate;

        long forward = _calc.FramesUntil(now, job.ActualStart);
        long signed = forward <= perDay / 2 ? forward : forward - perDay;

        return -signed;
    }

    private void TryRoll(IngestJob job, Timecode now)
    {
        // One recording per receiver. The card enforces this too, through RxLease, but the
        // scheduler should say "RX1 is already recording ingest 4f2a" rather than let the
        // operator read a lease error and wonder which job took it.
        if (RecordingOn(job.BoardIndex, job.PortIndex) is { } holder)
        {
            Move(job, IngestStatus.Failed,
                 $"Board {job.BoardIndex} {job.Port} is already recording {holder.ClipName}.");
            _log.Write($"Ingest {Short(job)} FAILED - {job.ErrorMessage}", IngestLogLevel.Error);
            QueueChanged?.Invoke();
            return;
        }

        IIngestRecorder recorder = _newRecorder();
        recorder.Message += (text, level) => _log.Write(text, level);
        recorder.Finished += result => _completions.Enqueue(result);

        if (!recorder.TryStart(job, out string? problem))
        {
            recorder.Dispose();
            Move(job, IngestStatus.Failed, problem ?? "The recording could not be started.");
            _log.Write($"Ingest {Short(job)} FAILED - {job.ErrorMessage}", IngestLogLevel.Error);
            QueueChanged?.Invoke();
            return;
        }

        long late = FramesLate(job, now);

        lock (_gate)
        {
            _running[job.Id] = new Running(
                job, recorder, job.ActualStart,
                deadline: DateTime.Now
                          + TimeSpan.FromSeconds(job.RecordedLengthFrames / (double)Math.Max(1, job.FrameRate))
                          + TimeSpan.FromSeconds(30));
        }

        job.StartedAt = DateTime.Now;
        job.Mock = recorder.IsMock;
        Move(job, IngestStatus.Recording);

        _log.Write($"Ingest {Short(job)} RECORDING - {job.ClipName} from board {job.BoardIndex} {job.Port}, " +
                   $"rolled at {now}, running {job.RecordedLength}.", IngestLogLevel.Ok);

        // Reported rather than smoothed over: a start a few frames behind the mark is a
        // clip a few frames short of its preroll, and the operator decides whether that is
        // acceptable, not this scheduler.
        if (late > 1)
            _log.Write($"    rolled {late} frame(s) after {job.ActualStartTimecode}.", IngestLogLevel.Warn);

        QueueChanged?.Invoke();
    }

    private void UpdateRunning()
    {
        List<Running> running;
        lock (_gate) running = _running.Values.ToList();

        if (running.Count == 0) return;

        foreach (Running r in running)
        {
            r.Job.FramesRecorded = r.Recorder.FramesRecorded;

            // A recorder that is still going well past its own end has stopped counting
            // frames - a receiver that lost lock, an encoder that wedged. Ending it here is
            // what stops a wedged ingest from holding a receiver for the rest of the day.
            if (r.Recorder.IsRunning && DateTime.Now > r.Deadline)
            {
                _log.Write($"Ingest {Short(r.Job)} has run past its end without finishing - stopping it.",
                           IngestLogLevel.Error);
                r.Recorder.Stop();
            }
        }

        if (DateTime.Now - _lastDiskCheck < TimeSpan.FromSeconds(5)) return;
        _lastDiskCheck = DateTime.Now;

        foreach (Running r in running)
        {
            long? free = DiskSpace.AvailableBytes(r.Job.Directory);
            if (free is not { } bytes) continue;

            if (bytes < CriticalSpaceBytes)
            {
                _log.Write($"Ingest {Short(r.Job)}: only {DiskSpace.Describe(bytes)} left on " +
                           $"{r.Job.Directory} - stopping the recording before the disk fills.",
                           IngestLogLevel.Error);

                lock (_gate) _cancelling.Remove(r.Job.Id);   // this is a failure, not a cancel
                r.DiskExhausted = true;
                r.Recorder.Stop();
            }
            else if (bytes < LowSpaceBytes && !r.WarnedLowSpace)
            {
                r.WarnedLowSpace = true;
                _log.Write($"Ingest {Short(r.Job)}: {DiskSpace.Describe(bytes)} left on {r.Job.Directory}.",
                           IngestLogLevel.Warn);
            }
        }
    }

    /// <summary>
    /// Finishes off recordings that have reported back.
    ///
    /// This runs on the scheduler thread rather than in the recorder's own callback for a
    /// concrete reason: disposing a recorder waits for the very thread that raised the
    /// event, so finalising inline would deadlock on the first completed ingest.
    /// </summary>
    private void DrainCompletions()
    {
        while (_completions.TryDequeue(out IngestRecorderResult? result))
        {
            Running? running;
            bool cancelled;

            lock (_gate)
            {
                _running.Remove(result.JobId, out running);
                cancelled = _cancelling.Remove(result.JobId);
            }

            if (running is null) continue;

            IngestJob job = running.Job;
            job.FramesRecorded = result.Frames;

            try { running.Recorder.Dispose(); } catch { /* already torn down */ }

            IngestRecording recording = _registrar.Register(job, result, running.StartTimecode, cancelled);
            _store.SaveRecording(recording);

            job.FilePath = recording.FilePath.Length > 0 ? recording.FilePath : null;
            job.ProxyPath = recording.ProxyPath;
            job.FileSize = recording.FileSize;
            job.CompletedAt = DateTime.Now;

            if (running.DiskExhausted)
            {
                Move(job, IngestStatus.Failed,
                     $"Stopped after {new Timecode(result.Frames, job.FrameRate)}: the disk ran out of room.");
                _log.Write($"Ingest {Short(job)} FAILED - {job.ErrorMessage}", IngestLogLevel.Error);
            }
            else if (cancelled)
            {
                Move(job, IngestStatus.Cancelled,
                     $"Cancelled after {new Timecode(result.Frames, job.FrameRate)}.");
                _log.Write($"Ingest {Short(job)} CANCELLED - {job.ErrorMessage}", IngestLogLevel.Warn);
            }
            else if (recording.Status == IngestStatus.Completed)
            {
                Move(job, IngestStatus.Completed);
                _log.Write($"Ingest {Short(job)} COMPLETED - {Path.GetFileName(recording.FilePath)}, " +
                           $"{recording.Length} recorded, {recording.SizeText}.", IngestLogLevel.Ok);
                _log.Write($"    registered with the media store: {recording.FilePath}");
            }
            else
            {
                Move(job, IngestStatus.Failed, recording.ErrorMessage ?? "The recording did not complete.");
                _log.Write($"Ingest {Short(job)} FAILED - {job.ErrorMessage}", IngestLogLevel.Error);
            }

            QueueChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ state

    /// <summary>
    /// The only way a job's status changes. Every move is checked against the state machine
    /// and written to the store before anything is told about it, so what is on screen and
    /// what would survive a crash are never different things.
    /// </summary>
    private void Move(IngestJob job, IngestStatus to, string? error = null)
    {
        IngestStatusRules.EnsureCanTransition(job.Status, to);

        job.Status = to;
        if (error is not null) job.ErrorMessage = error;

        if (to == IngestStatus.Recording) job.StartedAt ??= DateTime.Now;
        if (IngestStatusRules.IsTerminal(to)) job.CompletedAt ??= DateTime.Now;

        _store.Save(job);
        JobChanged?.Invoke(job);
    }

    private static string Short(IngestJob job) => job.Id.ToString("N")[..8];

    public void Dispose()
    {
        Stop();

        List<Running> running;
        lock (_gate)
        {
            running = _running.Values.ToList();
            _running.Clear();
        }

        foreach (Running r in running)
        {
            try { r.Recorder.Dispose(); } catch { /* shutting down */ }
        }
    }

    /// <summary>One job with a receiver open.</summary>
    private sealed class Running
    {
        public Running(IngestJob job, IIngestRecorder recorder, Timecode startTimecode, DateTime deadline)
        {
            Job = job;
            Recorder = recorder;
            StartTimecode = startTimecode;
            Deadline = deadline;
        }

        public IngestJob Job { get; }
        public IIngestRecorder Recorder { get; }
        public Timecode StartTimecode { get; }

        /// <summary>Wall-clock time past which the recording is considered stuck.</summary>
        public DateTime Deadline { get; }

        public bool WarnedLowSpace { get; set; }
        public bool DiskExhausted { get; set; }
    }
}
