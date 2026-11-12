using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Emerald.Core;

/// <summary>
/// Which table a line belongs in. <see cref="Errors"/> is not an area a line comes from: every
/// warning and error is copied there as well as into its own area, so "what went wrong today"
/// is one table and not a search across seven.
/// </summary>
public enum LogArea { Errors, Capture, Playback, LiveEdit, Edl, OnAir, Ingest, App }

/// <summary>One drive at one moment, as Task Manager's disk page shows it.</summary>
public sealed record DiskSample(
    string Name,
    double ActivePercent,
    double ReadBytesPerSecond,
    double WriteBytesPerSecond,
    double QueueLength,
    long FreeBytes,
    long TotalBytes);

/// <summary>The machine at one moment: processor, memory, graphics, drives, and Emerald's share.</summary>
public sealed record ResourceSample(
    DateTimeOffset At,
    double CpuPercent,
    long RamUsedBytes,
    long RamTotalBytes,
    long EmeraldPrivateBytes,
    long FfmpegPrivateBytes,
    double? GpuPercent,
    double? GpuEncodePercent,
    long? GpuMemoryBytes,
    IReadOnlyList<DiskSample> Disks,
    string? Session = null);

/// <summary>
/// Everything Emerald records, in one SQLite file: the activity record split by area, the
/// recordings it made, and the machine's processor, memory, graphics and drives while it did.
///
/// <b>One writer.</b> Writes come from the activity log's own thread and the resource sampler's,
/// a batch at a time inside a transaction, through one connection under a lock. Reads, which
/// come from the Monitor, open a connection of their own: the file is in WAL mode, so a reader
/// never waits for the writer and the writer never waits for a reader.
///
/// <b>Nothing here is on a real-time thread.</b> <see cref="ActivityLog.Write"/> only queues;
/// this is what the queue drains into.
/// </summary>
public sealed class LogDatabase : IDisposable
{
    public const string FileName = "emerald.db";

    /// <summary>The areas a line can come from, in the order the Monitor lists them.</summary>
    public static IReadOnlyList<LogArea> Areas { get; } = new[]
    {
        LogArea.Capture, LogArea.Playback, LogArea.LiveEdit, LogArea.Edl,
        LogArea.OnAir, LogArea.Ingest, LogArea.App,
    };

    private static readonly object InitGate = new();
    private static bool _initialised;

    private readonly SqliteConnection _write;
    private readonly object _gate = new();
    private int _disposed;

    public LogDatabase(string path)
    {
        lock (InitGate)
        {
            if (!_initialised) { SQLitePCL.Batteries_V2.Init(); _initialised = true; }
        }

        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);

        _write = new SqliteConnection(ConnectionString(path, readOnly: false));
        _write.Open();

        Execute("pragma journal_mode=WAL;");
        // NORMAL in WAL mode loses at most the last transaction on a power cut, and never
        // corrupts the file. FULL would ask the disk for a flush on every batch.
        Execute("pragma synchronous=NORMAL;");
        Execute("pragma busy_timeout=5000;");

        foreach (LogArea area in Areas.Append(LogArea.Errors))
        {
            string table = TableFor(area);

            Execute($@"create table if not exists {table} (
                id integer primary key,
                at_ms integer not null,
                at text not null,
                seq integer not null,
                timecode text,
                level text not null,
                source text not null,
                event text,
                correlation text,
                file text,
                message text not null,
                detail text{(area == LogArea.Errors ? ",\n                area text not null" : "")});");
            Execute($"create index if not exists ix_{table}_at on {table}(at_ms);");
        }

        Execute(@"create table if not exists recording_session (
            id text primary key,
            started_ms integer not null,
            started text not null,
            finished_ms integer,
            finished text,
            detail text);");

        Execute(@"create table if not exists resource_sample (
            id integer primary key,
            at_ms integer not null,
            at text not null,
            session text,
            cpu_percent real not null,
            ram_used integer not null,
            ram_total integer not null,
            emerald_private integer not null,
            ffmpeg_private integer not null,
            gpu_percent real,
            gpu_encode_percent real,
            gpu_memory integer);");
        Execute("create index if not exists ix_resource_sample_at on resource_sample(at_ms);");

        Execute(@"create table if not exists resource_disk (
            id integer primary key,
            sample_id integer not null,
            at_ms integer not null,
            disk text not null,
            active_percent real not null,
            read_bps real not null,
            write_bps real not null,
            queue real not null,
            free_bytes integer not null,
            total_bytes integer not null);");
        Execute("create index if not exists ix_resource_disk_at on resource_disk(at_ms);");

