using System.IO;
using System.IO.Pipes;
using System.Windows;
using Emerald.Core;
using Emerald.Edl;
using Emerald.Ingest;
using Emerald.Video;

namespace Emerald.App;

/// <summary>
/// Emerald's entry point.
///
/// The shell is what opens by default, but a module can also be started on its own:
///
///     Emerald.App.exe            the capture deck, with everything reachable from it
///     Emerald.App.exe --edl      the EDL Generator alone
///     Emerald.App.exe --ingest   the Ingest Controller alone
///     Emerald.App.exe --monitor  the monitoring page alone
///
/// <b>Emerald only ever runs once.</b> Launching it again while it is already up does not
/// start a second copy: the new process hands its request to the one already running,
/// which opens that window, and then exits. So run-edl.bat and run-ingest.bat give you two
/// windows side by side, and neither closes the other.
///
/// That is not a nicety. The receiver allows exactly one open handle, and Emerald arbitrates
/// between its own claimants — the deck's preview, the EDL's recorder, an ingest — through
/// <see cref="Emerald.Deltacast.RxLease"/>, which is a lock inside one process. Two Emerald
/// processes could not negotiate over a card neither of them owns; the second would simply
/// fail with a channel-in-use error. One process, many windows, is the only arrangement in
/// which those modules can share hardware.
/// </summary>
public partial class App : Application
{
    private const string InstanceName = "Emerald.App.SingleInstance";
    private const string PipeName = "Emerald.App.Activate";

    /// <summary>Held for the life of the process; owning it is what makes this the primary.</summary>
    private static Mutex? _instance;

    /// <summary>
    /// One settings object for the whole application, handed to every window.
    ///
    /// Emerald is one process with several windows, so it should have one copy of the
    /// operator's configuration in memory — otherwise the timecode generator's address
    /// changed in the EDL would be written to settings.json while the Ingest Controller went
    /// on holding the old one.
    /// </summary>
    public static AppSettings Settings { get; } = AppSettings.Load();

    /// <summary>
    /// Where the tidal lock delay ring is written. Local by default: it needs a fast disk.
    ///
    /// Never the drive the recordings go to. The ring takes about 100 MB/s of writes while a
    /// delay is held; on the drive the masters are written to it would leave no room for
    /// anything else. A ring folder set there is passed over for the default, and said so.
    /// </summary>
    public static string TidalLockRingFolder(AppSettings settings)
    {
        Emerald.Video.StorePaths store = Emerald.Media.MediaLibrary.PathsFor(settings);

        bool OnStore(string folder) => SameVolume(folder, store.High) || SameVolume(folder, store.Low);

        string fallback = AutomaticRingFolder(store, OnStore);

        if (string.IsNullOrWhiteSpace(settings.TidalLockRingFolder)) return fallback;

        string chosen = settings.TidalLockRingFolder;

        if (!OnStore(chosen) || OnStore(fallback)) return chosen;

        if (Interlocked.Exchange(ref _ringWarned, 1) == 0)
            ActivityLog.Shared.Warn(LogSource.Capture,
                $"The delay ring folder {chosen} is on the drive recordings go to, so the ring is kept in " +
                $"{fallback} instead. Choose a folder on another drive for it.",
                @event: "delay.samedisk");

        return fallback;
    }

    private static int _ringWarned;

    /// <summary>The folder used when none is set: what the delay path box goes back to when cleared.</summary>
    public static string AutomaticTidalLockRingFolder(AppSettings settings)
    {
        Emerald.Video.StorePaths store = Emerald.Media.MediaLibrary.PathsFor(settings);
        return AutomaticRingFolder(store, f => SameVolume(f, store.High) || SameVolume(f, store.Low));
    }

    /// <summary>The old default, under %LOCALAPPDATA% - on the Windows drive. Now the last resort.</summary>
    public static string SystemRingFolder => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Emerald", "delay");

    /// <summary>Free space a drive needs before the ring is put on it: the longest ready ring plus headroom.</summary>
    private const long RingDriveMinFree = 64L * 1024 * 1024 * 1024;

    private static string? _autoRing;
    private static string? _autoRingKey;
    private static readonly object _autoRingGate = new();

