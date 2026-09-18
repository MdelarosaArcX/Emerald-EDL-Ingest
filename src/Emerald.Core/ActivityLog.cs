using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emerald.Core;

/// <summary>How loud a line is. The four every module already used, stated once.</summary>
public enum LogLevel { Info, Ok, Warn, Error }

/// <summary>
/// Which part of the plant said it.
///
/// A field rather than a prefix in the text. Roughly fifty messages already began with
/// "Capture:" or "Tidal lock " or "Audio " and no two modules agreed on the punctuation, so
/// the source could not be filtered on — only searched for, badly.
/// </summary>
public enum LogSource { App, Capture, Preview, TidalLock, Edl, Playout, Playback, Ingest, Media, Board }

/// <summary>
/// One line of the record.
///
/// Most of this is what any log carries. Four fields are here for reasons worth stating,
/// because without them the questions an operator actually asks cannot be answered:
///
/// <list type="bullet">
/// <item><b>Seq</b> — a wall clock is not an ordering key. Two threads inside one millisecond,
/// or an NTP step, and the capture deck and the playout engine interleave wrongly on screen.
/// This is the tie-break, and it is the only thing that makes "what happened first" answerable
/// at all.</item>
/// <item><b>Event</b> — a stable tag, so counting how many messages used a file is a comparison
/// and never a regular expression over prose that someone will later reword.</item>
/// <item><b>File</b> — media identity as a field, so the same question is a group-by.</item>
/// <item><b>Transient</b> — the playout engine reports once a second while a message runs.
/// Those belong on screen and not in the file, or the record is a heartbeat trace with the
/// events buried in it.</item>
/// </list>
/// </summary>
public sealed record LogLine(
    long Seq,
    DateTimeOffset At,
    string? Timecode,
    LogSource Source,
    LogLevel Level,
    string Message,
    string? Event = null,
    string? Correlation = null,
    string? File = null,
    string? Detail = null,
    bool Transient = false)
{
    /// <summary>Milliseconds included: two lines in the same second are common and ordered.</summary>
    [JsonIgnore]
    public string Time => At.ToString("HH:mm:ss.fff");

    [JsonIgnore]
    public string TimecodeText => Timecode ?? "--:--:--:--";

    [JsonIgnore]
    public string SourceText => Source.ToString().ToUpperInvariant();

    /// <summary>The file's bare name, which is what a column has room for.</summary>
    [JsonIgnore]
    public string FileText => File is null ? "" : Path.GetFileName(File);
}

/// <summary>
/// The one record of what Emerald did, from the receiver to the transmitter.
///
/// Every module used to narrate into a list of its own — the EDL kept 500 lines, the playback
/// deck 400, the Ingest Controller 500 and rebuilt them whenever SIMULATE was toggled, and the
/// capture deck kept <i>one</i>, in a label that the next message overwrote. None of it was
/// written down. Close a window and the record of what went to air ceased to exist, which for
/// an application that puts pictures on air is the wrong way round.
///
/// So the modules go on narrating exactly as they did — the convention that a service raises
/// what it is doing and somebody else displays it is a good one and is kept — but they narrate
/// <i>here</i>, and the windows subscribe. One line, one place, one order.
///
/// <b>This is written to from real-time loops.</b> The capture thread, the playout thread and
/// the ingest scheduler all call <see cref="Write"/>, and the playout thread is inside the loop
/// that pushes frames at a transmitter. So <see cref="Write"/> stamps, appends to a ring, hands
/// the line to a queue and returns; the disk is a different thread's problem entirely. A
/// synchronous file append from that loop would put disk latency between two frames and drop
/// them on air.
///
/// A class with a <see cref="Shared"/> instance rather than a static class, following
/// <see cref="TidalLock"/>: ordering, retention and rotation are exactly the things worth
/// testing, and a static cannot be stood up twice.
/// </summary>
public sealed class ActivityLog : IDisposable
{
    /// <summary>
    /// Lines kept in memory. Ten times what any one window kept, so opening the Monitor half an
    /// hour into a session shows the half hour — where opening the EDL used to show a blank panel.
    /// </summary>
    public const int RingSize = 5000;