        Execute(@"create table if not exists resource_process (
            id integer primary key,
            at_ms integer not null,
            at text not null,
            snapshot text not null);");
        Execute("create index if not exists ix_resource_process_at on resource_process(at_ms);");
    }

    public string Path { get; }

    /// <summary>The database a data folder holds.</summary>
    public static string PathIn(string folder) => System.IO.Path.Combine(folder, FileName);

    public static string TableFor(LogArea area) => area switch
    {
        LogArea.Errors => "log_error",
        LogArea.Capture => "log_capture",
        LogArea.Playback => "log_playback",
        LogArea.LiveEdit => "log_liveedit",
        LogArea.Edl => "log_edl",
        LogArea.OnAir => "log_onair",
        LogArea.Ingest => "log_ingest",
        _ => "log_app",
    };

    /// <summary>
    /// The area a line belongs to. By its event first, where the event says more than the
    /// source: the EDL log writes as Live Edit but every one of its lines is an EDL line, and the
    /// on-air transmitter's lines are on-air whichever window started it.
    /// </summary>
    public static LogArea AreaOf(LogLine line)
    {
        string? e = line.Event;

        if (e is not null)
        {
            if (e.StartsWith("edl.", StringComparison.Ordinal)) return LogArea.Edl;
            if (e.StartsWith("delay.", StringComparison.Ordinal) ||
                e.StartsWith("tidallock.", StringComparison.Ordinal)) return LogArea.OnAir;
            if (e.StartsWith("liveair.", StringComparison.Ordinal)) return LogArea.LiveEdit;
            if (e.StartsWith("capture.", StringComparison.Ordinal) ||
                e.StartsWith("rx.", StringComparison.Ordinal) ||
                e.StartsWith("board.", StringComparison.Ordinal)) return LogArea.Capture;
            if (e.StartsWith("playout.", StringComparison.Ordinal)) return LogArea.Playback;
        }

        return line.Source switch
        {
            LogSource.Capture or LogSource.Preview or LogSource.Board or LogSource.Media => LogArea.Capture,
            LogSource.Playout or LogSource.Playback => LogArea.Playback,
            LogSource.LiveEdit => LogArea.LiveEdit,
            LogSource.Edl => LogArea.Edl,
            LogSource.TidalLock => LogArea.OnAir,
            LogSource.Ingest => LogArea.Ingest,
            _ => LogArea.App,
        };
    }

    private static bool IsProblem(LogLine line) => line.Level is LogLevel.Warn or LogLevel.Error;

    // ------------------------------------------------------------------ writing

    /// <summary>Writes a batch of lines in one transaction. Warnings and errors go to their area and to the errors table.</summary>
    public void WriteLines(IReadOnlyList<LogLine> lines)
    {
        if (lines.Count == 0) return;

        lock (_gate)
        {
            if (_disposed != 0) return;

            using SqliteTransaction tx = _write.BeginTransaction();
            var commands = new Dictionary<LogArea, SqliteCommand>();

            try
            {
                foreach (LogLine line in lines)
                {
                    LogArea area = AreaOf(line);
                    Insert(commands, tx, area, line, null);
                    if (IsProblem(line)) Insert(commands, tx, LogArea.Errors, line, area);
                }

                tx.Commit();
            }
            finally
            {
                foreach (SqliteCommand c in commands.Values) c.Dispose();
            }
        }
    }

    private void Insert(Dictionary<LogArea, SqliteCommand> commands, SqliteTransaction tx,
                        LogArea table, LogLine line, LogArea? from)
    {
        if (!commands.TryGetValue(table, out SqliteCommand? c))
        {
            c = _write.CreateCommand();
            c.Transaction = tx;
            c.CommandText =
                $"insert into {TableFor(table)} (at_ms, at, seq, timecode, level, source, event, correlation, file, message, detail" +
                (table == LogArea.Errors ? ", area" : "") +
                ") values ($ms, $at, $seq, $tc, $level, $source, $event, $corr, $file, $msg, $detail" +
                (table == LogArea.Errors ? ", $area" : "") + ");";

            foreach (string p in new[] { "$ms", "$at", "$seq", "$tc", "$level", "$source", "$event", "$corr", "$file", "$msg", "$detail" })
                c.Parameters.Add(new SqliteParameter(p, null));
            if (table == LogArea.Errors) c.Parameters.Add(new SqliteParameter("$area", null));

            c.Prepare();
            commands[table] = c;
        }

        c.Parameters["$ms"].Value = line.At.ToUnixTimeMilliseconds();
        c.Parameters["$at"].Value = Stamp(line.At);
        c.Parameters["$seq"].Value = line.Seq;
        c.Parameters["$tc"].Value = (object?)line.Timecode ?? DBNull.Value;
        c.Parameters["$level"].Value = line.Level.ToString();
        c.Parameters["$source"].Value = line.Source.ToString();
        c.Parameters["$event"].Value = (object?)line.Event ?? DBNull.Value;
        c.Parameters["$corr"].Value = (object?)line.Correlation ?? DBNull.Value;
        c.Parameters["$file"].Value = (object?)line.File ?? DBNull.Value;
        c.Parameters["$msg"].Value = line.Message;
        c.Parameters["$detail"].Value = (object?)line.Detail ?? DBNull.Value;
        if (table == LogArea.Errors) c.Parameters["$area"].Value = from.ToString();

        c.ExecuteNonQuery();
    }

    /// <summary>A recording has started. Its resource samples carry the same id.</summary>
    public void SessionStarted(string id, DateTimeOffset at, string? detail)
    {
        Locked(c =>
        {
            c.CommandText = "insert or replace into recording_session (id, started_ms, started, detail) values ($id, $ms, $at, $d);";
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$ms", at.ToUnixTimeMilliseconds());
            c.Parameters.AddWithValue("$at", Stamp(at));
            c.Parameters.AddWithValue("$d", (object?)detail ?? DBNull.Value);
            c.ExecuteNonQuery();
        });
    }

    public void SessionFinished(string id, DateTimeOffset at)
    {
        Locked(c =>
        {
            c.CommandText = "update recording_session set finished_ms = $ms, finished = $at where id = $id;";
            c.Parameters.AddWithValue("$id", id);
            c.Parameters.AddWithValue("$ms", at.ToUnixTimeMilliseconds());
            c.Parameters.AddWithValue("$at", Stamp(at));
            c.ExecuteNonQuery();
        });
    }

    public void WriteSample(ResourceSample s)
    {
        lock (_gate)
        {
            if (_disposed != 0) return;

            using SqliteTransaction tx = _write.BeginTransaction();
            using SqliteCommand c = _write.CreateCommand();
            c.Transaction = tx;
            c.CommandText = @"insert into resource_sample
                (at_ms, at, session, cpu_percent, ram_used, ram_total, emerald_private, ffmpeg_private, gpu_percent, gpu_encode_percent, gpu_memory)
                values ($ms, $at, $session, $cpu, $used, $total, $emerald, $ffmpeg, $gpu, $enc, $gmem);
                select last_insert_rowid();";
            long ms = s.At.ToUnixTimeMilliseconds();
            c.Parameters.AddWithValue("$ms", ms);
            c.Parameters.AddWithValue("$at", Stamp(s.At));
            c.Parameters.AddWithValue("$session", (object?)s.Session ?? DBNull.Value);
            c.Parameters.AddWithValue("$cpu", s.CpuPercent);
            c.Parameters.AddWithValue("$used", s.RamUsedBytes);
            c.Parameters.AddWithValue("$total", s.RamTotalBytes);
            c.Parameters.AddWithValue("$emerald", s.EmeraldPrivateBytes);
            c.Parameters.AddWithValue("$ffmpeg", s.FfmpegPrivateBytes);
            c.Parameters.AddWithValue("$gpu", (object?)s.GpuPercent ?? DBNull.Value);
            c.Parameters.AddWithValue("$enc", (object?)s.GpuEncodePercent ?? DBNull.Value);
            c.Parameters.AddWithValue("$gmem", (object?)s.GpuMemoryBytes ?? DBNull.Value);
            long id = (long)c.ExecuteScalar()!;

            using SqliteCommand d = _write.CreateCommand();
            d.Transaction = tx;
            d.CommandText = @"insert into resource_disk
                (sample_id, at_ms, disk, active_percent, read_bps, write_bps, queue, free_bytes, total_bytes)
                values ($id, $ms, $disk, $active, $read, $write, $queue, $free, $total);";

            foreach (DiskSample disk in s.Disks)
            {
                d.Parameters.Clear();
                d.Parameters.AddWithValue("$id", id);
                d.Parameters.AddWithValue("$ms", ms);
                d.Parameters.AddWithValue("$disk", disk.Name);
                d.Parameters.AddWithValue("$active", disk.ActivePercent);
                d.Parameters.AddWithValue("$read", disk.ReadBytesPerSecond);
                d.Parameters.AddWithValue("$write", disk.WriteBytesPerSecond);
                d.Parameters.AddWithValue("$queue", disk.QueueLength);
                d.Parameters.AddWithValue("$free", disk.FreeBytes);
                d.Parameters.AddWithValue("$total", disk.TotalBytes);
                d.ExecuteNonQuery();
            }

            tx.Commit();
        }
    }

    /// <summary>The developer memory record (<see cref="MemoryMonitor"/>), one JSON snapshot a row.</summary>
    public void WriteProcessSnapshot(DateTimeOffset at, string json)
    {
        Locked(c =>
        {
            c.CommandText = "insert into resource_process (at_ms, at, snapshot) values ($ms, $at, $json);";
            c.Parameters.AddWithValue("$ms", at.ToUnixTimeMilliseconds());
            c.Parameters.AddWithValue("$at", Stamp(at));
            c.Parameters.AddWithValue("$json", json);
            c.ExecuteNonQuery();
        });
    }

    /// <summary>
    /// Deletes everything older than <paramref name="cutoff"/>, a few thousand rows at a time so
    /// the writer is never held for long. Returns the rows deleted.
    /// </summary>
    public int Sweep(DateTimeOffset cutoff)
    {
        long ms = cutoff.ToUnixTimeMilliseconds();
        int total = 0;

        IEnumerable<string> tables = Areas.Append(LogArea.Errors).Select(TableFor)
            .Concat(new[] { "resource_sample", "resource_disk", "resource_process" });

        foreach (string table in tables)
        {
            while (true)
            {
                int deleted = 0;

                Locked(c =>
                {
                    c.CommandText = $"delete from {table} where id in (select id from {table} where at_ms < $ms limit 5000);";
                    c.Parameters.AddWithValue("$ms", ms);
                    deleted = c.ExecuteNonQuery();
                });

                total += deleted;
                if (deleted < 5000) break;
            }
        }

        Locked(c =>
        {
            c.CommandText = "delete from recording_session where coalesce(finished_ms, started_ms) < $ms;";
            c.Parameters.AddWithValue("$ms", ms);
            total += c.ExecuteNonQuery();
        });

        return total;
    }

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// One local day's lines, oldest first. <paramref name="area"/> null reads every area (not
    /// the errors table, whose lines are already in their areas); <see cref="LogArea.Errors"/>
    /// reads the warnings and errors alone.
    /// </summary>
    public LogLine[] ReadDay(DateTime day, LogArea? area = null, int limit = 100_000)
    {
        DateTimeOffset from = new(day.Date, TimeZoneInfo.Local.GetUtcOffset(day.Date));
        DateTimeOffset to = new(day.Date.AddDays(1), TimeZoneInfo.Local.GetUtcOffset(day.Date.AddDays(1)));
        return Read(from, to, area, limit);
    }

    public LogLine[] Read(DateTimeOffset from, DateTimeOffset to, LogArea? area = null, int limit = 100_000)
    {
        IEnumerable<LogArea> tables = area is { } one ? new[] { one } : Areas;

        string sql = string.Join(" union all ", tables.Select(a =>
            $"select at_ms, seq, timecode, level, source, event, correlation, file, message, detail from {TableFor(a)} where at_ms >= $from and at_ms < $to"));

        var lines = new List<LogLine>();

        using SqliteConnection c = OpenReader();
        using SqliteCommand q = c.CreateCommand();
        q.CommandText = $"select * from ({sql}) order by at_ms, seq limit $limit;";
        q.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
        q.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());
        q.Parameters.AddWithValue("$limit", limit);

        using SqliteDataReader r = q.ExecuteReader();

        while (r.Read())
        {
            lines.Add(new LogLine(
                Seq: r.GetInt64(1),
                At: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(0)).ToLocalTime(),
                Timecode: r.IsDBNull(2) ? null : r.GetString(2),
                Source: Enum.TryParse(r.GetString(4), out LogSource s) ? s : LogSource.App,
                Level: Enum.TryParse(r.GetString(3), out LogLevel l) ? l : LogLevel.Info,
                Message: r.GetString(8),
                Event: r.IsDBNull(5) ? null : r.GetString(5),
                Correlation: r.IsDBNull(6) ? null : r.GetString(6),
                File: r.IsDBNull(7) ? null : r.GetString(7),
                Detail: r.IsDBNull(9) ? null : r.GetString(9)));
        }

        return lines.ToArray();
    }

    /// <summary>Resource samples between two moments, oldest first, each with its drives.</summary>
    public ResourceSample[] ReadSamples(DateTimeOffset from, DateTimeOffset to)
    {
        using SqliteConnection c = OpenReader();

        var disks = new Dictionary<long, List<DiskSample>>();

        using (SqliteCommand q = c.CreateCommand())
        {
            q.CommandText = @"select sample_id, disk, active_percent, read_bps, write_bps, queue, free_bytes, total_bytes
                              from resource_disk where at_ms >= $from and at_ms < $to order by id;";
            q.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
            q.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

            using SqliteDataReader r = q.ExecuteReader();
            while (r.Read())
            {
                long id = r.GetInt64(0);
                if (!disks.TryGetValue(id, out List<DiskSample>? list)) disks[id] = list = new();
                list.Add(new DiskSample(r.GetString(1), r.GetDouble(2), r.GetDouble(3), r.GetDouble(4),
                                        r.GetDouble(5), r.GetInt64(6), r.GetInt64(7)));
            }
        }

        var samples = new List<ResourceSample>();

        using (SqliteCommand q = c.CreateCommand())
        {
            q.CommandText = @"select id, at_ms, session, cpu_percent, ram_used, ram_total, emerald_private, ffmpeg_private,
                                     gpu_percent, gpu_encode_percent, gpu_memory
                              from resource_sample where at_ms >= $from and at_ms < $to order by at_ms;";
            q.Parameters.AddWithValue("$from", from.ToUnixTimeMilliseconds());
            q.Parameters.AddWithValue("$to", to.ToUnixTimeMilliseconds());

            using SqliteDataReader r = q.ExecuteReader();
            while (r.Read())
            {
                long id = r.GetInt64(0);
                samples.Add(new ResourceSample(
                    At: DateTimeOffset.FromUnixTimeMilliseconds(r.GetInt64(1)).ToLocalTime(),
                    CpuPercent: r.GetDouble(3),
                    RamUsedBytes: r.GetInt64(4),
                    RamTotalBytes: r.GetInt64(5),
                    EmeraldPrivateBytes: r.GetInt64(6),
                    FfmpegPrivateBytes: r.GetInt64(7),
                    GpuPercent: r.IsDBNull(8) ? null : r.GetDouble(8),
                    GpuEncodePercent: r.IsDBNull(9) ? null : r.GetDouble(9),
                    GpuMemoryBytes: r.IsDBNull(10) ? null : r.GetInt64(10),
                    Disks: disks.TryGetValue(id, out List<DiskSample>? d) ? d : Array.Empty<DiskSample>(),
                    Session: r.IsDBNull(2) ? null : r.GetString(2)));
            }
        }

        return samples.ToArray();
    }

    /// <summary>Rows in a table, for tests and for the Monitor's footer.</summary>
    public long Count(string table)
    {
        using SqliteConnection c = OpenReader();
        using SqliteCommand q = c.CreateCommand();
        q.CommandText = $"select count(*) from {table};";
        return (long)q.ExecuteScalar()!;
    }

    // ------------------------------------------------------------------ plumbing

    private SqliteConnection OpenReader()
    {
        var c = new SqliteConnection(ConnectionString(Path, readOnly: true));
        c.Open();
        return c;
    }

    private static string ConnectionString(string path, bool readOnly) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = readOnly ? SqliteOpenMode.ReadOnly : SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            // One connection per reader, closed after each read: a pool would keep the file
            // open long after the Monitor has gone.
            Pooling = false,
        }.ToString();

    private void Execute(string sql)
    {
        using SqliteCommand c = _write.CreateCommand();
        c.CommandText = sql;
        c.ExecuteNonQuery();
    }

    private void Locked(Action<SqliteCommand> work)
    {
        lock (_gate)
        {
            if (_disposed != 0) return;
            using SqliteCommand c = _write.CreateCommand();
            work(c);
        }
    }

    private static string Stamp(DateTimeOffset at) =>
        at.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture);

    public void Dispose()
    {
        lock (_gate)
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _write.Dispose();
        }
    }
}
