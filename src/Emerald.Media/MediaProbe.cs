using Emerald.Core;
using Emerald.Video;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;

namespace Emerald.Media;

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
    int Height)
{
    public Timecode EndTimecode(int rate) =>
        StartTimecode.AddWrapping((long)Math.Round(Duration.TotalSeconds * rate));

    public Timecode Length(int rate) => new((long)Math.Round(Duration.TotalSeconds * rate), rate);

    /// <summary>
    /// Length leads, because that is what bounds SOM and EOM. The media's own timecode is
    /// reported for reference only — SOM is an offset from the head of the file, not a
    /// match against this value.
    /// </summary>
    public string Summary(int rate) =>
        $"length {Length(rate)}{(HasAudio ? ", has audio" : ", no audio")}" +
        (HasEmbeddedTimecode ? $", media TC starts {StartTimecode}" : "");
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

        if (root.TryGetProperty("streams", out JsonElement streams))
        {
            foreach (JsonElement s in streams.EnumerateArray())
            {
                string? type = s.TryGetProperty("codec_type", out JsonElement t) ? t.GetString() : null;

                if (type == "audio") hasAudio = true;

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
            Height: height);
    }

    private static string? Tag(JsonElement element, string name) =>
        element.TryGetProperty("tags", out JsonElement tags) && tags.TryGetProperty(name, out JsonElement value)
            ? value.GetString()
            : null;
}
