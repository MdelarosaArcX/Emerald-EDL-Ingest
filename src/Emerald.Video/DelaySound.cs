using Emerald.Core;

namespace Emerald.Video;

/// <summary>
/// Where the preview's sound is read from: a recorded file, or the delay. The same calls for both,
/// so the sound runs on from one into the other.
/// </summary>
public interface ISoundSource
{
    /// <summary>
    /// <paramref name="count"/> samples of every channel of <paramref name="tracks"/> stereo tracks
    /// from <paramref name="sample"/> on, 16-bit interleaved. False when they are not there yet.
    /// </summary>
    bool Read(string path, int tracks, long sample, int count, byte[] into);

    /// <summary>Has the sound at a sample made ready. Returns at once.</summary>
    void Want(string path, int tracks, long sample);

    /// <summary>How far in the sound starts, in samples; null until that is known.</summary>
    long? Lead(string path, int tracks);

    /// <summary>How many samples there are; null while that is not known.</summary>
    long? Length(string path, int tracks);
}

/// <summary>
/// The sound of the segment still being recorded, out of the delay - the one place it is before the
/// recorder closes the file. Known by a name made from the segment's own path (<see cref="KeyOf"/>),
/// so when the file closes the preview carries on from the file at the same moment.
///
/// Holds the line only while it is being listened to: a line kept is tens of gigabytes kept, and
/// after a recording ends nothing else may be holding it.
/// </summary>
public sealed class DelaySound : ISoundSource, IDisposable
{
    public const string Prefix = "delay:";

    public static string KeyOf(string path) => Prefix + path;

    public static bool IsKey(string path) => path.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>The file a name is for, delay or not.</summary>
    public static string PathOf(string path) => IsKey(path) ? path[Prefix.Length..] : path;

    private readonly object _gate = new();
    private readonly Dictionary<string, long> _starts = new(StringComparer.Ordinal);
    private DelayLine? _line;
    private DelayFrameBuffer? _frame;

    /// <summary>
    /// Says that <paramref name="key"/> is read from <paramref name="line"/>, its first sample being
    /// the start of frame <paramref name="recordingStart"/> of the recording.
    /// </summary>
    public void Use(string key, IDelayLine line, long recordingStart)
    {
        DelayLine? dropped = null;

        lock (_gate)
        {
            if (!ReferenceEquals(line, _line))
            {
                dropped = _line;
                _line = null;
                _frame = null;
                _starts.Clear();

                if (line is DelayLine taken && taken.TryAddRef())
                {
                    _line = taken;
                    _frame = taken.NewBuffer();
                }
            }

            if (_line is not null)
            {
                if (_starts.Count > 8 && !_starts.ContainsKey(key)) _starts.Clear();
                _starts[key] = recordingStart;
            }
        }

        Let(dropped);
    }

    /// <summary>Not listened to any more: the line is let go.</summary>
    public void Drop()
    {
        DelayLine? dropped;

        lock (_gate)
        {
            dropped = _line;
            _line = null;
            _frame = null;
            _starts.Clear();
        }

        Let(dropped);
    }

    /// <summary>Off the caller's thread: the last release of a line closes and deletes its file.</summary>
    private static void Let(DelayLine? line)
    {
        if (line is null) return;

        _ = Task.Run(() =>
        {
            try { line.Release(); } catch { /* the sweep will get the file */ }
        });
    }

    public bool Read(string path, int tracks, long sample, int count, byte[] into)
    {
        int width = tracks * 4;
        Array.Clear(into, 0, count * width);

        lock (_gate)
        {
            if (_line is not { } line || _frame is not { } frame || !_starts.TryGetValue(path, out long start)) return true;

            int perFrame = SegmentSound.SampleRate / Math.Max(1, line.FrameRate);
            int done = 0;

            if (sample < 0)
            {
                done = (int)Math.Min(count, -sample);
                sample += done;
            }

            while (done < count)
            {
                long wanted = start + sample / perFrame;
                int into0 = (int)(sample % perFrame);
                int take = Math.Min(count - done, perFrame - into0);

                long seq = line.SequenceOf(wanted);

                if (seq < 0)
                {
                    // Not recorded yet: waited for. Older than the line reaches back: silence.
                    long newest = line.RecordingFrameAt(line.FramesWritten - 1);
                    if (newest < 0 || wanted > newest) return false;
                }
                else
                {
                    DelayReadResult read = line.TryReadAudio(seq, frame);
                    if (read == DelayReadResult.NotYetWritten) return false;
                    if (read == DelayReadResult.Ok) Copy(frame, into0, take, tracks, into, done);
                }

                done += take;
                sample += take;
            }
        }

        return true;
    }

