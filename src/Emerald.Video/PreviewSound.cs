using System.Diagnostics;
using Emerald.Core;

namespace Emerald.Video;

/// <summary>Where the preview's picture is, and what is to be heard there.</summary>
/// <param name="StartFrame">The captured frame the file begins at, which the switches and levels are counted in.</param>
/// <param name="Position">How far into the file the picture is, in seconds, at <paramref name="At"/>.</param>
/// <param name="At">A <see cref="Stopwatch"/> timestamp.</param>
/// <param name="Volume">The window's own level, 0 to 1. Nought is silence.</param>
/// <param name="Next">The file that follows, once it is near, so its sound is in memory before it is reached.</param>
public sealed record PreviewMoment(
    string Path, int Tracks, long StartFrame, int Rate, double Position, long At,
    IReadOnlyList<AudioSwitch> Switches, IReadOnlyList<AudioEdit> Edits, double Volume,
    (string Path, int Tracks)? Next);

/// <summary>
/// Where the sound should be read from to be heard with the picture. The picture is the clock: the
/// sound card's own runs a few parts in a million faster or slower, and what a player says of its
/// position wanders by a frame either way, so the sound is left alone for that and moved only when it
/// is out by more than can go unnoticed.
///
/// Pure, so it can be tested without a sound card.
/// </summary>
public sealed class SoundCursor
{
    /// <summary>Out by this much on average, or by <see cref="JumpSeconds"/> at once, and it is moved.</summary>
    public const double DriftSeconds = 0.06;
    public const double JumpSeconds = 0.25;

    private const double Smoothing = 0.08;

    private double _average;

    /// <summary>The next sample to read, or null before the first.</summary>
    public long? Sample { get; private set; }

    /// <summary>
    /// Puts the cursor where <paramref name="wanted"/> seconds into the file is, if it is not near
    /// enough already. True when it was moved, and the sound is to be brought in again from silence.
    /// </summary>
    public bool Follow(double wanted)
    {
        if (Sample is { } at)
        {
            double error = wanted - at / (double)SegmentSound.SampleRate;
            _average += (error - _average) * Smoothing;

            if (Math.Abs(error) < JumpSeconds && Math.Abs(_average) < DriftSeconds) return false;
        }

        Sample = (long)Math.Round(wanted * SegmentSound.SampleRate);
        _average = 0;
        return true;
    }

    public void Advance(int samples) => Sample += samples;

    public void Forget()
    {
        Sample = null;
        _average = 0;
    }
}

/// <summary>
/// Plays the sound of Live Edit's preview for recordings of several tracks: the track chosen for each
/// frame, at the level set on it, exactly as the transmitter does it (<see cref="RecordingSound"/>).
///
/// The picture's player has no way to choose a track, and giving it a different file at every change
/// stopped the picture and the playhead each time. So the player is silent, its one file plays
/// straight through, and the sound is played here alongside it.
///
/// Its own thread, so a busy window does not break up the sound. It reads where the picture is from
/// what the window last said, and carries on from that between times.
/// </summary>
public sealed class PreviewSound : IDisposable
{
    /// <summary>Sound queued on the card. Heard that much later, which is allowed for, so it costs no lip sync.</summary>
    private const int QueueMs = 160;

    /// <summary>With nothing said for this long the window is stuck, and so is the picture.</summary>
    private const double StaleSeconds = 1.0;

    private readonly SegmentSound _source;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private PreviewMoment? _now;
    private volatile bool _stop;
    private long _moved, _waited;

    public PreviewSound(string ffmpeg)
    {
        _source = new SegmentSound(ffmpeg);

        _thread = new Thread(Run) { IsBackground = true, Name = "live edit preview sound", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Times the sound was put back in step, and times it was not in memory when wanted.</summary>
    public (long Moved, long Waited) Counts => (Interlocked.Read(ref _moved), Interlocked.Read(ref _waited));

    public long Bytes => _source.Bytes;

    /// <summary>Why nothing is heard, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Playing, and here. Called every tick.</summary>
    public void Play(PreviewMoment now)
    {
        bool waking = Volatile.Read(ref _now) is null;

        Volatile.Write(ref _now, now);
        if (waking) _wake.Set();
    }

    /// <summary>Not playing.</summary>
    public void Pause()
    {
        if (Interlocked.Exchange(ref _now, null) is not null) _wake.Set();
    }

    /// <summary>Has the sound here ready before it is played: where the playhead has been put.</summary>
    public void Prepare(string path, int tracks, double seconds) =>
        _source.Want(path, tracks, (long)(seconds * SegmentSound.SampleRate));

    private void Run()
    {
        SpeakerOutput? speaker = null;
        var sound = new RecordingSound();
        var cursor = new SoundCursor();
        string? path = null;
        int rate = 0;
        byte[] buffer = Array.Empty<byte>();

        try
        {
            while (!_stop)
            {
                PreviewMoment? now = Volatile.Read(ref _now);
                double age = now is null ? 0 : Stopwatch.GetElapsedTime(now.At).TotalSeconds;

                if (now is null || age > StaleSeconds)
                {
                    if (cursor.Sample is not null)
                    {
                        speaker?.Flush();
                        cursor.Forget();
                        sound.Silenced();
                    }

                    _wake.WaitOne(now is null ? 500 : 20);
                    continue;
                }

                if (speaker is null || rate != now.Rate)
                {
                    speaker?.Dispose();
                    rate = now.Rate;
                    speaker = SpeakerOutput.Open(rate, QueueMs);
                    Problem = speaker.Problem;
                }

                if (path != now.Path)
                {
                    path = now.Path;
                    cursor.Forget();
                }

                int samples = SegmentSound.SampleRate / now.Rate;
                int channels = now.Tracks * 2;
                if (buffer.Length < samples * channels * 2) buffer = new byte[samples * channels * 2];

                int queued = speaker.LatencyMs;

                // Full: the card plays a frame before there is room for another.
                if (queued + 1000 / now.Rate > QueueMs)
                {
                    _wake.WaitOne(8);
                    continue;
                }

                if (cursor.Follow(now.Position + age + queued / 1000.0))
                {
                    sound.Silenced();
                    Interlocked.Increment(ref _moved);
                }

                long at = cursor.Sample!.Value;

                if (!_source.Read(now.Path, now.Tracks, at, samples, buffer))
                {
                    // Being read. Found again from the picture when it is there.
                    Interlocked.Increment(ref _waited);
                    cursor.Forget();
                    sound.Silenced();
                    _wake.WaitOne(10);
                    continue;
                }

                long frame = now.StartFrame + Math.Max(0, at + samples / 2) / samples;
                int track = AudioSwitches.TrackAt(frame, now.Switches);

                double level = AudioEdits.GainDbAt(frame, now.Edits) +
                               (now.Volume <= 0.0001 ? double.NegativeInfinity : 20 * Math.Log10(now.Volume));

                if (sound.Frame(buffer, samples * channels * 2, channels, track, level) is { } pair)
                    speaker.Push(pair[0], pair[1], samples);

                cursor.Advance(samples);

                if (now.Next is { } next) _source.Want(next.Path, next.Tracks, 0);
            }
        }
        finally
        {
            speaker?.Dispose();
        }
    }

    public void Dispose()
    {
        _stop = true;
        _wake.Set();
        if (_thread.IsAlive) _thread.Join(TimeSpan.FromSeconds(2));
        _source.Dispose();
    }
}
