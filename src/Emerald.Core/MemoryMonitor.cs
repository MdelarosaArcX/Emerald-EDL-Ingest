using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace Emerald.Core;

/// <summary>One figure a module reports about itself: how much it holds, or how many.</summary>
public sealed record MemoryGauge(string Module, string Name, long Value, string Unit);

/// <summary>An ffmpeg or ffprobe this process started, and what it is using.</summary>
public sealed record ChildProcessMemory(int Id, string Name, string Role, long PrivateBytes, long WorkingSetBytes, int Threads);

/// <summary>
/// The process's memory at one moment, split the ways that say different things.
///
/// <b>Working set is not the figure to watch.</b> A delay ring is a mapped file, and every page of
/// it touched counts toward the working set although Windows can take it back at will. Private
/// bytes is what the process alone is answerable for, and the managed heap is the part of that
/// the garbage collector owns.
/// </summary>
public sealed record MemorySnapshot(
    DateTimeOffset At,
    double UptimeSeconds,
    long WorkingSetBytes,
    long PrivateBytes,
    long ManagedHeapBytes,
    long ManagedCommittedBytes,
    long Gen0Bytes,
    long Gen1Bytes,
    long Gen2Bytes,
    long LargeObjectHeapBytes,
    long PinnedHeapBytes,
    long FragmentedBytes,
    long PrivateOutsideManagedHeapBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections,
    double GcPauseMsTotal,
    double GcPausePercent,
    long AllocatedBytesTotal,
    double AllocatedBytesPerSecond,
    int Handles,
    int Threads,
    IReadOnlyList<ChildProcessMemory> Children,
    IReadOnlyList<MemoryGauge> Gauges);

/// <summary>
/// Where modules say what they are holding, so a figure for the process can be laid against the
/// things that make it up.
///
/// The managed heap cannot be divided between modules from the inside - that needs a profiler. What
/// can be said is what each module knows it holds: a ring's mapped bytes, a queue's depth, a
/// cache's entries. Those are the things that have turned out to matter.
/// </summary>
public static class MemoryGauges
{
    private sealed class Entry : IDisposable
    {
        public required string Module { get; init; }
        public required string Name { get; init; }
        public required string Unit { get; init; }
        public required Func<long> Read { get; init; }

        public void Dispose() => Entries.TryRemove(this, out _);
    }

    private static readonly ConcurrentDictionary<Entry, byte> Entries = new();
    private static readonly ConcurrentDictionary<int, string> Roles = new();

    /// <summary>
    /// Registers a figure, read whenever a snapshot is taken - from any thread, so it must be
    /// cheap and must not touch anything that belongs to the UI. Dispose to withdraw it.
    /// </summary>
    public static IDisposable Register(string module, string name, Func<long> read, string unit = "bytes")
    {
        var entry = new Entry { Module = module, Name = name, Unit = unit, Read = read };
        Entries[entry] = 0;
        return entry;
    }

    /// <summary>Says what a child process is for, so it is not just another ffmpeg in the list.</summary>
    public static void NameChild(int processId, string role) => Roles[processId] = role;

    internal static string RoleOf(int processId) =>
        Roles.TryGetValue(processId, out string? role) ? role : "unnamed";

    internal static void Forget(IEnumerable<int> alive)
    {
        var keep = new HashSet<int>(alive);

        foreach (int id in Roles.Keys)
            if (!keep.Contains(id)) Roles.TryRemove(id, out _);
    }

    public static IReadOnlyList<MemoryGauge> Read()
    {
        var read = new List<MemoryGauge>();

        foreach (Entry entry in Entries.Keys)
        {
            long value;

            // A gauge that throws is a gauge about something already gone.
            try { value = entry.Read(); }
            catch { continue; }

            read.Add(new MemoryGauge(entry.Module, entry.Name, value, entry.Unit));
        }

        read.Sort((a, b) => string.CompareOrdinal(a.Module + "/" + a.Name, b.Module + "/" + b.Name));
        return read;
    }
}

/// <summary>
/// A developer's view of what the application is using: on request, and written to a file every
/// few seconds when asked for.
///
/// Reads only - it never asks for a collection, which would change the thing being measured.
/// </summary>
public static class MemoryMonitor
{
    /// <summary>Set to 1 to write the record without touching the settings file.</summary>
    public const string EnvironmentSwitch = "EMERALD_MEMORY_LOG";

    private static readonly object Gate = new();
    private static readonly Stopwatch Since = Stopwatch.StartNew();
    private static long _lastAllocated = GC.GetTotalAllocatedBytes(false);
    private static double _lastAt;
    private static double _lastRate;

    private static Thread? _writer;
    private static CancellationTokenSource? _stop;

