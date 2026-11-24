using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Emerald.Core;

namespace Emerald.Video;

/// <summary>
/// Where a delay held in RAM puts the frames that leave it still wanted: DNxHD, in a file.
///
/// <b>Why.</b> An insert makes the delay longer for the rest of the transmission, and raw frames are
/// 104 MB/s: memory runs out after seconds of inserts, and this plant's delay drive cannot take that
/// rate each way for hours (measured at about 80). DNxHD 145 - what the masters already are - is a
/// seventh of it. So the delay proper stays raw, in memory, and is what goes to air untouched; only
/// what an insert pushes past the end of memory is compressed, once, and decoded again for air.
///
/// <b>Frames are matched by order, and checked.</b> Both directions are an ffmpeg with a pipe at each
/// end. Every DNxHD frame of a given kind is the same size, and starts with the same five bytes, so
/// what comes out of the encoder is cut into frames by size and each is checked; the decoder gives
/// one picture for each frame put in. <see cref="Probe"/> proves both on this machine, with this
/// ffmpeg, before any of it is relied on.
///
/// <b>The disk may stall.</b> This drive does, for ten seconds at a time. Half a minute of compressed
/// frames is kept in memory on the way to the disk and read from there if wanted before it gets there,
/// and twenty seconds are read ahead of the transmitter on the way back. About a gigabyte in all,
/// taken only once an insert needs it.
///
/// <b>Nothing here may hold up the recorder.</b> Frames are handed over by a thread of the line's own.
/// If it falls behind, the frame leaving memory is lost from the delay - a held picture when air
/// reaches it - rather than the writer waiting.
/// </summary>
internal sealed unsafe class DelayOverflow : IDisposable
{
    /// <summary>One 1920x1080 8-bit DNxHD frame at 145 Mb/s (compression ID 1237), whatever the picture.</summary>
    internal const int DnxFrameBytes = 606_208;

    private const string DnxBitrate = "145M";
    private const int Alignment = 4096;
    private const int HeaderBytes = 64;

    // Compressed slot header: sequence, audio bytes, flags, recording frame.
    private const int OffSequence = 0;
    private const int OffAudioBytes = 8;
    private const int OffFlags = 12;
    private const int OffRecordingFrame = 16;

    private const int WriteCacheSeconds = 30;
    private const int ReadAheadSeconds = 20;
    private const int DecodeAheadSeconds = 2;

    /// <summary>The raw DNxHD parser sees the end of a frame in the start of the next: a frame or two must follow.</summary>
    private const int DecoderLag = 3;

    private const long IdleMs = 5000;
    private const long SpareDiskBytes = 10L * 1024 * 1024 * 1024;

    private const FileOptions NoBuffering = (FileOptions)0x20000000;

    private static readonly byte[] Magic = { 0x00, 0x00, 0x02, 0x80, 0x01 };

    private readonly DelayLine _line;
    private readonly string _ffmpeg;
    private readonly CaptureFormat _format;
    private readonly long _slots;
    private readonly int _audioCapacity;
    private readonly int _rawStride;
    private readonly int _stride;
    private readonly FileStream _file;
    private readonly string _path;

    private volatile bool _stop;
    private volatile bool _faulted;

    // By ring slot: which frame is waiting for the encoder, which is on disk, and its recording frame.
    private readonly long[] _pending;
    private readonly long[] _stored;
    private readonly long[] _recording;
    private long _lastSubmitted = -1;
    private long _lastSubmitMs;

    private byte* _cache;
    private readonly int _cacheCount;
    private readonly long[] _cacheSeq;
    private readonly long[] _cacheOnDisk;
    private readonly BlockingCollection<long> _toDisk = new();
    private Thread? _diskWriter;

    private Coder? _encoder;
    private long _encoderRetryAt;
    private readonly object _encoderLock = new();

    private long _want = -1;
    private long _wantMs;
    private byte* _ahead;
    private readonly int _aheadCount;
    private readonly long[] _aheadSeq;
    private byte* _decoded;
    private readonly int _decodedCount;
    private readonly long[] _decodedSeq;
    private Thread? _fetcher;
    private Thread? _feeder;
    private readonly object _readStart = new();

    private long _encoded, _missing, _missingSaidMs;
    private int _saidStarted;
    private readonly IDisposable _gauge;

    /// <summary>Frames compressed so far.</summary>
    public long Encoded => Interlocked.Read(ref _encoded);

