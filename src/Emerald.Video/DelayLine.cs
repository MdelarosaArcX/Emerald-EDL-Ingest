using System.IO;
using System.IO.MemoryMappedFiles;
using Emerald.Core;

namespace Emerald.Video;

public sealed class DelayLineException : Exception
{
    public DelayLineException(string message) : base(message) { }
}

/// <summary>Why a read did not produce a frame.</summary>
public enum DelayReadResult
{
    Ok,

    /// <summary>The writer has not reached this frame yet.</summary>
    NotYetWritten,

    /// <summary>The writer has been all the way round and overwritten it.</summary>
    Lapped,

    /// <summary>Sealed, and this frame is past the end.</summary>
    EndOfLine,
}

/// <summary>
/// One frame read out of the line. Owned by the reader and reused, so a failed read never
/// has to leave a half-copied frame anywhere.
/// </summary>
public sealed class DelayFrameBuffer
{
    public DelayFrameBuffer(int frameBytes, int audioCapacityBytes)
    {
        Video = new byte[frameBytes];
        Audio = new byte[audioCapacityBytes];
    }

    public byte[] Video { get; }
    public byte[] Audio { get; }
    public int VideoBytes { get; internal set; }
    public int AudioBytes { get; internal set; }
    public long Sequence { get; internal set; } = -1;

    /// <summary>True when the receiver genuinely carried audio, rather than silence standing in.</summary>
    public bool AudioWasReal { get; internal set; }
}

/// <summary>
/// The delay itself: a ring of raw frames on disk, written at one end by the recorder and
/// read at the other, a fixed number of frames behind, by the transmitter.
///
/// <b>Why a ring of raw frames.</b> A delay line's whole job is to hold N frames and hand
/// them back in order. Encoding them first would put a codec in the path to air - generation
/// loss on every frame, and a decoder's buffering deciding how long the delay actually is.
/// Holding them as they arrived means the bytes the receiver produced are the bytes the
/// transmitter sends, and the delay is a number of frames rather than an emergent property.
///
/// <b>Why a file.</b> A minute of 1080p at 25 is six gigabytes. That is not a thing to hold
/// in the heap, but it is an unremarkable file, and a memory-mapped one lets the operating
/// system decide how much stays resident - so a ten-second delay never really touches the
/// disk while a five-minute one does not exhaust memory.
///
/// <b>How it stays safe.</b> There is no lock on the hot path; a four-megabyte copy under a
/// lock would put the transmitter's cadence at the mercy of the writer. Instead each slot
/// carries its own sequence number, written last and checked twice by the reader: once
/// before copying and once after. If it changed in between, the writer came round mid-copy
/// and the frame is discarded rather than transmitted torn. The reader alternates between
/// two buffers, so a discarded frame is never the one it was about to fall back on.
/// </summary>
public sealed class DelayLine : IDelayLine, IDisposable
{
    // ------------------------------------------------------------------ layout

    private const int FileHeaderBytes = 4096;
    private const int SlotHeaderBytes = 64;
    private const long Alignment = 4096;

    // Slot header offsets.
    private const int OffSequence = 0;      // long, written last
    private const int OffVideoBytes = 8;    // int
    private const int OffAudioBytes = 12;   // int
    private const int OffFlags = 16;        // int
    private const int FlagAudioReal = 1;

    /// <summary>
    /// Four frames of stereo headroom. The SDK returns whatever audio the slot carried, which
    /// is near but not always equal to one frame's worth, so the room is generous and the
    /// actual length is recorded per slot rather than assumed.
    /// </summary>
    public static int AudioCapacityFor(int frameRate) =>
        48000 / Math.Max(1, frameRate) * 2 * 2 * 4;

    /// <summary>
    /// Bytes per slot, rounded up to a page. The alignment is not cosmetic: it keeps the
    /// writer's copy off any page the reader is part-way through.
    /// </summary>
    public static long SlotStrideFor(CaptureFormat format)
    {
        long raw = SlotHeaderBytes + format.FrameBytes + AudioCapacityFor(format.FrameRate);
        return (raw + Alignment - 1) / Alignment * Alignment;
    }

