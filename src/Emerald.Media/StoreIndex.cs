using System.IO;
using Emerald.Core;
using Emerald.Video;

namespace Emerald.Media;

/// <summary>
/// One master file of a recording, placed on the clock by its own length.
///
/// The master is cut at <see cref="RecordingProfile.MasterSegmentSeconds"/> whatever the proxy is
/// cut at, so a master is not "the proxy's other half" any more: it holds the picture of several
/// proxies, and a proxy can straddle two masters. Each is placed the way a proxy is, from the
/// moment its folder rolled plus the lengths of the masters before it.
/// </summary>
public sealed record MasterSegment(string Path, DateTime Starts, TimeSpan Runs, long Bytes, string? Codec)
{
    /// <summary>True while the recorder still has it open: the movie header is written last.</summary>
    public bool Open => Runs <= TimeSpan.Zero;

    /// <summary>Where it ends, or never for one still being written.</summary>
    public DateTime Ends => Open ? DateTime.MaxValue : Starts + Runs;
}

/// <summary>
/// The store, kept listed rather than listed again.
///
/// <b>Why it exists.</b> Live Edit listed the whole store - every proxy of every day, every master,
/// a probe of each - every time one segment closed, which at ten-second segments is eight thousand
/// times a day over a store that grows all day: the cost of each listing rose with the number of
/// files, and the number of listings with the time. This keeps what it has read. A refresh walks
/// the directories, which is cheap, and opens only a file it has not seen or one that was still
/// being written the last time; a recording folder nothing has changed in is not even walked
/// except on the occasional full pass that notices deletions.
///
/// <b>Masters are placed, not paired.</b> Each recording folder's masters are laid out on their own
/// measured lengths, and a proxy is given the masters its span of the clock falls in. Recordings
/// made when both were cut alike come out exactly as before - one master each - because that is
/// the special case of the same rule.
///
/// Not thread-safe: one refresh at a time, which is how its callers use it. <see cref="Clips"/> is a
/// fresh list after every change, so a list handed out earlier is never changed under its reader.
/// </summary>
public sealed class StoreIndex
{
    /// <summary>How often a refresh walks every folder, to notice files deleted from a finished recording.</summary>
    public static readonly TimeSpan FullEvery = TimeSpan.FromSeconds(60);

    /// <summary>Overlap shorter than this is a rounding of lengths, not a master holding part of a proxy.</summary>
    private static readonly TimeSpan Slack = TimeSpan.FromMilliseconds(20);

    private readonly string _root;
    private readonly string? _ffprobe;
    private readonly int _rate;

    private readonly Dictionary<string, Folder> _folders = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Known> _loose = new(StringComparer.OrdinalIgnoreCase);
    private List<CapturedClip> _looseClips = new();

    private IReadOnlyList<CapturedClip> _clips = Array.Empty<CapturedClip>();

    /// <summary>Each folder's masters as of the last refresh, replaced whole so it can be read while one runs.</summary>
    private IReadOnlyDictionary<string, IReadOnlyList<MasterSegment>> _masters =
        new Dictionary<string, IReadOnlyList<MasterSegment>>();
    private DateTime _lastFull = DateTime.MinValue;

    public StoreIndex(string root, string? ffprobePath, int frameRate = 25)
    {
        _root = root;
        _ffprobe = ffprobePath;
        _rate = frameRate;
    }

    public string Root => _root;

    /// <summary>Everything in the store, newest first, as of the last refresh.</summary>
    public IReadOnlyList<CapturedClip> Clips => _clips;

    /// <summary>How many files have been opened since this index was made: what a refresh costs.</summary>
    public int FilesRead { get; private set; }

    /// <summary>
    /// The masters of one recording folder, in order, as of the last refresh. Safe to ask from
    /// another thread while a refresh is running: it reads the answer the last one left.
    /// </summary>
    public IReadOnlyList<MasterSegment> MastersOf(string recordingFolder) =>
        _masters.TryGetValue(recordingFolder, out IReadOnlyList<MasterSegment>? masters) ? masters : Array.Empty<MasterSegment>();

