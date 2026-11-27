using System.Diagnostics;
using Emerald.Core;

namespace Emerald.Video;

/// <summary>Where the preview's picture is, and what is to be heard there.</summary>
/// <param name="StartFrame">The captured frame the file begins at, which the levels are counted in.</param>
/// <param name="Track">The pair the editor is listening to. Only heard here: it does not go to air.</param>
/// <param name="Position">How far into the file the picture is, in seconds, at <paramref name="At"/>.</param>
/// <param name="At">A <see cref="Stopwatch"/> timestamp.</param>
/// <param name="Volume">The window's own level, 0 to 1. Nought is silence.</param>
/// <param name="Frames">How many pictures the file has.</param>
/// <param name="Next">
/// The file that plays straight on from this one, once it is near. Its sound follows this one's with
/// nothing between, whichever moment the picture changes over.
/// </param>
/// <param name="Held">For the segment still being recorded, played from the delay: the line and where in it.</param>
public sealed record PreviewMoment(
    string Path, int Tracks, long StartFrame, long Frames, int Rate, double Position, long At,
    int Track, IReadOnlyList<AudioEdit> Edits, double Volume,
    PreviewNext? Next, PreviewHeld? Held = null);

/// <summary>The segment after the one playing, and the captured frame it begins at.</summary>
public sealed record PreviewNext(string Path, int Tracks, long StartFrame, PreviewHeld? Held = null);

/// <summary>A segment heard from the delay: the line, and the frame of the recording the segment starts at.</summary>
public sealed record PreviewHeld(IDelayLine Line, long RecordingStart);

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

    /// <summary>Counted from another file's start, the sound itself running on: nothing has moved.</summary>
    public void Rebase(long sample) => Sample = sample;

    public void Forget()
    {
        Sample = null;
        _average = 0;
    }
}

/// <summary>
/// Frames of sound out of one recorded file after another. At the end of a file the next one's sound
/// follows its last sample in the same frame, so nothing is heard at a join - the picture may change
/// over a moment before or after, and the sound does not wait for it.
///
/// Without a sound card, so it can be tested.
/// </summary>
public sealed class SoundFeed
{
    /// <summary>A length further out than this from the picture's is not believed.</summary>
    private const long LengthSlack = SegmentSound.SampleRate / 4;

    private readonly ISoundSource _source;
    private readonly SoundCursor _cursor = new();
    private byte[] _rest = Array.Empty<byte>();
    private string? _path;
    private int _tracks;
    private long _start;

    public SoundFeed(ISoundSource source) => _source = source;

    /// <summary>Whether anything has been given since <see cref="Forget"/>.</summary>
    public bool Started => _cursor.Sample is not null;

    /// <summary>The samples of the last frame given.</summary>
    public byte[] Buffer { get; private set; } = Array.Empty<byte>();

    public int Channels => _tracks * 2;

    /// <summary>The captured frame the last frame given is on.</summary>
    public long Frame { get; private set; }

    /// <summary>The last frame given does not follow the one before.</summary>
    public bool Moved { get; private set; }

    public void Forget() => _cursor.Forget();

    /// <summary>
    /// The next frame of sound, to be heard <paramref name="ahead"/> seconds after
    /// <paramref name="now"/> was said. False while it is being read.
    /// </summary>
    public bool Take(PreviewMoment now, double ahead, int samples)
    {
        double wanted = now.StartFrame / (double)now.Rate + now.Position + ahead;

        if (_path != now.Path && _path != now.Next?.Path)
        {
            // The segment that was being recorded, now closed: the same sound from the file, on
            // the same count, where the delay left off.
            if (_path is not null && DelaySound.PathOf(_path) == DelaySound.PathOf(now.Path) && _start == now.StartFrame)
                (_path, _tracks) = (now.Path, now.Tracks);
            else
                _cursor.Forget();
        }

        Moved = !Started || _cursor.Follow(wanted - _start / (double)now.Rate);

        if (Moved)
        {
            (_path, _tracks, _start) = now.Next is { } over && wanted >= over.StartFrame / (double)now.Rate
                ? (over.Path, over.Tracks, over.StartFrame)
                : (now.Path, now.Tracks, now.StartFrame);

            _cursor.Forget();
            _cursor.Follow(wanted - _start / (double)now.Rate);
        }

        int bytes = samples * _tracks * 4;
        if (Buffer.Length < bytes) Buffer = new byte[bytes];

        long at = _cursor.Sample!.Value;
        Frame = _start + Math.Max(0, at + samples / 2) / samples;

        if (now.Next is { } next && _path == now.Path && next.Path != now.Path)
        {
            long length = LengthOf(now, samples);

            if (at + samples > length)
            {
                if (_source.Lead(next.Path, next.Tracks) is not { } lead)
                {
                    _source.Want(next.Path, next.Tracks, 0);
                    return false;
                }

                // Of as many tracks, the frame is the end of one and the start of the other. Of
                // another number it is a different recording, and changes over between two frames.
                int head = (int)Math.Clamp(length - at, 0, samples);

                if (next.Tracks == _tracks || head == 0)
                {
                    int rest = samples - head;
                    if (_rest.Length < samples * next.Tracks * 4) _rest = new byte[samples * next.Tracks * 4];

                    if (head > 0 && !_source.Read(now.Path, _tracks, at, head, Buffer)) return false;
                    if (!_source.Read(next.Path, next.Tracks, lead, rest, _rest)) return false;

                    (_path, _tracks, _start) = (next.Path, next.Tracks, next.StartFrame);
                    if (Buffer.Length < samples * _tracks * 4) Buffer = new byte[samples * _tracks * 4];

                    System.Buffer.BlockCopy(_rest, 0, Buffer, head * _tracks * 4, rest * _tracks * 4);

                    _cursor.Rebase(lead + rest);
                    return true;
                }
            }
        }

        if (!_source.Read(_path!, _tracks, at, samples, Buffer)) return false;

        _cursor.Advance(samples);
        if (now.Next is { } soon) _source.Want(soon.Path, soon.Tracks, 0);

        return true;
    }

