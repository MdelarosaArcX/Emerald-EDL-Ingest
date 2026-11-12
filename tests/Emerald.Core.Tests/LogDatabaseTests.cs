using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Emerald.Core;
using Xunit;

namespace Emerald.Core.Tests;

/// <summary>
/// The record in a database: each line in its area's table, every warning and error in the
/// errors table as well, resource samples beside the recording they were taken during, and the
/// machine's figures read the way Task Manager reads them.
/// </summary>
public sealed class LogDatabaseTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "emerald-logdb", Guid.NewGuid().ToString("N"));
    private readonly LogDatabase _db;

    public LogDatabaseTests()
    {
        _db = new LogDatabase(LogDatabase.PathIn(_folder));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static long _seq;

    private static LogLine Line(LogSource source, string? @event = null, LogLevel level = LogLevel.Info,
                                DateTimeOffset? at = null, string message = "m") =>
        new(Interlocked.Increment(ref _seq), at ?? DateTimeOffset.Now, null, source, level, message, @event);

    // ------------------------------------------------------------------ which table

    [Theory]
    [InlineData(LogSource.Capture, null, LogArea.Capture)]
    [InlineData(LogSource.Preview, null, LogArea.Capture)]
    [InlineData(LogSource.Board, null, LogArea.Capture)]
    [InlineData(LogSource.Media, null, LogArea.Capture)]
    [InlineData(LogSource.App, "capture.segment", LogArea.Capture)]
    [InlineData(LogSource.Playout, null, LogArea.Playback)]
    [InlineData(LogSource.Playback, null, LogArea.Playback)]
    [InlineData(LogSource.LiveEdit, null, LogArea.LiveEdit)]
    [InlineData(LogSource.LiveEdit, "edl.editCreated", LogArea.Edl)]
    [InlineData(LogSource.Edl, null, LogArea.Edl)]
    [InlineData(LogSource.Capture, "delay.ready", LogArea.OnAir)]
    [InlineData(LogSource.TidalLock, null, LogArea.OnAir)]
    [InlineData(LogSource.App, "liveair.onair", LogArea.LiveEdit)]
    [InlineData(LogSource.Ingest, null, LogArea.Ingest)]
    [InlineData(LogSource.App, "app.started", LogArea.App)]
    public void Each_line_is_kept_in_its_areas_table(LogSource source, string? @event, LogArea area)
    {
        Assert.Equal(area, LogDatabase.AreaOf(Line(source, @event)));
    }

    [Fact]
    public void A_warning_or_an_error_is_kept_in_its_area_and_in_the_errors_table()
    {
        _db.WriteLines(new[]
        {
            Line(LogSource.Capture, "capture.segment"),
            Line(LogSource.Capture, "capture.lowdisk", LogLevel.Warn),
            Line(LogSource.Playout, "playout.drop", LogLevel.Error),
        });

        Assert.Equal(2, _db.Count("log_capture"));
        Assert.Equal(1, _db.Count("log_playback"));
        Assert.Equal(2, _db.Count("log_error"));

        LogLine[] errors = _db.ReadDay(DateTime.Today, LogArea.Errors);
        Assert.Equal(new[] { "capture.lowdisk", "playout.drop" }, errors.Select(e => e.Event));

        // Reading every area does not count a problem twice.
        Assert.Equal(3, _db.ReadDay(DateTime.Today).Length);
    }

    [Fact]
    public void A_day_is_read_back_in_order_and_only_that_day()
    {
        DateTimeOffset today = DateTimeOffset.Now;

        _db.WriteLines(new[]
        {
            Line(LogSource.App, message: "yesterday", at: today.AddDays(-1)),
            Line(LogSource.Capture, message: "first", at: today.AddSeconds(-2)),
            Line(LogSource.Playout, message: "second", at: today.AddSeconds(-1)),
        });

        Assert.Equal(new[] { "first", "second" }, _db.ReadDay(DateTime.Today).Select(l => l.Message));
        Assert.Equal(new[] { "yesterday" }, _db.ReadDay(DateTime.Today.AddDays(-1)).Select(l => l.Message));
    }

    [Fact]
    public void Rows_older_than_the_cutoff_are_swept()
    {
        DateTimeOffset now = DateTimeOffset.Now;

        _db.WriteLines(new[]
        {
            Line(LogSource.App, message: "old", at: now.AddDays(-30), level: LogLevel.Error),
            Line(LogSource.App, message: "new", at: now),
        });
        _db.WriteSample(Sample(now.AddDays(-30)));
        _db.WriteSample(Sample(now));

        int swept = _db.Sweep(now.AddDays(-14));

        Assert.Equal(4, swept);   // the old line, its errors copy, the old sample and its one drive
        Assert.Equal(1, _db.Count("log_app"));
        Assert.Equal(0, _db.Count("log_error"));
        Assert.Equal(1, _db.Count("resource_sample"));
        Assert.Equal(1, _db.Count("resource_disk"));
    }

    // ------------------------------------------------------------------ resources

    private static ResourceSample Sample(DateTimeOffset at, string? session = null) => new(
        at, 37.5, 8L << 30, 64L << 30, 2L << 30, 1L << 30, 12, 30, 3L << 30,
        new[] { new DiskSample("Disk 1 (E:)", 88, 1_000_000, 16_000_000, 0.4, 2600L << 30, 2794L << 30) },
        session);

    [Fact]
    public void A_sample_comes_back_with_its_drives_and_its_recording()
    {
        DateTimeOffset at = DateTimeOffset.Now;
        _db.SessionStarted("rec1", at.AddMinutes(-1), "Recording media");
        _db.WriteSample(Sample(at, "rec1"));
        _db.SessionFinished("rec1", at.AddMinutes(1));

        ResourceSample back = Assert.Single(_db.ReadSamples(at.AddMinutes(-5), at.AddMinutes(5)));

        Assert.Equal("rec1", back.Session);
        Assert.Equal(37.5, back.CpuPercent);
        Assert.Equal(64L << 30, back.RamTotalBytes);
        Assert.Equal(30, back.GpuEncodePercent);
        DiskSample disk = Assert.Single(back.Disks);
        Assert.Equal("Disk 1 (E:)", disk.Name);
        Assert.Equal(88, disk.ActivePercent);
        Assert.Equal(1, _db.Count("recording_session"));
    }

    [Fact]
    public void A_recording_starting_and_finishing_is_followed_from_the_record()
    {
        using var log = new ActivityLog();
        using var sampler = new ResourceSampler();
        sampler.Attach(log, _db);

        log.Ok(LogSource.Capture, "Recording", @event: "capture.started", correlation: "session-a");
        Assert.Equal("session-a", sampler.Session);

        log.Ok(LogSource.Capture, "Finished", @event: "capture.finished", correlation: "session-a");
        Assert.Null(sampler.Session);

        // The session rows are written off the line's thread.
        var waited = Stopwatch.StartNew();
        while (_db.Count("recording_session") == 0 && waited.ElapsedMilliseconds < 3000) Thread.Sleep(20);
        Assert.Equal(1, _db.Count("recording_session"));
    }

    [Fact]
    public void The_busiest_graphics_engine_is_the_graphics_figure_as_Task_Manager_counts_it()
    {
        (double? all, double? encode) = ResourceSampler.GpuBusiest(new[]
        {
            ("pid_100_luid_0x0_0xC4B5_phys_0_eng_0_engtype_3D", 20.0),
            ("pid_200_luid_0x0_0xC4B5_phys_0_eng_0_engtype_3D", 15.0),
            ("pid_300_luid_0x0_0xC4B5_phys_0_eng_3_engtype_VideoEncode", 30.0),
            ("pid_100_luid_0x0_0xC4B5_phys_0_eng_5_engtype_Copy", 5.0),
        });

        Assert.Equal(35, all);      // two processes on the same 3D engine add up
        Assert.Equal(30, encode);
        Assert.Equal((null, null), ResourceSampler.GpuBusiest(Array.Empty<(string, double)>()));
    }

    [Theory]
    [InlineData("1 E:", "Disk 1 (E:)")]
    [InlineData("0 C:", "Disk 0 (C:)")]
    [InlineData("2 F: G:", "Disk 2 (F: G:)")]
    [InlineData("3", "Disk 3")]
    public void A_physical_disk_is_named_as_Task_Manager_names_it(string instance, string name)
    {
        Assert.Equal(name, ResourceSampler.DiskName(instance));
    }

    [Fact]
    public void This_machine_reads_back_figures_that_make_sense()
    {
        using var sampler = new ResourceSampler();

        sampler.Sample();
        Thread.Sleep(1100);
        ResourceSample s = sampler.Sample();

        Assert.InRange(s.CpuPercent, 0, 100);
        Assert.True(s.RamTotalBytes > 0);
        Assert.InRange(s.RamUsedBytes, 1, s.RamTotalBytes);
        Assert.True(s.EmeraldPrivateBytes > 0);
        Assert.NotEmpty(s.Disks);
        Assert.All(s.Disks, d => Assert.InRange(d.ActivePercent, 0, 100));
        Assert.Contains(s.Disks, d => d.TotalBytes > 0);
    }

    // ------------------------------------------------------------------ the remote page

    [Fact]
    public void An_area_named_in_a_query_is_read_in_any_case_and_nonsense_is_all()
    {
        Assert.Equal(LogArea.Capture, MonitorFeed.ParseArea("capture"));
        Assert.Equal(LogArea.Errors, MonitorFeed.ParseArea("Errors"));
        Assert.Equal(LogArea.LiveEdit, MonitorFeed.ParseArea("LIVEEDIT"));
        Assert.Null(MonitorFeed.ParseArea("nonsense"));
        Assert.Null(MonitorFeed.ParseArea(null));
        Assert.Null(MonitorFeed.ParseArea("99"));
    }

    [Fact]
    public void The_live_log_can_be_narrowed_to_an_area_and_says_each_lines_area()
    {
        using var log = new ActivityLog();
        log.Info(LogSource.Capture, "seg", @event: "capture.segment");
        log.Warn(LogSource.Playout, "late", @event: "playout.drop");
        log.Info(LogSource.App, "hello");

        using JsonDocument all = JsonDocument.Parse(MonitorFeed.Log(log, 0, 100));
        Assert.Equal(3, all.RootElement.GetProperty("count").GetInt32());
        Assert.Equal("Capture", all.RootElement.GetProperty("lines")[0].GetProperty("area").GetString());

        using JsonDocument errors = JsonDocument.Parse(MonitorFeed.Log(log, 0, 100, LogArea.Errors));
        Assert.Equal("late", errors.RootElement.GetProperty("lines").EnumerateArray().Single().GetProperty("message").GetString());
    }

    [Fact]
    public void A_day_is_served_from_the_database_newest_last()
    {
        using var log = new ActivityLog();
        log.StartWriting(_db, retentionDays: 14);
        for (int i = 0; i < 10; i++) log.Info(LogSource.Capture, $"line {i}", @event: "capture.segment");
        log.Flush(TimeSpan.FromSeconds(5));

        using JsonDocument day = JsonDocument.Parse(MonitorFeed.History(log, DateTime.Today, LogArea.Capture, 4));

        Assert.Equal(10, day.RootElement.GetProperty("total").GetInt32());
        Assert.Equal(new[] { "line 6", "line 7", "line 8", "line 9" },
                     day.RootElement.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("message").GetString()));
    }

    [Fact]
    public void The_machines_figures_are_served_with_the_last_ten_minutes()
    {
        using var sampler = new ResourceSampler();
        using JsonDocument doc = JsonDocument.Parse(MonitorFeed.Resources(sampler));

        Assert.Equal(JsonValueKind.Array, doc.RootElement.GetProperty("history").ValueKind);
    }

    // ------------------------------------------------------------------ background priority

    [Fact]
    public void A_helper_process_is_put_behind_everything_else_for_the_disk()
    {
        using Process p = Process.Start(new ProcessStartInfo("cmd.exe", "/c ping -n 5 127.0.0.1 >nul")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;

        try
        {
            Assert.True(BackgroundPriority.Apply(p));
            Assert.Equal(0, BackgroundPriority.IoPriorityOf(p));
            Assert.Equal(ProcessPriorityClass.BelowNormal, p.PriorityClass);
        }
        finally
        {
            try { p.Kill(entireProcessTree: true); } catch { }
        }
    }
}
