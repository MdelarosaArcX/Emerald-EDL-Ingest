using Emerald.Deltacast;
using System.Diagnostics;
using System.IO;

namespace Emerald.Video;

/// <summary>
/// Decodes one media file into raw UYVY frames by piping it through ffmpeg. VideoMaster
/// takes uncompressed frames only and does no decoding of its own, so something has to
/// turn MXF/MOV/MP4 into pixels; ffmpeg does that and scales/pads/rate-converts to the
/// SDI format in the same pass, which is why any source plays on any board.
/// </summary>
public sealed class FrameSource : IDisposable
{
    private const int MaxDiagnosticLines = 12;

    private readonly Process _ffmpeg;
    private readonly Stream _output;
    private readonly int _frameBytes;
    private readonly Queue<string> _diagnostics = new();

    public string Path { get; }
    public long FramesRead { get; private set; }

    /// <summary>
    /// Whatever ffmpeg complained about, if anything. Without this a failed decode is
    /// indistinguishable from a short file — the stream simply ends — and the operator is
    /// told only that no frames arrived.
    /// </summary>
    public string? Diagnostics
    {
        get
        {
            lock (_diagnostics)
                return _diagnostics.Count == 0 ? null : string.Join(" | ", _diagnostics);
        }
    }

    private FrameSource(Process ffmpeg, string path, int frameBytes)
    {
        _ffmpeg = ffmpeg;
        _output = ffmpeg.StandardOutput.BaseStream;
        _frameBytes = frameBytes;
        Path = path;
    }

    private void Note(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;

        lock (_diagnostics)
        {
            _diagnostics.Enqueue(line.Trim());
            while (_diagnostics.Count > MaxDiagnosticLines) _diagnostics.Dequeue();
        }
    }

    /// <param name="seek">
    /// In-point within the media (the SOM). Placed before -i so ffmpeg seeks by keyframe
    /// and then decodes forward, which is fast and frame-accurate for playout.
    /// </param>
    public static FrameSource Open(string ffmpegPath, string mediaPath, VideoFormat format, TimeSpan? seek = null)
    {
        // scale keeps the source aspect, pad centres it in the raster, fps conforms the
        // rate. Audio is dropped: this is a video playout path.
        string filter =
            $"scale={format.Width}:{format.Height}:force_original_aspect_ratio=decrease," +
            $"pad={format.Width}:{format.Height}:(ow-iw)/2:(oh-ih)/2:color=black," +
            $"fps={format.FrameRate}";

        var info = new ProcessStartInfo(ffmpegPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        info.ArgumentList.Add("-hide_banner");
        info.ArgumentList.Add("-loglevel"); info.ArgumentList.Add("error");
        info.ArgumentList.Add("-nostdin");

        if (seek is { TotalSeconds: > 0 } som)
        {
            info.ArgumentList.Add("-accurate_seek");
            info.ArgumentList.Add("-ss");
            info.ArgumentList.Add(som.TotalSeconds.ToString("F3", System.Globalization.CultureInfo.InvariantCulture));
        }

        info.ArgumentList.Add("-i"); info.ArgumentList.Add(mediaPath);
        info.ArgumentList.Add("-an");
        info.ArgumentList.Add("-vf"); info.ArgumentList.Add(filter);
        info.ArgumentList.Add("-pix_fmt"); info.ArgumentList.Add("uyvy422");
        info.ArgumentList.Add("-f"); info.ArgumentList.Add("rawvideo");
        info.ArgumentList.Add("pipe:1");

        Process ffmpeg = Process.Start(info)
            ?? throw new InvalidOperationException($"Could not start ffmpeg at {ffmpegPath}.");

        var source = new FrameSource(ffmpeg, mediaPath, format.FrameBytes);

        // stderr must be drained or a chatty decode fills the pipe and wedges the process —
        // but it is kept, not discarded, so a failure can be reported.
        ffmpeg.ErrorDataReceived += (_, e) => source.Note(e.Data);
        ffmpeg.BeginErrorReadLine();

        return source;
    }

    /// <summary>
    /// Fills <paramref name="frame"/> with the next decoded frame. Returns false at end of
    /// file, including a trailing partial frame, which is discarded.
    /// </summary>
    public bool TryReadFrame(byte[] frame)
    {
        int filled = 0;

        while (filled < _frameBytes)
        {
            int read = _output.Read(frame, filled, _frameBytes - filled);
            if (read <= 0) return false;
            filled += read;
        }

        FramesRead++;
        return true;
    }

    public void Dispose()
    {
        try
        {
            if (!_ffmpeg.HasExited)
            {
                _ffmpeg.Kill(entireProcessTree: true);
                _ffmpeg.WaitForExit(2000);
            }
        }
        catch
        {
            // Process already gone; nothing to clean up.
        }

        _ffmpeg.Dispose();
    }
}
