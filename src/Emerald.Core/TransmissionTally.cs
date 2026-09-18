namespace Emerald.Core;

/// <summary>
/// One file under one message: what it is, when it was booked, and every time it reached the
/// transmitter.
///
/// Picture and sound are separate entries even when they came from the same command, because
/// on air they are separate things — a message can carry eight languages off eight files, each
/// looping independently of the video and of each other, so "what went out under this EDL" has
/// as many answers as there are tracks.
/// </summary>
public sealed class TransmittedFile
{
    /// <summary>The eight-hex id of the message this belongs to, or "" for anything unattributed.</summary>
    public required string EdlId { get; init; }

    /// <summary>0 for the picture, 1-8 for an audio track. What splits the rows apart.</summary>
    public required int Track { get; init; }

    public required string Path { get; init; }

    /// <summary>The track's name, for audio. Empty for picture.</summary>
    public string Label { get; internal set; } = "";

    /// <summary>The SDI channels an audio track lands on, as "3-4". Empty for picture.</summary>
    public string Channels { get; internal set; } = "";

    public bool IsVideo => Track == 0;

    /// <summary>"VIDEO", or "AUDIO 3" — what the row is called on the page.</summary>
    public string Kind => IsVideo ? "VIDEO" : $"AUDIO {Track}";

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>When the message that carries it was queued, if that was seen.</summary>
    public DateTimeOffset? Queued { get; internal set; }

    /// <summary>
    /// Every distinct moment this file reached the transmitter.
    ///
    /// A set rather than a counter, and keyed on the instant, because the same line can be
    /// read twice — once from today's file and once from the ring that still holds it. Two
    /// readings of one transmission must not count as two transmissions.
    /// </summary>
    internal readonly HashSet<DateTimeOffset> AiredAt = new();

    /// <summary>How many times this file has been put to air.</summary>
    public int Times => AiredAt.Count;

    public bool Aired => AiredAt.Count > 0;

    public DateTimeOffset? FirstAired => AiredAt.Count == 0 ? null : AiredAt.Min();
    public DateTimeOffset? LastAired => AiredAt.Count == 0 ? null : AiredAt.Max();

    /// <summary>"QUEUED" until it has been to air, then "AIRED".</summary>
    public string State => Aired ? "AIRED" : "QUEUED";

    public string TimesText => Times switch
    {
        0 => "-",
        1 => "once",
        var n => $"{n} times",
    };

    public string LastAiredText => LastAired is { } at ? at.ToString("HH:mm:ss") : "-";
}

/// <summary>What one message is, for the heading above its files.</summary>
public sealed class TransmittedEdl
{
    public required string EdlId { get; init; }

    /// <summary>The line the EDL wrote when it queued it, which already reads as a summary.</summary>
    public string Headline { get; internal set; } = "";

    public DateTimeOffset At { get; internal set; }

    /// <summary>The full 32-character command id, which the queued line carries in its detail.</summary>
    public string? CommandId { get; internal set; }
}

/// <summary>
/// Everything that has been put to air, per message.
///
/// Built by folding the record rather than by keeping a second set of books: the log is
/// already the thing that is written down, kept for a fortnight and ordered, so a tally that
/// is a projection of it cannot disagree with it. It is fed the same lines twice without
/// harm — today's file on the way in, then the ring — which is what lets the page show more
/// than the five thousand lines held in memory.
///
/// Only the tags are read, never the prose: <c>edl.queued</c>, <c>edl.video</c>,
/// <c>edl.audio</c>, <c>playout.video</c> and <c>playout.audio</c>, plus the two the record
/// used before picture and sound were told apart. A message reworded tomorrow does not change
/// a number here.
/// </summary>
public sealed class TransmissionTally
{
    private readonly Dictionary<string, TransmittedEdl> _edls = new();
    private readonly Dictionary<(string Edl, int Track, string Path), TransmittedFile> _files = new();

    /// <summary>Order of first appearance, so the newest message is at the bottom as in the log.</summary>
    private readonly List<string> _order = new();

    public int EdlCount => _edls.Count;
    public int FileCount => _files.Count;

