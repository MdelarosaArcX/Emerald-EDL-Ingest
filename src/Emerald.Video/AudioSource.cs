using System.Diagnostics;
using System.IO;

namespace Emerald.Video;

/// <summary>
/// Decodes one media file's audio into a rolling PCM buffer, and serves any frame of it at
/// an offset that can change at any moment.
///
/// Audio is decoded in its **own ffmpeg process**, entirely independent of the video
/// decode, and always from the natural start of the file — the offset is *never* passed to
/// ffmpeg. Instead a decoder thread keeps a few seconds of audio buffered around the
/// current position, and the offset simply moves where the playout loop reads from. That is
/// what makes the offset adjustable live: nudging it re-points a read index, so nothing is
/// restarted, no process is respawned, and the video pipeline is not touched at all.
///
/// The buffer holds history and lookahead, so audio can be pushed later than picture
/// (positive offset, reading back in time) or pulled earlier (negative offset, reading
/// ahead) without either direction needing a re-seek.
/// </summary>
public sealed class AudioSource : IDisposable
{
    public const int SampleRate = 48000;
    private const int Channels = 2;
    private const int BytesPerSample = 2;

    /// <summary>Seconds of audio kept buffered. Must comfortably exceed the offset limit.</summary>
    private const int BufferSeconds = 3;

    private readonly Process? _ffmpeg;
    private readonly Stream? _output;
    private readonly Thread? _decoder;
    private readonly CancellationTokenSource _cts = new();

    private readonly short[] _left;
    private readonly short[] _right;
    private readonly int _capacity;
    private readonly object _gate = new();

    /// <summary>Absolute count of samples decoded so far; the buffer holds the last _capacity of them.</summary>
    private long _written;
    private bool _endOfStream;

    /// <summary>Where playout currently is, so the decoder knows how far it may run ahead.</summary>
    private long _playPosition;

    /// <summary>End of the last range actually read, which is offset-adjusted. See Exhausted.</summary>
    private long _lastReadEnd;

    public int SamplesPerFrame { get; }

    private AudioSource(Process? ffmpeg, int samplesPerFrame)
    {
        _ffmpeg = ffmpeg;
        _output = ffmpeg?.StandardOutput.BaseStream;
        SamplesPerFrame = samplesPerFrame;

        _capacity = SampleRate * BufferSeconds;
        _left = new short[_capacity];
        _right = new short[_capacity];

        if (ffmpeg is null)
        {
            _endOfStream = true;
            return;
        }

        _decoder = new Thread(DecodeLoop) { IsBackground = true, Name = "audio decode" };
        _decoder.Start();
    }

    public static AudioSource Silent(int frameRate) => new(null, SampleRate / frameRate);

    public static AudioSource Open(string ffmpegPath, string mediaPath, int frameRate)
    {
        // Every rate the app outputs divides 48000 exactly, so a frame is a whole number of
        // samples and audio cannot drift against picture.
        int samplesPerFrame = SampleRate / frameRate;

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
        info.ArgumentList.Add("-i"); info.ArgumentList.Add(mediaPath);
        info.ArgumentList.Add("-vn");
        info.ArgumentList.Add("-ar"); info.ArgumentList.Add(SampleRate.ToString());
        info.ArgumentList.Add("-ac"); info.ArgumentList.Add(Channels.ToString());
        info.ArgumentList.Add("-f"); info.ArgumentList.Add("s16le");
        info.ArgumentList.Add("pipe:1");

        Process? ffmpeg;
        try { ffmpeg = Process.Start(info); }
        catch { ffmpeg = null; }

        if (ffmpeg is null) return Silent(frameRate);

        ffmpeg.ErrorDataReceived += (_, _) => { };
        ffmpeg.BeginErrorReadLine();

        return new AudioSource(ffmpeg, samplesPerFrame);
    }

    // ------------------------------------------------------------------ decoding

    private void DecodeLoop()
    {
        var chunk = new byte[SamplesPerFrame * Channels * BytesPerSample];

        while (!_cts.IsCancellationRequested)
        {
            // Stay ahead of playout, but not so far that history is lost — both directions
            // of offset are served out of this window.
            long ahead;
            lock (_gate) ahead = _written - _playPosition;

            if (ahead > _capacity / 2)
            {
                Thread.Sleep(3);
                continue;
            }

            int filled = 0;
            while (filled < chunk.Length)
            {
                int read;
                try { read = _output!.Read(chunk, filled, chunk.Length - filled); }
                catch { read = 0; }

                if (read <= 0) break;
                filled += read;
            }

            if (filled == 0)
            {
                lock (_gate) _endOfStream = true;
                return;
            }

            Append(chunk, filled);
        }
    }

    private void Append(byte[] interleaved, int byteCount)
    {
        int samples = byteCount / (Channels * BytesPerSample);

        lock (_gate)
        {
            for (int i = 0; i < samples; i++)
            {
                int slot = (int)((_written + i) % _capacity);
                int o = i * 4;
                _left[slot] = (short)(interleaved[o] | (interleaved[o + 1] << 8));
                _right[slot] = (short)(interleaved[o + 2] | (interleaved[o + 3] << 8));
            }

            _written += samples;
        }
    }

    // ------------------------------------------------------------------ playout

    /// <summary>
    /// Fills one frame of audio for the video frame sitting at <paramref name="framePosition"/>
    /// samples from the start, shifted by <paramref name="offsetMs"/>. Positive delays audio
    /// (reads back in time), negative advances it (reads ahead). Anything not yet decoded, or
    /// before the start of the file, comes out as silence rather than stalling picture.
    /// </summary>
    public void ReadFrame(short[] left, short[] right, long framePosition, int offsetMs)
    {
        Array.Clear(left);
        Array.Clear(right);

        long offsetSamples = (long)offsetMs * SampleRate / 1000;
        long want = framePosition - offsetSamples;

        lock (_gate)
        {
            _playPosition = framePosition;
            _lastReadEnd = want + left.Length;

            long oldest = Math.Max(0, _written - _capacity);

            for (int i = 0; i < left.Length; i++)
            {
                long at = want + i;
                if (at < oldest || at >= _written) continue;   // silence

                int slot = (int)(at % _capacity);
                left[i] = _left[slot];
                right[i] = _right[slot];
            }
        }
    }

    /// <summary>
    /// True once the decoder has run out and everything decoded has actually been read.
    ///
    /// This keys off where the last read really reached, not off <c>_playPosition</c>: with a
    /// positive offset the read point lags the frame position, so measuring against the frame
    /// position would call the source exhausted while up to the offset's worth of tail is still
    /// unplayed — clipping it at every loop point.
    /// </summary>
    public bool Exhausted
    {
        get { lock (_gate) return _endOfStream && _lastReadEnd >= _written; }
    }

    public void Dispose()
    {
        _cts.Cancel();

        try
        {
            if (_ffmpeg is { HasExited: false })
            {
                _ffmpeg.Kill(entireProcessTree: true);
                _ffmpeg.WaitForExit(2000);
            }
        }
        catch
        {
            // Already gone.
        }

        _decoder?.Join(TimeSpan.FromSeconds(2));
        _ffmpeg?.Dispose();
        _cts.Dispose();
    }
}
