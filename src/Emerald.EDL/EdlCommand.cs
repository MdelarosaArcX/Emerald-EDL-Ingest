using Emerald.Core;
using Emerald.Deltacast;
using Emerald.Video;
using Emerald.Media;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emerald.Edl;

/// <summary>
/// One EDL command as a structured record: what was played, on which board and channel,
/// from when and for how long. Rendered as JSON in the EDL RECORD panel. See PROTOCOL.md
/// for a field-by-field description of the shape.
/// </summary>
public sealed class EdlCommand
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };


    [JsonPropertyName("type")] public string Type { get; set; } = "edl.command";
    [JsonPropertyName("version")] public int Version { get; set; } = 3;
    [JsonPropertyName("id")] public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("issuedAt")] public DateTimeOffset IssuedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// Capture and playback each carry their own board: the RX may be on board 0 while the
    /// TX is on board 1. They are frequently the same board, but nothing requires it.
    /// </summary>
    [JsonPropertyName("capture")] public PortRef Capture { get; set; } = new();
    [JsonPropertyName("playback")] public PortRef Playback { get; set; } = new();
    [JsonPropertyName("timing")] public TimingSpec Timing { get; set; } = new();

    /// <summary>The video source. null means no video: the TX carries black.</summary>
    [JsonPropertyName("media")] public MediaSpec? Media { get; set; }

    /// <summary>
    /// The language tracks, in list order. null or empty means no audio was selected and the
    /// message plays out silent — the video file's own audio is deliberately not used.
    /// </summary>
    [JsonPropertyName("audio")] public List<AudioTrackSpec>? Audio { get; set; }

    public string ToPrettyJson() => JsonSerializer.Serialize(this, Json);

    public sealed class BoardRef
    {
        [JsonPropertyName("index")] public uint Index { get; set; }
        [JsonPropertyName("model")] public string Model { get; set; } = "";
        [JsonPropertyName("type")] public uint Type { get; set; }
    }

    public sealed class PortRef
    {
        [JsonPropertyName("board")] public BoardRef Board { get; set; } = new();
        [JsonPropertyName("port")] public string Port { get; set; } = "";
        [JsonPropertyName("index")] public int Index { get; set; }
    }

    public sealed class TimingSpec
    {
        [JsonPropertyName("frameRate")] public int FrameRate { get; set; }

        [JsonPropertyName("startTimecode")] public string StartTimecode { get; set; } = "00:00:00:00";
        [JsonPropertyName("startFrame")] public long StartFrame { get; set; }

        /// <summary>
        /// When video actually reaches the TX: start + SOM. Between the start timecode and
        /// this moment the output holds the post-play fill.
        /// </summary>
        [JsonPropertyName("onAirTimecode")] public string OnAirTimecode { get; set; } = "00:00:00:00";
        [JsonPropertyName("onAirFrame")] public long OnAirFrame { get; set; }

        /// <summary>
        /// The media's own start timecode, read from the file. Recorded for reference only:
        /// SOM and EOM are offsets from the head of the file, not matched against this.
        /// </summary>
        [JsonPropertyName("mediaStartTimecode")] public string MediaStartTimecode { get; set; } = "00:00:00:00";

        /// <summary>Start of message: an offset from the head of the media file.</summary>
        [JsonPropertyName("som")] public string Som { get; set; } = "00:00:00:00";
        [JsonPropertyName("somFrame")] public long SomFrame { get; set; }

        /// <summary>End of message. null plays to the end of the media and loops.</summary>
        [JsonPropertyName("eom")] public string? Eom { get; set; }
        [JsonPropertyName("eomFrame")] public long? EomFrame { get; set; }

        /// <summary>What the TX carries after the message: "blackScreen" or "freezeLastFrame".</summary>
        [JsonPropertyName("postPlay")] public string PostPlay { get; set; } = "blackScreen";

        /// <summary>null means "play until stopped".</summary>
        [JsonPropertyName("duration")] public string? Duration { get; set; }
        [JsonPropertyName("durationFrames")] public long? DurationFrames { get; set; }

        /// <summary>
        /// Stop time on the house clock: start + duration, wrapped at 24 h. null when the
        /// duration is open-ended.
        /// </summary>
        [JsonPropertyName("stopTime")] public string? StopTime { get; set; }
        [JsonPropertyName("stopFrame")] public long? StopFrame { get; set; }

        /// <summary>
        /// Always true: the source repeats to fill the requested duration, and is cut
        /// short mid-clip if the duration expires first.
        /// </summary>
        [JsonPropertyName("loop")] public bool Loop { get; set; } = true;
    }

    public sealed class MediaSpec
    {
        /// <summary>"folder" or "file".</summary>
        [JsonPropertyName("kind")] public string Kind { get; set; } = "folder";
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("fileCount")] public int FileCount { get; set; }
        [JsonPropertyName("files")] public List<string> Files { get; set; } = new();
    }

    /// <summary>
    /// One language. Only one track is embedded at a time, on channels 1-2; `default` marks
    /// which one starts, and the operator can switch between them mid-message.
    /// </summary>
    public sealed class AudioTrackSpec
    {
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("kind")] public string Kind { get; set; } = "file";
        [JsonPropertyName("source")] public string Source { get; set; } = "";
        [JsonPropertyName("fileCount")] public int FileCount { get; set; }
        [JsonPropertyName("files")] public List<string> Files { get; set; } = new();

        /// <summary>Per-language trim in milliseconds; positive delays audio behind picture.</summary>
        [JsonPropertyName("offsetMs")] public int OffsetMs { get; set; }

        [JsonPropertyName("default")] public bool Default { get; set; }
    }
}
