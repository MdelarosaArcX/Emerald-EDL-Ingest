using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using System.IO;
using System.Runtime.CompilerServices;
using Emerald.Core;

namespace Emerald.Ingest;

/// <summary>
/// One requested ingest: a receiver, a moment on the timecode clock, and how much of it to
/// keep.
///
/// This is both the row in the job store and the thing the queue binds to, which is why it
/// notifies rather than being a record. Timecodes are persisted as HH:MM:SS:FF text beside
/// the rate they were counted at — a bare frame count in a database is unreadable, and a
/// bare timecode without its rate is ambiguous. The typed <see cref="Timecode"/> views are
/// derived, never stored.
/// </summary>
public sealed class IngestJob : INotifyPropertyChanged
{
    public Guid Id { get; set; } = Guid.NewGuid();

    private string _clipName = "";
    /// <summary>What the recorded files are named. Unique within <see cref="Directory"/>.</summary>
    public string ClipName { get => _clipName; set => Set(ref _clipName, value); }

    // ------------------------------------------------------------------ hardware

    public uint BoardIndex { get; set; }

    /// <summary>The board as it read on the day, kept so history survives the card being moved.</summary>
    public string BoardName { get; set; } = "";

    /// <summary>The receiver, as "RX1".</summary>
    public string Port { get; set; } = "";

    /// <summary>The receiver's channel number, which is what the SDK is actually given.</summary>
    public int PortIndex { get; set; }

    // ------------------------------------------------------------------ timing

    /// <summary>The rate every timecode on this job is counted at.</summary>
    public int FrameRate { get; set; } = 25;

    /// <summary>When the recorder rolls, on the station clock. Exactly this, with no preroll.</summary>
    public string ReferenceTimecode { get; set; } = "00:00:00:00";

    /// <summary>
    /// The timecode stamped into the head of the recorded file — not a moment on the station
    /// clock and not an offset from one. It is what ffprobe reads back as the media's start
    /// timecode, and what an editor marks against.
    /// </summary>
    public string Som { get; set; } = "00:00:00:00";

    /// <summary>Where the recording stops: start + duration.</summary>
    public string Eom { get; set; } = "00:00:00:00";

    /// <summary>How long the recording runs.</summary>
    public string Duration { get; set; } = "00:00:00:00";

    /// <summary>
    /// Where the recorder rolls. The same as <see cref="ReferenceTimecode"/> — kept as its
    /// own column because it is what the scheduler triggers on and what the history reports.
    /// </summary>
    public string ActualStartTimecode { get; set; } = "00:00:00:00";

    // ------------------------------------------------------------------ destination

    public string Directory { get; set; } = "";

    /// <summary>The operator's free-form note about what this is.</summary>
    public string Metadata { get; set; } = "";

    // ------------------------------------------------------------------ state

    private IngestStatus _status = IngestStatus.Created;
    public IngestStatus Status
    {
        get => _status;
        set
        {
            if (Set(ref _status, value)) Notify(nameof(StatusText));
        }
    }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// When, on the wall clock, the recording is expected to roll. The timecode is what
    /// actually triggers it; this is how the scheduler tells "in twenty hours" apart from
    /// "four hours ago and missed", which a 24-hour timecode alone cannot say.
    /// </summary>
    public DateTime? ScheduledAt { get; set; }

    public DateTime? StartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    /// <summary>The master file, once there is one.</summary>
    public string? FilePath { get; set; }

    /// <summary>The H.264 proxy written alongside the master.</summary>
    public string? ProxyPath { get; set; }

    public long FileSize { get; set; }

    private string? _errorMessage;
    public string? ErrorMessage { get => _errorMessage; set => Set(ref _errorMessage, value); }

    /// <summary>Recorded with a simulated receiver rather than a card. Never silently forgotten.</summary>
    public bool Mock { get; set; }

    private long _framesRecorded;
    /// <summary>Frames taken off the receiver so far, for the progress display.</summary>
    public long FramesRecorded
    {
        get => _framesRecorded;
        set
        {
            if (Set(ref _framesRecorded, value)) Notify(nameof(ProgressText));
        }
    }

    // ------------------------------------------------------------------ derived views

    [NotMapped] public Timecode Reference => Parse(ReferenceTimecode);
    [NotMapped] public Timecode SomTimecode => Parse(Som);
    [NotMapped] public Timecode EomTimecode => Parse(Eom);
    [NotMapped] public Timecode DurationTimecode => Parse(Duration);
    [NotMapped] public Timecode ActualStart => Parse(ActualStartTimecode);

    /// <summary>
    /// What goes to disk, which is exactly the duration: the recorder rolls at the start
    /// timecode and stops at EOM. This is what it is given as a frame limit.
    /// </summary>
    [NotMapped]
    public Timecode RecordedLength => DurationTimecode;

    [NotMapped] public long RecordedLengthFrames => RecordedLength.TotalFrames;

    [NotMapped] public string StatusText => IngestStatusRules.Display(Status);

    [NotMapped]
    public string ProgressText => Status == IngestStatus.Recording
        ? $"{new Timecode(FramesRecorded, FrameRate)} of {RecordedLength}"
        : "";

    /// <summary>The files this job will write, master last, matching the recorder's own order.</summary>
    [NotMapped]
    public IReadOnlyList<string> PlannedOutputs => Emerald.Video.RecordingProfile.Outputs
        .Select(o => Path.Combine(Emerald.Video.RecordingProfile.FolderFor(o, Directory),
                                  $"{ClipName}.{o.Extension}"))
        .ToList();

    private Timecode Parse(string text) =>
        Timecode.TryParse(text, FrameRate, out Timecode tc, out _) ? tc : Timecode.Zero(FrameRate);

    // ------------------------------------------------------------------ notification

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Notify([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Notify(name);
        return true;
    }
}
