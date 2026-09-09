using System.IO;
using System.Text.Json;
using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The record of what Emerald did.
///
/// Two properties matter more than the rest. It must be <b>ordered</b>, because the whole
/// point is answering what happened before what across four modules and as many threads. And
/// writing must <b>never block</b>, because the playout thread calls it between two frames
/// going to a transmitter — a log that can stall is a log that can drop frames on air.
/// </summary>
public sealed class ActivityLogTests : IDisposable
{
    private readonly string _folder;

    public ActivityLogTests()
    {
        _folder = Path.Combine(Path.GetTempPath(), "emerald-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
    }

    private static ActivityLog New(Func<Timecode?>? clock = null) => new(clock);

    // ------------------------------------------------------------------ ordering

    [Fact]
    public void Lines_come_back_in_the_order_they_were_written()
    {
        using ActivityLog log = New();

        for (int i = 0; i < 10; i++) log.Info(LogSource.App, $"line {i}");

        LogLine[] lines = log.Snapshot();

        Assert.Equal(10, lines.Length);
        Assert.Equal("line 0", lines[0].Message);
        Assert.Equal("line 9", lines[9].Message);
    }

    [Fact]
    public void Every_line_from_every_thread_gets_its_own_place_in_the_order()
    {
        using ActivityLog log = New();

        const int threads = 8, each = 500;

        Parallel.For(0, threads, t =>
        {
            for (int i = 0; i < each; i++) log.Info(LogSource.App, $"{t}:{i}");
        });

        long[] sequences = log.Snapshot().Select(l => l.Seq).ToArray();

        // A wall clock cannot do this - two threads inside one millisecond would tie, and a
        // clock step would put them in the wrong order entirely.
        Assert.Equal(threads * each, sequences.Length);
        Assert.Equal(sequences.Length, sequences.Distinct().Count());
        Assert.Equal(sequences.OrderBy(s => s), sequences);
    }

    // ------------------------------------------------------------------ retention

    [Fact]
    public void The_ring_keeps_the_newest_and_drops_the_oldest()
    {
        using ActivityLog log = New();

        for (int i = 0; i < ActivityLog.RingSize + 100; i++) log.Info(LogSource.App, $"line {i}");

        LogLine[] lines = log.Snapshot();

        Assert.Equal(ActivityLog.RingSize, lines.Length);
        Assert.Equal("line 100", lines[0].Message);
        Assert.Equal($"line {ActivityLog.RingSize + 99}", lines[^1].Message);
    }

    // ------------------------------------------------------------------ the clock

    [Fact]
    public void A_line_written_while_the_clock_is_offline_carries_no_timecode()
    {
        using ActivityLog log = New(() => null);

        Assert.Null(log.Info(LogSource.App, "before the generator answered").Timecode);
    }

    [Fact]
    public void A_line_is_stamped_with_the_timecode_that_was_on_air()
    {
        using ActivityLog log = New(() => new Timecode(25 * 60, 25));

        Assert.Equal("00:01:00:00", log.Info(LogSource.App, "on air").Timecode);
    }

    [Fact]
    public void A_clock_that_throws_costs_the_timecode_and_not_the_line()
    {
        using ActivityLog log = New(() => throw new InvalidOperationException("no clock"));

        LogLine line = log.Info(LogSource.App, "the plant carries on");

        Assert.Null(line.Timecode);
        Assert.Equal("the plant carries on", line.Message);
    }

    // ------------------------------------------------------------------ subscribers

    [Fact]
    public void A_subscriber_that_throws_does_not_cost_the_others_their_line()
    {
        using ActivityLog log = New();

        var seen = new List<string>();

        log.Line += _ => throw new InvalidOperationException("a window misbehaving");
        log.Line += l => seen.Add(l.Message);

        log.Info(LogSource.App, "still delivered");

        Assert.Equal(new[] { "still delivered" }, seen);
    }

    [Fact]
    public void Writing_returns_promptly_even_when_a_subscriber_is_slow()
    {
        using ActivityLog log = New();

        // Stands in for a window doing too much on the event. This is called from the loop
        // that pushes frames at a transmitter; it has a frame to get back to.
        log.Line += _ => Thread.Sleep(50);

        var clock = System.Diagnostics.Stopwatch.StartNew();
        log.Info(LogSource.Playout, "between two frames");
        clock.Stop();

        // Not a timing assertion so much as a shape one: the write itself does no work beyond
        // handing the line on, so anything approaching a second means something blocks.
        Assert.True(clock.ElapsedMilliseconds < 500,
                    $"a write took {clock.ElapsedMilliseconds} ms");
    }

    // ------------------------------------------------------------------ the file

    [Fact]
    public void The_record_is_written_as_one_json_object_per_line()
    {
        using (ActivityLog log = New())
        {
            log.StartWriting(_folder, retentionDays: 14);
            log.Ok(LogSource.Edl, "queued", @event: "edl.queued", correlation: "abc12345",
                   file: @"C:\media\promo.mov");

            Drain(log);
        }

        string[] lines = File.ReadAllLines(TodaysFile());

        Assert.Single(lines);

        LogLine? read = JsonSerializer.Deserialize<LogLine>(lines[0], Options);

        Assert.NotNull(read);
        Assert.Equal("queued", read!.Message);
        Assert.Equal("edl.queued", read.Event);
        Assert.Equal("abc12345", read.Correlation);
        Assert.Equal(@"C:\media\promo.mov", read.File);
    }

    [Fact]
    public void A_transient_line_reaches_the_screen_and_never_the_file()
    {
        using (ActivityLog log = New())
        {
            log.StartWriting(_folder, retentionDays: 14);

            log.Info(LogSource.Playout, "kept");
            log.Info(LogSource.Playout, "the once-a-second heartbeat", transient: true);

            Drain(log);
        }

        // The engine reports every second while a message runs. Those belong on the page and
        // not in the record, or the events are buried under them.
        string[] lines = File.ReadAllLines(TodaysFile());

        Assert.Single(lines);
        Assert.Contains("kept", lines[0]);
    }

    [Fact]
    public void Both_kinds_of_line_reach_a_subscriber()
    {
        using ActivityLog log = New();

        var seen = new List<bool>();
        log.Line += l => seen.Add(l.Transient);

        log.Info(LogSource.Playout, "event");
        log.Info(LogSource.Playout, "heartbeat", transient: true);

        Assert.Equal(new[] { false, true }, seen);
    }

    [Fact]
    public void The_file_can_still_be_read_while_Emerald_is_holding_it()
    {
        using ActivityLog log = New();

        log.StartWriting(_folder, retentionDays: 14);
        log.Info(LogSource.App, "tail me");
        Drain(log);

        // An operator watching a problem wants to tail the file now, not after the application
        // exits. A share mode that made that impossible would be a lock for nobody's benefit.
        using var stream = new FileStream(TodaysFile(), FileMode.Open, FileAccess.Read,
                                          FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        Assert.Contains("tail me", reader.ReadToEnd());
    }

    // ------------------------------------------------------------------ retention sweep

    [Fact]
    public void Records_older_than_the_window_are_swept_and_newer_ones_kept()
    {
        using ActivityLog log = New();

        string old = Write($"emerald-{DateTime.Today.AddDays(-30):yyyyMMdd}.jsonl");
        string edge = Write($"emerald-{DateTime.Today.AddDays(-14):yyyyMMdd}.jsonl");
        string recent = Write($"emerald-{DateTime.Today.AddDays(-2):yyyyMMdd}.jsonl");
        string today = Write($"emerald-{DateTime.Today:yyyyMMdd}.jsonl");

        int swept = log.Sweep(_folder, retentionDays: 14);

        Assert.Equal(1, swept);
        Assert.False(File.Exists(old));
        Assert.True(File.Exists(edge), "the edge of the window is kept, not swept");
        Assert.True(File.Exists(recent));
        Assert.True(File.Exists(today));
    }

    [Fact]
    public void The_sweep_reads_the_date_from_the_name_and_not_from_the_file()
    {
        // A copy or a restore rewrites the timestamp. Going by that would throw away a day
        // that was asked to be kept, which is the one thing a retention window must not do.
        string path = Write($"emerald-{DateTime.Today.AddDays(-30):yyyyMMdd}.jsonl");
        File.SetLastWriteTime(path, DateTime.Now);

        using ActivityLog log = New();

        Assert.Equal(1, log.Sweep(_folder, retentionDays: 14));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void A_rolled_file_belongs_to_the_day_in_its_name()
    {
        Assert.Equal(new DateTime(2026, 9, 9),
                     ActivityLog.DateFromName(@"C:\logs\emerald-20260909.jsonl"));

        Assert.Equal(new DateTime(2026, 9, 9),
                     ActivityLog.DateFromName(@"C:\logs\emerald-20260909-003.jsonl"));

        Assert.Null(ActivityLog.DateFromName(@"C:\logs\something-else.txt"));
    }

    [Fact]
    public void Keeping_everything_sweeps_nothing()
    {
        Write($"emerald-{DateTime.Today.AddDays(-900):yyyyMMdd}.jsonl");

        using ActivityLog log = New();

        Assert.Equal(0, log.Sweep(_folder, retentionDays: -1));
    }

    // ------------------------------------------------------------------ helpers

    private static readonly JsonSerializerOptions Options = new()
    {
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    private string TodaysFile() => Path.Combine(_folder, $"emerald-{DateTime.Today:yyyyMMdd}.jsonl");

    private string Write(string name)
    {
        string path = Path.Combine(_folder, name);
        File.WriteAllText(path, "{}\n");
        return path;
    }

    /// <summary>Waits for the writer thread, which does its work off the caller's thread.</summary>
    private static void Drain(ActivityLog log) =>
        Assert.True(log.Flush(TimeSpan.FromSeconds(5)), "the writer did not drain");

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }
}
