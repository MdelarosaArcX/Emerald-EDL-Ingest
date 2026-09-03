using System.IO;
using Emerald.Core;
using Emerald.Video;

namespace Emerald.Ingest;

/// <summary>
/// The Ingest Controller itself: the one thing the UI talks to.
///
/// Everything an ingest needs already exists somewhere in Emerald — the clock in
/// Emerald.Core, the receivers in Emerald.Deltacast, the encoder in Emerald.Video, the
/// store in Emerald.Media. What did not exist is something that checks an operator's
/// intent, turns it into a job, keeps that job safe across a restart, and hands it to the
/// scheduler at the right moment. That is all this is.
///
/// No view ever reaches past this to a board or an encoder.
/// </summary>
public interface IIngestControllerService : IDisposable
{
    IIngestSchedulerService Scheduler { get; }
    IIngestClock Clock { get; }
    IIngestHardware Hardware { get; }
    IClipNameService ClipNames { get; }
    ITimecodeCalculationService Timecodes { get; }
    IIngestLog Log { get; }

    /// <summary>Opens the store, recovers anything the last run left behind, starts the scheduler.</summary>
    void Initialise();

    /// <summary>Checks a request without acting on it. This is what the form calls as you type.</summary>
    IngestValidation Validate(IngestRequest request);

    /// <summary>Validates, persists and queues. The returned validation carries the job when it worked.</summary>
    IngestValidation StartIngest(IngestRequest request);

    bool Cancel(Guid jobId);
    bool Remove(Guid jobId);

    IReadOnlyList<IngestJob> Queue();
    IReadOnlyList<IngestJob> History(int limit = 200);
    IReadOnlyList<IngestRecording> RecentRecordings(int limit = 20);
    IReadOnlyList<IngestRecording> RecordingsFor(Guid jobId);
}

public sealed class IngestControllerService : IIngestControllerService
{
    /// <summary>A duration longer than this is almost certainly a typo, and is refused.</summary>
    private static readonly TimeSpan MaxDuration = TimeSpan.FromHours(12);

    /// <summary>Beyond this, a duration is accepted but said out loud.</summary>
    private static readonly TimeSpan LongDuration = TimeSpan.FromHours(2);

    /// <summary>Room that should still be free after the recording has been written.</summary>
    private const long HeadroomBytes = 5L * 1024 * 1024 * 1024;

    private readonly AppSettings _settings;
    private readonly IIngestStore _store;

    public IngestControllerService(
        AppSettings settings,
        IIngestClock clock,
        IIngestHardware hardware,
        IIngestStore? store = null,
        IIngestLog? log = null,
        IClipNameService? clipNames = null,
        ITimecodeCalculationService? timecodes = null,
        Func<IIngestRecorder>? recorderFactory = null,
        IIngestMediaRegistrar? registrar = null)
    {
        _settings = settings;
        Clock = clock;
        Hardware = hardware;
        Log = log ?? new IngestLog();
        ClipNames = clipNames ?? new ClipNameService();
        Timecodes = timecodes ?? TimecodeCalculationService.Instance;
        _store = store ?? new SqliteIngestStore();

        // Mock hardware records through the simulated recorder, real hardware through
        // Emerald.Video. The pairing is decided here rather than at each call site, so there
        // is no way to end up asking a card that is not there to record.
        Func<IIngestRecorder> recorders = recorderFactory
            ?? (hardware.IsMock
                    ? () => new MockIngestRecorder()
                    : () => new SdiIngestRecorder(settings));

        Scheduler = new IngestSchedulerService(
            Clock, recorders, _store,
            registrar ?? new MediaLibraryRegistrar(settings, Log),
            Log, Timecodes);
    }

    public IIngestSchedulerService Scheduler { get; }
    public IIngestClock Clock { get; }
    public IIngestHardware Hardware { get; }
    public IClipNameService ClipNames { get; }
    public ITimecodeCalculationService Timecodes { get; }
    public IIngestLog Log { get; }

    // ------------------------------------------------------------------ startup

    public void Initialise()
    {
        _store.Initialise();
        Log.Write("Ingest Controller started.");

        if (Hardware.IsMock)
            Log.Write("Mock mode: boards and recordings are simulated. Nothing is recorded from a card.",
                      IngestLogLevel.Warn);

        Recover();
        Scheduler.Start();
    }

