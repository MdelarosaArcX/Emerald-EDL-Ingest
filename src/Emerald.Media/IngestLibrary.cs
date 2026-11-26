using Emerald.Core;
using Emerald.Video;

namespace Emerald.Media;

/// <summary>One folder of the ingest media path: a capture recording's ingest media.</summary>
public sealed record IngestFolder(
    string Name,
    string Path,
    DateTime StartedAt,
    int Clips,
    bool IsLive,
    bool IsLoose)
{
    public string DisplayName => IsLoose ? "Earlier exports" : Name;
    public string DateText => IsLoose ? "not in a recording folder" : StartedAt.ToString("ddd d MMM yyyy  HH:mm:ss");
    public string ClipsText => Clips == 1 ? "1 clip" : $"{Clips} clips";
}

/// <summary>
/// The ingest media path: Live Edit's exports, the Ingest Controller's clips and anything dropped
/// in by hand, kept one folder per capture recording.
///
/// <code>
/// &lt;ingest path&gt;\
///   2026-11-21_14-05-00\        named like the capture recording it belongs to
///     high\CLIP.mov             what goes to air
///     low\CLIP.mp4              what the Ingest list shows and the timeline plays (960x540)
///   2026-11-20_09-00-00\ ...
///   old-export.mov              loose files from before folders: listed as "Earlier exports"
/// </code>
///
/// Only low-res files are listed, so only low-res is dragged; what is placed and sent to air is
/// the high-res beside it (<see cref="MasterFor"/>). A file dropped into a recording folder, or
/// into its high\, without a low-res copy is what <see cref="Pending"/> finds for the low-res
/// maker.
/// </summary>
public static class IngestLibrary
{
    public const string HighFolder = "high";
    public const string LowFolder = "low";

    /// <summary>The ingest media path in force: the one set in Live Edit, or the store's ingest folder.</summary>
    public static string RootFor(AppSettings settings)
    {
        string saved = AppSettings.SavedIngestMediaFolder();
        if (saved.Length == 0) saved = settings.IngestMediaFolder;

        return saved.Trim().Length > 0 ? saved.Trim() : MediaLibrary.PathsFor(settings).Exports;
    }

    /// <summary>The folder a capture recording's ingest media goes in.</summary>
    public static string FolderFor(string root, string recordingFolder) => Path.Combine(root, recordingFolder);

    public static string HighOf(string folder) => Path.Combine(folder, HighFolder);
    public static string LowOf(string folder) => Path.Combine(folder, LowFolder);