    /// <summary>
    /// Where the ring goes when no folder has been set.
    ///
    /// It used to be %LOCALAPPDATA%, which is always C:. The ring writes about 104 MB/s at
    /// 1080p25, nonstop, for as long as a recording runs. The Windows drive shares that with
    /// the page file, Windows Update, Defender and every other program, so it is the worst
    /// local drive to put it on, and when it fell behind, frames were missing on air. Now:
    /// the drive Emerald runs from if it is a local fixed drive that is not the Windows drive
    /// and not a recording drive; otherwise the local fixed drive like that with the most
    /// free space; and only if there is none, the old folder on C:.
    ///
    /// Worked out once per store layout and said in the log, so it is clear which disk a
    /// "could not keep up" line is about.
    /// </summary>
    private static string AutomaticRingFolder(Emerald.Video.StorePaths store, Func<string, bool> onStore)
    {
        string key = $"{store.High}|{store.Low}";

        lock (_autoRingGate)
        {
            if (_autoRing is not null && _autoRingKey == key) return _autoRing;

            string chosen = PickRingFolder(onStore) ?? SystemRingFolder;

            if (_autoRing != chosen)
                ActivityLog.Shared.Info(LogSource.Capture,
                    $"DELAY  ring folder is {chosen} ({DelayLine.DescribeDisk(chosen)})" +
                    (chosen == SystemRingFolder
                        ? " - no other local drive with room, so it is on the Windows drive. Set " +
                          "tidalLockRingFolder in settings.json to a fast drive of its own if frames drop."
                        : ", away from the Windows drive and the recording drives."),
                    @event: "delay.folder");

            _autoRing = chosen;
            _autoRingKey = key;
            return chosen;
        }
    }

    private static string? PickRingFolder(Func<string, bool> onStore)
    {
        string? system = SafeRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        string? appDrive = SafeRoot(AppContext.BaseDirectory);

        var candidates = new List<DriveInfo>();

        try
        {
            foreach (DriveInfo drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                    if (string.Equals(drive.RootDirectory.FullName, system, StringComparison.OrdinalIgnoreCase)) continue;
                    if (onStore(drive.RootDirectory.FullName)) continue;

                    // Sparse files, which the ring depends on to take only the disk it holds.
                    if (!string.Equals(drive.DriveFormat, "NTFS", StringComparison.OrdinalIgnoreCase) &&
                        !string.Equals(drive.DriveFormat, "ReFS", StringComparison.OrdinalIgnoreCase)) continue;

                    if (drive.AvailableFreeSpace < RingDriveMinFree) continue;

                    candidates.Add(drive);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }

        IEnumerable<DriveInfo> ordered = candidates
            .OrderByDescending(d => string.Equals(d.RootDirectory.FullName, appDrive, StringComparison.OrdinalIgnoreCase))
            .ThenByDescending(d => d.AvailableFreeSpace);

        foreach (DriveInfo drive in ordered)
        {
            string folder = System.IO.Path.Combine(drive.RootDirectory.FullName, "EmeraldDelay");

            try
            {
                Directory.CreateDirectory(folder);
                return folder;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        return null;
    }

    private static string? SafeRoot(string path)
    {
        try { return string.IsNullOrEmpty(path) ? null : System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(path)); }
        catch (ArgumentException) { return null; }
    }

    private static bool SameVolume(string a, string b)
    {
        try
        {
            return string.Equals(System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(a)),
                                 System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(b)),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (ArgumentException) { return false; }
    }

    /// <summary>
    /// The log database (<c>emerald.db</c> in the data folder), or null when it could not be
    /// opened anywhere - then the record is memory only and the start-up line says why.
    /// </summary>
    public static LogDatabase? Database { get; private set; }

    private CancellationTokenSource? _listener;

    private void App_Startup(object sender, StartupEventArgs e)
    {
        string? mode = e.Args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal))
                             ?.ToLowerInvariant();

        if (mode is not (null or "--edl" or "--ingest" or "--monitor"))
        {
            MessageBox.Show(
                $"Unknown option \"{mode}\".\n\n" +
                "Emerald.App.exe            the capture deck\n" +
                "Emerald.App.exe --edl      the EDL Generator\n" +
                "Emerald.App.exe --ingest   the Ingest Controller\n" +
                "Emerald.App.exe --monitor  the monitoring page",
                "Emerald", MessageBoxButton.OK, MessageBoxImage.Information);

            Shutdown(1);
            return;
        }

        _instance = new Mutex(initiallyOwned: true, InstanceName, out bool weArePrimary);

        if (!weArePrimary && Handoff(mode))
        {
            // Already running, and it has taken the request. Nothing more for this process
            // to do — the window opens over there.
            Shutdown(0);
            return;
        }

        // Either we are the first, or there is a stale mutex and nobody is listening. Both
        // mean this process is the one that runs.
        //
        // Six gigabytes of delay ring survives a crash perfectly well, and three crashes
        // would be eighteen. Swept once, here, before anything can allocate another.
        // The old default on C: too, so rings left there before the ring moved are not left for good.
        DelayLine.SweepOrphans(SystemRingFolder);
        DelayLine.SweepOrphans(TidalLockRingFolder(Settings));

        StartActivityLog();
        StartMemoryMonitor();

        // Before the first window exists: the render mode is the process's, and is read when
        // the first one is created.
        if (Settings.SoftwareRendering ||
            Environment.GetEnvironmentVariable("EMERALD_SOFTWARE_RENDER") == "1")
        {
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

            ActivityLog.Shared.Info(LogSource.App,
                "Drawing in software rather than on the graphics card, as the settings ask.",
                @event: "app.softwarerender");
        }

        StartListening();
        OpenModule(mode);
    }