    /// <summary>
    /// Picks up whatever the last run left behind.
    ///
    /// A queued ingest that quietly vanished because Emerald restarted would be the worst
    /// failure this module could have, so nothing is dropped: a job still ahead of its start
    /// is re-armed, and a job whose moment passed while Emerald was not running is failed
    /// with a reason, not forgotten.
    /// </summary>
    private void Recover()
    {
        IReadOnlyList<IngestJob> unfinished = _store.LoadUnfinished();
        if (unfinished.Count == 0) return;

        Log.Write($"Recovering {unfinished.Count} unfinished ingest job(s) from the last session.");

        foreach (IngestJob job in unfinished)
        {
            if (job.Status == IngestStatus.Recording)
            {
                // Whatever is on disk is a partial clip. It is left exactly where it is, and
                // named as evidence rather than deleted, but the job cannot be resumed: the
                // frames between the restart and now are simply gone.
                job.Status = IngestStatus.Failed;
                job.ErrorMessage = "Interrupted: Emerald stopped while this ingest was recording.";
                job.CompletedAt ??= DateTime.Now;
                _store.Save(job);

                Log.Write($"Ingest {Short(job)} ({job.ClipName}) was recording when Emerald stopped - " +
                          "marked FAILED. Any partial file is still on disk.", IngestLogLevel.Error);
                continue;
            }

            DateTime? due = job.ScheduledAt;

            if (due is null || due <= DateTime.Now)
            {
                job.Status = IngestStatus.Failed;
                job.ErrorMessage = due is null
                    ? "Recovered without a start time; it could not be re-armed."
                    : $"Its start time ({job.ActualStartTimecode}) passed while Emerald was not running.";
                job.CompletedAt ??= DateTime.Now;
                _store.Save(job);

                Log.Write($"Ingest {Short(job)} ({job.ClipName}) - {job.ErrorMessage}", IngestLogLevel.Error);
                continue;
            }

            // Still ahead of itself. Back on the queue, in whatever state it was left in.
            if (job.Status == IngestStatus.Waiting) job.Status = IngestStatus.Scheduled;
            Scheduler.Enqueue(job);

            Log.Write($"Ingest {Short(job)} ({job.ClipName}) re-armed for {job.ActualStartTimecode}.",
                      IngestLogLevel.Ok);
        }
    }

    // ------------------------------------------------------------------ validation

    public IngestValidation Validate(IngestRequest request) => Build(request, out _);

    public IngestValidation StartIngest(IngestRequest request)
    {
        IngestValidation validation = Build(request, out _);

        if (!validation.IsValid || validation.Job is not { } job)
        {
            foreach (string message in validation.Messages) Log.Write(message, IngestLogLevel.Error);
            return validation;
        }

        foreach (string warning in validation.Warnings) Log.Write(warning, IngestLogLevel.Warn);

        Log.Write($"Ingest {Short(job)} created: {job.ClipName}");
        Log.Write($"    board     {job.BoardIndex}. {job.BoardName}   {job.Port}");
        Log.Write($"    reference {job.ReferenceTimecode}   som {job.Som}");
        Log.Write($"    rolls     {job.ActualStartTimecode}   eom {job.Eom}");
        Log.Write($"    duration  {job.Duration}   records {job.RecordedLength} in total");
        Log.Write($"    directory {job.Directory}");

        Scheduler.Enqueue(job);
        return validation;
    }

