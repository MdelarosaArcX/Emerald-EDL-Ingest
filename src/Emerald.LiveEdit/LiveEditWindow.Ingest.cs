using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Emerald.Core;
using Emerald.Media;
using Emerald.Video;

namespace Emerald.LiveEdit;

/// <summary>
/// The Ingest tab: ingest media kept one folder per capture recording (see <see cref="IngestLibrary"/>).
///
/// <b>Folders first.</b> It opens on the list of recording folders, newest first, the one on air at
/// the top; double-clicking one shows its clips. Only the low-res copies are listed, so only
/// low-res is dragged onto the timeline - what is placed, and sent to air, is the high-res beside it.
///
/// <b>Keeps itself up to date.</b> The ingest path is watched: a new recording folder, an export
/// finishing or a file dropped in from Explorer shows within a second, without Refresh. Anything
/// in a recording folder without a low-res copy - a file dropped at the folder's top level is moved
/// into high\ first - gets one made, one at a time, in the background.
/// </summary>
public partial class LiveEditWindow
{
    /// <summary>The folder open on the Ingest tab, or null for the list of folders.</summary>
    private IngestFolder? _ingestFolder;

    /// <summary>A clip to select once the open folder has been read - the export just made.</summary>
    private string? _ingestSelectAfter;

    private bool _listingIngest;
    private bool _ingestAgain;

    private FileSystemWatcher? _ingestWatcher;
    private string? _ingestWatched;
    private int _ingestChangeQueued;
    private bool _ingestTimersWired;

    // A burst of changes - a copy writing, an export finishing - read once, shortly after it starts.
    private readonly DispatcherTimer _ingestSettle =
        new(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(700) };

    // And a slow sweep under it all: a watcher can miss changes on a busy or network volume, and a
    // file still being copied when it was first seen is only finished later.
    private readonly DispatcherTimer _ingestSweep =
        new(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };

    private CancellationTokenSource? _lowResCancel;
    private int _lowResRunning;
    private readonly HashSet<string> _lowResFailed = new(StringComparer.OrdinalIgnoreCase);

    private string IngestRoot() => IngestLibrary.RootFor(_settings);

    // ------------------------------------------------------------------ listing