    /// <summary>
    /// Opens the activity record and prunes what has aged out of it.
    ///
    /// Done here rather than by whichever window happens to open first, because the record has
    /// to be running before anything narrates into it — a capture that rolls before the log is
    /// writing is exactly the run somebody will want to read back.
    ///
    /// Also the point at which a crash stops being invisible: WPF's default is to take the
    /// process down with nothing but a Windows event-log entry, which is no use to an operator
    /// standing in front of a dark transmitter.
    /// </summary>
    /// <summary>
    /// What the caches hold, for whoever asks; and the ten-second record, for a developer who
    /// asked for it. Asking costs nothing until then: a gauge is only read when a snapshot is taken.
    /// </summary>
    private static void StartMemoryMonitor()
    {
        MemoryGauges.Register("Media", "probes remembered", () => Emerald.Media.MediaProbe.Remembered, "count");
        MemoryGauges.Register("Media", "codecs remembered", () => Emerald.Media.MediaCodec.Remembered, "count");
        MemoryGauges.Register("Media", "waveforms remembered", () => Emerald.Media.Waveform.Remembered, "count");
        MemoryGauges.Register("Media", "lengths remembered", () => Emerald.Media.MediaLengths.Remembered, "count");

        if (!Settings.MemoryDiagnostics &&
            Environment.GetEnvironmentVariable(MemoryMonitor.EnvironmentSwitch) != "1")
            return;

        if (Database is null) return;

        MemoryMonitor.Start(Database, TimeSpan.FromSeconds(10));

        ActivityLog.Shared.Info(LogSource.App,
            $"Memory use is being recorded every 10 s to {Database.Path} (resource_process).",
            @event: "app.memorylog");
    }

    private void StartActivityLog()
    {
        ActivityLog log = ActivityLog.Shared;
        string? problem = null;

        // The data folder the installer or the operator chose; if that cannot be opened (a
        // drive that is not there today), the default one, so the record is not lost with it.
        Database = OpenDatabase(Settings.DataFolderOrDefault, ref problem);
        if (Database is null && !string.Equals(Settings.DataFolderOrDefault, AppSettings.DefaultDataFolder,
                                                StringComparison.OrdinalIgnoreCase))
            Database = OpenDatabase(AppSettings.DefaultDataFolder, ref problem);

        int swept = ActivityLog.SweepOldFiles(Settings.LogFolderOrDefault, Settings.LogRetentionDays);
        if (Database is not null) log.StartWriting(Database, Settings.LogRetentionDays);

        ResourceSampler.Shared.Attach(log, Database);

        // Waveforms, once read, are kept beside the database rather than read again from the
        // recording disk every time Live Edit shows them.
        Emerald.Media.Waveform.CacheFolder = System.IO.Path.Combine(
            Database is null ? AppSettings.DefaultDataFolder : System.IO.Path.GetDirectoryName(Database.Path)!, "peaks");
        Emerald.Media.TrackCopy.Folder = System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(Emerald.Media.Waveform.CacheFolder)!, "tracks");
        _ = Task.Run(() => Emerald.Media.Waveform.SweepCache(Math.Max(30, Settings.LogRetentionDays)));

        DispatcherUnhandledException += (_, e) =>
        {
            log.Error(LogSource.App, $"Emerald stopped: {e.Exception.Message}",
                      @event: "app.crashed", detail: e.Exception.ToString());

            // Into the database before the process goes, which it may be about to.
            log.Flush(TimeSpan.FromSeconds(2));
        };

