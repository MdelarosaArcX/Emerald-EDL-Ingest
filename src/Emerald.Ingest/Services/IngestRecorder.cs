using System.IO;
using System.Text;
using Emerald.Core;
using Emerald.Video;

namespace Emerald.Ingest;

/// <summary>How one ingest recording ended, as the recorder saw it.</summary>
public sealed record IngestRecorderResult(
    Guid JobId,
    bool RanToLength,
    long Frames,
    string? MasterPath,
    string? ProxyPath,
    string? Error);

/// <summary>
/// Records one receiver for one job.
///
/// The ingest controller does not own an encoder. This is a thin adapter onto
/// <see cref="SdiCapture"/> — the same recorder the capture deck and the EDL use, with the
/// same profile, so a clip ingested here is encoded exactly the way everything else in
/// Emerald is. What it adds is the two things an ingest needs and a continuous capture does
/// not: a single file with the operator's name on it, and a stop on an exact frame count.
/// </summary>
public interface IIngestRecorder : IDisposable
{
    bool IsMock { get; }
    bool IsRunning { get; }
    long FramesRecorded { get; }

    /// <summary>
    /// Rolls. Returns false, with <paramref name="problem"/> set, when the recording could
    /// not even be attempted — no ffmpeg, no such folder, a clip already on disk.
    /// </summary>
    bool TryStart(IngestJob job, out string? problem);

    /// <summary>Ends the recording early. <see cref="Finished"/> still fires.</summary>
    void Stop();

    /// <summary>Narration, on the recorder's own thread.</summary>
    event Action<string, IngestLogLevel>? Message;

    /// <summary>Raised once the encoder has closed its files. Never call Stop from here.</summary>
    event Action<IngestRecorderResult>? Finished;
}

/// <summary>The real recorder: Emerald.Video against a DELTACAST receiver.</summary>
public sealed class SdiIngestRecorder : IIngestRecorder
{
    private readonly AppSettings _settings;
    private readonly SdiCapture _capture = new();

    private IngestJob? _job;

    public SdiIngestRecorder(AppSettings settings)
    {
        _settings = settings;

        _capture.Message += (text, problem) =>
            Message?.Invoke(text, problem ? IngestLogLevel.Error : IngestLogLevel.Info);

        _capture.Finished += OnCaptureFinished;
    }

    public bool IsMock => false;
    public bool IsRunning => _capture.IsRunning;
    public long FramesRecorded => _capture.FramesRecorded;

    public event Action<string, IngestLogLevel>? Message;
    public event Action<IngestRecorderResult>? Finished;

    public bool TryStart(IngestJob job, out string? problem)
    {
        _job = job;

        if (!RecordingSetup.TryBuild(
                _settings, job.BoardIndex, job.PortIndex, job.Directory, job.FrameRate,
                job.ClipName, out CaptureRequest? request, out problem,
                frameLimit: job.RecordedLengthFrames, singleFile: true,
                // SOM is the media's own start timecode, so it goes in verbatim: the file's
                // first frame reads back as exactly what the operator typed. This is what
                // puts a tmcd track in the container at all, and it is why SOM is a label
                // rather than a time — the recording rolls at the start timecode either way.
                startTimecode: job.Som))
        {
            return false;
        }

        // The last line of defence before the encoder opens the file. The controller checked
        // this when the job was created, but a clip can appear in the meantime — another
        // ingest, another operator, a copy dropped into the folder — and overwriting one is
        // the one failure this module must never have.
        foreach (string path in request!.OutputPaths)
        {
            if (File.Exists(path))
            {
                problem = $"{path} already exists; an ingest never overwrites a clip.";
                return false;
            }
        }

        _capture.Start(request);
        problem = null;
        return true;
    }

    public void Stop() => _capture.Stop();

    private void OnCaptureFinished(CaptureResult result)
    {
        IngestJob? job = _job;
        if (job is null) return;

        IReadOnlyList<string> outputs = result.Request.OutputPaths;

        Finished?.Invoke(new IngestRecorderResult(
            JobId: job.Id,
            RanToLength: result.ReachedFrameLimit,
            Frames: result.Frames,
            // Proxy first, master second — the order RecordingProfile.Outputs is declared in.
            ProxyPath: outputs.Count > 0 ? outputs[0] : null,
            MasterPath: outputs.Count > 1 ? outputs[1] : null,
            Error: result.Error));
    }

    public void Dispose()
    {
        _capture.Finished -= OnCaptureFinished;
        _capture.Dispose();
    }
}

/// <summary>
/// A recording that never happens.
///
/// It advances a frame counter in real time, honours the same frame limit, and at the end
/// writes a small placeholder where each file would have been so that the verify-and-
/// register path downstream is exercised rather than skipped. The placeholder says in plain
/// text what it is, and every job recorded this way carries <see cref="IngestJob.Mock"/>,
/// so a simulated ingest can never be mistaken for a real one on disk or in the history.
/// </summary>
public sealed class MockIngestRecorder : IIngestRecorder
{
    /// <summary>Wall-clock seconds the simulation runs for, however long the job asks for.</summary>
    private const int MaxSimulatedSeconds = 20;

