using System.IO;
using System.IO.Pipes;
using System.Windows;
using Emerald.Core;
using Emerald.Edl;
using Emerald.Ingest;

namespace Emerald.App;

/// <summary>
/// Emerald's entry point.
///
/// The shell is what opens by default, but a module can also be started on its own:
///
///     Emerald.App.exe            the capture deck, with everything reachable from it
///     Emerald.App.exe --edl      the EDL Generator alone
///     Emerald.App.exe --ingest   the Ingest Controller alone
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

    private CancellationTokenSource? _listener;

    private void App_Startup(object sender, StartupEventArgs e)
    {
        string? mode = e.Args.FirstOrDefault(a => a.StartsWith("--", StringComparison.Ordinal))
                             ?.ToLowerInvariant();

        if (mode is not (null or "--edl" or "--ingest"))
        {
            MessageBox.Show(
                $"Unknown option \"{mode}\".\n\n" +
                "Emerald.App.exe            the capture deck\n" +
                "Emerald.App.exe --edl      the EDL Generator\n" +
                "Emerald.App.exe --ingest   the Ingest Controller",
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
        StartListening();
        OpenModule(mode);
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
            _ => new ShellWindow(),
        };

        Show(window);
    }

    private T? Existing<T>() where T : Window => Windows.OfType<T>().FirstOrDefault();

    private void Show(Window window)
    {
        MainWindow ??= window;

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

        _listener?.Cancel();
        _listener?.Dispose();

        try { _instance?.ReleaseMutex(); } catch { /* never owned it */ }
        _instance?.Dispose();

        base.OnExit(e);
    }
}