    /// <summary>Folds one line in. Anything it does not recognise is ignored.</summary>
    public void Add(LogLine line)
    {
        switch (line.Event)
        {
            case "edl.queued":
                Edl(line.Correlation).Headline = line.Message;
                Edl(line.Correlation).At = line.At;
                Edl(line.Correlation).CommandId = line.Detail;
                break;

            case "edl.video":
                if (line.File is { Length: > 0 }) File(line, track: 0).Queued ??= line.At;
                break;

            // What the record called these before picture and sound were tagged apart. Read so
            // that a fortnight of history already on the disk is not a blank page: "edl.media"
            // said which file without saying which kind, so the track detail decides, and
            // "playout.playing" carried a path only on the line where a file reached air.
            case "edl.media":
                if (line.File is { Length: > 0 }) File(line, TrackOrVideo(line)).Queued ??= line.At;
                break;

            case "playout.playing":
                if (line.File is { Length: > 0 }) File(line, TrackOrVideo(line)).AiredAt.Add(line.At);
                break;

            case "edl.audio":
                if (line.File is { Length: > 0 }) File(line, TrackOf(line)).Queued ??= line.At;
                break;

            case "playout.video":
                if (line.File is { Length: > 0 }) File(line, track: 0).AiredAt.Add(line.At);
                break;

            case "playout.audio":
                if (line.File is { Length: > 0 }) File(line, TrackOf(line)).AiredAt.Add(line.At);
                break;
        }
    }

    /// <summary>
    /// The messages, oldest first, each with its files: picture first, then the audio tracks
    /// in channel order, and within a track the files in the order they were named.
    /// </summary>
    public IReadOnlyList<(TransmittedEdl Edl, IReadOnlyList<TransmittedFile> Files)> Rows()
    {
        var rows = new List<(TransmittedEdl, IReadOnlyList<TransmittedFile>)>(_order.Count);

        foreach (string id in _order)
        {
            List<TransmittedFile> files = _files.Values
                .Where(f => f.EdlId == id)
                .OrderBy(f => f.Track)
                .ThenBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (files.Count > 0) rows.Add((_edls[id], files));
        }

        return rows;
    }

    /// <summary>Every file, flat, for a grid that groups by the id itself.</summary>
    public IReadOnlyList<TransmittedFile> Flat() => Rows().SelectMany(r => r.Files).ToList();

    private TransmittedEdl Edl(string? id)
    {
        string key = id ?? "";

        if (_edls.TryGetValue(key, out TransmittedEdl? edl)) return edl;

        edl = new TransmittedEdl
        {
            EdlId = key,

            // A message the page never saw queued — the playback deck putting a clip straight
            // to air does not go through the EDL — still needs a heading, or its files would
            // have nowhere to sit.
            Headline = key.Length == 0 ? "Not under an EDL" : $"EDL {key}",
        };

        _edls[key] = edl;
        _order.Add(key);
        return edl;
    }

    private TransmittedFile File(LogLine line, int track)
    {
        string id = line.Correlation ?? "";
        Edl(id);

        var key = (id, track, line.File!);

        if (_files.TryGetValue(key, out TransmittedFile? file)) return file;

        file = new TransmittedFile { EdlId = id, Track = track, Path = line.File! };

        if (track > 0)
        {
            file.Label = Field(line.Detail, "label");
            file.Channels = Field(line.Detail, "channels");
        }

        _files[key] = file;
        return file;
    }

    /// <summary>
    /// Which track an audio line belongs to, from the detail the writer put there.
    ///
    /// Read from a named field rather than counted by arrival: lines arrive interleaved from
    /// eight beds rolling at their own pace, so position says nothing about which track a
    /// file belongs to. Anything unreadable becomes track 1, which keeps the row rather than
    /// dropping the transmission on the floor.
    /// </summary>
    /// <summary>
    /// For the old untagged lines: an audio track if it says so, otherwise the picture.
    ///
    /// The detail is asked first and answers for anything written since picture and sound were
    /// tagged apart. Older lines have none, and the only thing that distinguishes them is the
    /// word in the message — which is prose, and read here for that reason alone: a language
    /// taken from the same file as the picture would otherwise land on the same key and
    /// disappear into the video row, and a fortnight of history would show no audio at all.
    /// </summary>
    private static int TrackOrVideo(LogLine line) =>
        Field(line.Detail, "track").Length > 0 ? TrackOf(line)
        : line.Message.Contains(" audio ", StringComparison.OrdinalIgnoreCase) ? 1
        : 0;

    private static int TrackOf(LogLine line) =>
        int.TryParse(Field(line.Detail, "track"), out int track) && track > 0 ? track : 1;

    /// <summary>Pulls "name value" out of the newline-separated detail block.</summary>
    private static string Field(string? detail, string name)
    {
        if (detail is null) return "";

        foreach (string part in detail.Split('\n'))
        {
            string trimmed = part.Trim();

            if (trimmed.Length > name.Length + 1 &&
                trimmed.StartsWith(name, StringComparison.Ordinal) &&
                trimmed[name.Length] == ' ')
                return trimmed[(name.Length + 1)..].Trim();
        }

        return "";
    }
}
