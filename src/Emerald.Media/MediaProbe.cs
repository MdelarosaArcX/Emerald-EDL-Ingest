using Emerald.Core;
using Emerald.Video;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Emerald.Media;

/// <summary>
/// One audio stream inside a media file — in practice, one language.
///
/// <see cref="Index"/> counts among the audio streams alone, not among all streams, because
/// that is the number ffmpeg's <c>0:a:N</c> takes and the number everything downstream needs.
/// </summary>
public sealed record MediaAudioStream(
    int Index,
    string Codec,
    int Channels,
    string? Language,
    string? Title)
{
    /// <summary>
    /// What to call it. A file that was mastered properly names its tracks; most are not, so
    /// the language code is the next best thing and a bare number the last resort.
    /// </summary>
    public string Label =>
        !string.IsNullOrWhiteSpace(Title) ? Title!.Trim()
        : !string.IsNullOrWhiteSpace(Language) && !Language!.Equals("und", StringComparison.OrdinalIgnoreCase)
            ? Language!.Trim()
            : $"Track {Index + 1}";

    /// <summary>The technical line under the label, so an operator can tell two apart.</summary>
    public string Detail =>
        $"stream {Index + 1} - {Codec} {Channels}ch" +
        (string.IsNullOrWhiteSpace(Language) ? "" : $" [{Language}]");
}

/// <summary>
/// What ffprobe can tell us about a media file. <see cref="StartTimecode"/> is the key
/// one: broadcast media conventionally starts at 01:00:00:00, so SOM and EOM are quoted
/// against that, not against elapsed time from the head of the file.
/// </summary>
public sealed record MediaInfo(
    string Path,
    TimeSpan Duration,
    Timecode StartTimecode,
    bool HasEmbeddedTimecode,
    bool HasAudio,
    string VideoCodec,
    int Width,
    int Height,
    IReadOnlyList<MediaAudioStream>? AudioStreams = null)
{
    /// <summary>Every audio stream in the file, in file order. Never null.</summary>
    public IReadOnlyList<MediaAudioStream> Audio => AudioStreams ?? Array.Empty<MediaAudioStream>();

    public Timecode EndTimecode(int rate) =>
        StartTimecode.AddWrapping((long)Math.Round(Duration.TotalSeconds * rate));

    public Timecode Length(int rate) => new((long)Math.Round(Duration.TotalSeconds * rate), rate);

    /// <summary>
    /// Length leads, because that is what bounds SOM and EOM. The media's own timecode is
    /// reported for reference only — SOM is an offset from the head of the file, not a
    /// match against this value.
    /// </summary>
    public string Summary(int rate) =>
        $"length {Length(rate)}{AudioSummary}" +
        (HasEmbeddedTimecode ? $", media TC starts {StartTimecode}" : "");

    /// <summary>
    /// How many languages are in there. The count is what matters to an operator loading a
    /// multi-language master, so it leads rather than a bare "has audio".
    /// </summary>
    private string AudioSummary => Audio.Count switch
    {
        0 => HasAudio ? ", has audio" : ", no audio",
        1 => ", 1 audio track",
        var n => $", {n} audio tracks",
    };
}

public static class MediaProbe
{
    /// <summary>ffprobe sits next to ffmpeg in every build that ships them.</summary>
    public static string? LocateFfprobe(string? ffmpegPath)
    {
        if (string.IsNullOrWhiteSpace(ffmpegPath)) return null;

        string? dir = Path.GetDirectoryName(ffmpegPath);
        if (dir is null) return null;

        string candidate = Path.Combine(dir, "ffprobe.exe");
        return File.Exists(candidate) ? candidate : null;
    }

    /// <summary>
    /// Reads duration, start timecode and stream layout. Returns null when ffprobe is
    /// missing or the file cannot be read; callers fall back to treating SOM as elapsed.
    /// </summary>
    public static MediaInfo? Probe(string? ffprobePath, string mediaPath, int frameRate)
    {
        if (ffprobePath is null || !File.Exists(mediaPath)) return null;

        try
        {
            var info = new ProcessStartInfo(ffprobePath)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            info.ArgumentList.Add("-v"); info.ArgumentList.Add("error");
            info.ArgumentList.Add("-print_format"); info.ArgumentList.Add("json");
            info.ArgumentList.Add("-show_format");
            info.ArgumentList.Add("-show_streams");
            info.ArgumentList.Add(mediaPath);

            using Process? probe = Process.Start(info);
            if (probe is null) return null;

            string json = probe.StandardOutput.ReadToEnd();
            probe.StandardError.ReadToEnd();
            probe.WaitForExit(15000);

            return Parse(json, mediaPath, frameRate);
        }
        catch
        {
            return null;
        }
    }