    /// <summary>
    /// How many lines may be waiting for the disk before they start being dropped.
    ///
    /// A bound rather than none: if the writer wedges — a full disk, a network path that has
    /// gone away — an unbounded queue eats the process. Dropping the record is bad; taking the
    /// transmitter down with it is worse.
    /// </summary>
    public const int PendingLimit = 20_000;

    /// <summary>Roll to a new file past this, so no single day's file becomes unopenable.</summary>
    public const long MaxFileBytes = 32L * 1024 * 1024;

    /// <summary>A very long line is a mistake somewhere; keep the head of it and move on.</summary>
    public const int MaxDetailChars = 4096;

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// The application's one record. Windows join it; none of them owns it.
    ///
    /// Stamped from the station clock, so a line can be read against the timecode that was on
    /// air when it happened rather than against this machine's idea of the time. Every module
    /// already shares that clock through <see cref="TimecodeLink"/>; this shares it too.
    /// </summary>
    public static ActivityLog Shared { get; } =
        new(static () => TimecodeLink.Service.TryGetCurrent(out Timecode tc) ? tc : null);

    private readonly object _ringGate = new();
    private readonly Queue<LogLine> _ring = new(RingSize);

    private readonly ConcurrentQueue<LogLine> _pending = new();
    private readonly AutoResetEvent _wake = new(false);

    private readonly Func<Timecode?> _clock;

    private long _seq;
    private long _dropped;
    private int _pendingCount;

    private Thread? _writer;
    private CancellationTokenSource? _cts;
    private string? _folder;
    private int _retentionDays;

    // The house clock, sampled rather than asked. See SampleTimecode.
    private string? _timecodeCache;
    private long _timecodeSampledTicks;
    private static readonly long ClockSampleTicks = Stopwatch.Frequency / 10;   // 100 ms

    /// <param name="clock">
    /// Where the house timecode comes from. Left null, lines carry none — which is what a test
    /// wants, and what the truth is before the generator has been dialled.
    /// </param>
    public ActivityLog(Func<Timecode?>? clock = null) => _clock = clock ?? (static () => null);

    /// <summary>
    /// Raised for every line, on whichever thread wrote it — which may be the capture thread or
    /// the playout thread. A UI subscriber must marshal, and must not block.
    ///
    /// This is a long-lived object, so <b>a window that does not unsubscribe when it closes is
    /// leaked for the life of the application</b>. Same warning as
    /// <see cref="TimecodeLink.UrlChanged"/>, same reason.
    /// </summary>
    public event Action<LogLine>? Line;

    /// <summary>Lines dropped because the disk could not keep up. Reported, never hidden.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Where the record is being written, or null when it is memory only.</summary>
    public string? Folder => _folder;

    // ------------------------------------------------------------------ writing