    /// <summary>
    /// Slots for a delay: the delay itself plus two seconds of guard. The guard is what keeps
    /// the writer from arriving at the slot the reader is reading; without it the two would
    /// meet exactly at the design point.
    /// </summary>
    public static long SlotCountFor(CaptureFormat format, TimeSpan delay)
    {
        long wanted = (long)Math.Ceiling(delay.TotalSeconds * format.FrameRate);
        return wanted + Math.Max(2L * format.FrameRate, 50);
    }

    public static long SizeFor(CaptureFormat format, TimeSpan delay) =>
        FileHeaderBytes + SlotCountFor(format, delay) * SlotStrideFor(format);

    // ------------------------------------------------------------------ state

    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _view;
    private readonly FileStream _file;
    private readonly long _stride;
    private readonly int _audioCapacity;

    private unsafe byte* _base;

    private readonly System.Collections.Concurrent.BlockingCollection<CapturedFrame>? _queue;
    private readonly Thread? _writer;

    private long _written;
    private long _accepted;
    private long _sealedAt = -1;
    private long _lastWriteTicks;
    private long _dropped;
    private int _refs = 1;
    private int _disposed;

    public string Path { get; }
    public int Width { get; }
    public int Height { get; }
    public int FrameRate { get; }
    public int FrameBytes { get; }
    public long SlotCount { get; }
    public long TargetFrames { get; }
    public CaptureFormat Format { get; }

    public long FramesWritten => Volatile.Read(ref _written);
    public bool IsSealed => Volatile.Read(ref _sealedAt) >= 0;
    public long SealedAt => Volatile.Read(ref _sealedAt);

    /// <summary>Frames the writer had to throw away because the ring could not take them.</summary>
    public long Dropped => Interlocked.Read(ref _dropped);

    /// <summary>Set when a write failed. The line seals itself rather than failing the recording.</summary>
    public string? Fault { get; private set; }

    /// <summary>Milliseconds since the recorder last committed a frame; 0 before the first.</summary>
    public long StalledMs
    {
        get
        {
            long last = Volatile.Read(ref _lastWriteTicks);
            if (last == 0) return 0;

            return (long)((System.Diagnostics.Stopwatch.GetTimestamp() - last) * 1000.0
                          / System.Diagnostics.Stopwatch.Frequency);
        }
    }

    private unsafe DelayLine(string path, FileStream file, MemoryMappedFile mmf,
                             MemoryMappedViewAccessor view, CaptureFormat format,
                             long slotCount, long stride, long targetFrames)
    {
        Path = path;
        _file = file;
        _mmf = mmf;
        _view = view;
        Format = format;
        Width = format.Width;
        Height = format.Height;
        FrameRate = format.FrameRate;
        FrameBytes = format.FrameBytes;
        SlotCount = slotCount;
        TargetFrames = targetFrames;
        _stride = stride;
        _audioCapacity = AudioCapacityFor(format.FrameRate);

        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);

        // Eight frames of slack between the receiver and the disk - the same depth the
        // recorder allows its encoder, and for the same reason.
        _queue = new System.Collections.Concurrent.BlockingCollection<CapturedFrame>(8);

        _writer = new Thread(WriteLoop)
        {
            IsBackground = true,
            Name = "tidal delay writer",
        };

