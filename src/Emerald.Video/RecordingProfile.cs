using System.IO;
using Emerald.Core;

namespace Emerald.Video;

/// <summary>One choice offered for a recording setting: what ffmpeg is told, and what the operator reads.</summary>
public sealed record RecordingOption<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// One of the two files a recording produces.
///
/// Every recording is written twice from the same receiver: a small H.264 proxy that opens
/// and scrubs instantly, and a ProRes master that is worth editing from. They are separate
/// outputs of one ffmpeg, not two passes, so the pair is frame-for-frame the same picture and
/// the receiver is only read once.
/// </summary>
public sealed record RecordingOutput(
    string Key,
    string Folder,
    string Extension,
    string Muxer,
    string VideoCodec,
    string VideoLabel,
    string PixelFormat,
    bool HalfSize)
{
    public string ContainerLabel => Extension.ToUpperInvariant();
}

/// <summary>
/// How a recording is written.
///
/// The deck and the EDL both record the same receiver through <see cref="SdiCapture"/>, so
/// they settle this in one place rather than each assembling its own encoder command. What
/// the operator sets on the deck is therefore also what the EDL records with, and there is a
/// single answer to "how was this file made".
/// </summary>
public sealed record RecordingProfile(
    int ProxyBitrateKbps,
    int AudioBitrateKbps,
    int AudioSampleRate,
    int SegmentSeconds)
{
    /// <summary>The proxy: what the clip strip lists and the stage plays.</summary>
    public static RecordingOutput LowRes { get; } = new(
        Key: "low",
        Folder: "low",
        Extension: "mp4",
        Muxer: "mp4",
        VideoCodec: "libx264",
        VideoLabel: "H.264",
        PixelFormat: "yuv420p",
        HalfSize: true);

    /// <summary>The master, kept at full raster.</summary>
    public static RecordingOutput HighRes { get; } = new(
        Key: "high",
        Folder: "high",
        Extension: "mov",
        Muxer: "mov",
        VideoCodec: "prores_ks",
        VideoLabel: "ProRes 422",
        PixelFormat: "yuv422p10le",
        HalfSize: false);

    /// <summary>Both outputs, proxy first, which is the order ffmpeg is given them in.</summary>
    public static IReadOnlyList<RecordingOutput> Outputs { get; } = new[] { LowRes, HighRes };

    /// <summary>AAC on both files: ProRes carries it perfectly well inside a MOV.</summary>
    public const string AudioCodec = "aac";
    public const string AudioLabel = "AAC";

    public static RecordingProfile Default { get; } = new(0, 192, 48000, 120);

    // The lists the deck offers. They live here so the values ffmpeg is given and the words
    // the operator picks between cannot drift apart.

    /// <summary>0 is the quality-driven default the recorder has always used.</summary>
    public static IReadOnlyList<RecordingOption<int>> ProxyBitrates { get; } = new[]
    {
        new RecordingOption<int>(0, "Auto detect"),
        new RecordingOption<int>(1500, "1500 kbps"),
        new RecordingOption<int>(3000, "3000 kbps"),
        new RecordingOption<int>(5000, "5000 kbps"),
        new RecordingOption<int>(8000, "8000 kbps"),
    };

    public static IReadOnlyList<RecordingOption<int>> AudioBitrates { get; } = new[]
    {
        new RecordingOption<int>(128, "128 kbps"),
        new RecordingOption<int>(192, "192 kbps"),
        new RecordingOption<int>(256, "256 kbps"),
        new RecordingOption<int>(320, "320 kbps"),
    };

    /// <summary>SDI embedded audio is 48 kHz; anything else here is a resample on the way out.</summary>
    public static IReadOnlyList<RecordingOption<int>> SampleRates { get; } = new[]
    {
        new RecordingOption<int>(48000, "48 kHz"),
        new RecordingOption<int>(44100, "44.1 kHz"),
        new RecordingOption<int>(32000, "32 kHz"),
    };

    public static IReadOnlyList<RecordingOption<int>> SegmentLengths { get; } = new[]
    {
        new RecordingOption<int>(60, "1 min"),
        new RecordingOption<int>(120, "2 min"),
        new RecordingOption<int>(300, "5 min"),
        new RecordingOption<int>(600, "10 min"),
    };

    public static RecordingProfile From(AppSettings settings) => new(
        ProxyBitrateKbps: Pick(ProxyBitrates, settings.RecordingVideoBitrateKbps, Default.ProxyBitrateKbps),
        AudioBitrateKbps: Pick(AudioBitrates, settings.RecordingAudioBitrateKbps, Default.AudioBitrateKbps),
        AudioSampleRate: Pick(SampleRates, settings.RecordingAudioSampleRate, Default.AudioSampleRate),
        SegmentSeconds: Pick(SegmentLengths, settings.RecordingSegmentSeconds, Default.SegmentSeconds));

    public void ApplyTo(AppSettings settings)
    {
        settings.RecordingVideoBitrateKbps = ProxyBitrateKbps;
        settings.RecordingAudioBitrateKbps = AudioBitrateKbps;
        settings.RecordingAudioSampleRate = AudioSampleRate;
        settings.RecordingSegmentSeconds = SegmentSeconds;
    }

    /// <summary>An unknown value in the settings file falls back rather than reaching ffmpeg.</summary>
    private static T Pick<T>(IReadOnlyList<RecordingOption<T>> options, T value, T fallback) =>
        options.Any(o => EqualityComparer<T>.Default.Equals(o.Value, value)) ? value : fallback;

    public static string Label<T>(IReadOnlyList<RecordingOption<T>> options, T value) =>
        options.FirstOrDefault(o => EqualityComparer<T>.Default.Equals(o.Value, value))?.Label
        ?? value?.ToString() ?? "";

    /// <summary>Where an output's files land under the operator's recording folder.</summary>
    public static string FolderFor(RecordingOutput output, string root) =>
        Path.Combine(root, output.Folder);

    /// <summary>The folders a recording writes into, both of which must exist first.</summary>
    public static IEnumerable<string> FoldersFor(string root) =>
        Outputs.Select(o => FolderFor(o, root));

    /// <summary>What the operator reads back for one output: "MP4 | H.264 | AAC".</summary>
    public static string Summary(RecordingOutput output) =>
        $"{output.ContainerLabel}  |  {output.VideoLabel}  |  {AudioLabel}";

    /// <summary>
    /// The encoder command for one recording, writing both files. Video arrives on stdin as
    /// raw UYVY and audio on a named pipe, which is the only way to get both into one muxed
    /// file from a single process; each output then maps the same two inputs.
    /// </summary>
    /// <param name="singleFile">
    /// True to write one file per output, named exactly <paramref name="namePrefix"/>, rather
    /// than the timestamped segments a continuous capture produces. An ingest is a single
    /// clip the operator named and will hand on; it cannot come back as a pile of segments.
    /// </param>
    /// <param name="startTimecode">
    /// HH:MM:SS:FF to stamp into the file as its start timecode, or null for none.
    ///
    /// This is what puts a tmcd track in the container, and it is the difference between a
    /// clip that knows where it sits on the station clock and one that starts at zero. The
    /// EDL reads it back through ffprobe and quotes SOM and EOM against it; without it a
    /// recording is just fifteen minutes of pictures from nowhere in particular.
    ///
    /// Only meaningful with <paramref name="singleFile"/>: a segmented capture would stamp
    /// every segment with the same start, which is worse than stamping none of them.
    /// </param>
    public IEnumerable<string> EncoderArguments(CaptureFormat format, string pipeName,
                                                string folder, string namePrefix, int inputSampleRate,
                                                bool singleFile = false, string? startTimecode = null)
    {
        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "rawvideo", "-pix_fmt", "uyvy422",
            "-s", $"{format.Width}x{format.Height}", "-r", format.FrameRate.ToString(), "-i", "pipe:0",
            "-f", "s16le", "-ar", inputSampleRate.ToString(), "-ac", "2", "-i", $@"\\.\pipe\{pipeName}",
        };

        foreach (RecordingOutput output in Outputs)
        {
            args.Add("-map"); args.Add("0:v:0");
            args.Add("-map"); args.Add("1:a:0");

            if (output.HalfSize)
            {
                (int width, int height) = HalfRaster(format);
                args.Add("-filter:v"); args.Add($"scale={width}:{height}");
            }

            args.Add("-c:v"); args.Add(output.VideoCodec);

            if (output.VideoCodec == "prores_ks")
            {
                // Profile 2 is ProRes 422 proper - not LT below it, not HQ above.
                args.Add("-profile:v"); args.Add("2");
            }
            else
            {
                args.Add("-preset"); args.Add("ultrafast");
                args.Add("-tune"); args.Add("zerolatency");

                // No bitrate means the encoder's own rate control, which is what Emerald
                // recorded with before the setting existed.
                if (ProxyBitrateKbps > 0)
                {
                    args.Add("-b:v"); args.Add($"{ProxyBitrateKbps}k");
                    args.Add("-maxrate"); args.Add($"{ProxyBitrateKbps}k");
                    args.Add("-bufsize"); args.Add($"{ProxyBitrateKbps * 2}k");
                }
            }

            args.Add("-pix_fmt"); args.Add(output.PixelFormat);

            args.Add("-c:a"); args.Add(AudioCodec);
            args.Add("-b:a"); args.Add($"{AudioBitrateKbps}k");
            if (AudioSampleRate != inputSampleRate) { args.Add("-ar"); args.Add(AudioSampleRate.ToString()); }

            // Before the output, so it applies to this file and not to the input.
            if (singleFile && !string.IsNullOrWhiteSpace(startTimecode))
            {
                args.Add("-timecode"); args.Add(startTimecode);
            }

            if (singleFile)
            {
                // No -y: ffmpeg is left to refuse rather than quietly replace a clip that is
                // already on disk. The ingest checks for the file before it ever gets here,
                // and this is the backstop for the race between that check and the encoder.
                args.Add("-f"); args.Add(output.Muxer);
                args.Add(Path.Combine(FolderFor(output, folder), $"{namePrefix}.{output.Extension}"));
            }
            else
            {
                args.Add("-f"); args.Add("segment");
                args.Add("-segment_time"); args.Add(SegmentSeconds.ToString());
                args.Add("-segment_format"); args.Add(output.Muxer);
                args.Add("-reset_timestamps"); args.Add("1");
                args.Add("-strftime"); args.Add("1");
                args.Add(Path.Combine(FolderFor(output, folder),
                                      $"{namePrefix}_%Y-%m-%d_%H-%M-%S.{output.Extension}"));
            }
        }

        return args;
    }

    /// <summary>
    /// Half the source raster, rounded to even in both axes — H.264 cannot encode an odd
    /// dimension, and a 1080-line source halves to 540 either way.
    /// </summary>
    public static (int Width, int Height) HalfRaster(CaptureFormat format) =>
        (Math.Max(2, format.Width / 2 / 2 * 2), Math.Max(2, format.Height / 2 / 2 * 2));
}