    /// <summary>
    /// Records one line and returns it.
    ///
    /// Never blocks, never throws, never touches the disk. Safe from any thread, including the
    /// ones that are holding a transmitter up.
    /// </summary>
    public LogLine Write(
        LogSource source,
        LogLevel level,
        string message,
        string? @event = null,
        string? correlation = null,
        string? file = null,
        string? detail = null,
        bool transient = false)
    {
        string? timecode = SampleTimecode();
        string? trimmed = Trim(detail);

        LogLine line;

        // The number and the place in the ring are taken together, under one lock.
        //
        // Numbering outside it looks cheaper and is wrong: two threads take 45 and 46, and
        // whichever reaches the lock first goes in first — so the ring ends up holding 46
        // before 45 and the sequence stops meaning the order things happened. That is the one
        // job the sequence has. The lock covers two allocations and no I/O; it is never held
        // long enough to matter to a caller that has a frame to get back to.
        lock (_ringGate)
        {
            line = new LogLine(
                Seq: ++_seq,
                At: DateTimeOffset.Now,
                Timecode: timecode,
                Source: source,
                Level: level,
                Message: message ?? "",
                Event: @event,
                Correlation: correlation,
                File: file,
                Detail: trimmed,
                Transient: transient);

            _ring.Enqueue(line);
            while (_ring.Count > RingSize) _ring.Dequeue();
        }

        // Transient lines are for the screen. Queuing them would fill the record with the
        // playout engine's once-a-second progress and bury the events among it.
        if (!transient && _writer is not null)
        {
            if (Interlocked.Increment(ref _pendingCount) <= PendingLimit)
            {
                _pending.Enqueue(line);
                _wake.Set();
            }
            else
            {
                Interlocked.Decrement(ref _pendingCount);
                Interlocked.Increment(ref _dropped);
            }
        }

        // Last, and each handler in its own try: one subscriber throwing must not cost the
        // others their line, and must certainly not unwind into the capture loop.
        foreach (Delegate handler in Line?.GetInvocationList() ?? Array.Empty<Delegate>())
        {
            try { ((Action<LogLine>)handler).Invoke(line); }
            catch { /* a display is not worth a recording */ }
        }

        return line;
    }

    public LogLine Info(LogSource source, string message, string? @event = null,
                        string? correlation = null, string? file = null, string? detail = null,
                        bool transient = false) =>
        Write(source, LogLevel.Info, message, @event, correlation, file, detail, transient);

    public LogLine Ok(LogSource source, string message, string? @event = null,
                      string? correlation = null, string? file = null, string? detail = null) =>
        Write(source, LogLevel.Ok, message, @event, correlation, file, detail);

    public LogLine Warn(LogSource source, string message, string? @event = null,
                        string? correlation = null, string? file = null, string? detail = null) =>
        Write(source, LogLevel.Warn, message, @event, correlation, file, detail);

    public LogLine Error(LogSource source, string message, string? @event = null,
                         string? correlation = null, string? file = null, string? detail = null) =>
        Write(source, LogLevel.Error, message, @event, correlation, file, detail);

    /// <summary>Everything still in memory, oldest first. A copy; safe to enumerate at leisure.</summary>
    public LogLine[] Snapshot()
    {
        lock (_ringGate) return _ring.ToArray();
    }

    public void Clear()
    {
        lock (_ringGate) _ring.Clear();
    }

    private static string? Trim(string? detail) =>
        detail is not null && detail.Length > MaxDetailChars
            ? detail[..MaxDetailChars] + "\n... truncated"
            : detail;

    /// <summary>
    /// The house timecode, taken at most ten times a second and reused in between.
    ///
    /// <see cref="TimecodeService.TryGetCurrent"/> is not a cheap read: it takes the clock's
    /// own lock and moves its anti-backstep mark. Calling it once per line, from the capture
    /// thread, would contend with the deck's render timer for the station clock and would
    /// consume that mark on behalf of something that is not drawing it. Sampling costs the
    /// clock exactly what it costs today.
    /// </summary>
    private string? SampleTimecode()
    {
        long now = Stopwatch.GetTimestamp();
        long sampled = Volatile.Read(ref _timecodeSampledTicks);

        if (sampled != 0 && now - sampled < ClockSampleTicks) return Volatile.Read(ref _timecodeCache);

        // Two threads may both sample here. That is harmless — they get the same answer, and
        // the later write wins — and it is cheaper than a lock on a path this hot.
        Volatile.Write(ref _timecodeSampledTicks, now);

        string? value;
        try { value = _clock() is { } tc ? tc.ToString() : null; }
        catch { value = null; }

        Volatile.Write(ref _timecodeCache, value);
        return value;
    }

    // ------------------------------------------------------------------ the file

