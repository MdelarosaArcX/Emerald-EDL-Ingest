using System.IO;
using Microsoft.EntityFrameworkCore;

namespace Emerald.Ingest;

/// <summary>
/// Where ingest jobs and their recordings live between runs.
///
/// A job booked for 03:00 has to survive the operator closing Emerald at midnight, and a
/// history of what was recorded is worth more the older it gets. Emerald keeps preferences
/// in settings.json, which is right for preferences and wrong for a growing table of jobs,
/// so this is the one part of the solution that has a database.
///
/// It stores information <b>about</b> recordings. The video stays on disk.
/// </summary>
public interface IIngestStore
{
    /// <summary>Creates the database if it is not there. Safe to call more than once.</summary>
    void Initialise();

    /// <summary>Inserts or updates one job.</summary>
    void Save(IngestJob job);

    void SaveRecording(IngestRecording recording);

    /// <summary>Jobs that had not finished when the process last stopped, oldest first.</summary>
    IReadOnlyList<IngestJob> LoadUnfinished();

    /// <summary>Finished jobs, newest first.</summary>
    IReadOnlyList<IngestJob> History(int limit = 200);

    /// <summary>Completed recordings, newest first — what "Recent Ingests" lists.</summary>
    IReadOnlyList<IngestRecording> RecentRecordings(int limit = 20);

    IReadOnlyList<IngestRecording> RecordingsFor(Guid jobId);

    /// <summary>True when a clip of this name is already booked into this directory.</summary>
    bool ClipNameTaken(string directory, string clipName, Guid exceptJobId);
}

/// <summary>
/// SQLite through EF Core, one context per operation.
///
/// The scheduler writes from its own thread while the UI reads from the dispatcher, and a
/// DbContext is not safe to share across the two. Contexts are cheap; a lost ingest is not,
/// so every call opens and closes its own rather than passing one around and hoping.
/// </summary>
public sealed class SqliteIngestStore : IIngestStore
{
    private readonly string _databasePath;
    private readonly object _gate = new();

    /// <summary>Beside settings.json, so an operator's Emerald state is all in one folder.</summary>
    public static string DefaultDatabasePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Emerald", "ingest.db");

    public SqliteIngestStore(string? databasePath = null) =>
        _databasePath = databasePath ?? DefaultDatabasePath;

    public string DatabasePath => _databasePath;

    private IngestDbContext Open() => new(_databasePath);

    public void Initialise()
    {
        lock (_gate)
        {
            string? folder = Path.GetDirectoryName(_databasePath);
            if (!string.IsNullOrEmpty(folder)) Directory.CreateDirectory(folder);

            using IngestDbContext db = Open();
            db.Database.EnsureCreated();
        }
    }

    public void Save(IngestJob job)
    {
        lock (_gate)
        {
            using IngestDbContext db = Open();

            // Upsert by hand: the job the caller holds is the live object the UI is bound to,
            // and attaching it to a context would tie its lifetime to one.
            IngestJob? existing = db.Jobs.FirstOrDefault(j => j.Id == job.Id);

            if (existing is null) db.Jobs.Add(Clone(job));
            else CopyInto(job, existing);

            db.SaveChanges();
        }
    }

    public void SaveRecording(IngestRecording recording)
    {
        lock (_gate)
        {
            using IngestDbContext db = Open();

            IngestRecording? existing = db.Recordings.FirstOrDefault(r => r.Id == recording.Id);
            if (existing is null) db.Recordings.Add(recording);
            else db.Entry(existing).CurrentValues.SetValues(recording);

            db.SaveChanges();
        }
    }

    public IReadOnlyList<IngestJob> LoadUnfinished()
    {
        lock (_gate)
        {
            using IngestDbContext db = Open();

            return db.Jobs.AsNoTracking()
                .Where(j => j.Status == IngestStatus.Created
                         || j.Status == IngestStatus.Scheduled
                         || j.Status == IngestStatus.Waiting
                         || j.Status == IngestStatus.Recording)
                .OrderBy(j => j.CreatedAt)
                .ToList();
        }
    }

    public IReadOnlyList<IngestJob> History(int limit = 200)
    {
        lock (_gate)
        {
            using IngestDbContext db = Open();

            return db.Jobs.AsNoTracking()
                .Where(j => j.Status == IngestStatus.Completed
                         || j.Status == IngestStatus.Cancelled
                         || j.Status == IngestStatus.Failed)
                .OrderByDescending(j => j.CompletedAt ?? j.CreatedAt)
                .Take(limit)
                .ToList();
        }
    }