        log.Info(LogSource.App,
                 (Database is null
                     ? "Emerald started. The log database could not be opened, so the record is in memory only."
                     : $"Emerald started. Recording activity to {Database.Path}, keeping " +
                       (Settings.LogRetentionDays < 0 ? "everything." : $"{Settings.LogRetentionDays} day(s).")) +
                 (swept > 0 ? $" {swept} old log file(s) swept." : ""),
                 @event: "app.started");

        if (problem is not null)
            log.Warn(LogSource.App, $"The log database: {problem}", @event: "app.databasefailed");
    }

    private static LogDatabase? OpenDatabase(string folder, ref string? problem)
    {
        try
        {
            return new LogDatabase(LogDatabase.PathIn(folder));
        }
        catch (Exception ex)
        {
            problem = (problem is null ? "" : problem + " ") + $"{folder}: {ex.Message}";
            return null;
        }
    }

    // ------------------------------------------------------------------ windows

    /// <summary>
    /// Opens a module, or brings it forward if it is already open.
    ///
    /// When the deck is up it is asked to do this, so a window opened from the taskbar and
    /// one opened by a batch file are the same window, sharing the deck's settings object —
    /// which is what makes a recording profile chosen on the deck the one an ingest records
    /// with.
    /// </summary>
    public void OpenModule(string? mode)
    {
        if (Windows.OfType<ShellWindow>().FirstOrDefault() is { } shell)
        {
            shell.OpenModule(mode);
            return;
        }

        Window window = mode switch
        {
            "--edl" => Existing<EdlWindow>() ?? new EdlWindow(Settings),
            "--ingest" => Existing<IngestControllerWindow>() ?? new IngestControllerWindow(Settings),
            "--monitor" => Existing<MonitorWindow>() ?? new MonitorWindow(Settings),
            _ => new ShellWindow(),
        };

        Show(window);
    }

    private T? Existing<T>() where T : Window => Windows.OfType<T>().FirstOrDefault();

    private void Show(Window window)
    {
        MainWindow ??= window;

        // Here as well as in the shell, because the --edl, --ingest and --monitor modes open
        // their window directly without the shell ever being created.
        AppIcon.ApplyTo(window);

        if (!window.IsLoaded && !window.IsVisible)
        {
            window.Show();
            return;
        }

        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    // ------------------------------------------------------------------ handoff

    /// <summary>
    /// Passes the request to the running instance. False when there is nobody to pass it to,
    /// which means the mutex was left behind by a process that is gone.
    /// </summary>
    private static bool Handoff(string? mode)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            pipe.Connect(2000);

            using var writer = new StreamWriter(pipe) { AutoFlush = true };
            writer.WriteLine(mode ?? "");
            return true;
        }
        catch
        {
            // Timed out or refused. Rather than refuse to start, this process becomes the
            // instance — an operator who double-clicks Emerald should get Emerald.
            return false;
        }
    }

    private void StartListening()
    {
        _listener = new CancellationTokenSource();
        CancellationToken ct = _listener.Token;

        // One connection at a time is plenty: these arrive when somebody runs a batch file.
        Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    using var pipe = new NamedPipeServerStream(
                        PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);

                    await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);

                    using var reader = new StreamReader(pipe);
                    string? requested = (await reader.ReadLineAsync(ct).ConfigureAwait(false))?.Trim();

                    await Dispatcher.InvokeAsync(() =>
                        OpenModule(string.IsNullOrEmpty(requested) ? null : requested));
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch
                {
                    // A malformed or abandoned connection is not worth taking the listener
                    // down for; the next one is accepted normally.
                }
            }
        }, ct);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // The clock outlives every window, so it is closed here rather than by whichever
        // module happened to be shut last.
        TimecodeLink.Shutdown();
        MemoryMonitor.Stop();
        ResourceSampler.Shared.Dispose();

        // What is still queued reaches the database before it closes.
        ActivityLog.Shared.Flush(TimeSpan.FromSeconds(3));
        ActivityLog.Shared.Dispose();
        Database?.Dispose();

        _listener?.Cancel();
        _listener?.Dispose();

        try { _instance?.ReleaseMutex(); } catch { /* never owned it */ }
        _instance?.Dispose();

        base.OnExit(e);
    }
}
