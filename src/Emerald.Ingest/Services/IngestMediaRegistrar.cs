using System.IO;
using Emerald.Core;
using Emerald.Media;
using Emerald.Video;

namespace Emerald.Ingest;

/// <summary>
/// Turns a finished recording into a verified, registered media asset.
///
/// The recorder says what it thinks it wrote. This reads the files back off the disk and
/// says what is actually there — which is the only account worth keeping, and the reason
/// verification is a separate step rather than a flag the encoder sets on its way out.
/// </summary>
public interface IIngestMediaRegistrar
{
    /// <param name="cancelled">
    /// True when the operator stopped the recording. A short clip is then expected, and the
    /// registrar must not report it as a failure on top of the cancellation.
    /// </param>
    IngestRecording Register(IngestJob job, IngestRecorderResult result, Timecode startTimecode, bool cancelled);
}

/// <summary>
/// Verification through <see cref="MediaLibrary"/> and <see cref="MediaProbe"/>, which is
/// how the rest of Emerald reads a clip.
///
/// Nothing is copied or moved. The clip is written into the operator's directory, which is
/// laid out the way the media store expects — masters under <c>high</c>, proxies under
/// <c>low</c> — so registering it is a matter of reading it back and writing down what it
/// is, not of importing it anywhere.
/// </summary>
public sealed class MediaLibraryRegistrar : IIngestMediaRegistrar
{
    private readonly AppSettings _settings;
    private readonly IIngestLog _log;

    public MediaLibraryRegistrar(AppSettings settings, IIngestLog log)
    {
        _settings = settings;
        _log = log;
    }

    public IngestRecording Register(IngestJob job, IngestRecorderResult result, Timecode startTimecode, bool cancelled)
    {
        int rate = job.FrameRate > 0 ? job.FrameRate : 25;

        var recording = new IngestRecording
        {
            IngestJobId = job.Id,
            ActualStartTimecode = startTimecode.ToString(),
            ActualEndTimecode = startTimecode.AddWrapping(result.Frames).ToString(),
            FilePath = result.MasterPath ?? "",
            ProxyPath = result.ProxyPath,
            FrameRate = rate,
            Frames = result.Frames,
            StartedAt = job.StartedAt ?? DateTime.Now,
            CompletedAt = DateTime.Now,
            Mock = job.Mock,
        };

        if (result.Error is { } encoderError)
        {
            recording.Status = IngestStatus.Failed;
            recording.ErrorMessage = encoderError;
        }

        string? ffprobe = MediaProbe.LocateFfprobe(Ffmpeg.Locate(_settings.FfmpegPath));

        CapturedClip? master = result.MasterPath is null
            ? null
            : MediaLibrary.Describe(result.MasterPath, ffprobe, rate);

        CapturedClip? proxy = result.ProxyPath is null
            ? null
            : MediaLibrary.Describe(result.ProxyPath, ffprobe, rate);

        recording.FileSize = master?.Bytes ?? 0;
        recording.ProxyFileSize = proxy?.Bytes ?? 0;

        if (master?.Info is { } info)
        {
            recording.Codec = info.VideoCodec;
            recording.Resolution = info.Width > 0 ? $"{info.Width}x{info.Height}" : "";
        }
        else if (job.Mock)
        {
            recording.Codec = "simulated";
        }

        // Now the questions worth refusing over, in the order they matter.
        string? problem =
            master is null ? $"No file was produced at {result.MasterPath}."
            : master.Bytes == 0 ? $"{Path.GetFileName(master.Path)} is empty."
            : result.Error is not null ? result.Error
            : !result.RanToLength && !cancelled
                ? $"The recording stopped at {new Timecode(result.Frames, rate)} of {job.RecordedLength}."
                : null;

        if (cancelled)
        {
            recording.Status = IngestStatus.Cancelled;
            recording.ErrorMessage ??= "Cancelled by the operator.";
        }
        else if (problem is not null)
        {
            recording.Status = IngestStatus.Failed;
            recording.ErrorMessage = problem;
        }
        else
        {
            recording.Status = IngestStatus.Completed;
            recording.ErrorMessage = null;
        }

        if (master is not null)
        {
            _log.Write($"    verified  {master.Name}  {master.SizeText}  " +
                       $"{(master.Info is null ? "not probed" : master.FormatText)}");
        }

        if (job.Metadata.Trim().Length > 0)
            _log.Write($"    metadata  {job.Metadata.Trim().Replace(Environment.NewLine, " | ")}");

        return recording;
    }
}
