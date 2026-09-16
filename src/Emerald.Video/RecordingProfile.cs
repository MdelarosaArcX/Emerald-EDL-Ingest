using System.Globalization;
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
/// and scrubs instantly, and a DNxHD master that is worth editing from. They are separate
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
    bool HalfSize,

    /// <summary>
    /// The encoder's <c>-profile:v</c>, when it needs one. ProRes picks a variant with it and
    /// DNxHR picks its whole quality tier with it, so it is a property of the output rather
    /// than a special case in the argument builder.
    /// </summary>
    string? Profile = null,

    /// <summary>The four-character code this output writes, for recognising it on disk later.</summary>
    string FourCc = "",

    /// <summary>
    /// A fixed video bitrate, when the codec is defined by one. DNxHD is: the number in
    /// "DNxHD 145" <i>is</i> the setting, and the encoder refuses anything that is not one of
    /// the values in its table.
    /// </summary>
    string? VideoBitrate = null)
{
    public string ContainerLabel => Extension.ToUpperInvariant();

    /// <summary>
    /// Whether this output can encode a given raster.
    ///
    /// Only DNxHD answers no to anything. It is defined as a table of frame sizes rather than
    /// as a general-purpose codec, and a receiver that locked to standard definition would
    /// have its recording refused by the encoder several frames in — taking the proxy with it,
    /// since both files come out of one ffmpeg. Checked before the encoder starts so the
    /// refusal is a sentence rather than a dead process.
    /// </summary>
    public bool Supports(int width, int height)
    {
        if (VideoCodec != "dnxhd" || Profile is { Length: > 0 }) return true;

        return (width, height) is (1920, 1080) or (1440, 1080) or (1280, 720);
    }

    /// <summary>Why a raster was refused, in words the operator can act on.</summary>
    public string Refuses(int width, int height) =>
        $"{VideoLabel} records 1920x1080, 1440x1080 and 1280x720 only - the receiver is on " +
        $"{width}x{height}. Choose a different master format, or a receiver that is not.";
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
        HalfSize: true,
        FourCc: "avc1");

    /// <summary>
    /// The master, kept at full raster: <b>DNxHD 145</b>.
    ///
    /// The number is the codec. DNxHD is defined as a table of frame size, rate and bitrate
    /// combinations rather than as a quality setting, so 145 Mbps at 8-bit 4:2:2 <i>is</i> the
    /// tier — and anything that is not one of the table's values is refused outright. Tested
    /// against this ffmpeg at 1920x1080p: 145 is accepted at 24, 25, 30, 50 and 60.
    ///
    /// <b>What it will not take is a raster outside the table.</b> 1920x1080, 1440x1080 and
    /// 1280x720 are in; standard definition is not, and a receiver on 720x576 has its
    /// recording refused — which would take the proxy with it, since both files come out of
    /// one ffmpeg. <see cref="RecordingOutput.Supports"/> is checked before the encoder starts
    /// so that arrives as a sentence rather than as a process that dies a few frames in.
    ///
    /// Against ProRes 422's measured 89 Mbps on this plant's feed, 145 is a little over one
    /// and a half times the disk.
    /// </summary>
    public static RecordingOutput HighRes { get; } = new(
        Key: "high",
        Folder: "high",
        Extension: "mov",
        Muxer: "mov",
        VideoCodec: "dnxhd",
        VideoLabel: "DNxHD 145",
        // 8-bit 4:2:2, which is what this tier is defined as.
        PixelFormat: "yuv422p",
        HalfSize: false,
        // No -profile:v. DNxHD is selected by its bitrate; naming a profile would put the
        // encoder into DNxHR instead, which is a different codec with a different fourcc.
        FourCc: "AVdn",
        VideoBitrate: "145M");

    /// <summary>Both outputs, proxy first, which is the order ffmpeg is given them in.</summary>
    public static IReadOnlyList<RecordingOutput> Outputs { get; } = new[] { LowRes, HighRes };

    /// <summary>AAC on both files: a MOV carries it perfectly well alongside either codec.</summary>
    public const string AudioCodec = "aac";
    public const string AudioLabel = "AAC";

    /// <summary>What the receiver's own sound is called in a file that carries more than one track.</summary>
    public const string OriginalAudioLabel = "Original";

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
    /// <param name="extraAudio">
    /// Audio files to record alongside the receiver's own sound, each becoming a further
    /// stream in both files. The receiver's audio is always stream 0 and is never replaced.
    ///
    /// Each is looped, and the outputs are cut to the shortest input. The receiver's own
    /// audio and the video both end together when the recording stops, so that makes the
    /// picture the thing that decides the length: a bed shorter than the recording repeats
    /// instead of falling silent, and one longer than it does not run past the end. That
    /// holds segment by segment, so every segment of a continuous recording carries every
    /// track rather than only the first one.
    ///
    /// A track carrying a gain or an offset goes through the filtergraph instead of straight
    /// from its input; one left alone is mapped exactly as it always was.
    /// </param>
    public IEnumerable<string> EncoderArguments(CaptureFormat format, string pipeName,
                                                string folder, string namePrefix, int inputSampleRate,
                                                bool singleFile = false, string? startTimecode = null,
                                                IReadOnlyList<CaptureAudioTrack>? extraAudio = null,
                                                int embeddedPairs = 1)
    {
        // The receiver's audio arrives on one pipe carrying every channel it had. One pair is
        // the ordinary case and is left exactly as it always was, straight through as stereo.
        // More than one has to be split, because a feed carrying four languages on four pairs
        // must come back as four tracks rather than one eight-channel jumble no player will
        // make sense of.
        int pairs = Math.Clamp(embeddedPairs, 1, SdiAudioReader.MaxPairs);
        int pipeChannels = pairs * 2;

        var args = new List<string>
        {
            "-hide_banner", "-loglevel", "error", "-nostdin",
            "-f", "rawvideo", "-pix_fmt", "uyvy422",
            "-s", $"{format.Width}x{format.Height}", "-r", format.FrameRate.ToString(), "-i", "pipe:0",
            "-f", "s16le", "-ar", inputSampleRate.ToString(), "-ac", pipeChannels.ToString(),
            "-i", $@"\\.\pipe\{pipeName}",
        };

        IReadOnlyList<CaptureAudioTrack> beds = extraAudio ?? Array.Empty<CaptureAudioTrack>();

        // Inputs 2 onwards. -stream_loop comes before the -i it applies to.
        foreach (CaptureAudioTrack track in beds)
        {
            args.Add("-stream_loop"); args.Add("-1");
            args.Add("-i"); args.Add(track.Path);
        }

        if (AudioGraph(pairs, Outputs.Count, beds) is { Length: > 0 } graph)
        {
            args.Add("-filter_complex"); args.Add(graph);
        }

        int outputIndex = 0;

        foreach (RecordingOutput output in Outputs)
        {
            args.Add("-map"); args.Add("0:v:0");

            if (pairs == 1)
            {
                args.Add("-map"); args.Add("1:a:0");
            }
            else
            {
                // Each pair was split once per output, because a filter output can only be
                // mapped a single time and both the master and the proxy want all of them.
                for (int p = 0; p < pairs; p++)
                {
                    args.Add("-map"); args.Add($"[{PairLabel(p, outputIndex)}]");
                }
            }

            for (int i = 0; i < beds.Count; i++)
            {
                // An adjusted track comes out of the graph, one copy per output file; an
                // untouched one is mapped straight from its input, which an input stream may
                // be as many times as there are outputs.
                args.Add("-map");
                args.Add(beds[i].IsAdjusted ? $"[{BedLabel(i, outputIndex)}]" : $"{i + 2}:a:0");
            }

            if (output.HalfSize)
            {
                (int width, int height) = HalfRaster(format);
                args.Add("-filter:v"); args.Add($"scale={width}:{height}");
            }

            args.Add("-c:v"); args.Add(output.VideoCodec);

            if (output.Profile is { Length: > 0 } videoProfile)
            {
                args.Add("-profile:v"); args.Add(videoProfile);
            }

            if (output.VideoBitrate is { Length: > 0 } videoBitrate)
            {
                args.Add("-b:v"); args.Add(videoBitrate);
            }

            if (output.VideoCodec == "libx264")
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

            if (beds.Count > 0 || pairs > 1)
            {
                // Named, so the tracks can be told apart in a player or an NLE rather than
                // being "Audio 1, Audio 2, Audio 3" and left to guess.
                for (int p = 0; p < pairs; p++)
                {
                    args.Add($"-metadata:s:a:{p}");
                    args.Add($"title={(pairs == 1 ? OriginalAudioLabel : $"{OriginalAudioLabel} {p + 1} (CH {p * 2 + 1}-{p * 2 + 2})")}");
                    args.Add($"-disposition:a:{p}"); args.Add(p == 0 ? "default" : "0");
                }

                for (int i = 0; i < beds.Count; i++)
                {
                    args.Add($"-metadata:s:a:{pairs + i}"); args.Add($"title={beds[i].Label}");
                    args.Add($"-disposition:a:{pairs + i}"); args.Add("0");
                }
            }

            if (beds.Count > 0)
            {
                // The beds are looped, so they never end; the picture and the receiver's own
                // audio do. That makes the picture what decides the length of the file.
                args.Add("-shortest");
            }

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

            outputIndex++;
        }

        return args;
    }

    /// <summary>The filter label carrying pair <paramref name="pair"/> for one output file.</summary>
    private static string PairLabel(int pair, int output) => $"p{pair}o{output}";

    private static string BedLabel(int bed, int output) => $"b{bed}o{output}";

    /// <summary>
    /// The one filtergraph both halves of the audio share: the receiver's pipe split into
    /// stereo pairs, and any added track the operator has adjusted.
    ///
    /// It has to be one graph because ffmpeg takes a single <c>-filter_complex</c>, and the
    /// two would otherwise overwrite each other — which is why the pair splitting moved in
    /// here rather than the beds getting a graph of their own.
    ///
    /// <c>pan</c> rather than <c>channelsplit</c> because pan names the source channels
    /// explicitly and does not care what channel layout ffmpeg guessed for eight raw channels;
    /// <c>asplit</c> because a filter output may be mapped only once, and both the master and
    /// the proxy want every pair and every bed.
    ///
    /// Returns an empty string when there is nothing to filter, and the caller then passes no
    /// graph at all — a single-pair recording with no adjusted track is exactly the command it
    /// always was.
    /// </summary>
    private static string AudioGraph(int pairs, int outputs, IReadOnlyList<CaptureAudioTrack> beds)
    {
        var chains = new List<string>();

        if (pairs > 1)
        {
            for (int p = 0; p < pairs; p++)
            {
                var labels = new System.Text.StringBuilder();
                for (int o = 0; o < outputs; o++) labels.Append($"[{PairLabel(p, o)}]");

                chains.Add($"[1:a]pan=stereo|c0=c{p * 2}|c1=c{p * 2 + 1},asplit={outputs}{labels}");
            }
        }

        for (int i = 0; i < beds.Count; i++)
        {
            if (!beds[i].IsAdjusted) continue;

            var labels = new System.Text.StringBuilder();
            for (int o = 0; o < outputs; o++) labels.Append($"[{BedLabel(i, o)}]");

            chains.Add($"[{i + 2}:a]{BedFilters(beds[i])},asplit={outputs}{labels}");
        }

        return string.Join(";", chains);
    }

    /// <summary>
    /// What an added track's gain and offset come to in filters.
    ///
    /// A positive offset delays the sound, for a bed that arrives ahead of the picture; a
    /// negative one trims that much off the front, which is the only way to pull sound earlier
    /// against a picture that cannot itself be delayed — the receiver's frames are already
    /// going to air through the delay line, and nothing here may touch them.
    ///
    /// Formatted invariantly. A machine with a comma for a decimal point would otherwise write
    /// <c>volume=-3,5dB</c>, and the comma is the filter separator: ffmpeg would read it as a
    /// second filter and refuse the graph.
    /// </summary>
    private static string BedFilters(CaptureAudioTrack track)
    {
        var parts = new List<string>();

        int offset = Math.Clamp(track.OffsetMs,
                                -CaptureAudioTrack.MaxOffsetMs, CaptureAudioTrack.MaxOffsetMs);

        if (offset > 0)
        {
            parts.Add($"adelay={offset}:all=1");
        }
        else if (offset < 0)
        {
            string seconds = (-offset / 1000.0).ToString("0.###", CultureInfo.InvariantCulture);
            parts.Add($"atrim=start={seconds}");
            parts.Add("asetpts=N/SR/TB");
        }

        double gain = Math.Clamp(track.GainDb,
                                 CaptureAudioTrack.MinGainDb, CaptureAudioTrack.MaxGainDb);

        if (gain != 0) parts.Add($"volume={gain.ToString("0.##", CultureInfo.InvariantCulture)}dB");

        // Something has to be in the chain: the caller only builds one for an adjusted track,
        // but a track adjusted to exactly nothing by rounding would otherwise emit "[2:a],asplit".
        return parts.Count == 0 ? "anull" : string.Join(",", parts);
    }

    /// <summary>
    /// Half the source raster, rounded to even in both axes — H.264 cannot encode an odd
    /// dimension, and a 1080-line source halves to 540 either way.
    /// </summary>
    public static (int Width, int Height) HalfRaster(CaptureFormat format) =>
        (Math.Max(2, format.Width / 2 / 2 * 2), Math.Max(2, format.Height / 2 / 2 * 2));
}