    /// <summary>
    /// Every check, in one pass, producing either a job or the reasons there is not one.
    ///
    /// It is deliberately one method. Validation split across a form, a view model and a
    /// service is validation that disagrees with itself, and the field that gets missed is
    /// always the one that mattered.
    /// </summary>
    private IngestValidation Build(IngestRequest request, out Timecode? scheduledStart)
    {
        var v = new IngestValidation();
        scheduledStart = null;

        // ---- hardware
        if (request.PortIndex < 0 || request.Port.Length == 0)
            v.Add(IngestFields.Port, "No RX port is selected.");

        if (request.BoardName.Length == 0)
            v.Add(IngestFields.Board, "No board is selected.");

        // ---- rate
        int rate = request.FrameRate > 0 ? request.FrameRate : Clock.FrameRate;
        if (rate <= 0)
        {
            v.Add(IngestFields.Reference, "The frame rate is unknown; the timecode source has not been read yet.");
            return v;
        }

        // ---- reference
        if (!Timecode.TryParse(request.ReferenceTimecode, rate, out Timecode reference, out string? refError))
            v.Add(IngestFields.Reference, $"Timecode: {refError}");

        // ---- SOM: the timecode the recorded file will carry at its first frame. It has no
        // bearing on when the recorder rolls; it only has to be a timecode.
        Timecode som = Timecode.Zero(rate);
        string somText = request.Som.Trim();

        if (somText.Length > 0 && !Timecode.TryParse(somText, rate, out som, out string? somError))
            v.Add(IngestFields.Som, $"SOM: {somError}");

        // ---- duration and EOM: one is typed, the other is derived.
        Timecode duration = Timecode.Zero(rate);
        Timecode eom = Timecode.Zero(rate);

        if (request.TimingMode == IngestTimingMode.EomControlsDuration)
        {
            if (!Timecode.TryParse(request.Eom, rate, out eom, out string? eomError))
                v.Add(IngestFields.Eom, $"EOM: {eomError}");
            else
                duration = Timecodes.CalculateDurationFromEom(reference, eom);
        }
        else
        {
            if (!Timecode.TryParse(request.Duration, rate, out duration, out string? durError))
                v.Add(IngestFields.Duration, $"Duration: {durError}");
            else
                eom = Timecodes.CalculateEomFromDuration(reference, duration);
        }

        if (v.For(IngestFields.Duration) is null && v.For(IngestFields.Eom) is null)
        {
            if (duration.TotalFrames <= 0)
            {
                v.Add(request.TimingMode == IngestTimingMode.EomControlsDuration
                          ? IngestFields.Eom
                          : IngestFields.Duration,
                      "The duration is zero; EOM must be later than the reference timecode.");
            }
            else if (duration.TotalSeconds > MaxDuration.TotalSeconds)
            {
                v.Add(IngestFields.Duration,
                      $"A duration of {duration} is longer than the {MaxDuration.TotalHours:F0} hour limit.");
            }
            else if (duration.TotalSeconds > LongDuration.TotalSeconds)
            {
                v.Warn($"This ingest runs for {duration}, which is a long recording - check the disk has room.");
            }
        }

        // The recorder rolls on the start timecode itself, and records for the duration.
        // SOM is a label on the file, not a preroll, so neither of these involves it.
        Timecode actualStart = reference;
        Timecode recordedLength = duration;

        // ---- clip name
        string clipName = request.ClipName.Trim();
        if (!ClipNames.IsValid(clipName, out string? nameError))
            v.Add(IngestFields.ClipName, nameError!);

        // ---- directory
        string directory = request.Directory.Trim();
        if (!DiskSpace.IsWritable(directory, out string? dirProblem))
            v.Add(IngestFields.Directory, dirProblem!);

        // ---- the clip must not already exist, on disk or in the queue
        var jobId = Guid.NewGuid();

        if (v.For(IngestFields.ClipName) is null && v.For(IngestFields.Directory) is null)
        {
            foreach (RecordingOutput output in RecordingProfile.Outputs)
            {
                string path = Path.Combine(RecordingProfile.FolderFor(output, directory),
                                           $"{clipName}.{output.Extension}");

                if (File.Exists(path))
                {
                    v.Add(IngestFields.ClipName, $"{path} already exists. Ingest never overwrites a clip.");
                    break;
                }
            }

            if (v.For(IngestFields.ClipName) is null && _store.ClipNameTaken(directory, clipName, jobId))
                v.Add(IngestFields.ClipName, $"An ingest of \"{clipName}\" is already queued for this directory.");
        }

        // ---- disk space
        if (v.For(IngestFields.Directory) is null && recordedLength.TotalFrames > 0)
        {
            long estimate = DiskSpace.EstimateBytes(
                recordedLength.TotalFrames, rate, RecordingProfile.From(_settings));

            v.EstimatedBytes = estimate;

            if (DiskSpace.AvailableBytes(directory) is { } free)
            {
                if (free < estimate)
                {
                    v.Add(IngestFields.Directory,
                          $"This ingest needs about {DiskSpace.Describe(estimate)} and only " +
                          $"{DiskSpace.Describe(free)} is free.");
                }
                else if (free - estimate < HeadroomBytes)
                {
                    v.Warn($"After this ingest about {DiskSpace.Describe(free - estimate)} will be left on disk.");
                }
            }
        }

        // ---- when, on the wall clock, this lands
        if (!Clock.TryGetCurrent(out Timecode now))
        {
            v.Add(IngestFields.Schedule,
                  "There is no realtime timecode; an ingest cannot be scheduled without a clock to run it against.");
        }
        else if (v.IsValid)
        {
            double secondsAway = Timecodes.FramesUntil(now, actualStart) / (double)rate;
            DateTime scheduledAt = DateTime.Now.AddSeconds(secondsAway);
            scheduledStart = actualStart;

            // Overlap on one receiver. The card would refuse the second claim anyway, but by
            // then the operator has lost the recording rather than been told in advance.
            IngestJob? clash = FindClash(request, scheduledAt, recordedLength, rate);

            if (clash is not null)
            {
                v.Add(IngestFields.Schedule,
                      $"Board {request.BoardIndex} {request.Port} is already booked by \"{clash.ClipName}\" " +
                      $"at {clash.ActualStartTimecode} for {clash.RecordedLength}.");
            }
            else
            {
                v.Job = new IngestJob
                {
                    Id = jobId,
                    ClipName = clipName,
                    BoardIndex = request.BoardIndex,
                    BoardName = request.BoardName,
                    Port = request.Port,
                    PortIndex = request.PortIndex,
                    FrameRate = rate,
                    ReferenceTimecode = reference.ToString(),
                    Som = som.ToString(),
                    Eom = eom.ToString(),
                    Duration = duration.ToString(),
                    ActualStartTimecode = actualStart.ToString(),
                    Directory = directory,
                    Metadata = request.Metadata.Trim(),
                    Status = IngestStatus.Created,
                    CreatedAt = DateTime.Now,
                    ScheduledAt = scheduledAt,
                    Mock = Hardware.IsMock,
                };

                if (secondsAway < 2)
                    v.Warn($"This ingest rolls in {secondsAway:F1} s - almost immediately.");
            }
        }

        return v;
    }