    /// <summary>
    /// Makes the recording's ingest folder, with high\ and low\, if it is not there. Never throws:
    /// a missing ingest folder must not stop a recording. Returns the folder, or null.
    /// </summary>
    public static string? Ensure(string root, string recordingFolder)
    {
        try
        {
            string folder = FolderFor(root, recordingFolder);
            Directory.CreateDirectory(HighOf(folder));
            Directory.CreateDirectory(LowOf(folder));
            return folder;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where an Ingest Controller job records, worked out as it starts: a job pointed at the ingest
    /// path itself goes into the folder of the recording on air - or, with none rolling, a new
    /// folder named for now - so its clip shows up in Live Edit beside that recording's exports. A
    /// job pointed anywhere else records where it was pointed.
    /// </summary>
    public static string JobFolder(string directory, string root)
    {
        if (!SameFolder(directory, root)) return directory;

        string recording = LiveRecordingFolder() ?? DateTime.Now.ToString(RecordingSequence.FolderFormat);
        return Ensure(root, recording) ?? directory;
    }

    /// <summary>True when the two paths name the same folder.</summary>
    public static bool SameFolder(string a, string b)
    {
        try
        {
            return a.Trim().Length > 0 && b.Trim().Length > 0 &&
                   string.Equals(Path.GetFullPath(a.Trim()).TrimEnd('\\', '/'), Path.GetFullPath(b.Trim()).TrimEnd('\\', '/'),
                                 StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>The recording folder name of the recording running now, or null.</summary>
    public static string? LiveRecordingFolder()
    {
        PipelineState state = PipelineState.Shared;
        return state.Recording && state.RecordingStartedAt is { } started
            ? started.ToString(RecordingSequence.FolderFormat)
            : null;
    }

    /// <summary>
    /// The folders, newest first by the recording they belong to, the one recording now at the top.
    /// Loose files left at the top level from before folders are one more entry at the end.
    /// </summary>
    public static IReadOnlyList<IngestFolder> Folders(string root, string? liveFolder)
    {
        var folders = new List<IngestFolder>();

        try
        {
            if (!Directory.Exists(root)) return folders;

            foreach (string path in Directory.EnumerateDirectories(root))
            {
                string name = Path.GetFileName(path);
                if (name.StartsWith('.')) continue;

                DateTime started = RecordingSequence.FolderStartedAt(name) ?? SafeCreationTime(path);
                bool live = liveFolder is not null && string.Equals(name, liveFolder, StringComparison.OrdinalIgnoreCase);

                folders.Add(new IngestFolder(name, path, started, CountClips(path), live, IsLoose: false));
            }

            int loose = LooseFiles(root).Count();
            if (loose > 0)
                folders.Add(new IngestFolder("", root, DateTime.MinValue, loose, false, IsLoose: true));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return folders
            .OrderByDescending(f => f.IsLive)
            .ThenBy(f => f.IsLoose)
            .ThenByDescending(f => f.StartedAt)
            .ToList();
    }

    /// <summary>
    /// The clips of one folder, newest first: its low-res files, each with the high-res beside it as
    /// <see cref="CapturedClip.MasterPath"/>. For the loose entry, the top-level files themselves.
    /// </summary>
    public static IReadOnlyList<CapturedClip> Clips(IngestFolder folder, string? ffprobePath, int frameRate)
    {
        var clips = new List<CapturedClip>();

        try
        {
            IEnumerable<string> files = folder.IsLoose
                ? LooseFiles(folder.Path)
                : Directory.Exists(LowOf(folder.Path)) ? Directory.EnumerateFiles(LowOf(folder.Path)).Where(IsListable) : [];

            foreach (string path in files)
            {
                var file = new FileInfo(path);
                clips.Add(new CapturedClip(
                    path, file.Name, file.LastWriteTime, file.Length,
                    ffprobePath is null ? null : MediaProbe.Probe(ffprobePath, path, frameRate),
                    MasterPath: folder.IsLoose ? null : MasterFor(path)));
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        clips.Sort((a, b) => b.Recorded.CompareTo(a.Recorded));
        return clips;
    }

    /// <summary>
    /// The high-res beside a low-res file - same name, any media extension, in the sibling high\ -
    /// or null when <paramref name="lowPath"/> is not in a low\ folder or has no high-res.
    /// </summary>
    public static string? MasterFor(string lowPath)
    {
        string? low = Path.GetDirectoryName(lowPath);
        if (low is null || !Path.GetFileName(low).Equals(LowFolder, StringComparison.OrdinalIgnoreCase)) return null;

        string high = Path.Combine(Path.GetDirectoryName(low) ?? "", HighFolder);
        if (!Directory.Exists(high)) return null;

        string stem = Path.GetFileNameWithoutExtension(lowPath);

        return Directory.EnumerateFiles(high, stem + ".*")
                        .FirstOrDefault(p => IsListable(p) &&
                                             Path.GetFileNameWithoutExtension(p).Equals(stem, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The low-res beside a high-res file, or null when there is none yet.</summary>
    public static string? LowFor(string highPath)
    {
        string? high = Path.GetDirectoryName(highPath);
        if (high is null || !Path.GetFileName(high).Equals(HighFolder, StringComparison.OrdinalIgnoreCase)) return null;

        string low = LowPathFor(highPath);
        return File.Exists(low) ? low : null;
    }

    /// <summary>Where the low-res of a high-res file goes, whether or not it exists yet.</summary>
    public static string LowPathFor(string highPath)
    {
        string folder = Path.GetDirectoryName(Path.GetDirectoryName(highPath) ?? "") ?? "";
        return Path.Combine(folder, LowFolder, Path.GetFileNameWithoutExtension(highPath) + ".mp4");
    }

    /// <summary>
    /// Media in a recording folder still wanting a low-res copy: files dropped at the folder's top
    /// level (to be moved into high\ first) and high-res files with no low-res beside them. Files
    /// still being written - by a copy, or by the Ingest Controller's recorder - are left for a
    /// later look.
    /// </summary>
    public static IReadOnlyList<string> Pending(string root)
    {
        var pending = new List<string>();

        try
        {
            if (!Directory.Exists(root)) return pending;

            foreach (string folder in Directory.EnumerateDirectories(root))
            {
                if (Path.GetFileName(folder).StartsWith('.')) continue;

                foreach (string dropped in Directory.EnumerateFiles(folder).Where(IsListable))
                    if (IsFinished(dropped)) pending.Add(dropped);

                string high = HighOf(folder);
                if (!Directory.Exists(high)) continue;

                foreach (string master in Directory.EnumerateFiles(high).Where(IsListable))
                    if (LowFor(master) is null && IsFinished(master)) pending.Add(master);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        return pending;
    }

    /// <summary>
    /// Moves a file dropped at a recording folder's top level into its high\, returning where it now
    /// is. A name already taken in high\ gets a number added, so nothing is overwritten.
    /// </summary>
    public static string MoveIntoHigh(string dropped)
    {
        string folder = Path.GetDirectoryName(dropped) ?? throw new IOException("no folder");
        string high = HighOf(folder);
        Directory.CreateDirectory(high);
        Directory.CreateDirectory(LowOf(folder));

        string stem = Path.GetFileNameWithoutExtension(dropped);
        string ext = Path.GetExtension(dropped);
        string target = Path.Combine(high, stem + ext);

        for (int n = 2; File.Exists(target) || File.Exists(LowPathFor(target)); n++)
            target = Path.Combine(high, $"{stem} ({n}){ext}");

        File.Move(dropped, target);
        return target;
    }

    /// <summary>True for a media file the Ingest list offers: playable, not hidden or half-written, not an old preview copy.</summary>
    public static bool IsListable(string path)
    {
        string name = Path.GetFileName(path);
        return !name.StartsWith('.') && MediaScanner.IsPlayable(path) && !RangeExport.IsPreview(path);
    }

    /// <summary>
    /// True when nothing is writing the file: it opens with others allowed only to read. A file
    /// still being copied in, or recorded, is open for writing and refuses.
    /// </summary>
    public static bool IsFinished(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return stream.Length > 0;
        }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> LooseFiles(string root) =>
        Directory.EnumerateFiles(root).Where(IsListable);

    private static int CountClips(string folder)
    {
        try
        {
            string low = LowOf(folder);
            return Directory.Exists(low) ? Directory.EnumerateFiles(low).Count(IsListable) : 0;
        }
        catch (IOException) { return 0; }
        catch (UnauthorizedAccessException) { return 0; }
    }

    private static DateTime SafeCreationTime(string path)
    {
        try { return Directory.GetCreationTime(path); }
        catch (IOException) { return DateTime.MinValue; }
        catch (UnauthorizedAccessException) { return DateTime.MinValue; }
    }
}