    /// <summary>
    /// Brings the index up to date with the disk. True when something an operator would see has
    /// changed: a file came or went, or one the recorder had open was closed. A file still growing
    /// is not a change - it is placed at an assumed length and has nothing new to show.
    /// </summary>
    /// <param name="full">Walk every folder, not only those that could have changed.</param>
    public bool Refresh(bool full = false, DateTime? now = null)
    {
        DateTime clock = now ?? DateTime.UtcNow;
        full |= clock - _lastFull >= FullEvery;

        string low = RecordingProfile.FolderFor(RecordingProfile.LowRes, _root);
        string high = RecordingProfile.FolderFor(RecordingProfile.HighRes, _root);

        bool changed = false;

        // The recording folders, which is one directory entry per recording: cheap every time.
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string name in RecordingFolders(low)) names.Add(name);
        foreach (string name in RecordingFolders(high)) names.Add(name);

        foreach (string gone in _folders.Keys.Where(k => !names.Contains(k)).ToList())
        {
            _folders.Remove(gone);
            changed = true;
        }

        string? newest = names.Count == 0 ? null : names.Max(StringComparer.OrdinalIgnoreCase);

        var walk = new List<Folder>();

        foreach (string name in names)
        {
            if (!_folders.TryGetValue(name, out Folder? folder))
            {
                folder = new Folder(name);
                _folders[name] = folder;
                changed = true;
            }

            // A finished recording cannot change except by deletion, which the full pass catches.
            if (full || folder.HasOpen || folder.Fresh ||
                string.Equals(name, newest, StringComparison.OrdinalIgnoreCase))
                walk.Add(folder);
        }

        var toRead = new List<(Dictionary<string, Known> Into, FileInfo File, Kind Kind)>();
        var dirty = new HashSet<Folder>();

        foreach (Folder folder in walk)
        {
            folder.Fresh = false;

            if (Gather(Path.Combine(low, folder.Name), folder.Proxies, Kind.Proxy, toRead)) dirty.Add(folder);
            if (Gather(Path.Combine(high, folder.Name), folder.MasterFiles, Kind.Master, toRead)) dirty.Add(folder);
        }

        // Recordings from before folders: loose in the proxy folder, and anything loose in the store
        // itself. Only on a full pass, because nothing writes them any more.
        bool looseChanged = false;

        if (full)
        {
            looseChanged |= GatherLoose(low, Kind.LooseProxy, toRead);
            looseChanged |= GatherLoose(_root, Kind.Other, toRead);
        }

        // Several at once, because each opens a file on a disk the recorder is writing to. Only the
        // first refresh has many; after that it is the one or two files that have just closed.
        var read = new Known?[toRead.Count];

        Parallel.For(0, toRead.Count, new ParallelOptions { MaxDegreeOfParallelism = 6 },
                     i => read[i] = Read(toRead[i].File, toRead[i].Kind));

        FilesRead += toRead.Count;

        for (int i = 0; i < toRead.Count; i++)
        {
            (Dictionary<string, Known> into, FileInfo file, Kind kind) = toRead[i];
            string path = file.FullName;

            bool wasClosed = into.TryGetValue(path, out Known? before) && before.Closed;

            if (read[i] is not { } known)
            {
                // Gone between the walk and the read: the storage limit takes the oldest all day.
                if (into.Remove(path)) Mark(path, kind);
                continue;
            }

            into[path] = known;

            if (before is null || known.Closed != wasClosed) Mark(path, kind);
        }

        foreach (Folder folder in walk)
            if (dirty.Contains(folder))
            {
                folder.Lay(_rate);
                changed = true;
            }

        if (looseChanged)
        {
            _looseClips = LayLoose(high);
            changed = true;
        }

        if (full) _lastFull = clock;