    /// <summary>
    /// A pending job on the same receiver whose recording window overlaps this one. Windows
    /// are compared on the wall clock, because that is the only axis on which "tomorrow at
    /// 20:57" and "today at 20:57" are different things.
    /// </summary>
    private IngestJob? FindClash(IngestRequest request, DateTime start, Timecode length, int rate)
    {
        DateTime end = start.AddSeconds(length.TotalFrames / (double)rate);

        return Scheduler.Snapshot()
            .Where(j => !IngestStatusRules.IsTerminal(j.Status))
            .Where(j => j.BoardIndex == request.BoardIndex && j.PortIndex == request.PortIndex)
            .FirstOrDefault(j =>
            {
                if (j.ScheduledAt is not { } theirStart) return false;

                DateTime theirEnd = theirStart.AddSeconds(
                    j.RecordedLengthFrames / (double)Math.Max(1, j.FrameRate));

                return start < theirEnd && theirStart < end;
            });
    }

    // ------------------------------------------------------------------ queue and history

    public bool Cancel(Guid jobId) => Scheduler.Cancel(jobId);
    public bool Remove(Guid jobId) => Scheduler.Remove(jobId);

    public IReadOnlyList<IngestJob> Queue() => Scheduler.Snapshot();
    public IReadOnlyList<IngestJob> History(int limit = 200) => _store.History(limit);
    public IReadOnlyList<IngestRecording> RecentRecordings(int limit = 20) => _store.RecentRecordings(limit);
    public IReadOnlyList<IngestRecording> RecordingsFor(Guid jobId) => _store.RecordingsFor(jobId);

    private static string Short(IngestJob job) => job.Id.ToString("N")[..8];

    public void Dispose() => Scheduler.Dispose();
}