    private readonly object _gate = new();
    private CancellationTokenSource? _cts;
    private Task? _run;
    private long _frames;

    public bool IsMock => true;
    public bool IsRunning => _run is { IsCompleted: false };
    public long FramesRecorded => Interlocked.Read(ref _frames);

    public event Action<string, IngestLogLevel>? Message;
    public event Action<IngestRecorderResult>? Finished;

    public bool TryStart(IngestJob job, out string? problem)
    {
        problem = null;

        if (job.Directory.Length == 0 || !Directory.Exists(job.Directory))
        {
            problem = $"{job.Directory} does not exist.";
            return false;
        }

        IReadOnlyList<string> outputs = job.PlannedOutputs;

        foreach (string path in outputs)
        {
            if (File.Exists(path))
            {
                problem = $"{path} already exists; an ingest never overwrites a clip.";
                return false;
            }
        }

        Interlocked.Exchange(ref _frames, 0);

        lock (_gate)
        {
            _cts = new CancellationTokenSource();
            CancellationToken ct = _cts.Token;
            _run = Task.Run(() => Simulate(job, outputs, ct), ct);
        }

        return true;
    }

    public void Stop()
    {
        CancellationTokenSource? cts;
        lock (_gate) cts = _cts;

        cts?.Cancel();
    }

    private void Simulate(IngestJob job, IReadOnlyList<string> outputs, CancellationToken ct)
    {
        long wanted = job.RecordedLengthFrames;
        int rate = job.FrameRate > 0 ? job.FrameRate : 25;

        Message?.Invoke($"Simulated RX{job.PortIndex} locked to 1080p{rate}, recording " +
                        $"{new Timecode(wanted, rate)} to {job.Directory}", IngestLogLevel.Info);

        // A fifteen-minute ingest cannot take fifteen minutes to demonstrate, so the counter
        // is run fast when the job is long. The clip's recorded length is still the length
        // that was asked for; only the waiting is compressed.
        double speed = Math.Max(1.0, wanted / (double)(MaxSimulatedSeconds * rate));
        var started = System.Diagnostics.Stopwatch.StartNew();

        while (!ct.IsCancellationRequested)
        {
            long frames = Math.Min(wanted, (long)(started.Elapsed.TotalSeconds * rate * speed));
            Interlocked.Exchange(ref _frames, frames);

            if (frames >= wanted) break;
            Thread.Sleep(50);
        }

        bool ranToLength = !ct.IsCancellationRequested;
        long taken = Interlocked.Read(ref _frames);
        string? error = null;

        try
        {
            foreach (string path in outputs) WritePlaceholder(path, job, taken, rate);
        }
        catch (Exception ex)
        {
            error = $"Simulated recording could not write its placeholder: {ex.Message}";
        }

        Message?.Invoke(
            ranToLength
                ? $"Simulated recording complete: {new Timecode(taken, rate)}."
                : $"Simulated recording stopped early at {new Timecode(taken, rate)}.",
            ranToLength ? IngestLogLevel.Ok : IngestLogLevel.Warn);

        Finished?.Invoke(new IngestRecorderResult(
            JobId: job.Id,
            RanToLength: ranToLength,
            Frames: taken,
            ProxyPath: outputs.Count > 0 ? outputs[0] : null,
            MasterPath: outputs.Count > 1 ? outputs[1] : null,
            Error: error));
    }

    private static void WritePlaceholder(string path, IngestJob job, long frames, int rate)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var text = new StringBuilder()
            .AppendLine("EMERALD INGEST - SIMULATED RECORDING")
            .AppendLine("This file contains no video. It was written by the Ingest Controller")
            .AppendLine("in mock mode, where there is no DELTACAST board to record from.")
            .AppendLine()
            .AppendLine($"clip       {job.ClipName}")
            .AppendLine($"job        {job.Id}")
            .AppendLine($"board      {job.BoardIndex}. {job.BoardName}")
            .AppendLine($"port       {job.Port}")
            .AppendLine($"reference  {job.ReferenceTimecode}")
            .AppendLine($"som        {job.Som}")
            .AppendLine($"eom        {job.Eom}")
            .AppendLine($"duration   {job.Duration}")
            .AppendLine($"recorded   {new Timecode(frames, rate)}  ({frames} frames @ {rate} fps)")
            .AppendLine($"written    {DateTime.Now:yyyy-MM-dd HH:mm:ss}")
            .ToString();

        File.WriteAllText(path, text);
    }

    public void Dispose()
    {
        Stop();
        try { _run?.Wait(TimeSpan.FromSeconds(2)); } catch { /* shutting down */ }

        lock (_gate)
        {
            _cts?.Dispose();
            _cts = null;
        }
    }
}