    public static MemorySnapshot Take()
    {
        using Process me = Process.GetCurrentProcess();

        GCMemoryInfo gc = GC.GetGCMemoryInfo(GCKind.Any);
        ReadOnlySpan<GCGenerationInfo> generations = gc.GenerationInfo;

        long Generation(ReadOnlySpan<GCGenerationInfo> all, int index) =>
            index < all.Length ? all[index].SizeAfterBytes : 0;

        long allocated = GC.GetTotalAllocatedBytes(false);
        double rate;

        // Between two snapshots, whoever asked for them. Close together they are too noisy to
        // mean anything, so the last figure worth having stands.
        lock (Gate)
        {
            double now = Since.Elapsed.TotalSeconds;

            if (now - _lastAt >= 1)
            {
                _lastRate = (allocated - _lastAllocated) / (now - _lastAt);
                _lastAllocated = allocated;
                _lastAt = now;
            }

            rate = _lastRate;
        }

        IReadOnlyList<ChildProcessMemory> children = Children(me.Id);
        long privateBytes = me.PrivateMemorySize64;

        return new MemorySnapshot(
            At: DateTimeOffset.Now,
            UptimeSeconds: Math.Round((DateTime.Now - me.StartTime).TotalSeconds),
            WorkingSetBytes: me.WorkingSet64,
            PrivateBytes: privateBytes,
            // Before the first collection there is nothing to report from one, so the heap is asked directly.
            ManagedHeapBytes: gc.HeapSizeBytes > 0 ? gc.HeapSizeBytes : GC.GetTotalMemory(false),
            ManagedCommittedBytes: gc.TotalCommittedBytes,
            Gen0Bytes: Generation(generations, 0),
            Gen1Bytes: Generation(generations, 1),
            Gen2Bytes: Generation(generations, 2),
            LargeObjectHeapBytes: Generation(generations, 3),
            PinnedHeapBytes: Generation(generations, 4),
            FragmentedBytes: gc.FragmentedBytes,
            PrivateOutsideManagedHeapBytes: Math.Max(0, privateBytes - gc.TotalCommittedBytes),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2),
            GcPauseMsTotal: Math.Round(GC.GetTotalPauseDuration().TotalMilliseconds, 1),
            GcPausePercent: gc.PauseTimePercentage,
            AllocatedBytesTotal: allocated,
            AllocatedBytesPerSecond: Math.Round(rate),
            Handles: me.HandleCount,
            Threads: me.Threads.Count,
            Children: children,
            Gauges: MemoryGauges.Read());
    }

    private static readonly JsonSerializerOptions Compact = new();

    /// <summary>A snapshot as one line of JSON, which is what the record holds and the monitor serves.</summary>
    public static string TakeJson() => JsonSerializer.Serialize(Take(), Compact);

    /// <summary>
    /// Writes a snapshot to the database's <c>resource_process</c> table every
    /// <paramref name="every"/>, until <see cref="Stop"/>. One row each: a day at ten seconds
    /// is a few megabytes.
    /// </summary>
    public static void Start(LogDatabase database, TimeSpan every)
    {
        lock (Gate)
        {
            if (_writer is not null) return;

            var stop = new CancellationTokenSource();
            _stop = stop;

            _writer = new Thread(() => WriteLoop(database, every, stop.Token))
            {
                IsBackground = true,
                Name = "memory monitor",
                Priority = ThreadPriority.BelowNormal,
            };

            _writer.Start();
        }
    }

    public static void Stop()
    {
        Thread? writer;

        lock (Gate)
        {
            writer = _writer;
            _writer = null;
            _stop?.Cancel();
        }

        writer?.Join(TimeSpan.FromSeconds(2));
    }

    private static void WriteLoop(LogDatabase database, TimeSpan every, CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                database.WriteProcessSnapshot(DateTimeOffset.Now, TakeJson());
            }
            catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
            {
                // A record that cannot be written is not worth anything else's time.
            }

            if (stop.WaitHandle.WaitOne(every)) return;
        }
    }

    // ------------------------------------------------------------------ child processes

    private static readonly string[] ChildNames = { "ffmpeg.exe", "ffprobe.exe" };

    /// <summary>
    /// The ffmpeg and ffprobe processes this one started. By parent rather than by name alone:
    /// another program's ffmpeg on the same machine is not this application's memory.
    /// </summary>
    /// <summary>The ffmpeg and ffprobe processes this one started, with their memory.</summary>
    public static IReadOnlyList<ChildProcessMemory> Children()
    {
        using Process me = Process.GetCurrentProcess();
        return Children(me.Id);
    }

    private static IReadOnlyList<ChildProcessMemory> Children(int parent)
    {
        var found = new List<ChildProcessMemory>();
        var alive = new List<int>();

        IntPtr snapshot = CreateToolhelp32Snapshot(SnapProcess, 0);
        if (snapshot == InvalidHandle) return found;

        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };

            for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
            {
                if (entry.ParentProcessId != parent) continue;
                if (!ChildNames.Contains(entry.ExeFile, StringComparer.OrdinalIgnoreCase)) continue;

                int id = (int)entry.ProcessId;
                alive.Add(id);

                try
                {
                    using Process child = Process.GetProcessById(id);

                    found.Add(new ChildProcessMemory(id, Path.GetFileNameWithoutExtension(entry.ExeFile),
                                                     MemoryGauges.RoleOf(id), child.PrivateMemorySize64,
                                                     child.WorkingSet64, child.Threads.Count));
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException
                                               or System.ComponentModel.Win32Exception)
                {
                    // Gone between the snapshot and the question.
                }
            }
        }
        finally
        {
            CloseHandle(snapshot);
        }

        MemoryGauges.Forget(alive);
        return found;
    }

    private const uint SnapProcess = 0x00000002;
    private static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public IntPtr DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);
}
