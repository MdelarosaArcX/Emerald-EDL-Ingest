using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace Emerald.Core;

/// <summary>
/// The machine's processor, memory, graphics and drives, as Task Manager shows them.
///
/// <b>When it runs.</b> Only while something wants it: a Monitor open (<see cref="Watch"/>), or
/// a recording running. It samples once a second; while a recording runs every fifth sample is
/// written to the database with the recording's id, from <c>capture.started</c> to
/// <c>capture.finished</c>, so a recording's resources can be read back beside its log.
///
/// <b>How.</b> Windows' own counters, through PDH and two kernel calls; no package and no
/// driver. Drives are physical disks, as Task Manager's "Disk 1 (E:)", with active time
/// (100 − idle time), which is the figure that reads 100% when a disk is saturated. The
/// graphics figure is the busiest engine's, as Task Manager's is; the encoder's is given apart
/// because the proxy is encoded there. A machine without graphics counters gets nulls.
/// </summary>
public sealed class ResourceSampler : IDisposable
{
    public static ResourceSampler Shared { get; } = new();

    /// <summary>Samples kept in memory: ten minutes at one a second.</summary>
    public const int HistoryLength = 600;

    /// <summary>Every how many samples one goes to the database while recording.</summary>
    public const int StoreEvery = 5;

    private readonly object _gate = new();
    private readonly Queue<ResourceSample> _history = new(HistoryLength);
    private readonly AutoResetEvent _wake = new(false);

    private Thread? _thread;
    private int _watchers;
    private string? _session;
    private LogDatabase? _database;
    private bool _disposed;

    private Counters? _counters;
    private long _lastIdle, _lastKernel, _lastUser;

    /// <summary>Raised on the sampler's thread after each sample. A UI subscriber must marshal.</summary>
    public event Action<ResourceSample>? Sampled;

    public ResourceSample? Latest
    {
        get { lock (_gate) return _history.Count == 0 ? null : _history.Last(); }
    }

    /// <summary>The last ten minutes, oldest first.</summary>
    public ResourceSample[] History()
    {
        lock (_gate) return _history.ToArray();
    }

    /// <summary>The recording being sampled into the database, or null.</summary>
    public string? Session
    {
        get { lock (_gate) return _session; }
    }

    /// <summary>
    /// Follows the activity record for recordings starting and finishing, and writes their
    /// samples to <paramref name="database"/>.
    /// </summary>
    public void Attach(ActivityLog log, LogDatabase? database)
    {
        lock (_gate) _database = database;
        log.Line += OnLine;
    }

    private void OnLine(LogLine line)
    {
        if (line.Event == "capture.started")
        {
            string id = line.Correlation ?? Guid.NewGuid().ToString("N");
            LogDatabase? db;

            lock (_gate) { _session = id; db = _database; }

            if (db is not null)
                Task.Run(() => { try { db.SessionStarted(id, line.At, line.Message); } catch { } });

            EnsureRunning();
        }
        else if (line.Event == "capture.finished")
        {
            string? id;
            LogDatabase? db;

            lock (_gate)
            {
                id = line.Correlation ?? _session;
                if (id == _session) _session = null;
                db = _database;
            }

            if (db is not null && id is not null)
                Task.Run(() => { try { db.SessionFinished(id, line.At); } catch { } });
        }
    }

    /// <summary>Keeps sampling until the returned handle is disposed. For a Monitor that is open.</summary>
    public IDisposable Watch()
    {
        Interlocked.Increment(ref _watchers);
        EnsureRunning();
        return new Watcher(this);
    }