        if (changed)
        {
            var all = new List<CapturedClip>(_looseClips);
            foreach (Folder folder in _folders.Values) all.AddRange(folder.Clips);

            all.Sort((a, b) => b.Recorded.CompareTo(a.Recorded));
            _clips = all;

            _masters = _folders.ToDictionary(f => f.Key, f => f.Value.Masters, StringComparer.OrdinalIgnoreCase);
        }

        return changed;

        void Mark(string path, Kind kind)
        {
            if (kind is Kind.LooseProxy or Kind.Other) { looseChanged = true; return; }

            if (MediaLibrary.RecordingFolder(path) is { } name && _folders.TryGetValue(name, out Folder? f))
                dirty.Add(f);
        }

        bool Gather(string directory, Dictionary<string, Known> known, Kind kind,
                    List<(Dictionary<string, Known>, FileInfo, Kind)> reads)
        {
            bool removed = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (FileInfo file in Files(directory, SearchOption.TopDirectoryOnly))
            {
                seen.Add(file.FullName);

                // Read again only a file that is new, or was open and has moved on since.
                if (known.TryGetValue(file.FullName, out Known? was) &&
                    (was.Closed || was.Length == file.Length && was.WriteTicks == file.LastWriteTimeUtc.Ticks))
                {
                    if (was.Closed && (was.Length != file.Length || was.WriteTicks != file.LastWriteTimeUtc.Ticks))
                        reads.Add((known, file, kind));     // replaced by another file of the same name
                    continue;
                }

                // A file that grew but is still open is read again to see whether it has closed.
                reads.Add((known, file, kind));
            }

            foreach (string path in known.Keys.Where(p => !seen.Contains(p)).ToList())
            {
                known.Remove(path);
                removed = true;
            }

            return removed;
        }

