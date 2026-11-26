using System.Buffers;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Emerald.Core;

namespace Emerald.Video;

/// <summary>
/// Where a delay held in RAM puts the frames that leave it still wanted: DNxHD, in memory taken as it is
/// needed and given back as air goes past.
///
/// <b>Why.</b> An insert makes the delay longer for the rest of the transmission. Raw, that is 104 MB for
/// every second of it. DNxHD 145 - what the masters already are - is about 16, so the delay proper stays
/// raw and is what goes to air untouched, and only what an insert adds to it is compressed, once, and
/// decoded again for air. A thirty-second insert takes half a gigabyte; five minutes, about five. None of
/// it touches the disk, which on this plant cannot be relied on.
///
/// <b>Frames are matched by order, and checked.</b> Both directions are an ffmpeg with a pipe at each
/// end. Every DNxHD frame of a given kind is the same size, and starts with the same five bytes, so
/// what comes out of the encoder is cut into frames by size and each is checked; the decoder gives
/// one picture for each frame put in. <see cref="Probe"/> proves both on this machine, with this
/// ffmpeg, before any of it is relied on.
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

    // Stored frame header: sequence, audio bytes, flags, recording frame.
    private const int OffSequence = 0;
    private const int OffAudioBytes = 8;
    private const int OffFlags = 12;
    private const int OffRecordingFrame = 16;

    private const int DecodeAheadSeconds = 1;
    private const long IdleMs = 5000;

    /// <summary>Free memory an insert must leave: the level the low-memory warning is at. Tests lower it.</summary>
    internal static long Reserve { get; set; } = LowMemoryWatch.LowBytes;

    /// <summary>Below this nothing more is taken, whatever was promised: the machine is about to page.</summary>
    internal static long Floor { get; set; } = LowMemoryWatch.CriticalBytes;

    private static readonly byte[] Magic = { 0x00, 0x00, 0x02, 0x80, 0x01 };

    private readonly DelayLine _line;
    private readonly string _ffmpeg;
    private readonly CaptureFormat _format;
    private readonly int _audioCapacity;
    private readonly int _rawStride;
    private readonly int _stride;
    private readonly int _chunkFrames;
    private readonly string _path;

    private volatile bool _stop;
    private volatile bool _faulted;

    /// <summary>A second of compressed frames, in memory of its own.</summary>
    private sealed class Chunk
    {
        public byte* Data;
        public long[] Seq = Array.Empty<long>();
    }

    // The store. Everything under the lock is short: a frame copied in or out, a chunk taken or given back.
    private readonly object _storeLock = new();
    private readonly Dictionary<long, Chunk> _chunks = new();
    private readonly HashSet<long> _pending = new();
    private long _bytes, _peakBytes;

    private long _lastSubmitted = -1;
    private long _lastSubmitMs;

    private Coder? _encoder;
    private long _encoderRetryAt;
    private readonly object _encoderLock = new();

    private long _want = -1;
    private long _wantMs;
    private byte* _decoded;
    private readonly object _decodedLock = new();
    private readonly int _decodedCount;
    private readonly long[] _decodedSeq;
    private Thread? _feeder;
    private readonly object _readStart = new();

    private long _encoded, _missing, _missingSaidMs;
    private int _saidStarted;
    private readonly IDisposable _gauge;

    /// <summary>Frames compressed so far.</summary>
    public long Encoded => Interlocked.Read(ref _encoded);

    /// <summary>Frames that should be here and are not: air holds the picture before for each.</summary>
    public long Missing => Interlocked.Read(ref _missing);

    /// <summary>Memory taken just now for compressed frames. Nothing while no insert needs any.</summary>
    public long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Something went wrong that will not mend itself; what is already held can still be read.</summary>
    public bool Faulted => _faulted;

    public static bool Supports(CaptureFormat format) =>
        format.Width == 1920 && format.Height == 1080 && format.FrameBytes == 1920 * 1080 * 2 && !format.IsInterlaced;

    public static int StrideFor(CaptureFormat format) =>
        (HeaderBytes + DnxFrameBytes + DelayLine.AudioCapacityFor(format.FrameRate) + Alignment - 1) / Alignment * Alignment;

    /// <summary>
    /// Frames this could hold in all, with the memory there is now: what it has, and what could be taken and
    /// still leave <see cref="Reserve"/>.
    /// </summary>
    public long RoomFrames => (Bytes + Math.Max(0, DelayLine.AvailablePhysical() - Reserve)) / _stride;

    public DelayOverflow(DelayLine line, string path, string ffmpeg, CaptureFormat format, long rawStride)
    {
        _line = line;
        _path = path;
        _ffmpeg = ffmpeg;
        _format = format;
        _audioCapacity = DelayLine.AudioCapacityFor(format.FrameRate);
        _rawStride = (int)rawStride;
        _stride = StrideFor(format);
        _chunkFrames = Math.Max(1, format.FrameRate);
        _decodedCount = _chunkFrames * DecodeAheadSeconds + 8;
        _decodedSeq = new long[_decodedCount];
        Array.Fill(_decodedSeq, -1L);

        _gauge = MemoryGauges.Register("Delay", $"inserts held compressed ({Path.GetFileName(path)})",
            () => Bytes + (_decoded is null ? 0L : (long)_decodedCount * _rawStride));
    }

    // ------------------------------------------------------------------ what is held

    /// <summary>The stored frame, or null. Under the store lock, and good only while it is held.</summary>
    private byte* Find(long seq)
    {
        if (seq < 0 || !_chunks.TryGetValue(seq / _chunkFrames, out Chunk? chunk)) return null;

        int i = (int)(seq % _chunkFrames);
        return chunk.Seq[i] == seq ? chunk.Data + (long)i * _stride : null;
    }

    private bool Has(long seq)
    {
        lock (_storeLock) return _pending.Contains(seq) || Find(seq) is not null;
    }

    /// <summary>Which frame of the recording <paramref name="seq"/> is, or -1 when it is not held here.</summary>
    public long RecordingFrameAt(long seq)
    {
        lock (_storeLock)
        {
            byte* held = Find(seq);
            return held is null ? -1 : *(long*)(held + OffRecordingFrame);
        }
    }

    /// <summary>
    /// The decoded frame, when the read-ahead has it. Otherwise <see cref="DelayReadResult.NotReady"/> when
    /// it is on its way and <see cref="DelayReadResult.Missing"/> when it never will be.
    /// </summary>
    public DelayReadResult TryRead(long seq, DelayFrameBuffer into)
    {
        int i = (int)(seq % _decodedCount);

        // Held while it copies, so the buffers are not given back under it. Nothing else waits on it.
        lock (_decodedLock)
        {
            if (_decoded is not null && Volatile.Read(ref _decodedSeq[i]) == seq)
            {
                DelayReadResult copied = _line.CopySlot(_decoded + (long)i * _rawStride, seq, into);

                // Unpublished before it is decoded over: still published means the copy was whole.
                Interlocked.MemoryBarrier();
                if (copied == DelayReadResult.Ok && Volatile.Read(ref _decodedSeq[i]) == seq) return DelayReadResult.Ok;
            }
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

        if (Interlocked.Exchange(ref _saidStarted, 1) == 0)
            ActivityLog.Shared.Info(LogSource.TidalLock,
                "DELAY  an insert has made the delay longer than the memory kept for it, so the part it added is compressed " +
                "(DNxHD 145) in memory taken as it is needed. The recording goes to air through that from here on.",
                @event: "delay.overflow", file: _path);

        int audioBytes = Math.Clamp(*(int*)(raw + DelayLine.OffAudioBytes), 0, _audioCapacity);
        var sound = new byte[audioBytes];
        Marshal.Copy((nint)(raw + DelayLine.SlotHeaderBytes + _format.FrameBytes), sound, 0, audioBytes);

        var meta = new Meta(seq, *(long*)(raw + DelayLine.OffRecordingFrame), *(int*)(raw + DelayLine.OffFlags), sound);

        lock (_storeLock) _pending.Add(seq);
        Volatile.Write(ref _lastSubmitted, seq);
        Volatile.Write(ref _lastSubmitMs, Environment.TickCount64);

        Coder? encoder = Encoder();

        if (encoder is null || !encoder.Put(meta, raw + DelayLine.SlotHeaderBytes, _format.FrameBytes))
            Lose(seq);
    }

    /// <summary>A frame that will not be here. Air holds the picture before it.</summary>
    public void Lose(long seq, string why = "The machine is too busy, or ffmpeg has stopped.")
    {
        lock (_storeLock) _pending.Remove(seq);

        if (seq > Volatile.Read(ref _lastSubmitted)) Volatile.Write(ref _lastSubmitted, seq);

        long missing = Interlocked.Increment(ref _missing);
        long now = Environment.TickCount64;

        if (now - Interlocked.Read(ref _missingSaidMs) < 5000) return;
        Interlocked.Exchange(ref _missingSaidMs, now);

        ActivityLog.Shared.Warn(LogSource.TidalLock,
            $"DELAY  a frame of the delay was lost ({missing} so far); air will hold the picture before it. {why}",
            @event: "delay.overflowlost", file: _path);
    }

    /// <summary>
    /// Gives back the memory of frames before <paramref name="floor"/>, which nothing will read again, and
    /// closes the encoder when nothing has been compressed for a while. From the line's overflow thread.
    /// </summary>
    public void Tidy(long floor)
    {
        lock (_storeLock)
        {
            List<long>? gone = null;

            foreach ((long key, Chunk chunk) in _chunks)
                if ((key + 1) * _chunkFrames <= floor)
                {
                    (gone ??= new List<long>()).Add(key);
                    Free(chunk);
                }

            if (gone is not null)
                foreach (long key in gone) _chunks.Remove(key);
        }

        long now = Environment.TickCount64;

        lock (_encoderLock)
            if (_encoder is { } encoder && now - Volatile.Read(ref _lastSubmitMs) > IdleMs)
            {
                _encoder = null;
                encoder.FinishInput();

                ActivityLog.Shared.Info(LogSource.TidalLock,
                    $"DELAY  nothing more needs compressing. {Encoded:N0} frames were, {Missing:N0} lost; at most " +
                    $"{Interlocked.Read(ref _peakBytes) / (double)(1L << 30):F2} GB of memory was taken for it.",
                    @event: "delay.overflowdone", file: _path);

                Volatile.Write(ref _saidStarted, 0);
                Interlocked.Exchange(ref _peakBytes, 0);
            }
    }

    private void Free(Chunk chunk)
    {
        NativeMemory.AlignedFree(chunk.Data);
        chunk.Data = null;
        Interlocked.Add(ref _bytes, -(long)_chunkFrames * _stride);
    }

    /// <summary>Puts a compressed frame in the store, taking a second's memory for it if there is none yet.</summary>
    private bool Store(Meta meta, byte[] coded)
    {
        lock (_storeLock)
        {
            if (_stop) return false;

            long key = meta.Seq / _chunkFrames;

            if (!_chunks.TryGetValue(key, out Chunk? chunk))
            {
                long size = (long)_chunkFrames * _stride;
                if (DelayLine.AvailablePhysical() - size < Floor) return false;

                byte* data;
                try { data = (byte*)NativeMemory.AlignedAlloc((nuint)size, Alignment); }
                catch (OutOfMemoryException) { return false; }

                chunk = new Chunk { Data = data, Seq = new long[_chunkFrames] };
                Array.Fill(chunk.Seq, -1L);
                _chunks[key] = chunk;

                long bytes = Interlocked.Add(ref _bytes, size);
                if (bytes > Interlocked.Read(ref _peakBytes)) Interlocked.Exchange(ref _peakBytes, bytes);
            }

            int i = (int)(meta.Seq % _chunkFrames);
            byte* slot = chunk.Data + (long)i * _stride;

            *(long*)(slot + OffSequence) = meta.Seq;
            *(int*)(slot + OffAudioBytes) = meta.Sound.Length;
            *(int*)(slot + OffFlags) = meta.Flags;
            *(long*)(slot + OffRecordingFrame) = meta.RecordingFrame;
            Marshal.Copy(coded, 0, (nint)(slot + HeaderBytes), DnxFrameBytes);
            Marshal.Copy(meta.Sound, 0, (nint)(slot + HeaderBytes + DnxFrameBytes), meta.Sound.Length);

            chunk.Seq[i] = meta.Seq;
            _pending.Remove(meta.Seq);
        }

        return true;
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

    /// <summary>One compressed frame out of the encoder, into the store. On the encoder's thread.</summary>
    private bool Encoding(Meta meta, Stream output)
    {
        byte[] coded = ArrayPool<byte>.Shared.Rent(DnxFrameBytes);

        try
        {
            fixed (byte* p = coded)
                if (!ReadWhole(output, p, DnxFrameBytes)) return false;

            if (!coded.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            {
                ActivityLog.Shared.Error(LogSource.TidalLock,
                    "DELAY  the encoder's output did not line up with its frames, so it is not trusted. Inserts past what " +
                    "memory holds are refused until the recording is started again.",
                    @event: "delay.overflowfault", file: _path);
                _faulted = true;
                return false;
            }

            if (Store(meta, coded)) Interlocked.Increment(ref _encoded);
            else if (!_stop) Lose(meta.Seq, "There is no more memory to hold it in: close other programs.");

            return !_stop;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(coded);
        }
    }

    /// <summary>Frames handed to an encoder that stopped before giving them back.</summary>
    private void Abandoned(Meta meta)
    {
        bool waiting;
        lock (_storeLock) waiting = _pending.Contains(meta.Seq);
        if (waiting && !_stop) Lose(meta.Seq);
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

            _feeder = new Thread(Feed) { IsBackground = true, Name = "tidal overflow decode", Priority = ThreadPriority.AboveNormal };
            _feeder.Start();
        }
    }

    private bool Wanted => Environment.TickCount64 - Volatile.Read(ref _wantMs) < IdleMs;

    /// <summary>Keeps the decoder a second ahead of the transmitter.</summary>
    private void Feed()
    {
        Coder? decoder = null;
        long next = -1, retryAt = 0;
        var coded = new byte[DnxFrameBytes];

        while (!_stop)
        {
            long want = Volatile.Read(ref _want);

            // Nobody reading: the decoder closed, and its buffers given back.
            if (!Wanted)
            {
                decoder?.Dispose();
                decoder = null;
                next = -1;
                FreeDecoded();
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

            Meta meta = default;
            bool found = false;

            lock (_storeLock)
            {
                byte* slot = Find(next);

                if (slot is not null)
                {
                    var sound = new byte[Math.Clamp(*(int*)(slot + OffAudioBytes), 0, _audioCapacity)];
                    Marshal.Copy((nint)(slot + HeaderBytes + DnxFrameBytes), sound, 0, sound.Length);
                    Marshal.Copy((nint)(slot + HeaderBytes), coded, 0, DnxFrameBytes);
                    meta = new Meta(next, *(long*)(slot + OffRecordingFrame), *(int*)(slot + OffFlags), sound);
                    found = true;
                }
            }

            if (!found)
            {
                // Never coming: go on to the next. On its way: wait for it.
                if (next <= Volatile.Read(ref _lastSubmitted) && !Has(next)) next++;
                else Thread.Sleep(5);
                continue;
            }

            if (decoder is null)
            {
                lock (_decodedLock)
                    if (_decoded is null)
                        _decoded = (byte*)NativeMemory.AlignedAlloc((nuint)((long)_decodedCount * _rawStride), Alignment);

                try { decoder = Coder.Start(_ffmpeg, DecoderArguments(_format), "overflow decoder", Decoding, _ => { }); }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    Say("decoder", ex.Message);
                    retryAt = Environment.TickCount64 + 1000;
                    continue;
                }
            }

            fixed (byte* p = coded) decoder.Put(meta, p, DnxFrameBytes);
            next++;
        }

        decoder?.Dispose();
        FreeDecoded();
    }

    private void FreeDecoded()
    {
        lock (_decodedLock)
        {
            if (_decoded is null) return;

            Array.Fill(_decodedSeq, -1L);
            NativeMemory.AlignedFree(_decoded);
            _decoded = null;
        }
    }

    /// <summary>One decoded picture out of the decoder. On its thread, which is joined before its buffers are freed.</summary>
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

            // It is in a read of a process that has just been ended, so this is moments - and its buffers
            // are given back after, so it has to have gone.
            if (Thread.CurrentThread != _reader && _reader.IsAlive) _reader.Join();

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
        _feeder?.Join(TimeSpan.FromSeconds(10));

        lock (_storeLock)
        {
            foreach (Chunk chunk in _chunks.Values) Free(chunk);
            _chunks.Clear();
            _pending.Clear();
        }
    }
}