        _writer.Start();
    }

    // ------------------------------------------------------------------ creation

    /// <summary>
    /// Checks a delay can be allocated before anything commits to it. Returns false with a
    /// sentence naming the sizes, because "it did not work" is no use to an operator standing
    /// in front of a transmitter.
    /// </summary>
    public static bool Validate(string folder, CaptureFormat format, TimeSpan delay,
                                out long sizeBytes, out string? problem)
    {
        sizeBytes = SizeFor(format, delay);
        problem = null;

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception ex)
        {
            problem = $"The delay line needs a folder at {folder}, and it could not be created: {ex.Message}";
            return false;
        }

        try
        {
            string? root = System.IO.Path.GetPathRoot(System.IO.Path.GetFullPath(folder));

            if (root is { Length: > 0 })
            {
                long free = new DriveInfo(root).AvailableFreeSpace;

                // Two gigabytes of headroom, so filling the ring cannot itself fill the volume.
                if (free < sizeBytes + 2_000_000_000L)
                {
                    problem = $"The delay line needs {Describe(sizeBytes)} on {root} for " +
                              $"{Describe(delay)} of {format.Name}, and only {Describe(free)} is free.";
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            problem = $"The delay line's disk could not be checked: {ex.Message}";
            return false;
        }

        return true;
    }

    public static DelayLine Create(string folder, CaptureFormat format, TimeSpan delay)
    {
        if (!Validate(folder, format, delay, out long size, out string? problem))
            throw new DelayLineException(problem!);

        long stride = SlotStrideFor(format);
        long slots = SlotCountFor(format, delay);
        long target = (long)Math.Ceiling(delay.TotalSeconds * format.FrameRate);

        string path = System.IO.Path.Combine(
            folder, $"tidal-{Environment.ProcessId}-{Guid.NewGuid():N}.ring");

        FileStream? file = null;
        MemoryMappedFile? mmf = null;
        MemoryMappedViewAccessor? view = null;

        try
        {
            file = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read,
                                  4096, FileOptions.RandomAccess);
            file.SetLength(size);

            // Touch the last byte so the extent is real now. Discovering the volume is full
            // forty minutes into a recording is not a discovery worth making.
            file.Seek(size - 1, SeekOrigin.Begin);
            file.WriteByte(0);
            file.Flush(true);

            mmf = MemoryMappedFile.CreateFromFile(file, null, size, MemoryMappedFileAccess.ReadWrite,
                                                  HandleInheritability.None, leaveOpen: true);
            view = mmf.CreateViewAccessor(0, size, MemoryMappedFileAccess.ReadWrite);

            var line = new DelayLine(path, file, mmf, view, format, slots, stride, target);
            line.WriteFileHeader();
            return line;
        }
        catch (Exception ex)
        {
            view?.Dispose();
            mmf?.Dispose();
            file?.Dispose();
            try { File.Delete(path); } catch { }

            throw new DelayLineException($"The delay line could not be created at {path}: {ex.Message}");
        }
    }

    /// <summary>Enough to identify an orphan on disk, and to know whose it was.</summary>
    private unsafe void WriteFileHeader()
    {
        ReadOnlySpan<byte> magic = "EMLDLY01"u8;
        for (int i = 0; i < magic.Length; i++) _base[i] = magic[i];

        *(int*)(_base + 8) = 1;
        *(int*)(_base + 12) = Width;
        *(int*)(_base + 16) = Height;
        *(int*)(_base + 20) = FrameRate;
        *(int*)(_base + 24) = FrameBytes;
        *(int*)(_base + 28) = _audioCapacity;
        *(long*)(_base + 32) = _stride;
        *(long*)(_base + 40) = SlotCount;
        *(long*)(_base + 48) = DateTime.UtcNow.Ticks;
        *(int*)(_base + 56) = Environment.ProcessId;
    }

    /// <summary>
    /// Deletes rings left behind by processes that are gone. Six gigabytes survives a crash
    /// perfectly well, and three crashes would be eighteen.
    /// </summary>
    public static void SweepOrphans(string folder)
    {
        try
        {
            if (!Directory.Exists(folder)) return;

            foreach (string path in Directory.EnumerateFiles(folder, "tidal-*.ring"))
            {
                string[] parts = System.IO.Path.GetFileNameWithoutExtension(path).Split('-');
                if (parts.Length < 3 || !int.TryParse(parts[1], out int pid)) continue;
                if (pid == Environment.ProcessId) continue;

                try
                {
                    System.Diagnostics.Process.GetProcessById(pid);
                    continue;                              // still running; leave it alone
                }
                catch (ArgumentException)
                {
                    // No such process: the ring outlived whoever made it.
                }

                try { File.Delete(path); } catch { }
            }
        }
        catch
        {
            // Housekeeping must never stop the application starting.
        }
    }

    // ------------------------------------------------------------------ writing

    private unsafe byte* SlotAt(long sequence) =>
        _base + FileHeaderBytes + sequence % SlotCount * _stride;

    /// <summary>
    /// Commits one frame. Called only by the delay line's own writer thread, never by the
    /// capture loop - a four-megabyte copy into a mapped view takes a page fault per page on
    /// first touch, and the receiver has only four slots of headroom to spare.
    ///
    /// Never throws. A ring that cannot be written seals itself and lets the drain finish;
    /// losing the delay must not cost the operator the recording.
    /// </summary>
    public unsafe void Write(byte[] video, int videoBytes, byte[]? audio, int audioBytes, bool audioReal)
    {
        if (Volatile.Read(ref _disposed) != 0) return;

        try
        {
            long seq = _written;
            byte* slot = SlotAt(seq);

            // Mark the slot in flight before touching its payload, so a reader arriving
            // mid-write sees a sequence matching nothing rather than half a frame.
            Volatile.Write(ref *(long*)(slot + OffSequence), -1L);

            int keptVideo = Math.Min(videoBytes, FrameBytes);
            fixed (byte* src = video)
                Buffer.MemoryCopy(src, slot + SlotHeaderBytes, FrameBytes, keptVideo);

            int keptAudio = 0;
            if (audio is not null && audioBytes > 0)
            {
                keptAudio = Math.Min(audioBytes, _audioCapacity);
                fixed (byte* src = audio)
                    Buffer.MemoryCopy(src, slot + SlotHeaderBytes + FrameBytes, _audioCapacity, keptAudio);
            }

            *(int*)(slot + OffVideoBytes) = keptVideo;
            *(int*)(slot + OffAudioBytes) = keptAudio;
            *(int*)(slot + OffFlags) = audioReal ? FlagAudioReal : 0;

            // Last, with a release: everything above must be visible before the sequence is.
            Volatile.Write(ref *(long*)(slot + OffSequence), seq);

            Volatile.Write(ref _lastWriteTicks, System.Diagnostics.Stopwatch.GetTimestamp());
            Volatile.Write(ref _written, seq + 1);
        }
        catch (Exception ex)
        {
            Fault ??= ex.Message;
            Seal();
        }
    }

    /// <summary>
    /// Hands a frame to the line from the capture loop. Returns immediately: the copy into
    /// the mapped view happens on the line's own thread.
    ///
    /// This is the whole reason the writer thread exists. Copying four megabytes into a
    /// mapped view takes a soft page fault per page on first touch, and once the ring wraps
    /// it contends with the operating system writing those pages back. The receiver has four
    /// slots of headroom; a single write-back stall would cost frames off the wire. So the
    /// slot loop does one bounded, non-blocking hand-off and nothing else — and if the line
    /// cannot keep up, the delay loses a frame rather than the recording doing so.
    /// </summary>
    public void Offer(CapturedFrame frame)
    {
        if (_queue is null || _queue.IsAddingCompleted) return;

        try
        {
            if (_queue.TryAdd(frame))
            {
                Interlocked.Increment(ref _accepted);
                return;
            }
        }
        catch (InvalidOperationException)
        {
            return;                       // sealed between the check and the add
        }

        Interlocked.Increment(ref _dropped);
    }

    private void WriteLoop()
    {
        try
        {
            foreach (CapturedFrame frame in _queue!.GetConsumingEnumerable())
                Write(frame.Video, frame.VideoBytes, frame.Audio, frame.AudioBytes, frame.AudioReal);
        }
        catch (Exception ex)
        {
            Fault ??= ex.Message;
        }
    }

    /// <summary>
    /// No more frames are coming. The drain runs to here and then stops.
    ///
    /// The end is the number of frames <i>accepted</i>, not the number written so far: some
    /// are still in the queue on their way to the ring, and telling the transmitter to stop
    /// short of them would clip the end of the recording off the transmission.
    /// </summary>
    public void Seal()
    {
        try { _queue?.CompleteAdding(); } catch (ObjectDisposedException) { }

        long end = Math.Max(Volatile.Read(ref _written), Interlocked.Read(ref _accepted));
        Interlocked.CompareExchange(ref _sealedAt, end, -1);
    }

    /// <summary>A frame the capture loop offered that the ring could not take.</summary>
    public void CountDrop() => Interlocked.Increment(ref _dropped);

    // ------------------------------------------------------------------ reading

    /// <summary>
    /// Reads one frame, or says why it could not. Called only by the transmitter's thread.
    ///
    /// On anything but <see cref="DelayReadResult.Ok"/> the buffer is left exactly as it was,
    /// so the caller can still fall back on whatever it last held.
    /// </summary>
    public unsafe DelayReadResult TryRead(long sequence, DelayFrameBuffer into)
    {
        if (Volatile.Read(ref _disposed) != 0) return DelayReadResult.EndOfLine;

        long sealedAt = Volatile.Read(ref _sealedAt);
        if (sealedAt >= 0 && sequence >= sealedAt) return DelayReadResult.EndOfLine;

        long written = Volatile.Read(ref _written);
        if (sequence >= written) return DelayReadResult.NotYetWritten;
        if (sequence < written - SlotCount) return DelayReadResult.Lapped;

        byte* slot = SlotAt(sequence);

        if (Volatile.Read(ref *(long*)(slot + OffSequence)) != sequence) return DelayReadResult.Lapped;

        int videoBytes = Math.Min(*(int*)(slot + OffVideoBytes), FrameBytes);
        int audioBytes = Math.Min(*(int*)(slot + OffAudioBytes), _audioCapacity);
        bool audioReal = (*(int*)(slot + OffFlags) & FlagAudioReal) != 0;

        fixed (byte* dst = into.Video)
            Buffer.MemoryCopy(slot + SlotHeaderBytes, dst, into.Video.Length, videoBytes);

        if (audioBytes > 0)
        {
            fixed (byte* dst = into.Audio)
                Buffer.MemoryCopy(slot + SlotHeaderBytes + FrameBytes, dst, into.Audio.Length, audioBytes);
        }

        // A full fence, not an acquire. Volatile.Read only stops later loads from being
        // hoisted above it; it does nothing to stop the copy above from sinking below it,
        // which would let the check pass on a frame that was still being copied as the
        // writer overwrote it. Release builds do exactly that, and the torn-frame test
        // catches it. The barrier pins the copy in front of the check.
        Interlocked.MemoryBarrier();

        // Read the sequence again. If the writer came all the way round while this was
        // copying, the buffer now holds half of two different frames - and it is deliberately
        // not the buffer the caller is still holding on to.
        if (Volatile.Read(ref *(long*)(slot + OffSequence)) != sequence) return DelayReadResult.Lapped;

        into.VideoBytes = videoBytes;
        into.AudioBytes = audioBytes;
        into.AudioWasReal = audioReal;
        into.Sequence = sequence;
        return DelayReadResult.Ok;
    }

    public DelayFrameBuffer NewBuffer() => new(FrameBytes, _audioCapacity);

    // ------------------------------------------------------------------ lifetime

    /// <summary>
    /// The transmitter has to outlive the recorder by a whole delay, or the last minute of
    /// the programme never reaches air. So the line is shared and counted rather than owned
    /// by whichever half happens to stop first.
    /// </summary>
    public void AddRef() => Interlocked.Increment(ref _refs);

    public void Release()
    {
        if (Interlocked.Decrement(ref _refs) > 0) return;
        Dispose();
    }

    public unsafe void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        // Let the writer finish what it has in hand before the mapping goes away underneath it.
        try { _queue?.CompleteAdding(); } catch (ObjectDisposedException) { }
        if (_writer is { IsAlive: true }) _writer.Join(TimeSpan.FromSeconds(5));
        _queue?.Dispose();

        if (_base is not null)
        {
            // Before the view: releasing them the other way round leaks the whole mapping.
            _view.SafeMemoryMappedViewHandle.ReleasePointer();
            _base = null;
        }

        _view.Dispose();
        _mmf.Dispose();
        _file.Dispose();

        try { File.Delete(Path); } catch { /* the sweep will get it */ }
    }

    private static string Describe(long bytes) => bytes >= 1L << 30
        ? $"{bytes / (double)(1L << 30):F1} GB"
        : $"{bytes / (double)(1L << 20):F0} MB";

    private static string Describe(TimeSpan delay) =>
        delay.TotalMinutes >= 1 && delay.TotalSeconds % 60 == 0
            ? $"{delay.TotalMinutes:F0} min"
            : $"{delay.TotalSeconds:F0} s";
}
