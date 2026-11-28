namespace Emerald.Video;

/// <summary>What a pulled frame is: new from the stream, the last one again, or black.</summary>
public enum RegulatedKind { Fresh, Repeated, Black }

/// <summary>
/// One frame for the clock: its picture, its sound (null for silence), and what it is. The
/// picture is the regulator's, valid until the next <see cref="FrameRegulator.Pull"/>.
/// </summary>
public readonly record struct RegulatedFrame(byte[] Video, byte[]? Audio, RegulatedKind Kind);

/// <summary>
/// Turns frames that arrive from a network - in bursts, late, or not at all - into exactly one
/// frame per tick of a steady clock, which is what the recorder, the delay line and the
/// transmitter are built on.
///
/// A small store is filled before anything is taken from it (<c>prebuffer</c> frames), so the
/// ordinary unevenness of a network never shows. When it runs dry, the last picture is shown
/// again with silence; a single frame short is one repeated frame, and only a stream that stays
/// away fills the store again before going on. A stream away for longer than <c>holdFrames</c>
/// shows black: a frozen picture would look like a live one. When frames pile up past
/// <c>maxQueue</c>, the oldest are dropped so the delay behind the stream does not grow.
///
/// Sound travels apart from the picture (two pipes from one ffmpeg) and each is numbered from the
/// start of the connection; a frame takes the sound with its own number, so they stay together
/// however the two arrive, and a dropped frame takes its sound with it. A new connection
/// (<see cref="Reset"/>) throws away whatever is left of the old one.
///
/// Picture buffers are pinned and used round and round, so a stream does not allocate four
/// megabytes a frame.
/// </summary>
public sealed class FrameRegulator
{
    private readonly object _gate = new();

    private readonly int _frameBytes;
    private readonly int _prebuffer;
    private readonly int _maxQueue;
    private readonly int _holdFrames;

    private readonly Queue<(byte[] Video, long Seq)> _video = new();
    private readonly Queue<(byte[] Audio, long Seq)> _audio = new();
    private readonly Stack<byte[]> _free = new();
    private readonly byte[] _black;

    private byte[]? _last;
    private int _generation;
    private bool _filling = true;
    private int _shortRun;
    private int _heldRun;

    private long _fresh, _repeated, _blacks, _dropped, _underruns;

    /// <summary>The newest frame and the newest sound pushed on this connection; -1 for none yet.</summary>
    private long _videoTail = -1, _audioTail = -1;

    /// <summary>A frame short this many ticks running is the stream gone, not just late: fill the store again.</summary>
    private const int ShortBeforeRefill = 3;

    public FrameRegulator(int frameBytes, int prebuffer, int maxQueue, int holdFrames)
    {
        _frameBytes = frameBytes;
        _prebuffer = Math.Max(1, prebuffer);
        _maxQueue = Math.Max(_prebuffer + 1, maxQueue);
        _holdFrames = Math.Max(0, holdFrames);

        _black = GC.AllocateArray<byte>(frameBytes, pinned: true);
        Uyvy.Black(_black);
    }

    public long Fresh => Interlocked.Read(ref _fresh);
    public long Repeated => Interlocked.Read(ref _repeated);
    public long Blacks => Interlocked.Read(ref _blacks);
    public long Dropped => Interlocked.Read(ref _dropped);
    public long Underruns => Interlocked.Read(ref _underruns);

    /// <summary>Frames waiting.</summary>
    public int Queued { get { lock (_gate) return _video.Count; } }

    /// <summary>A pinned buffer for the next frame off the stream, to fill and push.</summary>
    public byte[] Rent()
    {
        lock (_gate)
            return _free.Count > 0 ? _free.Pop() : GC.AllocateArray<byte>(_frameBytes, pinned: true);
    }

    /// <summary>A rented buffer that was not pushed after all.</summary>
    public void Return(byte[] buffer)
    {
        if (buffer.Length != _frameBytes) return;
        lock (_gate) _free.Push(buffer);
    }

