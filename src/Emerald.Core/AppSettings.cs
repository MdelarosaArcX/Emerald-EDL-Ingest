using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Emerald.Core;

/// <summary>One persisted audio track. Offsets are per-language and survive a restart.</summary>
public sealed class AudioTrackSetting
{
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    [JsonPropertyName("source")] public string Source { get; set; } = "";
    [JsonPropertyName("offsetMs")] public int OffsetMs { get; set; }
    [JsonPropertyName("isDefault")] public bool IsDefault { get; set; }

    /// <summary>
    /// Which audio stream of the source this track is, among the audio streams alone. -1 is
    /// "the file's own single track", which is what a separate .wav bed is; 0 and above name
    /// a language embedded in the message's media.
    /// </summary>
    [JsonPropertyName("stream")] public int Stream { get; set; } = -1;

    [JsonPropertyName("streamDetail")] public string StreamDetail { get; set; } = "";

    /// <summary>This language's trim in decibels, applied on the way to the card.</summary>
    [JsonPropertyName("gainDb")] public double GainDb { get; set; }
}

/// <summary>User-visible configuration, persisted to %APPDATA%\Emerald\settings.json.</summary>
public sealed class AppSettings
{
    [JsonPropertyName("timecodeApiUrl")] public string TimecodeApiUrl { get; set; } = "http://10.0.0.31:8888/api/timecode";
    [JsonPropertyName("captureBoardIndex")] public uint CaptureBoardIndex { get; set; }
    [JsonPropertyName("capturePort")] public string CapturePort { get; set; } = "RX0";
    [JsonPropertyName("playbackBoardIndex")] public uint PlaybackBoardIndex { get; set; }
    [JsonPropertyName("playbackPort")] public string PlaybackPort { get; set; } = "TX0";
    [JsonPropertyName("startTimecode")] public string StartTimecode { get; set; } = "00:00:00:00";
    [JsonPropertyName("som")] public string Som { get; set; } = "";
    [JsonPropertyName("eom")] public string Eom { get; set; } = "";

    /// <summary>"blackScreen" or "freezeLastFrame".</summary>
    [JsonPropertyName("postPlay")] public string PostPlay { get; set; } = "blackScreen";
    [JsonPropertyName("mediaSource")] public string MediaSource { get; set; } = "";

    /// <summary>Optional override; when empty, ffmpeg is looked up next to the app, on PATH, then C:\ffmpeg\bin.</summary>
    [JsonPropertyName("ffmpegPath")] public string FfmpegPath { get; set; } = "";

    /// <summary>Folder RX recordings are written to, in 2-minute files.</summary>
    [JsonPropertyName("captureFolder")] public string CaptureFolder { get; set; } = "";

    /// <summary>Rate the recorder falls back to when the receiver reports no standard.</summary>
    [JsonPropertyName("captureFrameRate")] public int CaptureFrameRate { get; set; } = 25;

    /// <summary>Names the files a recording writes, in place of the default "capture".</summary>
    [JsonPropertyName("recordingTitle")] public string RecordingTitle { get; set; } = "";

    /// <summary>The operator's note about what is being recorded.</summary>
    [JsonPropertyName("recordingDescription")] public string RecordingDescription { get; set; } = "";

    /// <summary>HH:MM:SS:FF to stop a recording after, or empty to record until stopped.</summary>
    [JsonPropertyName("recordingDuration")] public string RecordingDuration { get; set; } = "";

    /// <summary>
    /// The most the capture store may grow to, in gigabytes. Zero is no limit.
    ///
    /// Past it, the oldest recordings are deleted to make room for the newest — a rolling
    /// window rather than a recording that stops when the disk fills. Off by default, because
    /// deleting an operator's recordings is not something to start doing on their behalf.
    /// </summary>
    [JsonPropertyName("recordingStorageLimitGb")] public int RecordingStorageLimitGb { get; set; }

    // How recordings are encoded. Read through RecordingProfile, which is what both the
    // capture deck and the EDL record with, so a value here is never handed to ffmpeg
    // without first being checked against the list the deck offers.