    /// <summary>
    /// Where a file's sound ends: where it really does, which is some milliseconds past its last
    /// picture - the next file's starts that much in. By the pictures when that is not known.
    /// </summary>
    private long LengthOf(PreviewMoment now, int samples)
    {
        long pictures = now.Frames * samples;

        return _source.Length(now.Path, now.Tracks) is { } length && Math.Abs(length - pictures) < LengthSlack
            ? length
            : pictures;
    }
}

/// <summary>
/// Plays the sound of Live Edit's preview for recordings of several tracks: the pair the editor is
/// listening to, at the level set on each frame (<see cref="RecordingSound"/>). For the editor only -
/// air sends every pair and the hardware chooses.
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

    private readonly PreviewSources _source;
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _thread;
    private PreviewMoment? _now;
    private volatile bool _stop;
    private long _moved, _waited;

    public PreviewSound(string ffmpeg)
    {
        _source = new PreviewSources(new SegmentSound(ffmpeg), new DelaySound());

        _thread = new Thread(Run) { IsBackground = true, Name = "live edit preview sound", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>Times the sound was put back in step, and times it was not in memory when wanted.</summary>
    public (long Moved, long Waited) Counts => (Interlocked.Read(ref _moved), Interlocked.Read(ref _waited));

    public long Bytes => _source.Files.Bytes;

    /// <summary>Why nothing is heard, or null.</summary>
    public string? Problem { get; private set; }

    /// <summary>Playing, and here. Called every tick.</summary>
    public void Play(PreviewMoment now)
    {
        // The delay, held only while some of what is heard comes out of it.
        if (now.Held is { } held) _source.Delay.Use(now.Path, held.Line, held.RecordingStart);
        if (now.Next is { Held: { } after } next) _source.Delay.Use(next.Path, after.Line, after.RecordingStart);
        if (now.Held is null && now.Next?.Held is null) _source.Delay.Drop();

        bool waking = Volatile.Read(ref _now) is null;

        Volatile.Write(ref _now, now);
        if (waking) _wake.Set();
    }

    /// <summary>Not playing.</summary>
    public void Pause()
    {
        if (Interlocked.Exchange(ref _now, null) is not null) _wake.Set();
        _source.Delay.Drop();
    }

    /// <summary>Has the sound here ready before it is played: where the playhead has been put.</summary>
    public void Prepare(string path, int tracks, double seconds) =>
        _source.Want(path, tracks, (long)(seconds * SegmentSound.SampleRate));

    private void Run()
    {
        SpeakerOutput? speaker = null;
        var sound = new RecordingSound();
        var feed = new SoundFeed(_source);
        int rate = 0;

        try
        {
            while (!_stop)
            {
                PreviewMoment? now = Volatile.Read(ref _now);
                double age = now is null ? 0 : Stopwatch.GetElapsedTime(now.At).TotalSeconds;

                if (now is null || age > StaleSeconds)
                {
                    if (feed.Started)
                    {
                        speaker?.Flush();
                        feed.Forget();
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

                int samples = SegmentSound.SampleRate / now.Rate;
                int queued = speaker.LatencyMs;

                // Full: the card plays a frame before there is room for another.
                if (queued + 1000 / now.Rate > QueueMs)
                {
                    _wake.WaitOne(8);
                    continue;
                }

                if (!feed.Take(now, age + queued / 1000.0, samples))
                {
                    // Being read. Found again from the picture when it is there.
                    Interlocked.Increment(ref _waited);
                    feed.Forget();
                    sound.Silenced();
                    _wake.WaitOne(10);
                    continue;
                }

                if (feed.Moved)
                {
                    sound.Silenced();
                    Interlocked.Increment(ref _moved);
                }

                int track = now.Track;

                double level = AudioEdits.GainDbAt(feed.Frame, now.Edits) +
                               (now.Volume <= 0.0001 ? double.NegativeInfinity : 20 * Math.Log10(now.Volume));

                if (sound.Frame(feed.Buffer, samples * feed.Channels * 2, feed.Channels, track, level) is { } pair)
                    speaker.Push(pair[0], pair[1], samples);
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