    private static MediaInfo? Parse(string json, string mediaPath, int frameRate)
    {
        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        double duration = 0;
        string? timecodeTag = null;

        if (root.TryGetProperty("format", out JsonElement format))
        {
            if (format.TryGetProperty("duration", out JsonElement d) &&
                double.TryParse(d.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed))
                duration = parsed;

            timecodeTag = Tag(format, "timecode");
        }

        bool hasAudio = false;
        string codec = "?";
        int width = 0, height = 0;
        var audio = new List<MediaAudioStream>();

        if (root.TryGetProperty("streams", out JsonElement streams))
        {
            foreach (JsonElement s in streams.EnumerateArray())
            {
                string? type = s.TryGetProperty("codec_type", out JsonElement t) ? t.GetString() : null;

                if (type == "audio")
                {
                    hasAudio = true;

                    // The index is the position among audio streams, which is why it is the
                    // list count rather than the stream's own "index" field - ffmpeg's
                    // 0:a:N counts this way and the file's overall stream order does not.
                    audio.Add(new MediaAudioStream(
                        Index: audio.Count,
                        Codec: s.TryGetProperty("codec_name", out JsonElement ac) ? ac.GetString() ?? "?" : "?",
                        Channels: s.TryGetProperty("channels", out JsonElement ch) && ch.TryGetInt32(out int c) ? c : 0,
                        Language: Tag(s, "language"),
                        Title: StreamTitle(s)));
                }

                if (type == "video" && width == 0)
                {
                    codec = s.TryGetProperty("codec_name", out JsonElement c) ? c.GetString() ?? "?" : "?";
                    width = s.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0;
                    height = s.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : 0;
                }

                // The timecode track is usually a data stream carrying a "timecode" tag.
                timecodeTag ??= Tag(s, "timecode");
            }
        }

        Timecode start = Timecode.Zero(frameRate);
        bool hasTimecode = timecodeTag is not null &&
                           Timecode.TryParse(timecodeTag, frameRate, out start, out _);

        return new MediaInfo(
            Path: mediaPath,
            Duration: TimeSpan.FromSeconds(duration),
            StartTimecode: hasTimecode ? start : Timecode.Zero(frameRate),
            HasEmbeddedTimecode: hasTimecode,
            HasAudio: hasAudio,
            VideoCodec: codec,
            Width: width,
            Height: height,
            AudioStreams: audio);
    }

    /// <summary>
    /// What a stream calls itself. Matroska uses "title"; MOV and MP4 use "name" - which is
    /// what Emerald's own ingest writes. "handler_name" is a last resort and mostly noise:
    /// muxers fill it with boilerplate like "SoundHandler", which is worse than no name at
    /// all because it looks like one.
    /// </summary>
    private static string? StreamTitle(JsonElement stream)
    {
        string? named = Tag(stream, "title") ?? Tag(stream, "name");
        if (!string.IsNullOrWhiteSpace(named)) return named;

        string? handler = Tag(stream, "handler_name")?.Trim();

        return string.IsNullOrEmpty(handler) || GenericHandlers.Contains(handler) ? null : handler;
    }

    private static readonly HashSet<string> GenericHandlers = new(StringComparer.OrdinalIgnoreCase)
    {
        "SoundHandler", "Sound Media Handler", "Core Media Audio", "Apple Sound Media Handler",
        "GPAC ISO Audio Handler", "Mainconcept MP4 Sound Media Handler", "audio", "Audio",
    };

    private static string? Tag(JsonElement element, string name) =>
        element.TryGetProperty("tags", out JsonElement tags) && tags.TryGetProperty(name, out JsonElement value)
            ? value.GetString()
            : null;
}
