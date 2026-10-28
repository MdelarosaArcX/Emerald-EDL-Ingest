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

    /// <summary>Names the files a recording writes, in place of <see cref="RecordingSequence.DefaultPrefix"/>.</summary>
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
    /// <summary>
    /// How long each recorded segment runs.
    ///
    /// Thirty seconds rather than two minutes. The segment length is also how long a recording
    /// is unreadable for: ffmpeg writes the index last, so the segment being written cannot be
    /// played, probed or exported until it closes. At two minutes that meant up to two minutes
    /// of the most recent material was out of reach in Live Edit — which is the material anybody
    /// looking at a live plant actually wants.
    ///
    /// The cost is four times as many files and four times as many closing boundaries, which is
    /// cheap: a day is 2,880 segments per profile rather than 720.
    /// </summary>
    [JsonPropertyName("recordingSegmentSeconds")] public int RecordingSegmentSeconds { get; set; } = DefaultSegmentSeconds;

    public const int DefaultSegmentSeconds = 30;

    /// <summary>
    /// True once an operator has picked the segment length on the capture deck. From then on it
    /// is a decision rather than a leftover default, and <see cref="Migrate"/> leaves it alone -
    /// otherwise choosing two minutes would be quietly undone the next time the settings loaded,
    /// because two minutes is also what the old default was.
    /// </summary>
    [JsonPropertyName("recordingSegmentChosen")] public bool RecordingSegmentChosen { get; set; }

    /// <summary>
    /// The segment length written into every settings file before the default changed.
    ///
    /// Like <see cref="RecordingSequence.FormerDefaultPrefix"/>, this is not a choice anybody
    /// made: it was saved the first time the deck opened. A changed default reaches only a
    /// machine with no settings file, so without this every machine that has ever run Emerald
    /// would keep two-minute segments for good — which is also two minutes before the newest
    /// material can be played, probed or exported.
    ///
    /// There was no control for this on the deck then, so nobody could have chosen it. There is
    /// now, and a choice made there is marked by <see cref="RecordingSegmentChosen"/>.
    /// </summary>
    public const int FormerDefaultSegmentSeconds = 120;

    /// <summary>
    /// Whether this machine serves its monitoring to other machines on the network.
    ///
    /// Off unless asked for. Nothing listens, no port is open, and a plant that never wants
    /// this is exactly as it was.
    /// </summary>
    /// <summary>
    /// Every TX port the delayed feed goes out of, as names separated by a bar.
    ///
    /// Names rather than indices because a settings file outlives a board being moved to another
    /// slot. <see cref="PlaybackPort"/> is kept alongside it as the first of them, so anything
    /// that still expects a single port finds one.
    /// </summary>
    [JsonPropertyName("playbackPorts")] public string PlaybackPorts { get; set; } = "";

    // What Live Edit last went live with, so the Go Live box opens on it. Kept apart from the
    // playback deck's, because the two are separate transmissions and commonly on separate ports.
    [JsonPropertyName("liveBoardIndex")] public uint LiveBoardIndex { get; set; }

    /// <summary>TX port names, separated by a bar, as <see cref="PlaybackPorts"/>.</summary>
    [JsonPropertyName("livePorts")] public string LivePorts { get; set; } = "";

    [JsonPropertyName("liveDelaySeconds")] public int LiveDelaySeconds { get; set; } = 120;

    /// <summary>Whether Go Live last started a set delay behind, or from the first segment.</summary>
    [JsonPropertyName("liveDelayStart")] public DelayStart LiveDelayStart { get; set; }

    /// <summary>
    /// Whether the capture deck keeps a delay filling for as long as it records, so tidal lock
    /// and Go Live go to air the moment they are asked rather than after a delay of black.
    ///
    /// On by default. It costs what a tidal lock always cost while it ran - a ring of raw frames
    /// on disk, about six gigabytes a minute at 1080p25, written as it records - but now for the
    /// whole recording. Off puts back the old behaviour: the delay starts filling when armed.
    /// </summary>
    [JsonPropertyName("keepDelayReady")] public bool KeepDelayReady { get; set; } = true;

    /// <summary>
    /// Draws every window in software instead of on the graphics card.
    ///
    /// For a machine whose graphics driver has got into a state where WPF's hardware path
    /// presents nothing - every window comes up plain white while the application behind it runs
    /// normally. Restarting the driver or the machine is the cure; this is the way to keep working
    /// until then, at the cost of some CPU for the previews. Off by default. The environment
    /// variable EMERALD_SOFTWARE_RENDER=1 does the same without editing this file.
    /// </summary>
    [JsonPropertyName("softwareRendering")] public bool SoftwareRendering { get; set; }

    /// <summary>The extra the delay ring holds for inserts to grow the delay into.</summary>
    [JsonPropertyName("liveHeadroomSeconds")] public int LiveHeadroomSeconds { get; set; } = 300;

    /// <summary>
    /// Whether the EDL records the receiver by itself while a message is on air.
    ///
    /// <b>Off unless asked for, and that default is the point of this setting.</b> The EDL used
    /// to start a recording of its own every time playout began — no toggle, no mention of it on
    /// the page — writing the same receiver into the same store as the capture deck under the
    /// older flat naming. Two recorders, two schemes, one folder: a store with a recording in it
    /// that nobody had started, at roughly 1.8 GB of master every two minutes.
    ///
    /// The feature itself is defensible — what lands on disk lines up with what was transmitted —
    /// so it is kept and made a choice rather than removed.
    /// </summary>
    [JsonPropertyName("edlRecordWhileOnAir")] public bool EdlRecordWhileOnAir { get; set; }

    [JsonPropertyName("monitorApi")] public bool MonitorApi { get; set; }

    /// <summary>The port the monitoring is served on when it is on.</summary>
    [JsonPropertyName("monitorApiPort")] public int MonitorApiPort { get; set; } = MonitorServer.DefaultPort;

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
                return Migrate(
                    JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Options)
                    ?? new AppSettings());
        }
        catch
        {
            // A corrupt or unreadable settings file is never worth blocking startup over.
        }
        return new AppSettings();
    }

    /// <summary>
    /// Brings a settings file written by an older Emerald up to date.
    ///
    /// Only values that were defaults rather than decisions. A changed default reaches a fresh
    /// install and nothing else, so without this the only machines running the current behaviour
    /// would be ones that had never run Emerald before.
    ///
    /// Not saved here: loading is not the moment to write, and the next ordinary Save carries it.
    /// </summary>
    public static AppSettings Migrate(AppSettings settings)
    {
        if (!settings.RecordingSegmentChosen &&
            settings.RecordingSegmentSeconds == FormerDefaultSegmentSeconds)
            settings.RecordingSegmentSeconds = DefaultSegmentSeconds;

        return settings;
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