    /// <summary>
    /// Starts writing the record to <paramref name="folder"/>, one JSON object per line.
    ///
    /// Called once, by the application. Tests and anything hosting the log without a disk
    /// simply never call it, and the log stays in memory — which is why nothing here is done
    /// in the constructor.
    /// </summary>
    public void StartWriting(string folder, int retentionDays)
    {
        if (_writer is { IsAlive: true }) return;

        _folder = folder;
        _retentionDays = retentionDays;

        try { Directory.CreateDirectory(folder); }
        catch (Exception ex)
        {
            _folder = null;
            Warn(LogSource.App, $"The activity log cannot be written to {folder}: {ex.Message}");
            return;
        }

        _cts = new CancellationTokenSource();
        CancellationToken ct = _cts.Token;

        _writer = new Thread(() => WriteLoop(ct))
        {
            IsBackground = true,
            Name = "activity log",
            // Below everything that touches the plant. This thread must never be the reason a
            // frame is late.
            Priority = ThreadPriority.BelowNormal,
        };

        _writer.Start();
    }

    /// <summary>
    /// Waits until everything written so far has reached the disk.
    ///
    /// For anything that wants to read the file back straight after writing to it — an export,
    /// a test. Nothing on the hot path calls this, and nothing on the hot path should: waiting
    /// for a disk is the one thing <see cref="Write"/> exists not to do.
    /// </summary>
    public bool Flush(TimeSpan timeout)
    {
        if (_writer is null) return true;

        var waited = Stopwatch.StartNew();

        while (waited.Elapsed < timeout)
        {
            if (Volatile.Read(ref _pendingCount) == 0) return true;

            _wake.Set();
            Thread.Sleep(10);
        }

        return Volatile.Read(ref _pendingCount) == 0;
    }

    /// <summary>
    /// Deletes records older than the retention window.
    ///
    /// By the date in the name rather than the file's timestamp: a copy or a restore rewrites
    /// the timestamp and would throw away a day that was asked to be kept. Run once at start-up,
    /// beside the delay line's own sweep, for the same reason — last run's leftovers go before
    /// this run allocates more.
    /// </summary>
    public int Sweep(string folder, int retentionDays)
    {
        if (retentionDays < 0 || !Directory.Exists(folder)) return 0;

        DateTime cutoff = DateTime.Today.AddDays(-retentionDays);
        int swept = 0;

        foreach (string path in SafeFiles(folder))
        {
            if (DateFromName(path) is not { } day || day >= cutoff) continue;

            try { File.Delete(path); swept++; }
            catch (IOException) { /* in use, or gone already; it will be swept next time */ }
            catch (UnauthorizedAccessException) { }
        }

        return swept;
    }