    /// <summary>
    /// A new connection: everything queued from the last is thrown away, the store is filled again
    /// before frames are taken, and numbering starts again. Returns the connection's number, which
    /// every push carries so a straggler from the old one is not taken for the new.
    /// </summary>
    public int Reset()
    {
        lock (_gate)
        {
            while (_video.Count > 0) _free.Push(_video.Dequeue().Video);
            _audio.Clear();
            _videoTail = _audioTail = -1;

            _filling = true;
            _shortRun = 0;
            return ++_generation;
        }
    }

    public void PushVideo(int generation, long seq, byte[] frame)
    {
        lock (_gate)
        {
            if (generation != _generation) { _free.Push(frame); return; }
            _video.Enqueue((frame, seq));
            _videoTail = Math.Max(_videoTail, seq);
        }
    }

    public void PushAudio(int generation, long seq, byte[] chunk)
    {
        lock (_gate)
        {
            if (generation != _generation) return;

            _audio.Enqueue((chunk, seq));
            _audioTail = Math.Max(_audioTail, seq);
            while (_audio.Count > _maxQueue * 2) _audio.Dequeue();
        }
    }

    /// <summary>
    /// Frames ready to go: those whose sound is in hand as well as their picture. The sound comes a
    /// few frames behind the picture (ffmpeg writes it in blocks), and a frame taken before its sound
    /// arrived would go out silent and throw that sound away when it came - so a frame waits for it.
    /// A stream with no sound, or whose sound has fallen further behind than the store holds (it has
    /// stopped), goes on by its picture alone.
    /// </summary>
    private int Ready()
    {
        if (_video.Count == 0 || _audioTail < 0 || _videoTail - _audioTail > _maxQueue) return _video.Count;

        long head = _video.Peek().Seq;
        return (int)Math.Clamp(_audioTail - head + 1, 0, _video.Count);
    }

    /// <summary>The frame for this tick. Never waits.</summary>
    public RegulatedFrame Pull()
    {
        lock (_gate)
        {
            int ready = Ready();

            if (_filling && ready >= _prebuffer) _filling = false;

            if (_filling) return Hold();

            if (ready == 0)
            {
                // One frame short is the sender's clock a little slower than ours: show the last
                // again and go on. Short for longer, it has gone - fill the store before going on.
                if (++_shortRun >= ShortBeforeRefill)
                {
                    _filling = true;
                    Interlocked.Increment(ref _underruns);
                }

                return Hold();
            }

            _shortRun = 0;

            while (_video.Count > _maxQueue)
            {
                _free.Push(_video.Dequeue().Video);
                Interlocked.Increment(ref _dropped);
            }

            (byte[] video, long seq) = _video.Dequeue();

            byte[]? audio = null;
            while (_audio.Count > 0 && _audio.Peek().Seq < seq) _audio.Dequeue();
            if (_audio.Count > 0 && _audio.Peek().Seq == seq) audio = _audio.Dequeue().Audio;

            if (_last is not null) _free.Push(_last);
            _last = video;
            _heldRun = 0;

            Interlocked.Increment(ref _fresh);
            return new RegulatedFrame(video, audio, RegulatedKind.Fresh);
        }
    }

    private RegulatedFrame Hold()
    {
        if (_last is not null && _heldRun < _holdFrames)
        {
            _heldRun++;
            Interlocked.Increment(ref _repeated);
            return new RegulatedFrame(_last, null, RegulatedKind.Repeated);
        }

        Interlocked.Increment(ref _blacks);
        return new RegulatedFrame(_black, null, RegulatedKind.Black);
    }
}

/// <summary>8-bit 4:2:2 UYVY, as the card carries it.</summary>
public static class Uyvy
{
    /// <summary>Legal black: chroma 128, luma 16.</summary>
    public static void Black(byte[] frame)
    {
        for (int i = 0; i + 3 < frame.Length; i += 4)
        {
            frame[i] = 0x80;
            frame[i + 1] = 0x10;
            frame[i + 2] = 0x80;
            frame[i + 3] = 0x10;
        }
    }
}