    /// <summary>
    /// Bitrate of the H.264 proxy. 0 leaves rate control to the encoder, which is how Emerald
    /// recorded before this was settable. The ProRes master is quality-driven and has none.
    /// </summary>
    [JsonPropertyName("recordingVideoBitrateKbps")] public int RecordingVideoBitrateKbps { get; set; }
    [JsonPropertyName("recordingAudioBitrateKbps")] public int RecordingAudioBitrateKbps { get; set; } = 192;
    [JsonPropertyName("recordingAudioSampleRate")] public int RecordingAudioSampleRate { get; set; } = 48000;
    [JsonPropertyName("recordingSegmentSeconds")] public int RecordingSegmentSeconds { get; set; } = 120;

    /// <summary>The language tracks, in list order — which is also their engine track index.</summary>
    [JsonPropertyName("audioTracks")] public List<AudioTrackSetting> AudioTracks { get; set; } = new();

    // The Ingest Controller's last state. Kept here with everything else rather than in a
    // file of its own: an operator's board, port and preroll are the same kind of thing as
    // the capture deck's, and one settings file is one place to look.

    [JsonPropertyName("ingestBoardIndex")] public uint IngestBoardIndex { get; set; }
    [JsonPropertyName("ingestPort")] public string IngestPort { get; set; } = "RX0";

    /// <summary>
    /// The timecode stamped into the head of an ingested clip. 01:00:00:00 by default:
    /// broadcast media conventionally starts an hour in, which leaves room to mark ahead of
    /// the first frame without going negative.
    /// </summary>
    [JsonPropertyName("ingestSom")] public string IngestSom { get; set; } = "01:00:00:00";

    /// <summary>How long an ingest records for, measured from the start timecode.</summary>
    [JsonPropertyName("ingestDuration")] public string IngestDuration { get; set; } = "00:15:00:00";

    /// <summary>Where ingested clips are written. Empty means the Emerald media store.</summary>
    [JsonPropertyName("ingestDirectory")] public string IngestDirectory { get; set; } = "";

    /// <summary>The operator's standing note, carried between ingests.</summary>
    [JsonPropertyName("ingestMetadata")] public string IngestMetadata { get; set; } = "";

    /// <summary>"duration" or "eom" — which of the pair the operator is driving.</summary>
    [JsonPropertyName("ingestTimingMode")] public string IngestTimingMode { get; set; } = "duration";

    /// <summary>
    /// Where the tidal lock delay ring is written. Empty means a local folder under
    /// %LOCALAPPDATA%, which is the right default: the ring needs a fast local disk and has
    /// no business on a network share or in a synced folder.
    /// </summary>
    [JsonPropertyName("tidalLockRingFolder")] public string TidalLockRingFolder { get; set; } = "";

    /// <summary>
    /// Runs the Ingest Controller against simulated boards and a simulated recorder, so the
    /// queue and the UI can be worked on where there is no card. Off unless asked for.
    /// </summary>
    [JsonPropertyName("ingestMockMode")] public bool IngestMockMode { get; set; }

    /// <summary>
    /// Where the activity record is written. Empty means %APPDATA%\Emerald\logs, beside
    /// settings.json and the ingest database.
    /// </summary>
    [JsonPropertyName("logFolder")] public string LogFolder { get; set; } = "";

    /// <summary>
    /// How many days of the record to keep. Zero keeps today only; negative keeps everything
    /// and leaves the pruning to whoever wants it done.
    /// </summary>
    [JsonPropertyName("logRetentionDays")] public int LogRetentionDays { get; set; } = 14;

    /// <summary>The default home for the record, alongside everything else Emerald remembers.</summary>
    public static string DefaultLogFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Emerald", "logs");

    /// <summary>Where the record actually goes, honouring the override when there is one.</summary>
    public string LogFolderOrDefault =>
        string.IsNullOrWhiteSpace(LogFolder) ? DefaultLogFolder : LogFolder;

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    private static string AppDataFile(string folder) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        folder, "settings.json");

    private static string FilePath => AppDataFile("Emerald");

    /// <summary>Where the pre-Emerald EDL Generator kept the same settings.</summary>
    private static string LegacyFilePath => AppDataFile("EdlGenerator");

    public static AppSettings Load()
    {
        try
        {
            // Carry the operator's board, port, media source and audio tracks across the
            // rename, so the first Emerald launch is not a blank slate.
            string path = File.Exists(FilePath) ? FilePath
                        : File.Exists(LegacyFilePath) ? LegacyFilePath
                        : "";

            if (path.Length > 0)
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options) ?? new AppSettings();
        }
        catch
        {
            // A corrupt or unreadable settings file is never worth blocking startup over.
        }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch
        {
            // Losing preferences is not worth an error dialog on shutdown.
        }
    }
}