    private sealed class Watcher : IDisposable
    {
        private ResourceSampler? _owner;
        public Watcher(ResourceSampler owner) => _owner = owner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _owner, null) is { } owner) Interlocked.Decrement(ref owner._watchers);
        }
    }

    private bool Wanted
    {
        get { lock (_gate) return !_disposed && (Volatile.Read(ref _watchers) > 0 || _session is not null); }
    }

    private void EnsureRunning()
    {
        lock (_gate)
        {
            if (_disposed || _thread is { IsAlive: true }) return;

            _thread = new Thread(Loop)
            {
                IsBackground = true,
                Name = "resource sampler",
                Priority = ThreadPriority.BelowNormal,
            };
            _thread.Start();
        }
    }

    private void Loop()
    {
        int count = 0;

        // The rate counters need two readings before they have a value; the first is thrown away.
        try { Sample(); } catch { }

        while (Wanted)
        {
            _wake.WaitOne(1000);
            if (!Wanted) break;

            ResourceSample sample;
            try { sample = Sample(); }
            catch { continue; }

            LogDatabase? db;
            lock (_gate)
            {
                sample = sample with { Session = _session };
                _history.Enqueue(sample);
                while (_history.Count > HistoryLength) _history.Dequeue();
                db = _session is not null ? _database : null;
            }

            if (db is not null && count++ % StoreEvery == 0)
            {
                try { db.WriteSample(sample); } catch { /* the record is not worth the sampler */ }
            }

            foreach (Delegate handler in Sampled?.GetInvocationList() ?? Array.Empty<Delegate>())
            {
                try { ((Action<ResourceSample>)handler).Invoke(sample); } catch { }
            }
        }

        lock (_gate)
        {
            _thread = null;
            _counters?.Dispose();
            _counters = null;
        }

        // Asked for again between the check and the clear: start over.
        if (Wanted) EnsureRunning();
    }

    // ------------------------------------------------------------------ one sample

    /// <summary>Takes one sample now. Rates are since the previous call.</summary>
    public ResourceSample Sample()
    {
        double cpu = Cpu();
        (long used, long total) = Ram();

        long emerald;
        using (Process me = Process.GetCurrentProcess()) emerald = me.PrivateMemorySize64;

        long ffmpeg = 0;
        try { ffmpeg = MemoryMonitor.Children().Sum(c => c.PrivateBytes); } catch { }

        Counters counters;
        lock (_gate) counters = _counters ??= new Counters();

        counters.Collect();

        return new ResourceSample(
            At: DateTimeOffset.Now,
            CpuPercent: Math.Round(cpu, 1),
            RamUsedBytes: used,
            RamTotalBytes: total,
            EmeraldPrivateBytes: emerald,
            FfmpegPrivateBytes: ffmpeg,
            GpuPercent: counters.GpuPercent,
            GpuEncodePercent: counters.GpuEncodePercent,
            GpuMemoryBytes: counters.GpuMemoryBytes,
            Disks: counters.Disks());
    }

    private double Cpu()
    {
        if (!GetSystemTimes(out long idle, out long kernel, out long user)) return 0;

        long dIdle = idle - _lastIdle, dKernel = kernel - _lastKernel, dUser = user - _lastUser;
        (_lastIdle, _lastKernel, _lastUser) = (idle, kernel, user);

        long busy = dKernel + dUser;   // kernel time includes idle time
        return busy <= 0 ? 0 : Math.Clamp(100.0 * (busy - dIdle) / busy, 0, 100);
    }

    private static (long Used, long Total) Ram()
    {
        var status = new MemoryStatusEx { Length = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref status)) return (0, 0);
        return ((long)(status.TotalPhys - status.AvailPhys), (long)status.TotalPhys);
    }

    /// <summary>"0 C:" gives "Disk 0 (C:)"; "1 E: F:" gives "Disk 1 (E: F:)".</summary>
    public static string DiskName(string instance)
    {
        string[] parts = instance.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return instance;
        return parts.Length == 1 ? $"Disk {parts[0]}" : $"Disk {parts[0]} ({string.Join(" ", parts.Skip(1))})";
    }

    /// <summary>
    /// The busiest graphics engine, as Task Manager counts it: each engine's use summed across
    /// every process using it, then the highest. Instances look like
    /// <c>pid_1234_luid_0x0_0xC4B5_phys_0_eng_3_engtype_VideoEncode</c>.
    /// </summary>
    public static (double? All, double? Encode) GpuBusiest(IEnumerable<(string Instance, double Value)> engines)
    {
        var byEngine = new Dictionary<string, (double Sum, string Type)>();

        foreach ((string instance, double value) in engines)
        {
            int luid = instance.IndexOf("luid_", StringComparison.Ordinal);
            int type = instance.IndexOf("_engtype_", StringComparison.Ordinal);
            if (luid < 0 || type < luid) continue;

            string key = instance[luid..type];
            string kind = instance[(type + "_engtype_".Length)..];

            byEngine[key] = byEngine.TryGetValue(key, out var had) ? (had.Sum + value, kind) : (value, kind);
        }

        if (byEngine.Count == 0) return (null, null);

        double all = Math.Min(100, byEngine.Values.Max(e => e.Sum));
        double encode = Math.Min(100, byEngine.Values
            .Where(e => e.Type.Contains("Encode", StringComparison.OrdinalIgnoreCase))
            .Select(e => e.Sum).DefaultIfEmpty(0).Max());

        return (Math.Round(all, 1), Math.Round(encode, 1));
    }

    public void Dispose()
    {
        lock (_gate) _disposed = true;
        _wake.Set();
    }

    // ------------------------------------------------------------------ PDH

    private sealed class Counters : IDisposable
    {
        private readonly IntPtr _query;
        private readonly IntPtr _idle, _read, _write, _queue, _engine, _gpuMemory;

        public double? GpuPercent { get; private set; }
        public double? GpuEncodePercent { get; private set; }
        public long? GpuMemoryBytes { get; private set; }

        public Counters()
        {
            if (PdhOpenQueryW(null, IntPtr.Zero, out _query) != 0) { _query = IntPtr.Zero; return; }

            _idle = Add(@"\PhysicalDisk(*)\% Idle Time");
            _read = Add(@"\PhysicalDisk(*)\Disk Read Bytes/sec");
            _write = Add(@"\PhysicalDisk(*)\Disk Write Bytes/sec");
            _queue = Add(@"\PhysicalDisk(*)\Current Disk Queue Length");
            _engine = Add(@"\GPU Engine(*)\Utilization Percentage");
            _gpuMemory = Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
        }

        private IntPtr Add(string path) =>
            PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out IntPtr counter) == 0 ? counter : IntPtr.Zero;

        public void Collect()
        {
            if (_query == IntPtr.Zero) return;
            PdhCollectQueryData(_query);

            List<(string, double)> engines = Read(_engine);
            (GpuPercent, GpuEncodePercent) = _engine == IntPtr.Zero ? (null, null) : GpuBusiest(engines);

            List<(string, double)> memory = Read(_gpuMemory);
            GpuMemoryBytes = memory.Count == 0 ? null : (long)memory.Sum(m => m.Item2);
        }

        public IReadOnlyList<DiskSample> Disks()
        {
            Dictionary<string, double> read = Read(_read).ToDictionary(x => x.Item1, x => x.Item2);
            Dictionary<string, double> write = Read(_write).ToDictionary(x => x.Item1, x => x.Item2);
            Dictionary<string, double> queue = Read(_queue).ToDictionary(x => x.Item1, x => x.Item2);

            var disks = new List<DiskSample>();

            foreach ((string instance, double idle) in Read(_idle))
            {
                if (instance == "_Total") continue;

                long free = 0, total = 0;
                foreach (string letter in instance.Split(' ').Where(p => p.Length == 2 && p[1] == ':'))
                {
                    try
                    {
                        var drive = new DriveInfo(letter);
                        if (!drive.IsReady) continue;
                        free += drive.AvailableFreeSpace;
                        total += drive.TotalSize;
                    }
                    catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException) { }
                }

                disks.Add(new DiskSample(
                    DiskName(instance),
                    Math.Round(Math.Clamp(100 - idle, 0, 100), 1),
                    Math.Round(read.GetValueOrDefault(instance)),
                    Math.Round(write.GetValueOrDefault(instance)),
                    queue.GetValueOrDefault(instance),
                    free,
                    total));
            }

            return disks.OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase).ToList();
        }

        private static List<(string, double)> Read(IntPtr counter)
        {
            var values = new List<(string, double)>();
            if (counter == IntPtr.Zero) return values;

            uint size = 0, count = 0;
            int status = PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, IntPtr.Zero);
            if (status != PdhMoreData || size == 0) return values;

            IntPtr buffer = Marshal.AllocHGlobal((int)size);

            try
            {
                if (PdhGetFormattedCounterArrayW(counter, PdhFmtDouble, ref size, ref count, buffer) != 0) return values;

                int stride = IntPtr.Size + 16;   // name pointer, then status, padding and the 8-byte value

                for (int i = 0; i < count; i++)
                {
                    IntPtr item = buffer + i * stride;
                    string? name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
                    uint itemStatus = (uint)Marshal.ReadInt32(item + IntPtr.Size);
                    double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));

                    // Valid data, or new data: anything else is a counter without a reading yet.
                    if (name is null || itemStatus > 1) continue;
                    values.Add((name, value));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }

            return values;
        }

        public void Dispose()
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);
        }
    }

    private const uint PdhFmtDouble = 0x00000200;
    private const int PdhMoreData = unchecked((int)0x800007D2);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhAddEnglishCounterW(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern int PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern int PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, ref uint itemCount, IntPtr items);

    [DllImport("pdh.dll")]
    private static extern int PdhCloseQuery(IntPtr query);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint Length;
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);
}