    /// <summary>Samples of a frame into the preview's channels: as many of the frame's as it has room for.</summary>
    private static void Copy(DelayFrameBuffer frame, int from, int count, int tracks, byte[] into, int at)
    {
        int channels = Math.Max(2, frame.AudioChannels);
        int had = frame.AudioBytes / (channels * 2);
        int n = Math.Min(count, had - from);
        if (n <= 0) return;

        int wanted = tracks * 2;

        if (channels == wanted)
        {
            Buffer.BlockCopy(frame.Audio, from * channels * 2, into, at * wanted * 2, n * channels * 2);
            return;
        }

        int both = Math.Min(channels, wanted) * 2;

        for (int i = 0; i < n; i++)
            Buffer.BlockCopy(frame.Audio, (from + i) * channels * 2, into, (at + i) * wanted * 2, both);
    }

    /// <summary>
    /// The loudness of frames <paramref name="from"/> to <paramref name="to"/> of a recording, out of
    /// the line: the peak of each 1/<paramref name="peaksPerSecond"/> of a second of each stereo track, 0 to 1, into
    /// <paramref name="peaks"/>[track], where index 0 is the start of frame
    /// <paramref name="recordingStart"/>. For the waveform of the segment still being recorded.
    /// Returns the last frame read, or <paramref name="from"/> - 1 when none was there.
    /// </summary>
    public static long Peaks(DelayLine line, long recordingStart, long from, long to, float[][] peaks, int peaksPerSecond)
    {
        int rate = Math.Max(1, line.FrameRate);
        int perFrame = SegmentSound.SampleRate / rate;
        int perPeak = SegmentSound.SampleRate / Math.Max(1, peaksPerSecond);
        DelayFrameBuffer frame = line.NewBuffer();
        long last = from - 1;

        for (long f = from; f <= to; f++)
        {
            long seq = line.SequenceOf(recordingStart + f);
            if (seq < 0)
            {
                long newest = line.RecordingFrameAt(line.FramesWritten - 1);
                if (newest < 0 || recordingStart + f > newest) break;
                last = f;
                continue;
            }

            if (line.TryReadAudio(seq, frame) != DelayReadResult.Ok) { last = f; continue; }

            int channels = Math.Max(2, frame.AudioChannels);
            int samples = Math.Min(perFrame, frame.AudioBytes / (channels * 2));

            for (int i = 0; i < samples; i++)
            {
                long peak = (f * perFrame + i) / perPeak;

                for (int t = 0; t < peaks.Length && t * 2 + 1 < channels; t++)
                {
                    if (peak >= peaks[t].Length) continue;

                    int o = (i * channels + t * 2) * 2;
                    int l = Math.Abs((int)(short)(frame.Audio[o] | frame.Audio[o + 1] << 8));
                    int r = Math.Abs((int)(short)(frame.Audio[o + 2] | frame.Audio[o + 3] << 8));
                    float v = Math.Max(l, r) / 32768f;

                    if (v > peaks[t][peak]) peaks[t][peak] = v;
                }
            }

            last = f;
        }

        return last;
    }

    public void Want(string path, int tracks, long sample) { }

    public long? Lead(string path, int tracks) => 0;

    public long? Length(string path, int tracks) => null;

    public void Dispose() => Drop();
}

/// <summary>Files through <see cref="SegmentSound"/>, the segment still being recorded through <see cref="DelaySound"/>.</summary>
public sealed class PreviewSources : ISoundSource, IDisposable
{
    public PreviewSources(SegmentSound files, DelaySound delay)
    {
        Files = files;
        Delay = delay;
    }

    public SegmentSound Files { get; }
    public DelaySound Delay { get; }

    private ISoundSource For(string path) => DelaySound.IsKey(path) ? Delay : Files;

    public bool Read(string path, int tracks, long sample, int count, byte[] into) => For(path).Read(path, tracks, sample, count, into);

    public void Want(string path, int tracks, long sample) => For(path).Want(path, tracks, sample);

    public long? Lead(string path, int tracks) => For(path).Lead(path, tracks);

    public long? Length(string path, int tracks) => For(path).Length(path, tracks);

    public void Dispose()
    {
        Files.Dispose();
        Delay.Dispose();
    }
}