        bool GatherLoose(string directory, Kind kind, List<(Dictionary<string, Known>, FileInfo, Kind)> reads)
        {
            bool removed = false;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (FileInfo file in Files(directory, SearchOption.TopDirectoryOnly))
            {
                seen.Add(file.FullName);

                if (_loose.TryGetValue(file.FullName, out Known? was) &&
                    was.Length == file.Length && was.WriteTicks == file.LastWriteTimeUtc.Ticks)
                    continue;

                reads.Add((_loose, file, kind));
            }

            string prefix = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

            foreach (string path in _loose.Keys
                         .Where(p => !seen.Contains(p) &&
                                     string.Equals(Path.GetDirectoryName(p) + Path.DirectorySeparatorChar, prefix,
                                                   StringComparison.OrdinalIgnoreCase))
                         .ToList())
            {
                _loose.Remove(path);
                removed = true;
            }

            return removed;
        }
    }

    private Known? Read(FileInfo file, Kind kind)
    {
        try
        {
            file.Refresh();
            if (!file.Exists) return null;

            switch (kind)
            {
                case Kind.Proxy:
                {
                    // The header is written last, so a file without one is still being recorded.
                    // Checked first, by a walk of a few atoms: asking the probe instead sends an
                    // unfinished file on to ffprobe, a process every refresh for nothing.
                    MediaInfo? info = MediaCodec.ReadDuration(file.FullName) is null
                        ? null
                        : MediaProbe.ProbeSegment(_ffprobe, file.FullName, _rate);

                    return new Known(file.Length, file.LastWriteTimeUtc.Ticks, file.LastWriteTime,
                                     info is { Duration.Ticks: > 0 }, info, null, TimeSpan.Zero);
                }

                case Kind.Master:
                {
                    // The picture's own length, not the movie's: the sound runs a few milliseconds
                    // past the last frame, and laid end to end that is a timeline sliding behind.
                    TimeSpan? runs = MediaCodec.ReadPictureDuration(file.FullName) ?? MediaCodec.ReadDuration(file.FullName);
                    bool closed = runs is { Ticks: > 0 };

                    return new Known(file.Length, file.LastWriteTimeUtc.Ticks, file.LastWriteTime,
                                     closed, null, closed ? MediaCodec.Read(file.FullName) : null,
                                     closed ? runs!.Value : TimeSpan.Zero);
                }

                case Kind.LooseProxy:
                {
                    MediaInfo? info = MediaProbe.Probe(_ffprobe, file.FullName, _rate);
                    return new Known(file.Length, file.LastWriteTimeUtc.Ticks, file.LastWriteTime,
                                     info is { Duration.Ticks: > 0 }, info, null, TimeSpan.Zero);
                }

                default:
                {
                    MediaInfo? info = MediaProbe.Probe(_ffprobe, file.FullName, _rate);
                    return new Known(file.Length, file.LastWriteTimeUtc.Ticks, file.LastWriteTime,
                                     info is { Duration.Ticks: > 0 }, info, null, TimeSpan.Zero);
                }
            }
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The old flat files, each proxy paired with its master by the time in their names.</summary>
    private List<CapturedClip> LayLoose(string high)
    {
        var masters = new MediaLibrary.MasterIndex(high, SearchOption.TopDirectoryOnly);
        var clips = new List<CapturedClip>();
        string low = Path.GetFullPath(RecordingProfile.FolderFor(RecordingProfile.LowRes, _root));

        foreach ((string path, Known known) in _loose)
        {
            bool proxy = string.Equals(Path.GetDirectoryName(path), low.TrimEnd(Path.DirectorySeparatorChar),
                                       StringComparison.OrdinalIgnoreCase);

            string? master = proxy ? masters.For(path) : null;

            clips.Add(new CapturedClip(path, Path.GetFileName(path), known.Written, known.Length, known.Info,
                                       master, master is null ? null : MediaCodec.Read(master)));
        }

        return clips;
    }

    private static IEnumerable<string> RecordingFolders(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return Array.Empty<string>();

            return Directory.EnumerateDirectories(directory)
                .Select(Path.GetFileName)
                .OfType<string>()
                .Where(n => RecordingSequence.FolderStartedAt(n) is not null)
                .ToList();
        }
        catch (IOException) { return Array.Empty<string>(); }
        catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
    }

    /// <summary>The playable files in a directory, with their lengths from the listing itself.</summary>
    private static List<FileInfo> Files(string directory, SearchOption depth)
    {
        try
        {
            if (!Directory.Exists(directory)) return new List<FileInfo>();

            return new DirectoryInfo(directory)
                .EnumerateFiles("*", depth)
                .Where(f => MediaScanner.IsPlayable(f.Name))
                .ToList();
        }
        catch (IOException) { return new List<FileInfo>(); }
        catch (UnauthorizedAccessException) { return new List<FileInfo>(); }
    }

    /// <summary>
    /// Lays a run of numbered segments end to end from the moment their folder rolled.
    ///
    /// The first left is not always the first recorded - the storage limit deletes from the front -
    /// so it starts after everything the ledger in its folder says was deleted before it. A gap the
    /// ledger does not cover is filled at the run's typical length.
    /// </summary>
    internal static DateTime[] LayEndToEnd(IReadOnlyList<(string Path, TimeSpan Runs)> ordered, DateTime rolled)
    {
        var starts = new DateTime[ordered.Count];
        if (ordered.Count == 0) return starts;

        DateTime at = rolled;

        if (RecordingSequence.NumberIn(Path.GetFileName(ordered[0].Path)) is { } first &&
            first > RecordingSequence.FirstNumber &&
            Path.GetDirectoryName(ordered[0].Path) is { } dir)
            at = rolled + PruneLedger.StartOf(first, PruneLedger.Read(dir), Typical(ordered));

        for (int i = 0; i < ordered.Count; i++)
        {
            starts[i] = at;

            // One still open has no length, so the run stops being exact there. It is the last anyway.
            if (ordered[i].Runs > TimeSpan.Zero) at += ordered[i].Runs;
        }

        return starts;
    }

    /// <summary>The middle length of the finished ones, so a short first or last does not skew it.</summary>
    private static TimeSpan Typical(IReadOnlyList<(string Path, TimeSpan Runs)> ordered)
    {
        TimeSpan[] lengths = ordered.Select(o => o.Runs).Where(d => d > TimeSpan.Zero).OrderBy(d => d).ToArray();
        return lengths.Length == 0 ? TimeSpan.Zero : lengths[lengths.Length / 2];
    }

    /// <summary>The masters a proxy's span of the clock falls in. A proxy still open covers from its start on.</summary>
    internal static List<MasterSegment> Covering(IReadOnlyList<MasterSegment> masters, DateTime starts, TimeSpan runs)
    {
        var covering = new List<MasterSegment>(2);
        DateTime ends = runs > TimeSpan.Zero ? starts + runs : starts + Slack + Slack;

        foreach (MasterSegment master in masters)
        {
            DateTime from = master.Starts > starts ? master.Starts : starts;
            DateTime to = master.Ends < ends ? master.Ends : ends;

            if (to - from > Slack) covering.Add(master);
        }

        return covering;
    }

    private enum Kind { Proxy, Master, LooseProxy, Other }

    /// <summary>What was read of one file, and the listing it was read at.</summary>
    private sealed record Known(long Length, long WriteTicks, DateTime Written, bool Closed,
                                MediaInfo? Info, string? Codec, TimeSpan Runs);

    /// <summary>One recording: its proxies and masters as read, and as laid out on the clock.</summary>
    private sealed class Folder
    {
        public Folder(string name) => Name = name;

        public string Name { get; }

        public Dictionary<string, Known> Proxies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Known> MasterFiles { get; } = new(StringComparer.OrdinalIgnoreCase);

        public IReadOnlyList<CapturedClip> Clips { get; private set; } = Array.Empty<CapturedClip>();
        public IReadOnlyList<MasterSegment> Masters { get; private set; } = Array.Empty<MasterSegment>();

        /// <summary>Not yet walked: a folder seen for the first time is read whatever else is happening.</summary>
        public bool Fresh { get; set; } = true;

        /// <summary>Something in it was still being written at the last look, so it can still change.</summary>
        public bool HasOpen { get; private set; }

        public void Lay(int frameRate)
        {
            DateTime rolled = RecordingSequence.FolderStartedAt(Name) ?? DateTime.MinValue;

            List<KeyValuePair<string, Known>> masters = Numbered(MasterFiles);
            DateTime[] masterStarts = LayEndToEnd(masters.Select(m => (m.Key, m.Value.Runs)).ToList(), rolled);

            Masters = masters
                .Select((m, i) => new MasterSegment(m.Key, masterStarts[i], m.Value.Runs, m.Value.Length, m.Value.Codec))
                .ToList();

            List<KeyValuePair<string, Known>> proxies = Numbered(Proxies);
            DateTime[] proxyStarts = LayEndToEnd(
                proxies.Select(p => (p.Key, p.Value.Info is { Duration.Ticks: > 0 } i ? i.Runs : TimeSpan.Zero)).ToList(),
                rolled);

            var clips = new List<CapturedClip>(proxies.Count);

            for (int n = 0; n < proxies.Count; n++)
            {
                (string path, Known known) = (proxies[n].Key, proxies[n].Value);
                TimeSpan runs = known.Info is { Duration.Ticks: > 0 } info ? info.Runs : TimeSpan.Zero;

                List<MasterSegment> covering = Covering(Masters, proxyStarts[n], runs);
                MasterSegment? first = covering.Count > 0 ? covering[0] : null;

                clips.Add(new CapturedClip(path, Path.GetFileName(path), known.Written, known.Length, known.Info,
                                           first?.Path, first?.Codec, proxyStarts[n], covering));
            }

            Clips = clips;
            HasOpen = Proxies.Values.Any(k => !k.Closed) || MasterFiles.Values.Any(k => !k.Closed);
        }

        /// <summary>By the number, not the name: a count past six digits is wider and would sort first.</summary>
        private static List<KeyValuePair<string, Known>> Numbered(Dictionary<string, Known> files) =>
            files.OrderBy(f => RecordingSequence.NumberIn(Path.GetFileName(f.Key)) ?? 0).ToList();
    }
}