    /// <summary>
    /// Reads whatever the Ingest tab is showing - the folders, or the open folder's clips - off the
    /// dispatcher, since clips are probed. Asked again while reading, it reads once more after.
    /// </summary>
    private void LoadIngest()
    {
        if (_listingIngest)
        {
            _ingestAgain = true;
            return;
        }

        _listingIngest = true;

        string root = IngestRoot();
        EnsureLiveIngestFolder(root);

        string? live = IngestLibrary.LiveRecordingFolder();
        IngestFolder? open = _ingestFolder;
        string? ffprobe = MediaProbe.LocateFfprobe(Ffmpeg.Locate(_settings.FfmpegPath));
        int rate = _rate;

        Task.Run(() => open is null
                     ? (Folders: IngestLibrary.Folders(root, live), Clips: (IReadOnlyList<CapturedClip>?)null)
                     : (Folders: null, Clips: IngestLibrary.Clips(open, ffprobe, rate)))
            .ContinueWith(t =>
            {
                _listingIngest = false;
                if (_closed) return;

                // The view changed while it was being read: read the one showing now.
                if (!ReferenceEquals(open, _ingestFolder) || _ingestAgain)
                {
                    _ingestAgain = false;
                    LoadIngest();
                    return;
                }

                if (t.IsFaulted) return;

                if (t.Result.Folders is { } folders) ShowIngestFolders(folders, root);
                else ShowIngestClips(t.Result.Clips ?? [], open!, live);

            }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private void ShowIngestFolders(IReadOnlyList<IngestFolder> folders, string root)
    {
        string? was = (IngestFolderList.SelectedItem as IngestFolder)?.Path;

        IngestFolderHeader.Visibility = Visibility.Collapsed;
        IngestList.Visibility = Visibility.Collapsed;
        IngestFolderList.Visibility = Visibility.Visible;

        IngestFolderList.ItemsSource = folders;
        IngestFolderList.SelectedItem = folders.FirstOrDefault(f => string.Equals(f.Path, was, StringComparison.OrdinalIgnoreCase));

        IngestEmptyText.Text = $"No recording folders yet in {root}. One is made as soon as a recording starts; " +
                               "exports, Ingest Controller clips and video dropped here go into it.";
        IngestEmptyText.Visibility = folders.Count == 0 && !_exporting ? Visibility.Visible : Visibility.Collapsed;

        if (!_exporting)
            StoreText.Text = folders.Count == 0
                ? $"Ingest media is kept in {root}."
                : $"{folders.Count} folder{(folders.Count == 1 ? "" : "s")} in {root}";
    }

    private void ShowIngestClips(IReadOnlyList<CapturedClip> clips, IngestFolder open, string? live)
    {
        string? want = _ingestSelectAfter ?? (IngestList.SelectedItem as CapturedClip)?.Path;
        _ingestSelectAfter = null;

        IngestFolderList.Visibility = Visibility.Collapsed;
        IngestList.Visibility = Visibility.Visible;
        IngestFolderHeader.Visibility = Visibility.Visible;

        IngestFolderName.Text = open.DisplayName;
        IngestFolderLive.Visibility = !open.IsLoose && string.Equals(open.Name, live, StringComparison.OrdinalIgnoreCase)
            ? Visibility.Visible
            : Visibility.Collapsed;

        // Rebinding is not a choice by the operator: nothing restarts in the monitor because of it.
        _rebinding = true;
        IngestList.ItemsSource = clips;
        CapturedClip? select = clips.FirstOrDefault(c => string.Equals(c.Path, want, StringComparison.OrdinalIgnoreCase));
        IngestList.SelectedItem = select;
        if (select is not null) IngestList.ScrollIntoView(select);
        _rebinding = false;

        IngestEmptyText.Text = open.IsLoose
            ? "Nothing here."
            : "No clips in this folder yet. Export a marked range, or drop video files here - a low-res copy is made for each.";
        IngestEmptyText.Visibility = clips.Count == 0 && !_exporting ? Visibility.Visible : Visibility.Collapsed;

        if (!_exporting)
            StoreText.Text = $"{clips.Count} clip{(clips.Count == 1 ? "" : "s")} in {open.Path}";
    }

    // ------------------------------------------------------------------ folders

    private void OpenIngestFolder(IngestFolder folder)
    {
        _ingestFolder = folder;

        _rebinding = true;
        IngestList.ItemsSource = null;
        _rebinding = false;

        LoadIngest();
    }

    /// <summary>Opens a recording's ingest folder by its path, selecting <paramref name="select"/> once read.</summary>
    private void OpenIngestFolderAt(string folderPath, string? select = null)
    {
        string name = System.IO.Path.GetFileName(folderPath);

        _ingestSelectAfter = select;
        OpenIngestFolder(new IngestFolder(name, folderPath,
                                          RecordingSequence.FolderStartedAt(name) ?? DateTime.Now, 0,
                                          name == IngestLibrary.LiveRecordingFolder(), IsLoose: false));
    }

    private void IngestBack_Click(object sender, RoutedEventArgs e)
    {
        _ingestFolder = null;
        LoadIngest();
    }

    private void IngestFolderList_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (IngestFolderList.SelectedItem is IngestFolder folder) OpenIngestFolder(folder);
    }

    private void IngestFolderList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || IngestFolderList.SelectedItem is not IngestFolder folder) return;