    /// <summary>Frames that should be here and are not: air holds the picture before for each.</summary>
    public long Missing => Interlocked.Read(ref _missing);

    /// <summary>Something went wrong that will not mend itself; what is already held can still be read.</summary>
    public bool Faulted => _faulted;

    public static bool Supports(CaptureFormat format) =>
        format.Width == 1920 && format.Height == 1080 && format.FrameBytes == 1920 * 1080 * 2 && !IsInterlaced(format);

    private static bool IsInterlaced(CaptureFormat format) => format.IsInterlaced;

    public static int StrideFor(CaptureFormat format) =>
        (HeaderBytes + DnxFrameBytes + DelayLine.AudioCapacityFor(format.FrameRate) + Alignment - 1) / Alignment * Alignment;

    /// <summary>Free disk wanted before an overflow of this many slots is offered.</summary>
    public static long DiskFor(CaptureFormat format, long slots) => slots * StrideFor(format) + SpareDiskBytes;

    public DelayOverflow(DelayLine line, string path, string ffmpeg, CaptureFormat format, long slots, long rawStride)
    {
        _line = line;
        _path = path;
        _ffmpeg = ffmpeg;
        _format = format;
        _slots = slots;
        _audioCapacity = DelayLine.AudioCapacityFor(format.FrameRate);
        _rawStride = (int)rawStride;
        _stride = StrideFor(format);

        _pending = Filled(slots);
        _stored = Filled(slots);
        _recording = Filled(slots);

        int rate = Math.Max(1, format.FrameRate);
        _cacheCount = rate * WriteCacheSeconds;
        _cacheSeq = Filled(_cacheCount);
        _cacheOnDisk = Filled(_cacheCount);
        _aheadCount = rate * ReadAheadSeconds;
        _aheadSeq = Filled(_aheadCount);
        _decodedCount = rate * DecodeAheadSeconds + 8;
        _decodedSeq = Filled(_decodedCount);

        // Straight to the disk, as the direct ring is: whole sectors, past the Windows cache. Sparse,
        // so a line that never overflows takes nothing.
        _file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 0,
                               FileOptions.WriteThrough | NoBuffering);

        try
        {
            DelayLine.SetSparse(_file);
            _file.SetLength(slots * _stride);
        }
        catch
        {
            _file.Dispose();
            try { File.Delete(path); } catch (IOException) { }
            throw;
        }