    private static IEnumerable<string> SafeFiles(string folder)
    {
        try { return Directory.EnumerateFiles(folder, "emerald-*.jsonl").ToList(); }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>"emerald-20260909.jsonl" and "emerald-20260909-001.jsonl" both give that day.</summary>
    public static DateTime? DateFromName(string path)
    {
        string name = Path.GetFileNameWithoutExtension(path);
        if (!name.StartsWith("emerald-", StringComparison.OrdinalIgnoreCase)) return null;

        string stamp = name["emerald-".Length..];
        if (stamp.Length > 8) stamp = stamp[..8];

        return DateTime.TryParseExact(stamp, "yyyyMMdd", null,
                                      System.Globalization.DateTimeStyles.None, out DateTime day)
            ? day
            : null;
    }

    private void WriteLoop(CancellationToken ct)
    {
        StreamWriter? file = null;
        DateTime openFor = default;
        int index = 0;
        long bytes = 0;

        try
        {
            while (!ct.IsCancellationRequested)
            {
                _wake.WaitOne(500);

                bool wrote = false;

                int taken = 0;

                while (_pending.TryDequeue(out LogLine? line))
                {
                    taken++;

                    DateTime day = line.At.Date;

                    // A new day, or a file that has grown past what anything will happily open.
                    if (file is null || day != openFor || bytes >= MaxFileBytes)
                    {
                        if (file is not null && day == openFor && bytes >= MaxFileBytes) index++;
                        else if (day != openFor) index = 0;

                        file?.Dispose();
                        (file, bytes) = Open(day, ref index);
                        openFor = day;

                        if (file is null) break;   // the folder went away; stop trying
                    }

                    try
                    {
                        string json = JsonSerializer.Serialize(line, Json);
                        file.WriteLine(json);
                        bytes += json.Length + 2;
                        wrote = true;
                    }
                    catch (IOException)
                    {
                        // The disk answered badly. Drop this line rather than spin on it; the
                        // count is reported below and the next line gets its own chance.
                        Interlocked.Increment(ref _dropped);
                    }
                }

                // One flush per drain rather than per line: a crash costs at most this batch,
                // and the disk is not asked to do a hundred small writes a second.
                if (wrote)
                {
                    try { file?.Flush(); } catch (IOException) { }
                }

                // Counted down only now, after the batch is on the disk rather than as each
                // line leaves the queue. Flush watches this number, and decrementing it first
                // let it report a drain while the lines were still in the writer's hand — an
                // export, or a test, would then read a file that did not have them yet.
                if (taken > 0) Interlocked.Add(ref _pendingCount, -taken);

                ReportDrops();
            }
        }
        catch (Exception ex)
        {
            // The record failing must not be silent, and must not take anything else with it.
            try { Warn(LogSource.App, $"The activity log writer stopped: {ex.Message}"); } catch { }
        }
        finally
        {
            try { file?.Flush(); } catch (IOException) { }
            file?.Dispose();
        }
    }

    /// <summary>
    /// Opens today's file, appending to whatever is already there.
    ///
    /// <see cref="FileShare.ReadWrite"/> deliberately: an operator watching a problem wants to
    /// tail the file while Emerald still holds it, and a lock that made that impossible would
    /// be a lock for no one's benefit.
    /// </summary>
    private (StreamWriter? File, long Bytes) Open(DateTime day, ref int index)
    {
        if (_folder is null) return (null, 0);

        for (int attempt = 0; attempt < 100; attempt++)
        {
            string path = Path.Combine(_folder, Name(day, index));

            try
            {
                var info = new FileInfo(path);
                long existing = info.Exists ? info.Length : 0;

                // Already full from a previous run: take the next index rather than appending
                // to something that is over the cap.
                if (existing >= MaxFileBytes) { index++; continue; }

                var stream = new FileStream(path, FileMode.Append, FileAccess.Write,
                                            FileShare.ReadWrite);

                return (new StreamWriter(stream) { AutoFlush = false }, existing);
            }
            catch (IOException) { index++; }
            catch (UnauthorizedAccessException) { return (null, 0); }
        }

        return (null, 0);
    }

    private static string Name(DateTime day, int index) =>
        index == 0 ? $"emerald-{day:yyyyMMdd}.jsonl" : $"emerald-{day:yyyyMMdd}-{index:D3}.jsonl";

    /// <summary>
    /// Says how many lines the disk lost, once it is keeping up again.
    ///
    /// A record with a hole in it that does not say so is worse than one that does — the whole
    /// point of the file is that it can be trusted about what happened.
    /// </summary>
    private void ReportDrops()
    {
        long dropped = Interlocked.Exchange(ref _dropped, 0);
        if (dropped == 0) return;

        Write(LogSource.App, LogLevel.Warn,
              $"{dropped} log line(s) were dropped - the disk could not keep up.",
              @event: "log.dropped");
    }

    public void Dispose()
    {
        _cts?.Cancel();
        _wake.Set();

        _writer?.Join(TimeSpan.FromSeconds(3));
        _writer = null;

        _cts?.Dispose();
        _cts = null;

        _wake.Dispose();
    }
}
