using Emerald.Video;

namespace Emerald.Media;

/// <summary>
/// Makes the low-res copy of a piece of ingest media: what the Ingest list shows, the timeline
/// draws and the monitor plays. Air always plays the high-res beside it.
///
/// <b>Quarter size, on the video card.</b> 960x540 H.264. It is only ever a preview, and the
/// copy it replaced - full raster, libx264 on the processor, written after every export and
/// waited for - was most of the time an export took, competing for the processor with the live
/// recorder's own encode. Quarter the pixels is a quarter of the work, and NVENC or Quick Sync,
/// when the machine has one that really encodes (<see cref="GpuEncoder.Choose"/>), takes it off
/// the processor altogether.
///
/// Written under a dotted temporary name and renamed when whole, so the Ingest list - which
/// skips dotted names - never offers a copy still being written.
/// </summary>
public static class IngestProxy
{
    public const int Width = 960;
    public const int Height = 540;

    private static readonly object EncoderGate = new();
    private static string? _encoderFor;
    private static ProxyEncoder? _encoder;
    private static string _encoderAccount = "";

    /// <summary>How the last encoder was chosen, for the log.</summary>
    public static string EncoderAccount
    {
        get { lock (EncoderGate) return _encoderAccount; }
    }

    /// <summary>
    /// Writes <paramref name="lowPath"/> from <paramref name="source"/>. Runs ffmpeg; call it off
    /// the UI thread.
    /// </summary>
    public static FfmpegOutcome Make(string ffmpegPath, string source, string lowPath, int frameRate,
                                     Action<double>? fraction = null, CancellationToken cancel = default)
    {
        string folder = Path.GetDirectoryName(lowPath) ?? ".";
        string temp = Path.Combine(folder, $".{Path.GetFileNameWithoutExtension(lowPath)}.part.mp4");

        try
        {
            Directory.CreateDirectory(folder);

            MediaInfo? info = MediaProbe.Probe(MediaProbe.LocateFfprobe(ffmpegPath), source, frameRate);
            double seconds = (info?.Duration ?? MediaCodec.ReadDuration(source) ?? TimeSpan.Zero).TotalSeconds;
            bool video = info is null || info.Width > 0;

            var args = new List<string> { "-hide_banner", "-loglevel", "error", "-nostdin", "-y", "-i", source };

            if (video)
            {
                args.AddRange(new[] { "-map", "0:v:0", "-map", "0:a:0?" });

                // Even height, the source's own shape: a 4:3 or vertical source stays that shape.
                args.AddRange(new[] { "-vf", $"scale={Width}:{Height}:force_original_aspect_ratio=decrease:force_divisible_by=2,format=yuv420p" });

                if (EncoderFor(ffmpegPath, frameRate) is { } gpu)
                {
                    args.AddRange(gpu.Arguments(frameRate, 0));
                    args.AddRange(new[] { "-pix_fmt", gpu.PixelFormat });
                }
                else
                {
                    // Scrubbed and stepped through: no B-frames and a keyframe every half second, as
                    // on the capture proxy.
                    args.AddRange(new[]
                    {
                        "-c:v", "libx264", "-preset", "veryfast", "-crf", "23", "-profile:v", "high",
                        "-bf", "0", "-g", Math.Max(1, frameRate / 2).ToString(), "-sc_threshold", "0",
                    });
                }
            }
            else
            {
                args.AddRange(new[] { "-map", "0:a:0", "-vn" });
            }

            args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k", "-ac", "2", "-movflags", "+faststart", "-f", "mp4", temp });

            FfmpegOutcome outcome = FfmpegRun.Run(ffmpegPath, args, seconds, fraction, cancel);

            if (!outcome.Ok)
            {
                TryDelete(temp);
                return outcome;
            }

            File.Move(temp, lowPath, overwrite: true);
            return outcome;
        }
        catch (Exception ex)
        {
            TryDelete(temp);
            return new FfmpegOutcome(false, false, ex.Message);
        }
    }

    /// <summary>
    /// The card encoder to use, chosen once per ffmpeg: choosing runs a trial encode of up to a few
    /// seconds, and the answer does not change while Emerald runs. Null for the processor.
    /// </summary>
    private static ProxyEncoder? EncoderFor(string ffmpegPath, int frameRate)
    {
        lock (EncoderGate)
        {
            if (_encoderFor == ffmpegPath) return _encoder;

            try
            {
                _encoder = GpuEncoder.Choose(ffmpegPath, new CaptureFormat("ingest low-res", Width, Height, frameRate),
                                             0, out _encoderAccount);
            }
            catch (Exception ex)
            {
                _encoder = null;
                _encoderAccount = $"no card encoder ({ex.Message})";
            }

            _encoderFor = ffmpegPath;
            return _encoder;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
