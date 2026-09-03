using System.ComponentModel.DataAnnotations.Schema;
using Emerald.Core;

namespace Emerald.Ingest;

/// <summary>
/// What an ingest job actually produced, as read back off the disk rather than as intended.
///
/// The job says what was asked for; this says what arrived. They are separate rows because
/// they routinely disagree — a receiver that lost lock halfway leaves a job that asked for
/// fifteen minutes and a recording that holds four — and an operator needs to see both
/// numbers, not one number that has quietly been overwritten by the other.
///
/// The media itself is never in here. The clip stays on disk where Emerald.Media can see
/// it; this row is the record <i>about</i> the file.
/// </summary>
public sealed class IngestRecording
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid IngestJobId { get; set; }

    /// <summary>Where the recorder actually rolled, on the timecode clock.</summary>
    public string ActualStartTimecode { get; set; } = "00:00:00:00";

    /// <summary>Where it stopped — start plus however many frames were really taken.</summary>
    public string ActualEndTimecode { get; set; } = "00:00:00:00";

    /// <summary>The master. This is the file the ingest is judged on.</summary>
    public string FilePath { get; set; } = "";

    /// <summary>The H.264 proxy written from the same frames, for scrubbing.</summary>
    public string? ProxyPath { get; set; }

    public long FileSize { get; set; }
    public long ProxyFileSize { get; set; }

    /// <summary>As ffprobe read it back, not as the encoder was configured.</summary>
    public string Codec { get; set; } = "";

    /// <summary>"1920x1080", or empty when the file could not be probed.</summary>
    public string Resolution { get; set; } = "";

    public int FrameRate { get; set; } = 25;

    /// <summary>Frames taken off the receiver.</summary>
    public long Frames { get; set; }

    /// <summary>Mirrors the job's outcome: Completed, Cancelled or Failed.</summary>
    public IngestStatus Status { get; set; } = IngestStatus.Recording;

    public DateTime StartedAt { get; set; } = DateTime.Now;
    public DateTime? CompletedAt { get; set; }

    public string? ErrorMessage { get; set; }

    /// <summary>Produced by the simulated receiver rather than by a card.</summary>
    public bool Mock { get; set; }

    [NotMapped] public Timecode Length => new(Frames, FrameRate);

    [NotMapped]
    public string SizeText => FileSize >= 1L << 30
        ? $"{FileSize / (double)(1L << 30):F1} GB"
        : $"{FileSize / (double)(1L << 20):F0} MB";
}