    public IReadOnlyList<IngestRecording> RecentRecordings(int limit = 20)
    {
        lock (_gate)
        {
            using IngestDbContext db = Open();

            return db.Recordings.AsNoTracking()
                .Where(r => r.Status == IngestStatus.Completed)
                .OrderByDescending(r => r.CompletedAt ?? r.StartedAt)
                .Take(limit)
                .ToList();
        }
    }

    public IReadOnlyList<IngestRecording> RecordingsFor(Guid jobId)
    {
        lock (_gate)
        {
            using IngestDbContext db = Open();
            return db.Recordings.AsNoTracking()
                .Where(r => r.IngestJobId == jobId)
                .OrderBy(r => r.StartedAt)
                .ToList();
        }
    }

    public bool ClipNameTaken(string directory, string clipName, Guid exceptJobId)
    {
        lock (_gate)
        {
            using IngestDbContext db = Open();

            // Only jobs that still intend to write count. A completed job of the same name is
            // caught by the on-disk check instead, which is the one that actually matters.
            return db.Jobs.AsNoTracking().Any(j =>
                j.Id != exceptJobId &&
                j.ClipName == clipName &&
                j.Directory == directory &&
                (j.Status == IngestStatus.Created
                 || j.Status == IngestStatus.Scheduled
                 || j.Status == IngestStatus.Waiting
                 || j.Status == IngestStatus.Recording));
        }
    }

    private static IngestJob Clone(IngestJob job)
    {
        var copy = new IngestJob { Id = job.Id };
        CopyInto(job, copy);
        return copy;
    }

    /// <summary>
    /// Field by field rather than by reflection: an ingest row is small, and a property that
    /// silently stopped being persisted is exactly the kind of thing that is only noticed
    /// after a restart has lost a job.
    /// </summary>
    private static void CopyInto(IngestJob from, IngestJob to)
    {
        to.ClipName = from.ClipName;
        to.BoardIndex = from.BoardIndex;
        to.BoardName = from.BoardName;
        to.Port = from.Port;
        to.PortIndex = from.PortIndex;
        to.FrameRate = from.FrameRate;
        to.ReferenceTimecode = from.ReferenceTimecode;
        to.Som = from.Som;
        to.Eom = from.Eom;
        to.Duration = from.Duration;
        to.ActualStartTimecode = from.ActualStartTimecode;
        to.Directory = from.Directory;
        to.Metadata = from.Metadata;
        to.Status = from.Status;
        to.CreatedAt = from.CreatedAt;
        to.ScheduledAt = from.ScheduledAt;
        to.StartedAt = from.StartedAt;
        to.CompletedAt = from.CompletedAt;
        to.FilePath = from.FilePath;
        to.ProxyPath = from.ProxyPath;
        to.FileSize = from.FileSize;
        to.ErrorMessage = from.ErrorMessage;
        to.Mock = from.Mock;
        to.FramesRecorded = from.FramesRecorded;
    }
}

/// <summary>The two tables. Kept internal: the store is the only way in.</summary>
internal sealed class IngestDbContext : DbContext
{
    private readonly string _path;

    public IngestDbContext(string path) => _path = path;

    public DbSet<IngestJob> Jobs => Set<IngestJob>();
    public DbSet<IngestRecording> Recordings => Set<IngestRecording>();

    protected override void OnConfiguring(DbContextOptionsBuilder options) =>
        options.UseSqlite($"Data Source={_path}");

    protected override void OnModelCreating(ModelBuilder model)
    {
        model.Entity<IngestJob>(job =>
        {
            job.HasKey(j => j.Id);

            // As text, not as an integer. A status column somebody can read in a SQLite
            // browser at three in the morning is worth the handful of bytes.
            job.Property(j => j.Status).HasConversion<string>();

            job.HasIndex(j => j.Status);
            job.HasIndex(j => new { j.Directory, j.ClipName });
        });

        model.Entity<IngestRecording>(rec =>
        {
            rec.HasKey(r => r.Id);
            rec.Property(r => r.Status).HasConversion<string>();
            rec.HasIndex(r => r.IngestJobId);
        });
    }
}