        _gauge = MemoryGauges.Register("Delay", $"overflow buffers ({Path.GetFileName(path)})", () =>
            (_cache is null ? 0L : (long)_cacheCount * _stride) +
            (_ahead is null ? 0L : (long)_aheadCount * _stride) +
            (_decoded is null ? 0L : (long)_decodedCount * _rawStride));
    }

    private static long[] Filled(long count)
    {
        var a = new long[count];
        Array.Fill(a, -1L);
        return a;
    }

    // ------------------------------------------------------------------ what is held

    private int RingSlot(long seq) => (int)(seq % _slots);

    private bool Has(long seq) =>
        Volatile.Read(ref _pending[RingSlot(seq)]) == seq ||
        Volatile.Read(ref _cacheSeq[(int)(seq % _cacheCount)]) == seq ||
        Volatile.Read(ref _stored[RingSlot(seq)]) == seq;

    /// <summary>Which frame of the recording <paramref name="seq"/> is, or -1 when it is not held here.</summary>
    public long RecordingFrameAt(long seq)
    {
        if (!Has(seq)) return -1;
        long frame = Volatile.Read(ref _recording[RingSlot(seq)]);
        return Has(seq) ? frame : -1;
    }

    /// <summary>
    /// The decoded frame, when the read-ahead has it. Otherwise <see cref="DelayReadResult.NotReady"/> when
    /// it is on its way and <see cref="DelayReadResult.Missing"/> when it never will be.
    /// </summary>
    public DelayReadResult TryRead(long seq, DelayFrameBuffer into)
    {
        byte* decoded = _decoded;
        int i = (int)(seq % _decodedCount);

        if (decoded is not null && Volatile.Read(ref _decodedSeq[i]) == seq)
        {
            DelayReadResult copied = _line.CopySlot(decoded + (long)i * _rawStride, seq, into);

            // Unpublished before it is decoded over: still published means the copy was whole.
            Interlocked.MemoryBarrier();
            if (copied == DelayReadResult.Ok && Volatile.Read(ref _decodedSeq[i]) == seq) return DelayReadResult.Ok;
        }

        return Has(seq) || seq > Volatile.Read(ref _lastSubmitted) ? DelayReadResult.NotReady : DelayReadResult.Missing;
    }

    // ------------------------------------------------------------------ in

    /// <summary>
    /// Takes a frame about to leave memory: a whole raw slot, header, picture and sound. Waits while
    /// the encoder is busy; called only by the line's overflow thread.
    /// </summary>
    public void Submit(long seq, byte* raw)
    {
        if (_stop) return;

        if (_faulted)
        {
            Lose(seq);
            return;
        }

        EnsureWriteSide();

        int slot = RingSlot(seq);
        int audioBytes = Math.Min(*(int*)(raw + DelayLine.OffAudioBytes), _audioCapacity);
        var sound = new byte[audioBytes];

        if (audioBytes > 0)
            fixed (byte* dst = sound)
                Buffer.MemoryCopy(raw + DelayLine.SlotHeaderBytes + _format.FrameBytes, dst, audioBytes, audioBytes);

        var meta = new Meta(seq, *(long*)(raw + DelayLine.OffRecordingFrame), *(int*)(raw + DelayLine.OffFlags), sound);

        Volatile.Write(ref _stored[slot], -1);
        Volatile.Write(ref _recording[slot], meta.RecordingFrame);
        Volatile.Write(ref _pending[slot], seq);
        Volatile.Write(ref _lastSubmitted, seq);
        Volatile.Write(ref _lastSubmitMs, Environment.TickCount64);

        Coder? encoder = Encoder();

        if (encoder is null || !encoder.Put(meta, raw + DelayLine.SlotHeaderBytes, _format.FrameBytes))
            Lose(seq);
    }

    /// <summary>A frame the line could not hand over in time. Air holds the picture before it.</summary>
    public void Lose(long seq)
    {
        int slot = RingSlot(seq);
        Interlocked.CompareExchange(ref _pending[slot], -1, seq);

        if (seq > Volatile.Read(ref _lastSubmitted)) Volatile.Write(ref _lastSubmitted, seq);

        long missing = Interlocked.Increment(ref _missing);
        long now = Environment.TickCount64;

        if (now - Interlocked.Read(ref _missingSaidMs) < 5000) return;
        Interlocked.Exchange(ref _missingSaidMs, now);

        ActivityLog.Shared.Warn(LogSource.TidalLock,
            $"DELAY  a frame could not be compressed in time ({missing} so far); air will hold the picture before it. " +
            "The machine is too busy, or ffmpeg has stopped.",
            @event: "delay.overflowlost", file: _path);
    }

    /// <summary>Nothing has been wanted for a while: the encoder and decoder are closed until it is.</summary>
    public void CloseIfIdle()
    {
        long now = Environment.TickCount64;

        lock (_encoderLock)
            if (_encoder is { } encoder && now - Volatile.Read(ref _lastSubmitMs) > IdleMs)
            {
                _encoder = null;
                encoder.FinishInput();

                ActivityLog.Shared.Info(LogSource.TidalLock,
                    $"DELAY  nothing more is leaving memory; compression has stopped. {Encoded:N0} frames were compressed, {Missing:N0} lost.",
                    @event: "delay.overflowdone", file: _path);

                Volatile.Write(ref _saidStarted, 0);
            }
    }

    private void EnsureWriteSide()
    {
        if (_diskWriter is null)
        {
            _cache = (byte*)NativeMemory.AlignedAlloc((nuint)((long)_cacheCount * _stride), Alignment);

            _diskWriter = new Thread(WriteDisk) { IsBackground = true, Name = "tidal overflow disk" };
            _diskWriter.Start();
        }

        if (Interlocked.Exchange(ref _saidStarted, 1) == 0)
            ActivityLog.Shared.Info(LogSource.TidalLock,
                "DELAY  the delay has grown past what memory holds, so frames further back are compressed (DNxHD 145) to " +
                $"{DelayLine.DescribeDisk(_path)} and decoded for air. The picture goes through that compression from here on.",
                @event: "delay.overflow", file: _path);
    }

    private Coder? Encoder()
    {
        lock (_encoderLock)
        {
            if (_stop) return null;
            if (_encoder is { Alive: true } running) return running;

            if (_encoder is { } dead)
            {
                _encoder = null;
                Say("encoder", dead.Complaint);
                dead.Dispose();
                _encoderRetryAt = Environment.TickCount64 + 500;
            }

            if (Environment.TickCount64 < _encoderRetryAt) return null;

            try
            {
                _encoder = Coder.Start(_ffmpeg, EncoderArguments(_format), "overflow encoder", Encoding, Abandoned);
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Say("encoder", ex.Message);
                _encoderRetryAt = Environment.TickCount64 + 2000;
            }

            return _encoder;
        }
    }

    private void Say(string which, string why) =>
        ActivityLog.Shared.Warn(LogSource.TidalLock,
            $"DELAY  the delay's {which} stopped{(why.Length > 0 ? $" ({why})" : "")}; starting it again.",
            @event: "delay.overflowrestart", file: _path);

    /// <summary>One compressed frame out of the encoder, into the write cache. On the encoder's thread.</summary>
    private bool Encoding(Meta meta, Stream output)
    {
        int i = (int)(meta.Seq % _cacheCount);

        // The frame there half a minute ago, still not on disk: the disk is that far behind. Waiting
        // here holds up the encoder, and through it the line's overflow thread - never the recorder.
        while (!_stop && !_faulted && Volatile.Read(ref _cacheSeq[i]) is >= 0 and var old &&
               Volatile.Read(ref _cacheOnDisk[i]) != old)
            Thread.Sleep(20);

        if (_stop || _faulted) return false;

        byte* slot = _cache + (long)i * _stride;
        Volatile.Write(ref _cacheSeq[i], -1);
        Volatile.Write(ref _cacheOnDisk[i], -1);
        Interlocked.MemoryBarrier();

        if (!ReadWhole(output, slot + HeaderBytes, DnxFrameBytes)) return false;

        for (int b = 0; b < Magic.Length; b++)
            if (slot[HeaderBytes + b] != Magic[b])
            {
                ActivityLog.Shared.Error(LogSource.TidalLock,
                    "DELAY  the encoder's output did not line up with its frames, so it is not trusted. Inserts past what " +
                    "memory holds are refused until the recording is started again.",
                    @event: "delay.overflowfault", file: _path);
                _faulted = true;
                return false;
            }

        *(long*)(slot + OffSequence) = meta.Seq;
        *(int*)(slot + OffAudioBytes) = meta.Sound.Length;
        *(int*)(slot + OffFlags) = meta.Flags;
        *(long*)(slot + OffRecordingFrame) = meta.RecordingFrame;
        Marshal.Copy(meta.Sound, 0, (nint)(slot + HeaderBytes + DnxFrameBytes), meta.Sound.Length);

        Volatile.Write(ref _cacheSeq[i], meta.Seq);
        Interlocked.CompareExchange(ref _pending[RingSlot(meta.Seq)], -1, meta.Seq);
        Interlocked.Increment(ref _encoded);

        try { _toDisk.Add(meta.Seq); }
        catch (InvalidOperationException) { return false; }

        return true;
    }

    /// <summary>Frames handed to an encoder that stopped before giving them back.</summary>
    private void Abandoned(Meta meta)
    {
        if (!_stop && Volatile.Read(ref _pending[RingSlot(meta.Seq)]) == meta.Seq) Lose(meta.Seq);
    }

    private void WriteDisk()
    {
        bool said = false;

        try
        {
            foreach (long seq in _toDisk.GetConsumingEnumerable())
            {
                int i = (int)(seq % _cacheCount);

                try
                {
                    RandomAccess.Write(_file.SafeFileHandle, new ReadOnlySpan<byte>(_cache + (long)i * _stride, _stride),
                                       (long)RingSlot(seq) * _stride);
                    Volatile.Write(ref _stored[RingSlot(seq)], seq);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
                {
                    if (_stop) return;

                    _faulted = true;

                    if (!said)
                        ActivityLog.Shared.Error(LogSource.TidalLock,
                            $"DELAY  compressed frames could not be written to {DelayLine.DescribeDisk(_path)}: {ex.Message}",
                            @event: "delay.overflowfault", file: _path);
                    said = true;
                }
                finally
                {
                    Volatile.Write(ref _cacheOnDisk[i], seq);
                }
            }
        }
        catch (ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------ out

    /// <summary>The transmitter is at <paramref name="seq"/> and will want the frames from there on.</summary>
    public void Want(long seq)
    {
        if (_stop) return;

        Volatile.Write(ref _want, seq);
        Volatile.Write(ref _wantMs, Environment.TickCount64);

        if (_feeder is not null) return;

        lock (_readStart)
        {
            if (_feeder is not null || _stop) return;

            _ahead = (byte*)NativeMemory.AlignedAlloc((nuint)((long)_aheadCount * _stride), Alignment);
            _decoded = (byte*)NativeMemory.AlignedAlloc((nuint)((long)_decodedCount * _rawStride), Alignment);

            _fetcher = new Thread(Fetch) { IsBackground = true, Name = "tidal overflow read-ahead" };
            _fetcher.Start();

            _feeder = new Thread(Feed) { IsBackground = true, Name = "tidal overflow decode", Priority = ThreadPriority.AboveNormal };
            _feeder.Start();
        }
    }

    private bool Wanted => Environment.TickCount64 - Volatile.Read(ref _wantMs) < IdleMs;

    /// <summary>Compressed frames from the transmitter's place on, into memory, well before it needs them.</summary>
    private void Fetch()
    {
        while (!_stop)
        {
            long from = Volatile.Read(ref _want);
            bool idle = true;

            if (Wanted)
                for (long s = from; s < from + _aheadCount && !_stop; s++)
                {
                    long now = Volatile.Read(ref _want);
                    if (now < from || now > s) break;                         // moved: start again from there

                    int i = (int)(s % _aheadCount);
                    if (Volatile.Read(ref _aheadSeq[i]) == s) continue;
                    if (s > Volatile.Read(ref _lastSubmitted)) break;         // not here yet

                    if (!Has(s) || Volatile.Read(ref _pending[RingSlot(s)]) == s) continue;

                    Volatile.Write(ref _aheadSeq[i], -1);
                    Interlocked.MemoryBarrier();

                    if (ReadCompressed(s, _ahead + (long)i * _stride))
                    {
                        Volatile.Write(ref _aheadSeq[i], s);
                        idle = false;
                    }
                }

            if (idle) Thread.Sleep(15);
        }
    }

    /// <summary>One compressed frame: from the write cache if it is still there, otherwise off the disk.</summary>
    private bool ReadCompressed(long seq, byte* into)
    {
        int c = (int)(seq % _cacheCount);

        if (_cache is not null && Volatile.Read(ref _cacheSeq[c]) == seq)
        {
            Buffer.MemoryCopy(_cache + (long)c * _stride, into, _stride, _stride);
            Interlocked.MemoryBarrier();
            if (Volatile.Read(ref _cacheSeq[c]) == seq) return true;
        }

        int slot = RingSlot(seq);
        if (Volatile.Read(ref _stored[slot]) != seq) return false;

        try
        {
            if (RandomAccess.Read(_file.SafeFileHandle, new Span<byte>(into, _stride), (long)slot * _stride) < _stride)
                return false;
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            return false;
        }

        Interlocked.MemoryBarrier();
        return Volatile.Read(ref _stored[slot]) == seq && *(long*)(into + OffSequence) == seq;
    }

    /// <summary>Keeps the decoder a couple of seconds ahead of the transmitter.</summary>
    private void Feed()
    {
        Coder? decoder = null;
        long next = -1, retryAt = 0;

        while (!_stop)
        {
            long want = Volatile.Read(ref _want);

            if (!Wanted)
            {
                decoder?.Dispose();
                decoder = null;
                next = -1;
                Thread.Sleep(50);
                continue;
            }

            if (decoder is { Alive: false } dead)
            {
                Say("decoder", dead.Complaint);
                dead.Dispose();
                decoder = null;
                next = -1;                                  // what it had in hand is asked for again
                retryAt = Environment.TickCount64 + 300;
            }

            // The transmitter moved out of what has been fed - a cut, a resync. Pictures are matched to
            // frames by the order they went in, so the decoder need not be started again for it.
            if (next < 0 || want > next || want < next - _decodedCount) next = want;

            if (next >= want + _decodedCount - 4 || Environment.TickCount64 < retryAt)
            {
                Thread.Sleep(5);
                continue;
            }

            int i = (int)(next % _aheadCount);

            if (Volatile.Read(ref _aheadSeq[i]) != next)
            {
                // Never coming: go on to the next. On its way: wait for it.
                if (next <= Volatile.Read(ref _lastSubmitted) && !Has(next)) next++;
                else Thread.Sleep(5);
                continue;
            }

            if (decoder is null)
            {
                try { decoder = Coder.Start(_ffmpeg, DecoderArguments(_format), "overflow decoder", Decoding, _ => { }); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Say("decoder", ex.Message);
                    retryAt = Environment.TickCount64 + 1000;
                    continue;
                }
            }

            byte* slot = _ahead + (long)i * _stride;
            var sound = new byte[Math.Clamp(*(int*)(slot + OffAudioBytes), 0, _audioCapacity)];
            Marshal.Copy((nint)(slot + HeaderBytes + DnxFrameBytes), sound, 0, sound.Length);
            var meta = new Meta(next, *(long*)(slot + OffRecordingFrame), *(int*)(slot + OffFlags), sound);

            // The fetcher could have moved on and be reading another frame over this one.
            if (Volatile.Read(ref _aheadSeq[i]) != next) continue;

            decoder.Put(meta, slot + HeaderBytes, DnxFrameBytes);

            // The same, after: what the decoder was given may then be two frames' halves. It cannot be
            // taken back, so start it again.
            if (Volatile.Read(ref _aheadSeq[i]) != next)
            {
                decoder.Dispose();
                decoder = null;
                next = -1;
                continue;
            }

            next++;
        }

        decoder?.Dispose();
    }

    /// <summary>One decoded picture out of the decoder. On its thread.</summary>
    private bool Decoding(Meta meta, Stream output)
    {
        int i = (int)(meta.Seq % _decodedCount);
        byte* slot = _decoded + (long)i * _rawStride;

        Volatile.Write(ref _decodedSeq[i], -1);
        Interlocked.MemoryBarrier();

        if (!ReadWhole(output, slot + DelayLine.SlotHeaderBytes, _format.FrameBytes)) return false;

        *(long*)(slot + DelayLine.OffSequence) = meta.Seq;
        *(int*)(slot + DelayLine.OffVideoBytes) = _format.FrameBytes;
        *(int*)(slot + DelayLine.OffAudioBytes) = meta.Sound.Length;
        *(int*)(slot + DelayLine.OffFlags) = meta.Flags;
        *(long*)(slot + DelayLine.OffRecordingFrame) = meta.RecordingFrame;
        Marshal.Copy(meta.Sound, 0, (nint)(slot + DelayLine.SlotHeaderBytes + _format.FrameBytes), meta.Sound.Length);

        Volatile.Write(ref _decodedSeq[i], meta.Seq);
        return true;
    }

    private static bool ReadWhole(Stream stream, byte* into, int bytes)
    {
        int got = 0;

        while (got < bytes)
        {
            int n = stream.Read(new Span<byte>(into + got, bytes - got));
            if (n <= 0) return false;
            got += n;
        }

        return true;
    }

    // ------------------------------------------------------------------ ffmpeg

    private static string[] EncoderArguments(CaptureFormat format) => new[]
    {
        "-hide_banner", "-loglevel", "error", "-nostdin",
        "-f", "rawvideo", "-pixel_format", "uyvy422", "-video_size", $"{format.Width}x{format.Height}",
        "-framerate", format.FrameRate.ToString(), "-i", "pipe:0",
        "-an", "-c:v", "dnxhd", "-b:v", DnxBitrate, "-pix_fmt", "yuv422p",

        // Slices, not frames, as on the recorder's master: no picture waits for the ones after it.
        "-threads", "4", "-thread_type", "slice",
        "-fps_mode", "passthrough", "-flush_packets", "1", "-f", "dnxhd", "pipe:1",
    };

    private static string[] DecoderArguments(CaptureFormat format) => new[]
    {
        "-hide_banner", "-loglevel", "error", "-nostdin",

        // Told what it is, so it does not read seconds of it to find out.
        "-probesize", (DnxFrameBytes * 2).ToString(), "-analyzeduration", "0",
        "-threads", "4", "-thread_type", "slice",
        "-f", "dnxhd", "-framerate", format.FrameRate.ToString(), "-i", "pipe:0",
        "-an", "-pix_fmt", "uyvy422",
        "-fps_mode", "passthrough", "-flush_packets", "1", "-f", "rawvideo", "pipe:1",
    };

    internal readonly record struct Meta(long Seq, long RecordingFrame, int Flags, byte[] Sound);

    /// <summary>
    /// An ffmpeg that turns one frame into one other frame, in order: what goes in is queued beside it, and
    /// a thread takes what comes out, each with the entry it belongs to.
    /// </summary>
    private sealed class Coder : IDisposable
    {
        private readonly Process _process;
        private readonly Stream _in;
        private readonly Stream _out;
        private readonly ConcurrentQueue<Meta> _inside = new();
        private readonly SemaphoreSlim _entries = new(0);
        private readonly Func<Meta, Stream, bool> _take;
        private readonly Action<Meta> _abandoned;
        private readonly Thread _reader;
        private volatile bool _alive = true;
        private volatile bool _finishing;
        private string _complaint = "";
        private int _disposed;

        public bool Alive => _alive;
        public string Complaint => _complaint;

        private Coder(Process process, Func<Meta, Stream, bool> take, Action<Meta> abandoned, string name)
        {
            _process = process;
            _in = process.StandardInput.BaseStream;
            _out = process.StandardOutput.BaseStream;
            _take = take;
            _abandoned = abandoned;

            process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) _complaint = e.Data.Trim(); };
            process.BeginErrorReadLine();

            _reader = new Thread(Read) { IsBackground = true, Name = $"tidal {name}", Priority = ThreadPriority.AboveNormal };
            _reader.Start();
        }

        public static Coder Start(string ffmpeg, string[] arguments, string name, Func<Meta, Stream, bool> take, Action<Meta> abandoned)
        {
            var info = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            foreach (string a in arguments) info.ArgumentList.Add(a);

            Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started.");
            MemoryGauges.NameChild(process.Id, name);
            return new Coder(process, take, abandoned, name);
        }

        /// <summary>One frame in. False when ffmpeg has gone.</summary>
        public bool Put(Meta meta, byte* data, int bytes)
        {
            if (!_alive) return false;

            // Queued first: what comes out must find its entry.
            _inside.Enqueue(meta);
            _entries.Release();

            try
            {
                _in.Write(new ReadOnlySpan<byte>(data, bytes));
                _in.Flush();
                return true;
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // Its entry is given up with the rest, when the reader finds the process gone.
                _alive = false;
                Kill();
                return true;
            }
        }

        /// <summary>No more is coming: what is inside comes out, and it ends by itself.</summary>
        public void FinishInput()
        {
            _finishing = true;
            try { _in.Dispose(); } catch (IOException) { }
        }

        private void Read()
        {
            try
            {
                while (true)
                {
                    // Finished and nothing left inside: done, with nobody waiting on it.
                    if (!_entries.Wait(200))
                    {
                        if (_disposed != 0 || (_finishing && _inside.IsEmpty)) break;
                        if (!_finishing && HasExited()) break;
                        continue;
                    }

                    if (!_inside.TryPeek(out Meta meta)) continue;
                    if (!_take(meta, _out)) break;
                    _inside.TryDequeue(out _);
                }
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
            finally
            {
                _alive = false;
                while (_inside.TryDequeue(out Meta left)) _abandoned(left);
                if (_finishing) Dispose();
            }
        }

        private bool HasExited()
        {
            try { return _process.HasExited; }
            catch (InvalidOperationException) { return true; }
        }

        private void Kill()
        {
            try { if (!_process.HasExited) _process.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception) { }
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

            _alive = false;
            Kill();

            if (Thread.CurrentThread != _reader && _reader.IsAlive) _reader.Join(TimeSpan.FromSeconds(3));

            try { _in.Dispose(); } catch (IOException) { }
            _process.Dispose();
        }
    }

    // ------------------------------------------------------------------ proving it

    private static readonly ConcurrentDictionary<string, Task<string?>> Probes = new();

    /// <summary>
    /// Whether this ffmpeg really does it on this machine: a run of different pictures through the encoder
    /// and back through the decoder, expecting every one, in order, and recognisable. Null when it does, or
    /// what was wrong. Once per ffmpeg and rate, in the background.
    /// </summary>
    public static Task<string?> Probe(string ffmpeg, CaptureFormat format) =>
        Probes.GetOrAdd($"{ffmpeg}|{format.FrameRate}", _ => Task.Run(() =>
        {
            try { return ProbeNow(ffmpeg, format); }
            catch (Exception ex) { return ex.Message; }
        }));

    private static string? ProbeNow(string ffmpeg, CaptureFormat format)
    {
        const int frames = 12;
        var pictures = new byte[frames * format.FrameBytes];

        // Flat fields of a different level each, so a picture out of its place shows.
        for (int f = 0; f < frames; f++)
        {
            Span<byte> picture = pictures.AsSpan(f * format.FrameBytes, format.FrameBytes);
            byte luma = (byte)(40 + f * 15);

            for (int i = 0; i < picture.Length; i += 2)
            {
                picture[i] = 128;
                picture[i + 1] = luma;
            }
        }

        byte[] coded = Through(ffmpeg, EncoderArguments(format), pictures);

        if (coded.Length != frames * DnxFrameBytes)
            return $"the encoder gave {coded.Length:N0} bytes for {frames} frames, not {frames * DnxFrameBytes:N0}";

        for (int f = 0; f < frames; f++)
            if (!coded.AsSpan(f * DnxFrameBytes, Magic.Length).SequenceEqual(Magic))
                return $"frame {f} from the encoder does not start as DNxHD does";

        byte[] back = Through(ffmpeg, DecoderArguments(format), coded);

        if (back.Length != pictures.Length)
            return $"the decoder gave {back.Length / (double)format.FrameBytes:F2} pictures for {frames}";

        for (int f = 0; f < frames; f++)
        {
            long sum = 0;
            int n = 0;

            for (int i = 1; i < format.FrameBytes; i += 4002, n++) sum += back[f * format.FrameBytes + (i | 1)];

            double level = sum / (double)n;
            if (Math.Abs(level - (40 + f * 15)) > 3) return $"picture {f} came back at level {level:F0}, not {40 + f * 15}";
        }

        return null;
    }

    private static byte[] Through(string ffmpeg, string[] arguments, byte[] input)
    {
        var info = new ProcessStartInfo(ffmpeg)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        foreach (string a in arguments) info.ArgumentList.Add(a);

        using Process process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started");

        var complaints = new System.Text.StringBuilder();
        process.ErrorDataReceived += (_, e) => { if (e.Data is { Length: > 0 }) complaints.Append(e.Data).Append(' '); };
        process.BeginErrorReadLine();

        Task feed = Task.Run(() =>
        {
            try
            {
                using Stream to = process.StandardInput.BaseStream;
                to.Write(input);
            }
            catch (IOException) { }
        });

        using var output = new MemoryStream();
        Task read = Task.Run(() => process.StandardOutput.BaseStream.CopyTo(output));

        if (!read.Wait(TimeSpan.FromSeconds(15)))
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new InvalidOperationException("ffmpeg did not finish in time");
        }

        feed.Wait(TimeSpan.FromSeconds(2));
        process.WaitForExit(2000);

        return complaints.Length > 0 && output.Length == 0
            ? throw new InvalidOperationException(complaints.ToString().Trim())
            : output.ToArray();
    }

    // ------------------------------------------------------------------ closing

    /// <summary>No more frames, and nothing left waiting on the encoder. Before <see cref="Dispose"/>.</summary>
    public void Stop()
    {
        _stop = true;

        // Not under the lock: the thread handing frames over holds it while one is started, and the
        // frame it may be waiting to hand over is let go by the process ending.
        Coder? encoder;
        lock (_encoderLock)
        {
            encoder = _encoder;
            _encoder = null;
        }

        encoder?.Dispose();
    }

    public void Dispose()
    {
        Stop();
        _gauge.Dispose();

        _feeder?.Join(TimeSpan.FromSeconds(5));
        _fetcher?.Join(TimeSpan.FromSeconds(15));

        try { _toDisk.CompleteAdding(); } catch (ObjectDisposedException) { }
        bool written = _diskWriter is null || _diskWriter.Join(TimeSpan.FromSeconds(15));

        _file.Dispose();
        try { File.Delete(_path); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

        // Left to the process to give back if a thread is still in it: a leak is better than a crash.
        if (written && _cache is not null) NativeMemory.AlignedFree(_cache);
        if (_fetcher is null or { IsAlive: false })
        {
            if (_feeder is null or { IsAlive: false })
            {
                if (_ahead is not null) NativeMemory.AlignedFree(_ahead);
                if (_decoded is not null) NativeMemory.AlignedFree(_decoded);
            }
        }
    }
}