        OpenIngestFolder(folder);
        e.Handled = true;
    }

    private void IngestList_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Back || _ingestFolder is null) return;

        IngestBack_Click(sender, e);
        e.Handled = true;
    }

    /// <summary>
    /// The recording on air has its ingest folder from the start, whoever started it - the deck
    /// makes it when it rolls, and this covers a recording the deck did not start.
    /// </summary>
    private static void EnsureLiveIngestFolder(string root)
    {
        if (IngestLibrary.LiveRecordingFolder() is { } live && !Directory.Exists(IngestLibrary.FolderFor(root, live)))
            IngestLibrary.Ensure(root, live);
    }

    // ------------------------------------------------------------------ the path

    private void IngestSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new IngestPathDialog(AppSettings.SavedIngestMediaFolder(), MediaLibrary.PathsFor(_settings))
        {
            Owner = this,
        };

        if (dialog.ShowDialog() != true) return;

        string chosen = dialog.ChosenPath;
        if (chosen == AppSettings.SavedIngestMediaFolder()) return;

        AppSettings.SaveIngestMediaFolder(chosen);
        _settings.IngestMediaFolder = chosen;

        string root = IngestRoot();

        ActivityLog.Shared.Info(LogSource.LiveEdit,
            $"INGEST  media path is now {root}{(chosen.Length == 0 ? " (the default)" : "")}.",
            @event: "liveedit.ingestpath", file: root);

        _ingestFolder = null;
        StartIngestWatch();
        LoadIngest();
    }

    // ------------------------------------------------------------------ watching

    /// <summary>Watches the ingest path, and sweeps it now and then. Called again when the path changes.</summary>
    private void StartIngestWatch()
    {
        if (!_ingestTimersWired)
        {
            _ingestTimersWired = true;

            _ingestSettle.Tick += (_, _) =>
            {
                _ingestSettle.Stop();
                if (_closed) return;

                if (_showingIngest) LoadIngest();
                KickLowResMaker();
            };

            _ingestSweep.Tick += (_, _) =>
            {
                if (_closed) return;

                EnsureLiveIngestFolder(IngestRoot());
                KickLowResMaker();

                // The folder list carries the ON AIR mark and the counts; cheap to read again.
                if (_showingIngest && _ingestFolder is null) LoadIngest();
            };

            _ingestSweep.Start();
        }

        string root = IngestRoot();

        if (_ingestWatcher is not null && string.Equals(_ingestWatched, root, StringComparison.OrdinalIgnoreCase))
            return;

        StopWatcher();

        try
        {
            Directory.CreateDirectory(root);

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size | NotifyFilters.LastWrite,
                InternalBufferSize = 64 * 1024,
            };

            watcher.Created += OnIngestChanged;
            watcher.Deleted += OnIngestChanged;
            watcher.Renamed += OnIngestChanged;
            watcher.Changed += OnIngestChanged;
            watcher.Error += (_, _) => QueueIngestRead();
            watcher.EnableRaisingEvents = true;

            _ingestWatcher = watcher;
            _ingestWatched = root;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // The sweep still finds everything, a little later.
            ActivityLog.Shared.Warn(LogSource.LiveEdit,
                $"INGEST  {root} cannot be watched ({ex.Message}); new media shows within 15 s instead.",
                @event: "liveedit.ingestwatchfailed", file: root);
        }

        KickLowResMaker();
    }

    private void StopIngestWatch()
    {
        _ingestSettle.Stop();
        _ingestSweep.Stop();
        StopWatcher();

        try { _lowResCancel?.Cancel(); } catch (ObjectDisposedException) { }
    }

    private void StopWatcher()
    {
        if (_ingestWatcher is null) return;

        _ingestWatcher.EnableRaisingEvents = false;
        _ingestWatcher.Dispose();
        _ingestWatcher = null;
        _ingestWatched = null;
    }

    /// <summary>From the watcher's thread: something under the ingest path changed.</summary>
    private void OnIngestChanged(object sender, FileSystemEventArgs e) => QueueIngestRead();

    private void QueueIngestRead()
    {
        if (Interlocked.Exchange(ref _ingestChangeQueued, 1) == 1) return;

        Dispatcher.BeginInvoke(() =>
        {
            Volatile.Write(ref _ingestChangeQueued, 0);

            // Started by the first change of a burst and not pushed back by the rest, so a long copy
            // still shows within a second rather than only once it stops.
            if (!_closed && !_ingestSettle.IsEnabled) _ingestSettle.Start();
        });
    }

    // ------------------------------------------------------------------ low-res copies

    /// <summary>
    /// Makes low-res copies for whatever in the ingest path lacks one, one at a time, in the
    /// background. Waits while an export runs - it is making its own low-res, and the two would
    /// share the card and the disk. A file that fails is not tried again until Live Edit reopens.
    /// </summary>
    private void KickLowResMaker()
    {
        if (_closed || Interlocked.CompareExchange(ref _lowResRunning, 1, 0) != 0) return;

        if (Ffmpeg.Locate(_settings.FfmpegPath) is not { } ffmpeg)
        {
            Volatile.Write(ref _lowResRunning, 0);
            return;
        }

        string root = IngestRoot();
        int rate = _rate;
        var cancel = new CancellationTokenSource();
        _lowResCancel = cancel;

        _ = Task.Run(() =>
        {
            try
            {
                while (!cancel.IsCancellationRequested)
                {
                    if (Volatile.Read(ref _exportCancel) is not null)
                    {
                        Thread.Sleep(1000);
                        continue;
                    }

                    string? next;
                    lock (_lowResFailed) next = IngestLibrary.Pending(root).FirstOrDefault(p => !_lowResFailed.Contains(p));

                    if (next is null) break;

                    MakeLowRes(ffmpeg, next, rate, cancel.Token);
                }
            }
            catch (Exception ex)
            {
                ActivityLog.Shared.Warn(LogSource.LiveEdit, $"INGEST  the low-res maker stopped: {ex.Message}",
                                        @event: "liveedit.lowresfailed");
            }
            finally
            {
                Volatile.Write(ref _lowResRunning, 0);
                cancel.Dispose();
                Dispatcher.BeginInvoke(() => { if (!_closed) ProxyBusy.Visibility = Visibility.Collapsed; });
            }
        });
    }

    /// <summary>One file: moved into high\ if it was dropped at the folder's top level, then its low-res made.</summary>
    private void MakeLowRes(string ffmpeg, string next, int rate, CancellationToken cancel)
    {
        string source = next;
        string name = System.IO.Path.GetFileName(next);

        try
        {
            string? parent = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(next));

            if (!string.Equals(parent, IngestLibrary.HighFolder, StringComparison.OrdinalIgnoreCase))
            {
                source = IngestLibrary.MoveIntoHigh(next);

                ActivityLog.Shared.Info(LogSource.LiveEdit,
                    $"INGEST  {name} moved into high\\ of {System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(next))}.",
                    @event: "liveedit.ingestmoved", file: source);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Still being copied, or held open: tried again on the next look.
            return;
        }

        name = System.IO.Path.GetFileName(source);
        int queued = 0;

        Dispatcher.BeginInvoke(() => ShowLowRes(name, 0));

        var started = DateTime.Now;

        FfmpegOutcome outcome = IngestProxy.Make(ffmpeg, source, IngestLibrary.LowPathFor(source), rate, f =>
        {
            if (Interlocked.Exchange(ref queued, 1) == 1) return;

            Dispatcher.BeginInvoke(() =>
            {
                Volatile.Write(ref queued, 0);
                ShowLowRes(name, f);
            });
        }, cancel);

        if (outcome.Cancelled) return;

        if (outcome.Ok)
        {
            ActivityLog.Shared.Ok(LogSource.LiveEdit,
                $"INGEST  low-res made for {name} in {(DateTime.Now - started).TotalSeconds:F0} s ({IngestProxy.EncoderAccount}).",
                @event: "liveedit.lowresmade", file: source);
            return;
        }

        lock (_lowResFailed) _lowResFailed.Add(source);

        ActivityLog.Shared.Warn(LogSource.LiveEdit,
            $"INGEST  no low-res could be made for {name}, so it is not listed: {outcome.Problem}",
            @event: "liveedit.lowresfailed", file: source);
    }

    private void ShowLowRes(string name, double fraction)
    {
        if (_closed) return;

        ProxyBusy.Visibility = Visibility.Visible;
        ProxyBusyText.Text = $"Making low-res: {name}   {fraction * 100:F0}%";
        ProxyBusyBar.Value = fraction;
    }

    // ------------------------------------------------------------------ dropping files on the panel

    private static string[] DroppedFiles(DragEventArgs e) =>
        e.Data.GetDataPresent(IngestDragFormat) || !e.Data.GetDataPresent(DataFormats.FileDrop)
            ? []
            : (e.Data.GetData(DataFormats.FileDrop) as string[] ?? []).Where(MediaScanner.IsPlayable).ToArray();

    private void IngestPanel_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = DroppedFiles(e).Length > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// Video dropped on the Ingest tab from Explorer: copied into high\ of the open folder - or of
    /// the recording on air, or a new folder for now - and a low-res made for it.
    /// </summary>
    private void IngestPanel_Drop(object sender, DragEventArgs e)
    {
        string[] files = DroppedFiles(e);
        if (files.Length == 0) return;

        e.Handled = true;

        string root = IngestRoot();
        string folder = _ingestFolder is { IsLoose: false } open
            ? open.Path
            : IngestLibrary.FolderFor(root, IngestLibrary.LiveRecordingFolder() ?? DateTime.Now.ToString(RecordingSequence.FolderFormat));

        if (IngestLibrary.Ensure(root, System.IO.Path.GetFileName(folder)) is null)
        {
            StoreText.Text = $"{folder} could not be made.";
            return;
        }

        string high = IngestLibrary.HighOf(folder);

        ProxyBusy.Visibility = Visibility.Visible;
        ProxyBusyText.Text = $"Copying {files.Length} file{(files.Length == 1 ? "" : "s")} into {System.IO.Path.GetFileName(folder)}...";
        ProxyBusyBar.Value = 0;

        _ = Task.Run(() =>
        {
            int done = 0;

            foreach (string file in files)
            {
                try
                {
                    string stem = System.IO.Path.GetFileNameWithoutExtension(file);
                    string ext = System.IO.Path.GetExtension(file);
                    string target = System.IO.Path.Combine(high, stem + ext);

                    for (int n = 2; File.Exists(target) || File.Exists(IngestLibrary.LowPathFor(target)); n++)
                        target = System.IO.Path.Combine(high, $"{stem} ({n}){ext}");

                    // Under a dotted name until whole, so nothing lists or encodes half a copy.
                    string temp = System.IO.Path.Combine(high, $".{System.IO.Path.GetFileName(target)}.part");
                    File.Copy(file, temp, overwrite: true);
                    File.Move(temp, target);
                    done++;

                    ActivityLog.Shared.Info(LogSource.LiveEdit,
                        $"INGEST  {System.IO.Path.GetFileName(file)} copied into {System.IO.Path.GetFileName(folder)}.",
                        @event: "liveedit.ingestcopied", file: target);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ActivityLog.Shared.Warn(LogSource.LiveEdit,
                        $"INGEST  {System.IO.Path.GetFileName(file)} could not be copied in: {ex.Message}",
                        @event: "liveedit.ingestcopyfailed", file: file);
                }
            }

            Dispatcher.BeginInvoke(() =>
            {
                if (_closed) return;

                StoreText.Text = $"Copied {done} of {files.Length} into {folder}; making low-res copies.";
                ProxyBusy.Visibility = Visibility.Collapsed;

                if (_ingestFolder is null || !_ingestFolder.Path.Equals(folder, StringComparison.OrdinalIgnoreCase))
                    OpenIngestFolderAt(folder);

                KickLowResMaker();
            });
        });
    }
}
